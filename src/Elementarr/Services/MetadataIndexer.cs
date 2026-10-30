using System.Threading.Channels;
using System.Collections.Concurrent;

namespace Elementarr.Services;

public sealed record IndexStatus(bool Running, int Completed, int Total, int Failed, string? Error = null,
    List<Choice>? Unavailable = null);

public sealed class MetadataIndexer(StateStore store, MetadataService metadata, ILogger<MetadataIndexer> logger) : BackgroundService
{
    private readonly Channel<bool> queue = Channel.CreateBounded<bool>(1);
    private IndexStatus status = new(false, 0, 0, 0);
    public IndexStatus Status => Volatile.Read(ref status);

    /// <summary>Queues one explicit full metadata refresh without blocking the browser or duplicating jobs.</summary>
    public void Start(bool rematch = false)
    {
        if (Status.Running) return;
        var total = store.Snapshot.Library.Select(x => x.TmdbId).Concat(store.Snapshot.History.Where(x => x.TmdbId.HasValue)
            .Select(x => x.TmdbId!.Value)).Distinct().Count();
        Volatile.Write(ref status, new(true, 0, total, 0));
        if (!queue.Writer.TryWrite(rematch)) Volatile.Write(ref status, new(false, 0, total, 0, "A refresh is already queued."));
    }

    /// <summary>Fetches detailed metadata in bounded batches, saving progress so interruptions retain completed work.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var rematch in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (rematch) await RefreshMatchesAsync(stoppingToken);
            var ids = store.Snapshot.Library.Select(x => x.TmdbId).Concat(store.Snapshot.History.Where(x => x.TmdbId.HasValue)
                .Select(x => x.TmdbId!.Value)).Where(x => x > 0).Distinct().ToArray();
            int completed = 0, failed = 0;
            var unavailable = new ConcurrentBag<int>();
            var titles = store.Snapshot.Library.Select(x => (x.TmdbId, x.Title)).Concat(store.Snapshot.History
                .Where(x => x.TmdbId.HasValue).Select(x => (x.TmdbId!.Value, x.Title))).GroupBy(x => x.Item1)
                .ToDictionary(x => x.Key, x => x.First().Title);
            try
            {
                foreach (var batch in ids.Chunk(4))
                {
                    await Task.WhenAll(batch.Select(async id =>
                    {
                        try { await metadata.GetAsync(id, true, stoppingToken); }
                        catch (UpstreamException exception)
                        {
                            Interlocked.Increment(ref failed); unavailable.Add(id);
                            logger.LogWarning("Metadata for TMDB {MovieId} is unavailable (HTTP {Status}).", id, exception.StatusCode);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            Interlocked.Increment(ref failed); unavailable.Add(id);
                            // Stack frames identify parser failures without logging API payloads, credentials, or movie titles.
                            logger.LogWarning("Metadata for TMDB {MovieId} failed with {ExceptionType}. {StackTrace}",
                                id, exception.GetType().Name, exception.StackTrace);
                        }
                        Interlocked.Increment(ref completed);
                    }));
                    if (completed % 40 == 0 || completed == ids.Length) await metadata.SaveAsync(stoppingToken);
                    Volatile.Write(ref status, new(true, completed, ids.Length, failed, Unavailable: UnavailableMovies(unavailable, titles)));
                }
                Volatile.Write(ref status, new(false, completed, ids.Length, failed, Unavailable: UnavailableMovies(unavailable, titles)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning("Metadata cache write stopped with {ExceptionType}.", exception.GetType().Name);
                Volatile.Write(ref status, new(false, completed, ids.Length, failed, "Metadata refresh stopped. Existing cached metadata was retained.", UnavailableMovies(unavailable, titles)));
            }
        }
    }

    /// <summary>Repairs only unambiguous unmatched movie plays from the current Radarr title/year index, retaining explicit matches and all history.</summary>
    private async Task RefreshMatchesAsync(CancellationToken ct)
    {
        await store.Mutation.WaitAsync(ct);
        try
        {
            var snapshot = store.Snapshot;
            var index = snapshot.Library.GroupBy(x => (Title: x.Title.ToUpperInvariant(), x.Year))
                .ToDictionary(x => x.Key, x => x.Select(movie => movie.TmdbId).Distinct().ToArray());
            var history = snapshot.History.Select(play => !play.TmdbId.HasValue
                && index.TryGetValue((play.Title.ToUpperInvariant(), play.Year), out var matches) && matches.Length == 1
                ? play with { TmdbId = matches[0] } : play).ToList();
            if (!history.SequenceEqual(snapshot.History)) await store.SaveSnapshotAsync(snapshot with { History = history }, ct);
        }
        finally { store.Mutation.Release(); }
    }

    /// <summary>Identifies affected movies by their real local title without exposing integration credentials or unrelated history.</summary>
    private static List<Choice> UnavailableMovies(IEnumerable<int> ids, IReadOnlyDictionary<int, string> titles) =>
        ids.Distinct().Select(id => new Choice(id.ToString(), titles.GetValueOrDefault(id, id.ToString()))).ToList();
}
