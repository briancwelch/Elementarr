using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Elementarr.Services;

public sealed class TvService(StateStore store, MediaApi api, PlexService plex, TvMetadataService metadata)
{
    /// <summary>Refreshes TV sources independently under the installation's shared mutation lock.</summary>
    public async Task<(bool Library, bool History, List<string> Errors)> SyncLockedAsync(string source, CancellationToken ct)
    {
        bool library = false, history = false;
        List<string> errors = [];
        var settings = store.Settings;
        if (source is ("all" or "sonarr") && settings.SonarrKey.Length > 0)
        {
            try
            {
                var response = await api.SonarrAsync(settings, "series", ct);
                if (response.ValueKind != JsonValueKind.Array) throw new UpstreamException("Sonarr returned an invalid collection.");
                var series = response.Items().Where(x => x.Number("tvdbId") > 0).Select(ParseSonarr).ToList();
                series = await metadata.ResolveManyAsync(series, ct);
                await store.SaveTvAsync(store.Tv with { Library = series, LibrarySyncedAt = DateTimeOffset.UtcNow, LibraryError = null }, ct);
                library = true;
            }
            catch (UpstreamException exception)
            {
                errors.Add("Sonarr: " + exception.Message);
                await store.SaveTvAsync(store.Tv with { LibraryError = exception.Message }, ct);
            }
        }
        if (source is ("all" or "tv-history") && settings.PlexToken.Length > 0 && settings.PlexServerId.Length > 0)
        {
            try
            {
                var plays = await ReadHistoryAsync(settings, ct);
                await store.SaveTvAsync(store.Tv with { History = plays, HistorySyncedAt = DateTimeOffset.UtcNow, HistoryError = null }, ct);
                history = true;
            }
            catch (UpstreamException exception)
            {
                errors.Add("TV history: " + exception.Message);
                await store.SaveTvAsync(store.Tv with { HistoryError = exception.Message }, ct);
            }
        }
        if (source == "sonarr" && settings.SonarrKey.Length == 0) errors.Add("Configure Sonarr in Connections first.");
        if (source == "tv-history" && settings.PlexServerId.Length == 0) errors.Add("Select your owned Plex server in Connections first.");
        return (library, history, errors);
    }

