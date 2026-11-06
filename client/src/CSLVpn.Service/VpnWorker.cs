namespace CSLVpn.Service;

public sealed class VpnWorker(ILogger<VpnWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CSL VPN service started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            // The service owns device identity, management heartbeats, transport
            // lifecycle, and recovery. Engine adapters are added in later milestones.
            logger.LogDebug("VPN service supervisor tick");
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        logger.LogInformation("CSL VPN service stopped");
    }
}
