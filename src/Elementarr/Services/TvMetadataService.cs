using System.Collections.Concurrent;
using System.Text.Json;

namespace Elementarr.Services;

public sealed class TvMetadataService
{
    private readonly StateStore store;
    private readonly MediaApi api;
    private readonly string path;
    private readonly ConcurrentDictionary<string, TvSeries> entries = new();
    private readonly SemaphoreSlim writer = new(1, 1);

    /// <summary>Loads a persistent TV-only metadata index, separate from movie metadata and both watch snapshots.</summary>
    public TvMetadataService(StateStore store, MediaApi api, IConfiguration configuration)
    {
        this.store = store; this.api = api;
        path = Path.Combine(configuration["ELEMENTARR_DATA"]!, "tv-metadata.json");
        if (!File.Exists(path)) return;
        var saved = JsonSerializer.Deserialize<List<TvSeries>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Stored TV metadata is invalid.");
        foreach (var item in saved) Remember(item);
    }

    /// <summary>Combines cached show metadata with current collection state without substituting movie IDs or stale availability.</summary>
    public TvSeries Cached(TvSeries value)
    {
        TvSeries? saved = null;
        if (value.TmdbId is int tmdb) entries.TryGetValue("tmdb:" + tmdb, out saved);
        if (saved is null && value.TvdbId > 0) entries.TryGetValue("tvdb:" + value.TvdbId, out saved);
        return saved is null ? value : saved with { InSonarr = value.InSonarr, SonarrId = value.SonarrId,
            EpisodeCount = value.EpisodeCount, EpisodeFileCount = value.EpisodeFileCount,
            HasWatchedEpisodes = value.HasWatchedEpisodes, Reason = value.Reason,
            Poster = saved.Poster ?? value.Poster, TvdbId = saved.TvdbId > 0 ? saved.TvdbId : value.TvdbId };
    }

    /// <summary>Resolves TVDB identifiers through TMDB's TV namespace and loads actual show artwork, ratings, genres, and credits.</summary>
    public async Task<TvSeries> ResolveAsync(TvSeries value, CancellationToken ct, bool force = false)
    {
        var cached = Cached(value);
        if (store.Settings.TmdbToken.Length == 0) return cached;
        if (!force && cached.MetadataUpdatedAt > DateTimeOffset.UtcNow.AddDays(-7) && cached.TmdbId.HasValue) return cached;
        var tmdb = cached.TmdbId;
        if (!tmdb.HasValue && cached.TvdbId > 0)
        {
            var found = await api.TmdbAsync(store.Settings, $"find/{cached.TvdbId}?external_source=tvdb_id", ct);
            var matches = found.Items("tv_results").Where(x => x.Number("id") > 0).ToArray();
            if (matches.Length == 1) tmdb = (int)matches[0].Number("id");
        }
        if (!tmdb.HasValue) return cached;
        var json = await api.TmdbAsync(store.Settings, $"tv/{tmdb}?append_to_response=external_ids,credits", ct);
        if (json.Number("id") != tmdb) throw new UpstreamException("TMDB did not return the requested TV series.");
        var tvdb = json.TryGetProperty("external_ids", out var external) ? (int)external.Number("tvdb_id") : cached.TvdbId;
        var credits = json.TryGetProperty("credits", out var cast) ? cast : default;
        var runtime = json.Items("episode_run_time").FirstOrDefault();
        var result = cached with { TmdbId = tmdb, TvdbId = tvdb > 0 ? tvdb : cached.TvdbId,
            Title = json.Text("name", cached.Title), Overview = json.Text("overview", cached.Overview),
            Year = int.TryParse(json.Text("first_air_date").Split('-')[0], out var year) ? year : cached.Year,
            Poster = JsonFields.PosterPath(json.Text("poster_path")) ?? cached.Poster,
            Score = json.Number("vote_count") > 0 ? json.Number("vote_average") : cached.Score,
            MetadataUpdatedAt = DateTimeOffset.UtcNow,
            Facts = new(json.Items("genres").Select(x => x.Text("name")).Where(x => x.Length > 0).ToList(), json.Text("status"),
                (int)json.Number("number_of_seasons"), (int)json.Number("number_of_episodes"), runtime.TryGetInt32Safe(),
                json.Text("original_language"), JsonFields.PosterPath(json.Text("backdrop_path")),
                credits.Items("cast").Take(12).Select(x => new CastMember(x.Text("name"), x.Text("character"), JsonFields.PosterPath(x.Text("profile_path")))).ToList()) };
        Remember(result);
        return result;
    }

    /// <summary>Indexes shows in bounded batches and retains usable Sonarr metadata when optional TMDB requests fail.</summary>
    public async Task<List<TvSeries>> ResolveManyAsync(IEnumerable<TvSeries> source, CancellationToken ct)
    {
        List<TvSeries> result = [];
        foreach (var batch in source.Chunk(4))
        {
            result.AddRange(await Task.WhenAll(batch.Select(async item =>
            {
                try { return await ResolveAsync(item, ct); }
                catch (UpstreamException) { return Cached(item); }
            })));
        }
        await SaveAsync(ct);
        return result;
    }

    /// <summary>Atomically saves resolved TV metadata so normal browsing and restarts do not repeat all provider lookups.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(entries.Values.DistinctBy(x => x.TmdbId)), ct);
            File.Move(path + ".tmp", path, true);
        }
        finally { writer.Release(); }
    }

    /// <summary>Stores both show identifier namespaces as explicit TV keys.</summary>
    private void Remember(TvSeries value)
    {
        if (value.TmdbId is int tmdb) entries["tmdb:" + tmdb] = value;
        if (value.TvdbId > 0) entries["tvdb:" + value.TvdbId] = value;
    }
}

internal static class TvJsonFields
{
    /// <summary>Reads optional scalar runtimes without throwing on absent or malformed TMDB arrays.</summary>
    internal static int TryGetInt32Safe(this JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : 0;
}