    /// <summary>Imports only owner-profile episodes and resolves show identifiers from show metadata, never episode GUIDs.</summary>
    private async Task<List<TvWatch>> ReadHistoryAsync(Connections settings, CancellationToken ct)
    {
        await plex.CheckAsync(settings, ct);
        List<TvWatch> plays = [];
        Dictionary<string, JsonElement> shows = [];
        Dictionary<string, JsonElement> episodes = [];
        Dictionary<string, TvSeries> resolvedShows = [];
        HashSet<string> seen = [];
        var offset = 0;
        while (true)
        {
            var json = await api.PlexAsync(settings, $"status/sessions/history/all?accountID=1&sort=viewedAt:desc&X-Plex-Container-Start={offset}&X-Plex-Container-Size=500", ct);
            var container = PlexService.Container(json);
            var rows = container.Items("Metadata").ToArray();
            if (!int.TryParse(container.Text("totalSize"), out var total) || total < 0 || total > 100_000
                || container.Number("offset") != offset || (rows.Length == 0 && offset < total))
                throw new UpstreamException("Plex returned incomplete TV history. The previous TV history has been retained.");
            // Plex history omits parent fields on many servers; resolve episode metadata before resolving its parent show.
            var missing = rows.Where(x => x.Text("accountID") == "1" && x.Text("type") == "episode")
                .Where(x => ShowKey(x).Length == 0).Select(x => x.Text("ratingKey"))
                .Where(x => long.TryParse(x, out var key) && key > 0 && !episodes.ContainsKey(x)).Distinct().ToArray();
            foreach (var batch in missing.Chunk(100))
            {
                try
                {
                    var details = await api.PlexAsync(settings, "library/metadata/" + string.Join(',', batch) + "?includeGuids=1", ct);
                    foreach (var episode in PlexService.Container(details).Items("Metadata").Where(x => x.Text("type") == "episode"))
                        episodes[episode.Text("ratingKey")] = episode;
                }
                catch (UpstreamException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                { /* Deleted episodes remain visible and can still match an unambiguous Sonarr series title. */ }
            }
            foreach (var row in rows.Where(x => x.Text("accountID") == "1" && x.Text("type") == "episode"))
            {
                var key = row.Text("ratingKey");
                var date = (long)row.Number("viewedAt");
                var id = "plex-tv:" + row.Text("historyKey", key + ":" + date);
                if (!seen.Add(id)) continue;
                episodes.TryGetValue(key, out var episodeDetail);
                var showKey = ShowKey(row);
                if (showKey.Length == 0) showKey = ShowKey(episodeDetail);
                var title = row.Text("grandparentTitle", episodeDetail.Text("grandparentTitle", "Unknown series"));
                int? tmdb = null, tvdb = null;
                string? poster = null;
                int sonarrId = 0;
                if (long.TryParse(showKey, out var numericKey) && numericKey > 0)
                {
                    if (!shows.ContainsKey(showKey))
                    {
                        try
                        {
                            var details = await api.PlexAsync(settings, "library/metadata/" + showKey + "?includeGuids=1", ct);
                            shows[showKey] = PlexService.Container(details).Items("Metadata").FirstOrDefault(x => x.Text("type") == "show");
                        }
                        catch (UpstreamException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                        { shows[showKey] = default; /* Preserve episode history for removed shows. */ }
                    }
                    var show = shows[showKey];
                    tmdb = show.TmdbId();
                    tvdb = TvdbId(show);
                    if (show.Text("title").Length > 0) title = show.Text("title");
                    var matches = store.Tv.Library.Where(x => (tmdb.HasValue && x.TmdbId == tmdb)
                        || (tvdb.HasValue && x.TvdbId == tvdb)).ToArray();
                    if (matches.Length == 1) { tmdb ??= matches[0].TmdbId; tvdb ??= matches[0].TvdbId; poster = matches[0].Poster; sonarrId = matches[0].SonarrId; }
                    if (!resolvedShows.TryGetValue(showKey, out var resolved))
                    {
                        resolved = new TvSeries(tmdb, tvdb ?? 0, title, (int)show.Number("year"), show.Text("summary"), poster, SonarrId: sonarrId);
                        try { resolved = await metadata.ResolveAsync(resolved, ct); }
                        catch (UpstreamException) { /* A TMDB outage cannot discard readable Plex episode history. */ }
                        resolvedShows[showKey] = resolved;
                    }
                    tmdb = resolved.TmdbId; tvdb = resolved.TvdbId > 0 ? resolved.TvdbId : tvdb;
                    poster = resolved.Poster; sonarrId = resolved.SonarrId;
                }
                if (!tmdb.HasValue && !tvdb.HasValue)
                {
                    var matches = store.Tv.Library.Where(x => string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 1) { tmdb = matches[0].TmdbId; tvdb = matches[0].TvdbId; poster = matches[0].Poster; sonarrId = matches[0].SonarrId; }
                }
                plays.Add(new(id, key, showKey, title, row.Text("title", episodeDetail.Text("title")), Nonnegative(row, "parentIndex") ?? Nonnegative(episodeDetail, "parentIndex"),
                    Nonnegative(row, "index") ?? Nonnegative(episodeDetail, "index"), date, tmdb, tvdb, poster, sonarrId));
            }
            offset += rows.Length;
            if (offset >= total) break;
            if (offset >= 100_000) throw new UpstreamException("TV history exceeds 100,000 plays. Previous history retained.");
        }
        await metadata.SaveAsync(ct);
        return plays.OrderByDescending(x => x.WatchedAt).ToList();
    }

    /// <summary>Returns TV-only collection, episode history, or discovery pages with independent search and pagination.</summary>
    public async Task<TvPage> BrowseAsync(IQueryCollection query, CancellationToken ct)
    {
        var view = query["view"].ToString();
        var text = query["query"].ToString().Trim();
        var page = int.TryParse(query["page"], out var number) ? number : 1;
        var size = int.TryParse(query["pageSize"], out number) ? number : 25;
        if (page is < 1 or > 500 || size is not (10 or 25 or 50 or 100) || text.Length > 200)
            throw new ArgumentException("Choose a valid TV page and a search of up to 200 characters.");
        if (view == "tv-history")
        {
            var rows = store.Tv.History.Where(x => text.Length == 0 || x.SeriesTitle.Contains(text, StringComparison.OrdinalIgnoreCase)
                || x.EpisodeTitle.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (query["order"] == "asc") rows = rows.Reverse().ToArray();
            var pages = Math.Max(1, (int)Math.Ceiling(rows.Length / (double)size));
            return new([], rows.Skip((page - 1) * size).Take(size).ToList(), page, pages, rows.Length, page < pages);
        }
        List<TvSeries> series;
        int totalPages;
        int count;
        string? notice = null;
        if (view == "tv-library")
        {
            series = store.Tv.Library.Where(x => text.Length == 0 || x.Title.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(Annotate).ToList();
            series = Sort(series, query["sort"]!, query["order"]!);
            count = series.Count;
            totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)size));
            series = series.Skip((page - 1) * size).Take(size).ToList();
        }
        else
        {
            // Provider pagination stays explicit; movie filters and movie IDs never participate in TV feeds.
            var path = text.Length > 0 ? $"search/tv?query={Uri.EscapeDataString(text)}&include_adult=false&page={page}"
                : view switch
                {
                    "tv-trending" or "tv-for-you" => $"trending/tv/week?page={page}",
                    "tv-top-rated" => $"tv/top_rated?page={page}",
                    _ => throw new ArgumentException("Choose a valid TV view.")
                };
            var seed = store.Tv.History.Where(x => x.TmdbId.HasValue).Select(x => (Id: x.TmdbId!.Value, Title: x.SeriesTitle))
                .Concat(store.Tv.Library.Where(x => x.TmdbId.HasValue).Select(x => (Id: x.TmdbId!.Value, x.Title))).FirstOrDefault();
            if (view == "tv-for-you" && text.Length == 0 && seed.Id > 0) path = $"tv/{seed.Id}/recommendations?page={page}";
            var json = await api.TmdbAsync(store.Settings, path, ct);
            series = json.Items("results").Where(x => !x.Flag("adult")).Select(ParseTmdb).Select(Annotate).ToList();
            if (view == "tv-for-you" && text.Length == 0 && seed.Id > 0) series = series.Select(x => x with { Reason = "Because you watched " + seed.Title }).ToList();
            totalPages = Math.Max(1, Math.Min(500, (int)json.Number("total_pages")));
            count = (int)json.Number("total_results");
            notice = "TV discovery uses provider pages of up to 20 shows. Sorting applies to this page; movie filters do not apply.";
            series = Sort(series, query["sort"]!, query["order"]!);
        }
        return new(series, [], page, totalPages, count, page < totalPages, notice);
    }

    /// <summary>Loads details only from TMDB's TV namespace and annotates them only from the TV snapshot.</summary>
    public async Task<TvSeries> DetailAsync(int id, CancellationToken ct)
    {
        if (id <= 0) throw new ArgumentException("Choose a TV series.");
        var series = await metadata.ResolveAsync(new TvSeries(id, 0, "TV series", 0, "", null), ct);
        await metadata.SaveAsync(ct);
        return Annotate(series);
    }

    /// <summary>Opens managed series by TVDB ID even when optional TMDB mapping or artwork is unavailable.</summary>
    public async Task<TvSeries> ManagedDetailAsync(int tvdbId, CancellationToken ct)
    {
        var show = store.Tv.Library.FirstOrDefault(x => x.TvdbId == tvdbId) ?? throw new ArgumentException("This series is not in the current Sonarr collection.");
        try { show = await metadata.ResolveAsync(show, ct); await metadata.SaveAsync(ct); }
        catch (UpstreamException) { /* Keep useful Sonarr details and source artwork during TMDB outages. */ }
        return Annotate(show);
    }

    /// <summary>Adds a verified TVDB series to an explicitly selected Sonarr destination, with download search opt-in.</summary>
    public async Task AddAsync(int tmdbId, bool searchNow, CancellationToken ct)
    {
        await store.Mutation.WaitAsync(ct);
        try
        {
            var settings = store.Settings;
            var show = await DetailAsync(tmdbId, ct);
            if (show.TvdbId <= 0) throw new ArgumentException("This TV series has no TVDB identifier for Sonarr.");
            var existing = await api.SonarrAsync(settings, "series", ct);
            if (existing.Items().Any(x => x.Number("tvdbId") == show.TvdbId)) throw new ArgumentException("This series is already in Sonarr.");
            var profiles = await api.SonarrAsync(settings, "qualityprofile", ct);
            var roots = await api.SonarrAsync(settings, "rootfolder", ct);
            if (!profiles.Items().Any(x => x.Number("id") == settings.SonarrQualityProfileId)
                || !roots.Items().Any(x => x.Text("path") == settings.SonarrRootFolder))
                throw new ArgumentException("Select a current Sonarr quality profile and root folder in Connections first.");
            var lookup = await api.SonarrAsync(settings, "series/lookup?term=tvdb:" + show.TvdbId, ct);
            var match = lookup.Items().FirstOrDefault(x => x.Number("tvdbId") == show.TvdbId);
            if (match.ValueKind != JsonValueKind.Object) throw new UpstreamException("Sonarr could not resolve this TV series.");
            var body = JsonNode.Parse(match.GetRawText())!.AsObject();
            body.Remove("id");
            body["qualityProfileId"] = settings.SonarrQualityProfileId;
            body["rootFolderPath"] = settings.SonarrRootFolder;
            body["monitored"] = true;
            body["seasonFolder"] = true;
            body["seriesType"] = match.Text("seriesType") is "anime" or "daily" ? match.Text("seriesType") : "standard";
            body["addOptions"] = new JsonObject { ["monitor"] = "all", ["searchForMissingEpisodes"] = searchNow, ["searchForCutoffUnmetEpisodes"] = false };
            var added = await api.SonarrAsync(settings, "series", ct, body);
            if (added.Number("tvdbId") != show.TvdbId) throw new UpstreamException("Sonarr did not acknowledge the requested series.");
            await store.SaveTvAsync(store.Tv with { Library = [.. store.Tv.Library, ParseSonarr(added) with { TmdbId = tmdbId, Poster = show.Poster, Score = show.Score, Facts = show.Facts, MetadataUpdatedAt = show.MetadataUpdatedAt }] }, ct);
        }
        finally { store.Mutation.Release(); }
    }

    /// <summary>Marks a series as partly watched without treating one watched episode as a completed series.</summary>
    private TvSeries Annotate(TvSeries value)
    {
        var managed = store.Tv.Library.FirstOrDefault(x => (value.TmdbId.HasValue && x.TmdbId == value.TmdbId)
            || (value.TvdbId > 0 && x.TvdbId == value.TvdbId));
        value = metadata.Cached(value);
        return value with { SonarrId = managed?.SonarrId ?? value.SonarrId, InSonarr = managed is not null || value.InSonarr,
            EpisodeFileCount = managed?.EpisodeFileCount ?? value.EpisodeFileCount, EpisodeCount = managed?.EpisodeCount ?? value.EpisodeCount,
            HasWatchedEpisodes = store.Tv.History.Any(x => (value.TmdbId.HasValue && x.TmdbId == value.TmdbId)
                || (value.TvdbId > 0 && x.TvdbId == value.TvdbId)) };
    }

    /// <summary>Reads Sonarr series statistics and safe TMDB artwork without borrowing movie metadata.</summary>
    private static TvSeries ParseSonarr(JsonElement json)
    {
        var image = json.Items("images").FirstOrDefault(x => x.Text("coverType") == "poster").Text("remoteUrl");
        var poster = Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Host == "image.tmdb.org"
            ? JsonFields.PosterPath("/" + uri.Segments.Last()) : null;
        var stats = json.TryGetProperty("statistics", out var statistics) ? statistics : default;
        return new(Positive(json, "tmdbId"), (int)json.Number("tvdbId"), json.Text("title"), (int)json.Number("year"),
            json.Text("overview"), poster, InSonarr: true, EpisodeFileCount: (int)stats.Number("episodeFileCount"), EpisodeCount: (int)stats.Number("episodeCount"), SonarrId: (int)json.Number("id"));
    }

    /// <summary>Projects TMDB show fields; TV names and first-air dates are distinct from movie fields.</summary>
    private static TvSeries ParseTmdb(JsonElement json) => new(Positive(json, "id"), 0, json.Text("name"),
        int.TryParse(json.Text("first_air_date").Split('-')[0], out var year) ? year : 0, json.Text("overview"),
        JsonFields.PosterPath(json.Text("poster_path")), json.Number("vote_count") > 0 ? json.Number("vote_average") : null);

    /// <summary>Sorts the selected TV data without consulting movie ratings or movie watches.</summary>
    private static List<TvSeries> Sort(List<TvSeries> values, string sort, string order)
    {
        IEnumerable<TvSeries> sorted = sort switch
        { "title" => values.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase), "year" => values.OrderBy(x => x.Year), "score" => values.OrderBy(x => x.Score), _ => values };
        return ((sort is "title" or "year" or "score") && order == "desc" ? sorted.Reverse() : sorted).ToList();
    }

