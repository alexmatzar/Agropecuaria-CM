using Agropecuaria.Application;

namespace Agropecuaria.LocalApi;

public sealed class SyncWorker(SyncCoordinator coordinator, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await coordinator.RunOnceAsync(stoppingToken);
                failures = coordinator.Status.State is "Offline" or "AttentionRequired" ? Math.Min(failures + 1, 5) : 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Keep the local POS running; never discard a pending event.
                logger.LogError(ex, "Error interno de sincronización; los pendientes permanecen en SQLite.");
                failures = Math.Min(failures + 1, 5);
            }
            var delay = TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, failures)) + Random.Shared.NextDouble());
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
