using Npgsql;

namespace CSL.Vpn.ControlPlane;

/// <summary>
/// Hourly sweep that revokes devices offline for more than 14 days and
/// returns their addresses to the pool (see Security requirements in README).
/// </summary>
public sealed class CleanupService(NpgsqlDataSource dataSource, ILogger<CleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan MaxOffline = TimeSpan.FromDays(14);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            await SweepAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            var revoked = await DeviceMaintenance.RevokeExpiredDevicesAsync(dataSource, MaxOffline, cancellationToken);
            if (revoked > 0)
            {
                logger.LogInformation(
                    "Cleanup sweep revoked {Count} expired device(s) and released their addresses.", revoked);
            }
        }
        catch (NpgsqlException exception)
        {
            logger.LogError(exception, "Cleanup sweep failed; will retry on the next tick.");
        }
    }
}