    /// <summary>Accepts Plex's numeric parent field or its equivalent grandparent metadata path without treating an episode GUID as a show ID.</summary>
    private static string ShowKey(JsonElement row)
    {
        var key = row.Text("grandparentRatingKey");
        if (long.TryParse(key, out var id) && id > 0) return key;
        var path = row.Text("grandparentKey");
        const string prefix = "/library/metadata/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return "";
        key = path[prefix.Length..].Split('/', '?')[0];
        return long.TryParse(key, out id) && id > 0 ? key : "";
    }

    /// <summary>Reads positive identifiers while preserving the difference between zero and a missing identifier.</summary>
    private static int? Positive(JsonElement json, string field) => json.Number(field) is > 0 and <= int.MaxValue ? (int)json.Number(field) : null;

    /// <summary>Preserves unknown episode numbers instead of inventing season zero or episode zero.</summary>
    private static int? Nonnegative(JsonElement json, string field) => int.TryParse(json.Text(field), out var number) && number >= 0 ? number : null;

    /// <summary>Extracts a TVDB show identifier only from a show's external GUID list.</summary>
    private static int? TvdbId(JsonElement show)
    {
        foreach (var guid in show.Items("Guid").Select(x => x.Text("id")).Append(show.Text("guid")))
        {
            var prefix = guid.StartsWith("tvdb://", StringComparison.OrdinalIgnoreCase) ? "tvdb://"
                : guid.StartsWith("com.plexapp.agents.thetvdb://", StringComparison.OrdinalIgnoreCase) ? "com.plexapp.agents.thetvdb://" : "";
            if (prefix.Length > 0 && int.TryParse(guid[prefix.Length..].Split('?', '/')[0], out var id) && id > 0) return id;
        }
        return null;
    }
}
