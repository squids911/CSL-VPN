using CSLVpn.Client.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CSLVpn.Service;

public sealed class VpnWorker(ILogger<VpnWorker> logger, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CSL VPN service started");

        await InitializeManagementPlaneAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                // The data-plane engines are deliberately not started until their
                // adapters and server-side provisioning have passed integration tests.
                logger.LogDebug("CSL VPN supervisor is waiting for data-plane integration");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal Windows service shutdown.
        }

        logger.LogInformation("CSL VPN service stopped");
    }

    private async Task InitializeManagementPlaneAsync(CancellationToken stoppingToken)
    {
        var endpointValue = configuration["CSLVPN_SERVER_URL"];
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
        {
            logger.LogWarning("CSLVPN_SERVER_URL is not configured with an HTTPS URL; enrollment is deferred");
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var api = new ManagementApiClient(http, endpoint);
        var identityStore = new DeviceIdentityStore();
        var enrollment = new ManagementEnrollment(api, identityStore, logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var identity = await enrollment.EnsureEnrolledAsync(
                    configuration["CSLVPN_BOOTSTRAP_TOKEN"],
                    stoppingToken);
                var profile = await api.GetDeviceProfileAsync(
                    identity.DeviceCredential,
                    stoppingToken);

                if (!string.Equals(profile.Status, "active", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The CSL VPN device is not active on the management server.");

                if (!string.Equals(profile.DeviceId, identity.ServerDeviceId, StringComparison.Ordinal) ||
                    !string.Equals(profile.VpnIPv4, identity.VpnIPv4, StringComparison.Ordinal))
                    throw new InvalidDataException("The management server device profile does not match the enrolled device identity.");

                if (!string.Equals(profile.VpnPrefix, "10.255.0.0/22", StringComparison.Ordinal))
                    throw new InvalidDataException($"Unexpected CSL VPN address pool: {profile.VpnPrefix}");

                logger.LogInformation(
                    "CSL VPN device profile validated; server ID {DeviceId}, assigned VPN IP {VpnIPv4}",
                    profile.DeviceId,
                    profile.VpnIPv4);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CSL VPN management enrollment failed; retrying in 60 seconds");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
        }
    }
}
