using System.Collections.Concurrent;
using System.Text.Json;

namespace Elementarr.Services;

public sealed class TvMetadataService
{
    private readonly StateStore store;
    private readonly MediaApi api;
    private readonly string path;
    private readonly ConcurrentDictionary<string, TvSeries> entries = new();
    private readonly ConcurrentDictionary<string, TvEpisode> episodes = new();
    private readonly ConcurrentDictionary<string, TvSeasonPage> seasons = new();
    private readonly SemaphoreSlim writer = new(1, 1);

    /// <summary>Loads a persistent TV-only metadata index, separate from movie metadata and both watch snapshots.</summary>
    public TvMetadataService(StateStore store, MediaApi api, IConfiguration configuration)
    {
        this.store = store; this.api = api;
        path = Path.Combine(configuration["ELEMENTARR_DATA"]!, "tv-metadata.json");
        if (File.Exists(path + ".seasons"))
            foreach (var item in JsonSerializer.Deserialize<Dictionary<string, TvSeasonPage>>(File.ReadAllText(path + ".seasons")) ?? []) seasons[item.Key] = item.Value;
        if (File.Exists(path + ".episodes"))
            foreach (var item in JsonSerializer.Deserialize<Dictionary<string, TvEpisode>>(File.ReadAllText(path + ".episodes")) ?? []) episodes[item.Key] = item.Value;
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
            Poster = saved.Poster ?? value.Poster, TvdbId = saved.TvdbId > 0 ? saved.TvdbId : value.TvdbId,
            Facts = saved.Facts is null ? value.Facts : saved.Facts with {
                Imdb = value.Facts?.Imdb ?? saved.Facts.Imdb, RottenTomatoes = value.Facts?.RottenTomatoes ?? saved.Facts.RottenTomatoes,
                RatingOrigin = value.Facts?.Imdb.HasValue == true || value.Facts?.RottenTomatoes.HasValue == true ? "Sonarr / Plex / TMDB" : saved.Facts.RatingOrigin } };
    }

    /// <summary>Resolves TVDB identifiers through TMDB's TV namespace and loads actual show artwork, ratings, genres, and credits.</summary>
    public async Task<TvSeries> ResolveAsync(TvSeries value, CancellationToken ct, bool force = false)
    {
        var cached = Cached(value);
        if (store.Settings.TmdbToken.Length == 0) return cached;
        if (!force && cached.MetadataUpdatedAt > DateTimeOffset.UtcNow.AddDays(-7) && cached.Facts?.MetadataVersion == 2 && cached.TmdbId.HasValue) return cached;
        var tmdb = cached.TmdbId;
        if (!tmdb.HasValue && cached.TvdbId > 0)
        {
            var found = await api.TmdbAsync(store.Settings, $"find/{cached.TvdbId}?external_source=tvdb_id", ct);
            var matches = found.Items("tv_results").Where(x => x.Number("id") > 0).ToArray();
            if (matches.Length == 1) tmdb = (int)matches[0].Number("id");
        }
        if (!tmdb.HasValue) return cached;
        var json = await api.TmdbAsync(store.Settings, $"tv/{tmdb}?append_to_response=external_ids,credits,images,videos,content_ratings&include_image_language=en,null", ct);
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
                credits.Items("cast").Take(12).Select(x => new CastMember(x.Text("name"), x.Text("character"), JsonFields.PosterPath(x.Text("profile_path")))).ToList()) {
                    Logo = JsonFields.PosterPath(Child(json, "images").Items("logos").OrderByDescending(x => x.Text("iso_639_1") == "en").FirstOrDefault().Text("file_path")),
                    Tagline = json.Text("tagline"), Director = string.Join(", ", json.Items("created_by").Select(x => x.Text("name"))),
                    Certification = Child(json, "content_ratings").Items("results").FirstOrDefault(x => x.Text("iso_3166_1") == store.Settings.Region).Text("rating"),
                    ImdbId = external.Text("imdb_id"), Imdb = cached.Facts?.Imdb, RottenTomatoes = cached.Facts?.RottenTomatoes,
                    Tmdb = json.Number("vote_count") > 0 ? json.Number("vote_average") : null, Votes = (int)json.Number("vote_count"),
                    Countries = json.Items("production_countries").Select(x => x.Text("name")).ToList(),
                    Studios = json.Items("production_companies").Select(x => x.Text("name")).ToList(), Trailer = Trailer(json),
                    FirstAirDate = json.Text("first_air_date"), Country = json.Items("production_countries").FirstOrDefault().Text("iso_3166_1"), Popularity = json.Number("popularity"),
                    SeasonList = json.Items("seasons").Where(x => x.Number("season_number") >= 0)
                        .Select(x => new TvSeason((int)x.Number("season_number"), x.Text("name"), (int)x.Number("episode_count"), x.Text("air_date"))).ToList(),
                    RatingOrigin = cached.Facts?.Imdb.HasValue == true || cached.Facts?.RottenTomatoes.HasValue == true ? "Sonarr / Plex / TMDB" : "TMDB", MetadataVersion = 2
                } };
        Remember(result);
        return result;
    }

    /// <summary>Loads cached episode-specific stills, ratings and credits without confusing episode IDs with show IDs.</summary>
    public async Task<TvEpisode?> EpisodeAsync(int tmdb, int season, int episode, CancellationToken ct)
    {
        if (tmdb <= 0 || season < 0 || episode < 0) throw new ArgumentException("Choose a valid TV episode.");
        var key = $"{tmdb}:{season}:{episode}";
        episodes.TryGetValue(key, out var cached);
        if (cached?.UpdatedAt > DateTimeOffset.UtcNow.AddDays(-7) || store.Settings.TmdbToken.Length == 0) return cached;
        var json = await api.TmdbAsync(store.Settings, $"tv/{tmdb}/season/{season}/episode/{episode}?append_to_response=credits,external_ids,videos", ct);
        if (json.Number("id") <= 0 || json.Number("season_number") != season || json.Number("episode_number") != episode)
            throw new UpstreamException("TMDB did not return the requested TV episode.");
        var credits = Child(json, "credits");
        var result = new TvEpisode(json.Text("name"), json.Text("overview"), json.Text("air_date"), new MovieFacts {
            Runtime = (int)json.Number("runtime"), Backdrop = JsonFields.PosterPath(json.Text("still_path")),
            Tmdb = json.Number("vote_count") > 0 ? json.Number("vote_average") : null, Votes = (int)json.Number("vote_count"),
            ImdbId = Child(json, "external_ids").Text("imdb_id"), RatingOrigin = "TMDB", Trailer = Trailer(json),
            Director = string.Join(", ", credits.Items("crew").Concat(json.Items("crew")).Where(x => x.Text("job") == "Director").Select(x => x.Text("name")).Distinct()),
            Cast = credits.Items("cast").Concat(json.Items("guest_stars")).DistinctBy(x => x.Text("name")).Take(12)
                .Select(x => new CastMember(x.Text("name"), x.Text("character"), JsonFields.PosterPath(x.Text("profile_path")))).ToList()
        }, DateTimeOffset.UtcNow);
        episodes[key] = result;
        return result;
    }

    /// <summary>Caches provider season episodes without caching installation-specific watch flags.</summary>
    public async Task<List<TvSeasonEpisode>> SeasonAsync(int tmdb, int season, CancellationToken ct)
    {
        if (tmdb <= 0 || season is < 0 or > 10000) throw new ArgumentException("Choose a valid TV season.");
        var key = $"{tmdb}:{season}";
        seasons.TryGetValue(key, out var cached);
        if (cached?.UpdatedAt > DateTimeOffset.UtcNow.AddHours(-6)) return cached.Episodes;
        if (store.Settings.TmdbToken.Length == 0) return cached?.Episodes ?? [];
        var json = await api.TmdbAsync(store.Settings, $"tv/{tmdb}/season/{season}", ct);
        if (json.Number("id") <= 0 || json.Number("season_number") != season || !json.TryGetProperty("episodes", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new UpstreamException("TMDB did not return the requested TV season.");
        var result = rows.Items().Where(x => x.Number("season_number") == season && x.Number("episode_number") > 0)
            .Select(x => new TvSeasonEpisode(season, (int)x.Number("episode_number"), x.Text("name"), x.Text("air_date"),
                x.Text("overview"), JsonFields.PosterPath(x.Text("still_path")))).DistinctBy(x => x.Episode).OrderBy(x => x.Episode).ToList();
        seasons[key] = new(result, DateTimeOffset.UtcNow);
        await SaveAsync(ct);
        return result;
    }

    /// <summary>Reads an optional nested provider object without assuming it is present.</summary>
    private static JsonElement Child(JsonElement json, string name) => json.TryGetProperty(name, out var value) ? value : default;

    /// <summary>Accepts only a bounded YouTube trailer key for external playback.</summary>
    private static string? Trailer(JsonElement json)
    {
        var key = Child(json, "videos").Items("results").FirstOrDefault(x => x.Text("site") == "YouTube" && x.Text("type") == "Trailer").Text("key");
        return key.Length is >= 6 and <= 40 && key.All(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '-') ? "https://www.youtube.com/watch?v=" + key : null;
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
            await File.WriteAllTextAsync(path + ".episodes.tmp", JsonSerializer.Serialize(episodes), ct);
            File.Move(path + ".episodes.tmp", path + ".episodes", true);
            await File.WriteAllTextAsync(path + ".seasons.tmp", JsonSerializer.Serialize(seasons), ct);
            File.Move(path + ".seasons.tmp", path + ".seasons", true);
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
