using System.Text.Json;
using CSL.Vpn.WindowsService.Tunnel;

namespace CSL.Vpn.WindowsService;

/// <summary>
/// Management-plane agent loop: enroll with a one-time code when one is
/// available, otherwise heartbeat the control plane. No tunnel is
/// established yet — this is device lifecycle only.
/// </summary>
public sealed class Worker(
    ILogger<Worker> logger,
    IConfiguration configuration,
    ControlPlaneClient client,
    ClientIdentityStore identityStore,
    ClientState state,
    SoftEtherTunnelManager tunnel) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private string StateFile =>
        configuration["ControlPlane:StateFile"] ?? @"C:\ProgramData\CSL VPN\state.json";

    private string EnrollmentCodeFile =>
        configuration["ControlPlane:EnrollmentCodeFile"] ?? @"C:\ProgramData\CSL VPN\enrollment-code.txt";

    private TimeSpan HeartbeatInterval =>
        TimeSpan.FromSeconds(Math.Max(10, configuration.GetValue("ControlPlane:HeartbeatIntervalSeconds", 60)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CSL VPN agent started (management plane only; no tunnel is established).");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var healthy = await TickAsync(stoppingToken);
                await Task.Delay(healthy ? HeartbeatInterval : RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected error in the agent loop.");
                state.Update("error", message: exception.Message);
                PersistState();
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }

        await TryDisconnectAsync();
    }

    /// <summary>One loop iteration. Returns true when the next tick may wait a full heartbeat interval.</summary>
    private async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var identity = identityStore.TryLoad();
        if (identity is null)
        {
            return await TryEnrollAsync(cancellationToken);
        }

        var heartbeat = await client.HeartbeatAsync(identity.Credential, cancellationToken);
        if (heartbeat.Success && heartbeat.Value is not null)
        {
            state.Update("connected", heartbeat.Value.Address, heartbeat.Value.SessionId, heartbeat: true);
            PersistState();

            if (tunnel.Enabled)
            {
                if (heartbeat.Value.Address is { } address)
                {
                    await EnsureTunnelAsync(identity, address, cancellationToken);
                }
                else
                {
                    state.SetTunnel("down");
                    PersistState();
                }
            }

            return true;
        }

        if (heartbeat.Unauthorized)
        {
            logger.LogWarning(
                "The control plane rejected the device credential; forgetting it and waiting for a new enrollment code.");
            await tunnel.DisconnectAsync(cancellationToken);
            state.SetTunnel(tunnel.Enabled ? "down" : null);
            identityStore.Delete();
            state.Update("credential_rejected", message: heartbeat.Error);
            PersistState();
            return false;
        }

        state.Update("heartbeat_failed", message: heartbeat.Error);
        PersistState();
        return false;
    }

    private async Task<bool> TryEnrollAsync(CancellationToken cancellationToken)
    {
        if (!TryReadEnrollmentCode(out var code))
        {
            state.Update(
                "waiting_for_enrollment_code",
                message: $"Place a one-time enrollment code into {EnrollmentCodeFile}.");
            PersistState();
            return false;
        }

        var deviceId = identityStore.LoadOrCreateDeviceId();
        var result = await client.EnrollAsync(code, Environment.MachineName, deviceId, cancellationToken);
        if (result.Success && result.Value is not null)
        {
            identityStore.Save(new ClientIdentity(result.Value.DeviceId, result.Value.Credential));
            DeleteEnrollmentCode();
            state.Update("enrolled", result.Value.Address, message: "Enrollment successful.");
            PersistState();
            logger.LogInformation(
                "Enrolled as device {DeviceId} with address {Address}.",
                result.Value.DeviceId,
                result.Value.Address);
            return false; // next tick starts heartbeats immediately
        }

        state.Update("enrollment_failed", message: result.Error);
        PersistState();
        logger.LogWarning("Enrollment failed: {Error}", result.Error);
        return false;
    }

    private bool TryReadEnrollmentCode(out string code)
    {
        code = string.Empty;
        try
        {
            if (!File.Exists(EnrollmentCodeFile))
            {
                return false;
            }

            var content = File.ReadAllText(EnrollmentCodeFile).Trim();
            if (content.Length is 0 or > 128)
            {
                return false;
            }

            code = content;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the enrollment code file.");
            return false;
        }
    }

    private void DeleteEnrollmentCode()
    {
        try
        {
            if (File.Exists(EnrollmentCodeFile))
            {
                File.Delete(EnrollmentCodeFile);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete the used enrollment code file.");
        }
    }

    private void PersistState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(
                StateFile,
                JsonSerializer.Serialize(state.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the state file.");
        }
    }

    private async Task EnsureTunnelAsync(ClientIdentity identity, string address, CancellationToken cancellationToken)
    {
        try
        {
            var up = await tunnel.EnsureConnectedAsync(identity.DeviceId, identity.Credential, address, cancellationToken);
            state.SetTunnel(up ? "up" : "down");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Tunnel bring-up failed.");
            state.SetTunnel("error");
        }

        PersistState();
    }

    private async Task TryDisconnectAsync()
    {
        try
        {
            await tunnel.DisconnectAsync(CancellationToken.None);

            var identity = identityStore.TryLoad();
            if (identity is not null)
            {
                await client.DisconnectAsync(identity.Credential, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Disconnect on shutdown failed.");
        }
    }
}
