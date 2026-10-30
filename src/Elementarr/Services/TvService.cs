using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;

namespace Elementarr.Services;

public sealed class TvService(StateStore store, MediaApi api, PlexService plex, TvMetadataService metadata)
{
    private readonly ConcurrentDictionary<string, TvFeed> feeds = new();
    private sealed class TvFeed
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<TvSeries> Series = [];
        public int NextPage = 1;
        public int SourcePages = 500;
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
    }

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
        if (source is ("all" or "history" or "tv-history") && settings.PlexToken.Length > 0 && settings.PlexServerId.Length > 0)
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
            // Episode details provide source ratings, synopsis and parent identifiers missing from history rows.
            var missing = rows.Where(x => x.Text("accountID") == "1" && x.Text("type") == "episode")
                .Select(x => x.Text("ratingKey"))
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
                        var plexFacts = MetadataService.ParsePlex(show);
                        resolved = new TvSeries(tmdb, tvdb ?? 0, title, (int)show.Number("year"), show.Text("summary"), poster, SonarrId: sonarrId,
                            Facts: new([], "", 0, 0, 0, "", null, []) { Imdb = plexFacts.Imdb, RottenTomatoes = plexFacts.RottenTomatoes, RatingOrigin = "Plex / TMDB" });
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
                    Nonnegative(row, "index") ?? Nonnegative(episodeDetail, "index"), date, tmdb, tvdb, poster, sonarrId,
                    new(row.Text("title", episodeDetail.Text("title")), row.Text("summary", episodeDetail.Text("summary")),
                        row.Text("originallyAvailableAt", episodeDetail.Text("originallyAvailableAt")),
                        MetadataService.ParsePlex(episodeDetail.ValueKind == JsonValueKind.Object ? episodeDetail : row))));
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
        var filter = BrowseOptions.FromQuery(query);
        (filter with { View = "library" }).Validate();
        var text = query["query"].ToString().Trim();
        var page = int.TryParse(query["page"], out var number) ? number : 1;
        var size = int.TryParse(query["pageSize"], out number) ? number : 25;
        if (page is < 1 or > 500 || size is not (10 or 25 or 50 or 100) || text.Length > 200)
            throw new ArgumentException("Choose a valid TV page and a search of up to 200 characters.");
        if (view == "tv-upcoming") return await UpcomingAsync(text, page, size, filter, ct);
        if (view is "tv-history" or "tv-issues")
        {
            Dictionary<(int?, int?, string, string), TvSeries> shows = [];
            // Resolve each history show once; long histories can contain thousands of rewatches.
            TvSeries Show(TvWatch watch)
            {
                var key = (watch.TmdbId, watch.TvdbId, watch.SeriesKey, watch.SeriesTitle);
                if (!shows.TryGetValue(key, out var show)) shows[key] = show = Annotate(store.Tv.Library.FirstOrDefault(s => SameShow(s, watch))
                    ?? new TvSeries(watch.TmdbId, watch.TvdbId ?? 0, watch.SeriesTitle, 0, "", watch.Poster));
                return show;
            }
            var rows = store.Tv.History.Where(x => view != "tv-issues" || (x.TmdbId is null && x.TvdbId is null))
                .Where(x => text.Length == 0 || x.SeriesTitle.Contains(text, StringComparison.OrdinalIgnoreCase)
                || x.EpisodeTitle.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Where(x => WatchDateMatches(x, filter))
                .Where(x => Matches(Show(x), filter with { Query = "", Watched = "any", WatchedFrom = "", WatchedTo = "" }))
                .Where(_ => filter.Watched != "no").ToArray();
            if (filter.Sort is "default" or "watched") rows = (filter.Order == "asc" ? rows.OrderBy(x => x.WatchedAt) : rows.OrderByDescending(x => x.WatchedAt)).ToArray();
            else rows = (filter.Order == "asc" ? rows.OrderBy(x => SortKey(Show(x), filter.Sort) is null).ThenBy(x => SortKey(Show(x), filter.Sort))
                : rows.OrderBy(x => SortKey(Show(x), filter.Sort) is null).ThenByDescending(x => SortKey(Show(x), filter.Sort))).ToArray();
            var pages = Math.Max(1, (int)Math.Ceiling(rows.Length / (double)size));
            List<TvWatch> enriched = [];
            foreach (var batch in rows.Skip((page - 1) * size).Take(size).Chunk(4))
                enriched.AddRange(await Task.WhenAll(batch.Select(x => EnrichWatchAsync(x, ct))));
            await metadata.SaveAsync(ct);
            return new([], enriched, page, pages, rows.Length, page < pages);
        }
        List<TvSeries> series;
        int totalPages;
        int count;
        string? notice = null;
        if (view == "tv-library")
        {
            series = store.Tv.Library.Where(x => text.Length == 0 || x.Title.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(Annotate).Where(x => Matches(x, filter)).ToList();
            series = Sort(series, filter.Sort, filter.Order);
            count = series.Count;
            totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)size));
            series = series.Skip((page - 1) * size).Take(size).ToList();
        }
        else
        {
            return await DiscoverAsync(filter, ct);
        }
        return new(series, [], page, totalPages, count, page < totalPages, notice);
    }

    /// <summary>Accumulates bounded TV provider pages so filters and pagination apply to matching shows across pages.</summary>
    private async Task<TvPage> DiscoverAsync(BrowseOptions filter, CancellationToken ct)
    {
        if (filter.View is not ("tv-for-you" or "tv-trending" or "tv-top-rated")) throw new ArgumentException("Choose a valid TV view.");
        foreach (var old in feeds.Where(x => x.Value.Created < DateTimeOffset.UtcNow.AddMinutes(-20)).Select(x => x.Key)) feeds.TryRemove(old, out _);
        var key = JsonSerializer.Serialize(filter with { Page = 1, PageSize = 25 }) + store.Tv.LibrarySyncedAt + store.Tv.HistorySyncedAt + store.Settings.Region;
        if (!feeds.ContainsKey(key) && feeds.Count >= 80) throw new ArgumentException("Too many active TV filter combinations. Try again in a few minutes.");
        var feed = feeds.GetOrAdd(key, _ => new());
        await feed.Gate.WaitAsync(ct);
        try
        {
            var matches = feed.Series.Select(Annotate).Where(x => Matches(x, filter)).ToList();
            var seed = store.Tv.History.Where(x => x.TmdbId.HasValue).Select(x => (Id: x.TmdbId!.Value, Title: x.SeriesTitle))
                .Concat(store.Tv.Library.Where(x => x.TmdbId.HasValue).Select(x => (Id: x.TmdbId!.Value, x.Title))).FirstOrDefault();
            for (var scan = 0; matches.Count < filter.Page * filter.PageSize + 1 && feed.NextPage <= feed.SourcePages && scan < 5; scan++)
            {
                var providerPage = feed.NextPage;
                var route = filter.Query.Length > 0 ? $"search/tv?query={Uri.EscapeDataString(filter.Query)}&include_adult=false&page={providerPage}"
                    : filter.View == "tv-for-you" && seed.Id > 0 ? $"tv/{seed.Id}/recommendations?page={providerPage}"
                    : filter.View == "tv-top-rated" ? $"tv/top_rated?page={providerPage}" : $"trending/tv/week?page={providerPage}";
                var json = await api.TmdbAsync(store.Settings, route, ct);
                var known = feed.Series.Select(x => x.TmdbId).ToHashSet();
                var fresh = json.Items("results").Where(x => !x.Flag("adult") && x.Number("id") > 0).Select(ParseTmdb).Where(x => known.Add(x.TmdbId)).ToList();
                fresh = await metadata.ResolveManyAsync(fresh, ct);
                if (filter.View == "tv-for-you" && filter.Query.Length == 0 && seed.Id > 0)
                    fresh = fresh.Select(x => x with { Reason = "Because you watched " + seed.Title }).ToList();
                feed.Series.AddRange(fresh);
                feed.SourcePages = Math.Clamp((int)json.Number("total_pages"), 1, 500);
                feed.NextPage++;
                matches = feed.Series.Select(Annotate).Where(x => Matches(x, filter)).ToList();
            }
            matches = Sort(matches, filter.Sort, filter.Order);
            var more = feed.NextPage <= feed.SourcePages;
            return new(matches.Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList(), [], filter.Page,
                Math.Max(1, (int)Math.Ceiling(matches.Count / (double)filter.PageSize)), matches.Count,
                more || matches.Count > filter.Page * filter.PageSize,
                more ? "Searching TV provider pages for matching shows. Continue searching to check more results." : null,
                more && matches.Count < filter.Page * filter.PageSize);
        }
        finally { feed.Gate.Release(); }
    }

    /// <summary>Adapts only TV facts to the shared filter rules; movie snapshots and matching never participate.</summary>
    private bool Matches(TvSeries show, BrowseOptions filter)
    {
        var facts = show.Facts;
        var movie = new Movie(show.TmdbId ?? 0, show.Title, show.Year, show.Overview, show.Poster, facts?.FirstAirDate, show.Score ?? 0,
            InRadarr: show.InSonarr, Available: show.EpisodeFileCount > 0, Watched: show.HasWatchedEpisodes,
            Facts: new MovieFacts { Genres = facts?.Genres ?? [], Runtime = facts?.Runtime ?? 0, Language = facts?.Language ?? "",
                Country = facts?.Country ?? "", Certification = facts?.Certification ?? "", Imdb = facts?.Imdb,
                RottenTomatoes = facts?.RottenTomatoes, Tmdb = facts?.Tmdb ?? show.Score, Votes = facts?.Votes ?? 0 });
        if (!BrowseService.Matches(movie, filter with { Query = "", Country = "" })) return false;
        if (filter.Country.Length > 0 && !string.Equals(facts?.Country, filter.Country, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.Completion == "completed" && show.CompletionPercent != 100
            || filter.Completion == "partial" && !(show.HasWatchedEpisodes && show.CompletionPercent != 100)) return false;
        if (filter.Matched != "any" && (filter.Matched == "yes") != show.TmdbId.HasValue) return false;
        return (filter.WatchedFrom.Length == 0 && filter.WatchedTo.Length == 0)
            || store.Tv.History.Any(x => SameShow(show, x) && WatchDateMatches(x, filter));
    }

    /// <summary>Compares explicit show identifiers without matching movie IDs or ambiguous titles.</summary>
    private static bool SameShow(TvSeries show, TvWatch watch) => show.TmdbId.HasValue && show.TmdbId == watch.TmdbId
        || show.TvdbId > 0 && show.TvdbId == watch.TvdbId;

    /// <summary>Applies calendar watch dates using the browser's timezone offset.</summary>
    private static bool WatchDateMatches(TvWatch watch, BrowseOptions filter)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(watch.WatchedAt).AddMinutes(filter.UtcOffset).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return (filter.WatchedFrom.Length == 0 || string.CompareOrdinal(date, filter.WatchedFrom) >= 0)
            && (filter.WatchedTo.Length == 0 || string.CompareOrdinal(date, filter.WatchedTo) <= 0);
    }

    /// <summary>Loads season episodes and overlays current distinct watch history, with a Sonarr fallback for managed shows.</summary>
    public async Task<List<TvSeasonEpisode>> SeasonAsync(int id, int season, bool managed, CancellationToken ct)
    {
        if (id <= 0 || season is < 0 or > 10000) throw new ArgumentException("Choose a valid TV season.");
        var show = managed ? await ManagedDetailAsync(id, ct) : await DetailAsync(id, ct);
        List<TvSeasonEpisode> episodes = [];
        try { if (show.TmdbId is int tmdb) episodes = await metadata.SeasonAsync(tmdb, season, ct); }
        catch (UpstreamException) when (show.SonarrId > 0) { /* Sonarr can still describe the managed season. */ }
        if (episodes.Count == 0 && show.SonarrId > 0)
        {
            var json = await api.SonarrAsync(store.Settings, $"episode?seriesId={show.SonarrId}", ct);
            episodes = json.Items().Where(x => x.Number("seriesId") == show.SonarrId && x.Number("seasonNumber") == season)
                .Select(x => new TvSeasonEpisode(season, (int)x.Number("episodeNumber"), x.Text("title"), x.Text("airDate"), x.Text("overview")))
                .DistinctBy(x => x.Episode).OrderBy(x => x.Episode).ToList();
        }
        var watched = store.Tv.History.Where(x => SameShow(show, x) && x.Season == season && x.Episode.HasValue)
            .GroupBy(x => x.Episode!.Value).ToDictionary(x => x.Key, x => x.Max(w => w.WatchedAt));
        return episodes.Select(x => watched.TryGetValue(x.Episode, out var stamp) ? x with { Watched = true, WatchedAt = stamp } : x).ToList();
    }

    /// <summary>Lists actual upcoming Sonarr episodes over fourteen days, preserving air times and keeping movie discovery separate.</summary>
    private async Task<TvPage> UpcomingAsync(string text, int page, int size, BrowseOptions filter, CancellationToken ct)
    {
        const string notice = "Episodes airing in the next 14 days from your Sonarr collection. Air times use your browser's local time.";
        if (store.Settings.SonarrKey.Length == 0) return new([], [], 1, 1, 0, false, "Connect Sonarr in Connections to see episodes airing soon in your collection.");
        var now = DateTimeOffset.UtcNow;
        var end = now.AddDays(14);
        var response = await api.SonarrAsync(store.Settings, $"calendar?start={Uri.EscapeDataString(now.ToString("O"))}&end={Uri.EscapeDataString(end.ToString("O"))}&includeSeries=true", ct);
        if (response.ValueKind != JsonValueKind.Array) throw new UpstreamException("Sonarr returned an invalid episode calendar.");
        List<TvSeries> episodes = [];
        foreach (var episode in response.Items())
        {
            if (!DateTimeOffset.TryParse(episode.Text("airDateUtc"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var air) || air < now || air > end) continue;
            var seriesId = (int)episode.Number("seriesId");
            var show = store.Tv.Library.FirstOrDefault(x => x.SonarrId == seriesId);
            if (show is null && episode.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Object) show = ParseSonarr(series);
            if (show is null || (text.Length > 0 && !show.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !episode.Text("title").Contains(text, StringComparison.OrdinalIgnoreCase))) continue;
            show = Annotate(show);
            if (Matches(show, filter)) episodes.Add(show with { AiringEpisode = new((int)episode.Number("seasonNumber"), (int)episode.Number("episodeNumber"), episode.Text("title"), air) });
        }
        episodes = episodes.OrderBy(x => x.AiringEpisode!.AirsAt).ToList();
        var pages = Math.Max(1, (int)Math.Ceiling(episodes.Count / (double)size));
        return new(episodes.Skip((page - 1) * size).Take(size).ToList(), [], page, pages, episodes.Count, page < pages, notice);
    }

    /// <summary>Loads details only from TMDB's TV namespace and annotates them only from the TV snapshot.</summary>
    public async Task<TvSeries> DetailAsync(int id, CancellationToken ct)
    {
        if (id <= 0) throw new ArgumentException("Choose a TV series.");
        var series = await metadata.ResolveAsync(new TvSeries(id, 0, "TV series", 0, "", null), ct);
        await metadata.SaveAsync(ct);
        return Annotate(series);
    }

    /// <summary>Enriches only the displayed history page while retaining Plex details during optional metadata outages.</summary>
    private async Task<TvWatch> EnrichWatchAsync(TvWatch watch, CancellationToken ct)
    {
        var series = Annotate(new TvSeries(watch.TmdbId, watch.TvdbId ?? 0, watch.SeriesTitle, 0, "", watch.Poster, SonarrId: watch.SonarrId));
        try { series = Annotate(await metadata.ResolveAsync(series, ct)); }
        catch (UpstreamException) { /* Preserve source metadata and local artwork when TMDB is unavailable. */ }
        var details = watch.Details;
        if (series.TmdbId is int tmdb && watch.Season is int season && watch.Episode is int episode)
        {
            try
            {
                var resolved = await metadata.EpisodeAsync(tmdb, season, episode, ct);
                if (resolved is not null) details = resolved with { Facts = resolved.Facts with {
                    Imdb = details?.Facts.Imdb, RottenTomatoes = details?.Facts.RottenTomatoes,
                    RatingOrigin = details?.Facts.Imdb.HasValue == true || details?.Facts.RottenTomatoes.HasValue == true ? "Plex / TMDB" : "TMDB" } };
            }
            catch (UpstreamException) { /* Episode metadata failure cannot hide a recorded watch. */ }
        }
        return watch with { TmdbId = series.TmdbId, Poster = series.Poster, Details = details, Series = series };
    }

    /// <summary>Returns episode metadata in the TV namespace and saves it for subsequent drawer and timeline views.</summary>
    public async Task<TvEpisode?> EpisodeDetailAsync(int id, int season, int episode, CancellationToken ct)
    {
        var result = await metadata.EpisodeAsync(id, season, episode, ct);
        await metadata.SaveAsync(ct);
        return result;
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
        value = metadata.Cached(value with { Facts = managed?.Facts ?? value.Facts });
        var watched = store.Tv.History.Where(x => SameShow(value, x)).ToArray();
        var count = watched.Where(x => x.Season > 0 && x.Episode > 0).DistinctBy(x => (x.Season, x.Episode)).Count();
        var total = value.Facts?.Episodes > 0 ? value.Facts.Episodes : managed?.EpisodeCount ?? value.EpisodeCount;
        var seasons = value.Facts?.SeasonList.Count > 0 ? value.Facts.SeasonList : managed?.Facts?.SeasonList ?? [];
        return value with { SonarrId = managed?.SonarrId ?? value.SonarrId, InSonarr = managed is not null || value.InSonarr,
            EpisodeFileCount = managed?.EpisodeFileCount ?? value.EpisodeFileCount, EpisodeCount = managed?.EpisodeCount ?? value.EpisodeCount,
            Facts = value.Facts is null ? null : value.Facts with { SeasonList = seasons },
            HasWatchedEpisodes = watched.Length > 0, WatchedEpisodeCount = count,
            CompletionPercent = total > 0 ? count >= total ? 100 : Math.Min(99, (int)Math.Round(count * 100d / total, MidpointRounding.AwayFromZero)) : null };
    }

    /// <summary>Reads Sonarr series statistics and safe TMDB artwork without borrowing movie metadata.</summary>
    private static TvSeries ParseSonarr(JsonElement json)
    {
        var image = json.Items("images").FirstOrDefault(x => x.Text("coverType") == "poster").Text("remoteUrl");
        var poster = Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Host == "image.tmdb.org"
            ? JsonFields.PosterPath("/" + uri.Segments.Last()) : null;
        var stats = json.TryGetProperty("statistics", out var statistics) ? statistics : default;
        var ratings = json.TryGetProperty("ratings", out var scores) ? scores : default;
        var imdb = ratings.ValueKind == JsonValueKind.Object && ratings.TryGetProperty("imdb", out var score) ? score.Number("value") : (double?)null;
        var language = json.TryGetProperty("originalLanguage", out var originalLanguage) && originalLanguage.ValueKind == JsonValueKind.Object
            ? originalLanguage.Text("name") : json.Text("originalLanguage");
        return new(Positive(json, "tmdbId"), (int)json.Number("tvdbId"), json.Text("title"), (int)json.Number("year"),
            json.Text("overview"), poster, InSonarr: true, EpisodeFileCount: (int)stats.Number("episodeFileCount"), EpisodeCount: (int)stats.Number("episodeCount"), SonarrId: (int)json.Number("id"),
            Facts: new(json.Items("genres").Select(x => x.GetString() ?? "").ToList(), json.Text("status"), 0, 0, (int)json.Number("runtime"), language, null, []) {
                Imdb = imdb is >= 0 and <= 10 ? imdb : null, ImdbId = json.Text("imdbId"), RatingOrigin = "Sonarr",
                FirstAirDate = json.Text("firstAired").Split('T')[0],
                SeasonList = json.Items("seasons").Select(x => new TvSeason((int)x.Number("seasonNumber"),
                    x.Number("seasonNumber") == 0 ? "Specials" : "Season " + (int)x.Number("seasonNumber"),
                    x.TryGetProperty("statistics", out var s) ? (int)s.Number("totalEpisodeCount") : 0)).ToList() });
    }

    /// <summary>Projects TMDB show fields; TV names and first-air dates are distinct from movie fields.</summary>
    private static TvSeries ParseTmdb(JsonElement json) => new(Positive(json, "id"), 0, json.Text("name"),
        int.TryParse(json.Text("first_air_date").Split('-')[0], out var year) ? year : 0, json.Text("overview"),
        JsonFields.PosterPath(json.Text("poster_path")), json.Number("vote_count") > 0 ? json.Number("vote_average") : null);

    /// <summary>Sorts the selected TV data without consulting movie ratings or movie watches.</summary>
    private static List<TvSeries> Sort(List<TvSeries> values, string sort, string order)
    {
        if (sort == "default") return values;
        return (order == "asc" ? values.OrderBy(x => SortKey(x, sort) is null).ThenBy(x => SortKey(x, sort))
            : values.OrderBy(x => SortKey(x, sort) is null).ThenByDescending(x => SortKey(x, sort))).ToList();
    }

    /// <summary>Selects comparable TV fields while leaving missing ratings at the end of either sort direction.</summary>
    private static IComparable? SortKey(TvSeries show, string sort) => sort switch { "title" => show.Title.ToUpperInvariant(), "year" => show.Year,
        "tmdb" or "score" => show.Score, "imdb" => show.Facts?.Imdb, "rt" => show.Facts?.RottenTomatoes,
        "runtime" => show.Facts?.Runtime > 0 ? show.Facts.Runtime : null, "votes" => show.Facts?.Votes, "popularity" => show.Facts?.Popularity, _ => null };

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
