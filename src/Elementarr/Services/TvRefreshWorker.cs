using System.Threading.Channels;

namespace Elementarr.Services;

public sealed record TvRefreshStatus(bool Running, string Stage, DateTimeOffset? CompletedAt = null, string? Error = null);

public sealed class TvRefreshWorker(StateStore store, IServiceProvider services) : BackgroundService
{
    private readonly Channel<bool> queue = Channel.CreateBounded<bool>(1);
    private int running;
    private TvRefreshStatus status = new(false, "idle");
    public TvRefreshStatus Status => Volatile.Read(ref status);

    /// <summary>Queues a TV repair once without blocking the browser or launching duplicate source scans.</summary>
    public void Start()
    {
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        Volatile.Write(ref status, new(true, "queued"));
        if (!queue.Writer.TryWrite(true)) Interlocked.Exchange(ref running, 0);
    }

    /// <summary>Repairs legacy TV snapshots on startup and handles explicit refreshes without modifying upstream libraries.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if ((store.Tv.Library.Any(x => x.SonarrId == 0 || x.MetadataUpdatedAt is null) && store.Settings.SonarrKey.Length > 0)
            || store.Tv.History.Any(x => x.TmdbId is null && x.TvdbId is null)) Start();
        try
        {
            await foreach (var _ in queue.Reader.ReadAllAsync(stoppingToken))
            {
                List<string> errors = [];
                try
                {
                    var media = services.GetRequiredService<MediaService>();
                    if (store.Settings.SonarrKey.Length > 0)
                    {
                        Volatile.Write(ref status, new(true, "Indexing TV collection and artwork"));
                        errors.AddRange((await media.SyncAsync(stoppingToken, "sonarr")).Errors);
                    }
                    if (store.Settings.PlexServerId.Length > 0)
                    {
                        Volatile.Write(ref status, new(true, "Resolving Plex episodes and parent series"));
                        errors.AddRange((await media.SyncAsync(stoppingToken, "tv-history")).Errors);
                    }
                    Volatile.Write(ref status, new(false, "complete", DateTimeOffset.UtcNow, errors.Count > 0 ? string.Join(" ", errors) : null));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) when (exception is UpstreamException or ArgumentException)
                { Volatile.Write(ref status, new(false, "failed", Error: exception.Message)); }
                catch (Exception) { Volatile.Write(ref status, new(false, "failed", Error: "TV refresh stopped. Existing cached data was retained; check application logs.")); }
                finally { Interlocked.Exchange(ref running, 0); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Normal service shutdown. */ }
    }
}
