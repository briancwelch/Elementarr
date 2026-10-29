namespace Elementarr.Services;

public sealed class SyncWorker(IServiceProvider services, ILogger<SyncWorker> logger) : BackgroundService
{
    /// <summary>Refreshes configured libraries every thirty minutes while preserving cached data during upstream outages.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        // Initial synchronization remains explicit so configuring credentials does not trigger an unexpected long import.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var store = services.GetRequiredService<StateStore>();
            var settings = store.Settings;
            if (settings.RadarrKey.Length == 0 && settings.SonarrKey.Length == 0 && settings.PlexToken.Length == 0
                && (!settings.TautulliEnabled || settings.TautulliKey.Length == 0)) continue;
            try
            {
                var result = await services.GetRequiredService<MediaService>().SyncAsync(stoppingToken);
                foreach (var error in result.Errors) logger.LogWarning("Scheduled synchronization: {Reason}", error);
            }
            catch (UpstreamException exception) { logger.LogWarning("Scheduled synchronization failed: {Reason}", exception.Message); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
