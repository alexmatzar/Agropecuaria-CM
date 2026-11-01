using Agropecuaria.Application;

namespace Agropecuaria.LocalApi;

public sealed record IdentityMode(bool LegacyDevelopment);
public sealed class CloudSessionWorker(CloudOperatorService cloud, IdentityMode mode, ILogger<CloudSessionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (mode.LegacyDevelopment) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await cloud.RevalidateAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Falló la revalidación de identidad; no se extenderá la autorización sin conexión."); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
