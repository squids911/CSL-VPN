using System.Diagnostics;

namespace CSL.Vpn.WindowsService.Tunnel;

public enum TunnelLinkStatus
{
    Unknown,
    Connected,
    Disconnected,
}

/// <summary>
/// Data-plane prototype: drives the bundled SoftEther VPN Client
/// (vpnclient/vpncmd) so the machine joins the L2 segment on hub "CSL".
///
/// Layout: a virtual NIC "CSLVPN", one connection account named "CSLVPN"
/// authenticating to v.csl.by:5555 with the device GUID as username and the
/// control-plane credential as RADIUS password. After the link is up the
/// control-plane-assigned address is set statically on the adapter — the hub
/// has no DHCP (SecureNAT is disabled), the address pool is owned by the
/// control plane.
///
/// Experimental until validated on real Windows hardware; disabled unless
/// Tunnel:Enabled is true.
/// </summary>
public sealed class SoftEtherTunnelManager(
    ILogger<SoftEtherTunnelManager> logger,
    IConfiguration configuration)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _appliedFingerprint;

    public bool Enabled => configuration.GetValue("Tunnel:Enabled", false);

    public string Server => configuration["Tunnel:Server"] ?? "v.csl.by:5555";

    public string Hub => configuration["Tunnel:Hub"] ?? "CSL";

    public string NicName => configuration["Tunnel:NicName"] ?? "CSLVPN";

    public string AccountName => configuration["Tunnel:AccountName"] ?? "CSLVPN";

    public string Gateway => configuration["Tunnel:Gateway"] ?? "10.255.0.1";

    public string SubnetMask => configuration["Tunnel:SubnetMask"] ?? "255.255.252.0";

    public string SoftEtherDir =>
        configuration["Tunnel:SoftEtherDir"] ?? @"C:\Program Files\CSL VPN\softether";

    private string VpnCmdPath => Path.Combine(SoftEtherDir, "vpncmd.exe");

    /// <summary>The display name SoftEther gives the virtual NIC.</summary>
    public string AdapterDisplayName => $"VPN Client Adapter - {NicName}";

    /// <summary>
    /// Brings the tunnel up and applies the control-plane address. No-op
    /// (cheap) when the same device/credential/address is already applied and
    /// the link is connected. Returns true when the link is connected.
    /// </summary>
    public async Task<bool> EnsureConnectedAsync(
        Guid deviceId, string credential, string address, CancellationToken cancellationToken)
    {
        if (!Enabled || !OperatingSystem.IsWindows())
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var fingerprint = $"{deviceId}|{credential}|{address}";
            var status = await GetLinkStatusAsync(cancellationToken);
            if (fingerprint == _appliedFingerprint && status == TunnelLinkStatus.Connected)
            {
                return true;
            }

            await EnsureClientServiceAsync(cancellationToken);
            await EnsureNicAsync(cancellationToken);
            await EnsureAccountAsync(deviceId, cancellationToken);
            await SetPasswordAsync(credential, cancellationToken);
            await RunVpnCmdAsync($"AccountConnect {AccountName}", cancellationToken);

            status = await WaitForConnectionAsync(cancellationToken);
            if (status != TunnelLinkStatus.Connected)
            {
                logger.LogWarning("The VPN link did not reach the connected state within {Timeout}.", ConnectTimeout);
                return false;
            }

            await ConfigureAddressAsync(address, cancellationToken);
            _appliedFingerprint = fingerprint;
            logger.LogInformation("VPN tunnel up as {DeviceId} with address {Address}.", deviceId, address);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tears the link down (credential rotation, shutdown).</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _appliedFingerprint = null;
            var result = await RunVpnCmdAsync($"AccountDisconnect {AccountName}", cancellationToken);
            if (result.ExitCode != 0)
            {
                logger.LogDebug("AccountDisconnect exited with {ExitCode}: {Output}", result.ExitCode, result.Output.Trim());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string BuildAccountCreateArguments(string account, string server, string hub, Guid deviceId, string nic) =>
        $"AccountCreate {account} /SERVER:{server} /HUB:{hub} /USERNAME:{deviceId} /NICNAME:{nic}";

    public static string BuildPasswordArguments(string account, string credential) =>
        $"AccountPasswordSet {account} /PASSWORD:{credential} /TYPE:radius";

    public static string BuildNetshArguments(string adapterDisplayName, string address, string mask, string gateway) =>
        $"interface ip set address name=\"{adapterDisplayName}\" static {address} {mask} {gateway} 1";

    private async Task EnsureClientServiceAsync(CancellationToken cancellationToken)
    {
        var query = await RunAsync("sc.exe", "query SEVPNCLIENT", cancellationToken);
        if (query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var start = await RunAsync("sc.exe", "start SEVPNCLIENT", cancellationToken);
        if (start.ExitCode != 0
            && !start.Output.Contains("already running", StringComparison.OrdinalIgnoreCase)
            && !start.Output.Contains("1056"))
        {
            throw new InvalidOperationException($"Could not start the SoftEther VPN Client service: {start.Output.Trim()}");
        }
    }

    private async Task EnsureNicAsync(CancellationToken cancellationToken)
    {
        var list = await RunVpnCmdAsync("NicList", cancellationToken);
        if (SoftEtherOutput.ListContains(list.Output, NicName))
        {
            return;
        }

        var create = await RunVpnCmdAsync($"NicCreate {NicName}", cancellationToken);
        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException($"NicCreate failed: {create.Output.Trim()}");
        }
    }

    private async Task EnsureAccountAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var list = await RunVpnCmdAsync("AccountList", cancellationToken);
        if (SoftEtherOutput.ListContains(list.Output, AccountName))
        {
            return;
        }

        var create = await RunVpnCmdAsync(
            BuildAccountCreateArguments(AccountName, Server, Hub, deviceId, NicName), cancellationToken);
        if (create.ExitCode != 0)
        {
            throw new InvalidOperationException($"AccountCreate failed: {create.Output.Trim()}");
        }
    }

    private async Task SetPasswordAsync(string credential, CancellationToken cancellationToken)
    {
        var result = await RunVpnCmdAsync(BuildPasswordArguments(AccountName, credential), cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("AccountPasswordSet failed.");
        }
    }

    private async Task<TunnelLinkStatus> GetLinkStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunVpnCmdAsync($"AccountStatusGet {AccountName}", cancellationToken);
            return SoftEtherOutput.IsConnected(result.Output)
                ? TunnelLinkStatus.Connected
                : TunnelLinkStatus.Disconnected;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or TimeoutException)
        {
            return TunnelLinkStatus.Unknown;
        }
    }

    private async Task<TunnelLinkStatus> WaitForConnectionAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + ConnectTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await GetLinkStatusAsync(cancellationToken);
            if (status == TunnelLinkStatus.Connected)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return TunnelLinkStatus.Disconnected;
    }

    private async Task ConfigureAddressAsync(string address, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            "netsh.exe",
            BuildNetshArguments(AdapterDisplayName, address, SubnetMask, Gateway),
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Configuring {address} on '{AdapterDisplayName}' failed: {result.Output.Trim()}");
        }
    }

    private async Task<CommandResult> RunVpnCmdAsync(string command, CancellationToken cancellationToken) =>
        await RunAsync(VpnCmdPath, $"/client localhost /cmd {command}", cancellationToken);

    internal static async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"Command '{fileName} {arguments}' timed out after {CommandTimeout}.");
        }

        return new CommandResult(process.ExitCode, $"{await stdout}\n{await stderr}");
    }

    internal sealed record CommandResult(int ExitCode, string Output);
}

/// <summary>Tolerant parsers for vpncmd's human-readable output.</summary>
public static class SoftEtherOutput
{
    public static bool ListContains(string output, string name) =>
        output.Contains(name, StringComparison.OrdinalIgnoreCase);

    public static bool IsConnected(string output) =>
        output.Contains("Connection Completed", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Session Established", StringComparison.OrdinalIgnoreCase);
}
