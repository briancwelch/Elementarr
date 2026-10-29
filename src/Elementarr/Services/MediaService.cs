using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Elementarr.Services;

public sealed class MediaService(StateStore store, MediaApi api, PlexService plex, TvService tv)
{
    private SemaphoreSlim Mutation => store.Mutation;

    /// <summary>Validates connection changes and clears data when its server or user identity changes.</summary>
    public async Task SaveAsync(ConnectionUpdate update, CancellationToken ct)
    {
        await Mutation.WaitAsync(ct);
        try
        {
            var previous = store.Settings;
            var next = previous with
            {
                RadarrUrl = BaseUrl(string.IsNullOrWhiteSpace(update.RadarrUrl) ? previous.RadarrUrl : update.RadarrUrl),
                TautulliUrl = BaseUrl(string.IsNullOrWhiteSpace(update.TautulliUrl) ? previous.TautulliUrl : update.TautulliUrl),
                SonarrUrl = BaseUrl(string.IsNullOrWhiteSpace(update.SonarrUrl) ? previous.SonarrUrl : update.SonarrUrl),
                SonarrKey = Secret(update.SonarrKey, previous.SonarrKey),
                SonarrQualityProfileId = Math.Max(0, update.SonarrQualityProfileId), SonarrRootFolder = update.SonarrRootFolder.Trim(),
                RadarrKey = Secret(update.RadarrKey, previous.RadarrKey),
                TautulliKey = Secret(update.TautulliKey, previous.TautulliKey),
                TmdbToken = Secret(update.TmdbToken, previous.TmdbToken),
                PlexUserId = update.PlexUserId.Trim(), PlexUsername = update.PlexUsername.Trim(),
                QualityProfileId = Math.Max(0, update.QualityProfileId), RootFolder = update.RootFolder.Trim(),
                Region = update.Region.Trim().ToUpperInvariant(),
                PlexUrl = BaseUrl(string.IsNullOrWhiteSpace(update.PlexUrl) ? previous.PlexUrl : update.PlexUrl),
                TautulliEnabled = update.TautulliEnabled
            };
            if (next.PlexUserId.Length > 0 && (!long.TryParse(next.PlexUserId, out var userId) || userId <= 0))
                throw new ArgumentException("Select a valid Plex user.");
            if (next.Region.Length != 2 || !next.Region.All(char.IsAsciiLetter))
                throw new ArgumentException("Use a two-letter release region, such as US.");
            if (next.PlexUsername.Length > 100 || next.RootFolder.Length > 1024 || next.SonarrRootFolder.Length > 1024)
                throw new ArgumentException("The connection settings are too long.");
            var snapshot = store.Snapshot;
            if (previous.RadarrUrl != next.RadarrUrl)
                snapshot = snapshot with { Library = [], LibrarySyncedAt = null, LibraryError = null };
            if (previous.PlexUrl != next.PlexUrl || (snapshot.HistorySource != "plex"
                && (previous.TautulliUrl != next.TautulliUrl || previous.PlexUserId != next.PlexUserId
                    || previous.PlexUsername != next.PlexUsername)))
                snapshot = snapshot with { History = [], UserId = "", Warnings = [], HistorySyncedAt = null, HistoryError = null, HistorySource = "" };
            if (snapshot != store.Snapshot)
                await store.SaveSnapshotAsync(snapshot with { SyncedAt = new[] { snapshot.LibrarySyncedAt, snapshot.HistorySyncedAt }.Max() }, ct);
            var television = store.Tv;
            if (previous.SonarrUrl != next.SonarrUrl || previous.SonarrKey != next.SonarrKey)
                television = television with { Library = [], LibrarySyncedAt = null, LibraryError = null };
            if (previous.PlexUrl != next.PlexUrl)
                television = television with { History = [], HistorySyncedAt = null, HistoryError = null };
            if (television != store.Tv) await store.SaveTvAsync(television, ct);
            await store.SaveSettingsAsync(next, ct);
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Checks integrations and loads actual Radarr destinations and Plex users without modifying upstream data.</summary>
    public async Task<ConnectionCheck> CheckAsync(CancellationToken ct)
    {
        var settings = store.Settings;
        List<Choice> profiles = [], folders = [], users = [];
        List<string> errors = [];
        bool radarr = false, tautulli = false, tmdb = false, plexConnected = false;
        if (settings.RadarrKey.Length > 0) try
        {
            var profileJson = await api.RadarrAsync(settings, "qualityprofile", ct);
            var folderJson = await api.RadarrAsync(settings, "rootfolder", ct);
            profiles = profileJson.Items().Select(x => new Choice(x.Text("id"), x.Text("name"))).ToList();
            folders = folderJson.Items().Select(x => new Choice(x.Text("path"), x.Text("path"))).ToList();
            radarr = true;
        }
        catch (UpstreamException exception) { errors.Add(exception.Message); }
        if (settings.TautulliEnabled && settings.TautulliKey.Length > 0) try
        {
            var json = await api.TautulliAsync(settings, "get_users", ct);
            users = json.Items().Where(x => x.Text("user_id").Length > 0)
                .Select(x => new Choice(x.Text("user_id"), x.Text("username", x.Text("friendly_name")))).ToList();
            tautulli = true;
        }
        catch (UpstreamException exception) { errors.Add(exception.Message); }
        if (settings.TmdbToken.Length > 0) try { await api.TmdbAsync(settings, "configuration", ct); tmdb = true; }
        catch (UpstreamException exception) { errors.Add(exception.Message); }
        if (settings.PlexToken.Length > 0) try { await plex.CheckAsync(settings, ct); plexConnected = true; }
        catch (UpstreamException exception) { errors.Add(exception.Message); }
        bool sonarr = false;
        List<Choice> sonarrProfiles = [], sonarrFolders = [];
        if (settings.SonarrKey.Length > 0) try
        {
            var quality = await api.SonarrAsync(settings, "qualityprofile", ct);
            var roots = await api.SonarrAsync(settings, "rootfolder", ct);
            sonarrProfiles = quality.Items().Select(x => new Choice(x.Text("id"), x.Text("name"))).ToList();
            sonarrFolders = roots.Items().Select(x => new Choice(x.Text("path"), x.Text("path"))).ToList();
            sonarr = true;
        }
        catch (UpstreamException exception) { errors.Add(exception.Message); }
        return new(radarr, tautulli, tmdb, users, profiles, folders, errors, plexConnected, sonarr, sonarrProfiles, sonarrFolders);
    }

    /// <summary>Stores a verified Plex authorization while preserving collection data and pinning the linked owner.</summary>
    public async Task LinkPlexAsync(PlexAuthorization authorization, CancellationToken ct)
    {
        await Mutation.WaitAsync(ct);
        try
        {
            var settings = store.Settings;
            if (!authorization.Connected || authorization.Token.Length == 0 || authorization.OwnerId.Length == 0)
                throw new ArgumentException("Plex authorization is incomplete.");
            if (settings.PlexOwnerId.Length > 0 && settings.PlexOwnerId != authorization.OwnerId)
                throw new UnauthorizedAccessException("This Plex account is not authorized for this installation.");
            var identityChanged = settings.PlexOwnerId != authorization.OwnerId || settings.PlexServerId != authorization.ServerId;
            await store.SaveSettingsAsync(settings with
            {
                PlexToken = authorization.Token, PlexOwnerId = authorization.OwnerId, PlexOwnerName = authorization.Username,
                PlexUrl = authorization.ServerUrl.Length > 0 ? authorization.ServerUrl : settings.PlexUrl,
                PlexServerId = authorization.ServerId.Length > 0 ? authorization.ServerId : settings.PlexServerId,
                PlexUsername = authorization.Username
            }, ct);
            if (identityChanged) await store.SaveTvAsync(store.Tv with { History = [], HistorySyncedAt = null, HistoryError = null }, ct);
            if (identityChanged || (authorization.Connect && store.Snapshot.HistorySource != "plex"))
                await store.SaveSnapshotAsync(store.Snapshot with { History = [], Warnings = [], UserId = "",
                    HistorySyncedAt = null, HistoryError = null, HistorySource = "", SyncedAt = store.Snapshot.LibrarySyncedAt }, ct);
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Selects only a Plex-advertised owned server and clears both histories when its identity changes.</summary>
    public async Task SelectPlexServerAsync(string id, CancellationToken ct)
    {
        await Mutation.WaitAsync(ct);
        try
        {
            var selected = await plex.ResolveServerAsync(store.Settings, id, ct);
            if (selected.ServerId != store.Settings.PlexServerId)
            {
                await store.SaveSnapshotAsync(store.Snapshot with { History = [], HistorySyncedAt = null, HistoryError = null, Warnings = [], HistorySource = "" }, ct);
                await store.SaveTvAsync(store.Tv with { History = [], HistorySyncedAt = null, HistoryError = null }, ct);
            }
            await store.SaveSettingsAsync(store.Settings with { PlexUrl = selected.ServerUrl, PlexServerId = selected.ServerId }, ct);
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Synchronizes independently configured sources and commits each success even when the other source fails.</summary>
    public async Task<SyncResult> SyncAsync(CancellationToken ct, string source = "all")
    {
        if (source is not ("all" or "radarr" or "history" or "sonarr" or "tv-history")) throw new ArgumentException("Choose a valid sync source.");
        await Mutation.WaitAsync(ct);
        try
        {
            var settings = store.Settings;
            var scanRadarr = source is ("all" or "radarr") && settings.RadarrKey.Length > 0;
            var scanHistory = source is ("all" or "history") && (settings.PlexToken.Length > 0
                || (settings.TautulliEnabled && settings.TautulliKey.Length > 0));
            if ((source == "radarr" && !scanRadarr) || (source == "history" && !scanHistory)
                || (source == "sonarr" && settings.SonarrKey.Length == 0)
                || (source == "tv-history" && (settings.PlexToken.Length == 0 || settings.PlexServerId.Length == 0))
                || (source == "all" && !scanRadarr && !scanHistory && settings.SonarrKey.Length == 0))
                throw new ArgumentException("Configure the selected source in Connections first. Plex history also requires an owned server selection.");
            bool radarrSynced = false, historySynced = false;
            List<string> errors = [];
            if (scanRadarr)
            {
                try
                {
                    var libraryJson = await api.RadarrAsync(settings, "movie", ct);
                    var library = libraryJson.Items().Select(x =>
                    {
                        var movie = MetadataService.ParseRadarr(x);
                        return new LibraryMovie(movie.TmdbId, x.Text("imdbId"), movie.Title, movie.Year,
                            movie.Overview, movie.Poster, x.Flag("hasFile"), x.Flag("monitored"), movie.Facts, movie.ReleaseDate);
                    }).ToList();
                    var now = DateTimeOffset.UtcNow;
                    await store.SaveSnapshotAsync(store.Snapshot with { Library = library, LibrarySyncedAt = now,
                        SyncedAt = now, LibraryError = null }, ct);
                    radarrSynced = true;
                }
                catch (UpstreamException exception)
                {
                    errors.Add("Radarr: " + exception.Message);
                    await store.SaveSnapshotAsync(store.Snapshot with { LibraryError = exception.Message }, ct);
                }
            }
            if (scanHistory)
            {
                try
                {
                    HistoryImport? import = null;
                    var historySource = "plex";
                    List<string> fallbackWarnings = [];
                    if (settings.PlexToken.Length > 0)
                    {
                        try { import = await plex.ReadHistoryAsync(settings, store.Snapshot, ct); }
                        catch (UpstreamException exception) when (settings.TautulliEnabled && settings.TautulliKey.Length > 0)
                        { fallbackWarnings.Add("Plex history is unavailable; the optional Tautulli source was used. " + exception.Message); }
                    }
                    if (import is null)
                    {
                        import = await ReadTautulliHistoryAsync(settings, ct);
                        historySource = "tautulli";
                        settings = store.Settings;
                    }
                    var now = DateTimeOffset.UtcNow;
                    var scope = store.MatchScope(historySource);
                    var history = import.History.Where(x => !store.HistoryExclusions.Contains(scope + x.RatingKey)).Select(x => store.HistoryMatches.TryGetValue(scope + x.RatingKey, out var match)
                        ? x with { TmdbId = match, Facts = null } : x).ToList();
                    var warnings = HistoryWarnings([.. fallbackWarnings, .. import.Warnings], history);
                    await store.SaveSnapshotAsync(store.Snapshot with { History = history,
                        UserId = historySource == "plex" ? settings.PlexOwnerId : settings.PlexUserId,
                        Warnings = warnings, HistorySource = historySource,
                        HistorySyncedAt = now, SyncedAt = now, HistoryError = null }, ct);
                    historySynced = true;
                }
                catch (UpstreamException exception)
                {
                    errors.Add("History: " + exception.Message);
                    await store.SaveSnapshotAsync(store.Snapshot with { HistoryError = exception.Message }, ct);
                }
            }
            var television = await tv.SyncLockedAsync(source, ct);
            errors.AddRange(television.Errors);
            return new(radarrSynced, historySynced, store.Snapshot.HistorySource, errors, television.Library, television.History);
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Reads optional Tautulli history, isolating individual server-side record failures with visible warnings.</summary>
    private async Task<HistoryImport> ReadTautulliHistoryAsync(Connections settings, CancellationToken ct)
    {
        if (settings.PlexUserId.Length == 0)
        {
            var users = await api.TautulliAsync(settings, "get_users", ct);
            var matching = users.Items().Where(x => string.Equals(x.Text("username"), settings.PlexUsername,
                StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length != 1 || matching[0].Text("user_id").Length == 0)
                throw new UpstreamException("Select your Tautulli user in Connections before synchronizing history.");
            settings = settings with { PlexUserId = matching[0].Text("user_id") };
            await store.SaveSettingsAsync(settings, ct);
        }
        if (settings.PlexOwnerId.Length > 0 && settings.PlexOwnerId != settings.PlexUserId)
            throw new UpstreamException("Select the linked Plex owner's Tautulli user before using the optional fallback.");
        var snapshot = store.Snapshot;
        var previousMatches = snapshot.History.Where(x => x.TmdbId.HasValue && x.RatingKey.Length > 0)
            .GroupBy(x => x.RatingKey).ToDictionary(x => x.Key, x => x.First().TmdbId);
        List<Watch> history = [];
        var seen = new HashSet<string>();
        var offset = 0;
        var pageSize = 1000;
        int? total = null;
        var unreadable = 0;
        var consecutiveFailures = 0;
        while (true)
        {
            JsonElement result;
            try
            {
                result = await api.TautulliAsync(settings, "get_history", ct,
                    ("user_id", settings.PlexUserId), ("media_type", "movie"), ("grouping", "0"),
                    ("include_activity", "0"), ("order_column", "date"), ("order_dir", "desc"),
                    ("start", offset.ToString()), ("length", pageSize.ToString()));
            }
            catch (UpstreamException exception) when (exception.StatusCode == HttpStatusCode.InternalServerError)
            {
                // Only HTTP 500 is narrowed. Repeated outages abort instead of silently discarding history.
                if (pageSize > 1) { pageSize = Math.Max(1, pageSize / 2); continue; }
                unreadable++;
                consecutiveFailures++;
                if (consecutiveFailures >= 3 || unreadable > 25)
                    throw new UpstreamException("Tautulli repeatedly failed to return individual history records. Existing history has been retained; inspect its server log.");
                offset++;
                if (total.HasValue && offset >= total.Value) break;
                continue;
            }
            consecutiveFailures = 0;
            if (!result.TryGetProperty("recordsFiltered", out _)) throw new UpstreamException("Tautulli returned an invalid history page.");
            total = (int)result.Number("recordsFiltered");
            var rows = result.Items("data").ToArray();
            if (rows.Length == 0 && offset < total)
                throw new UpstreamException("Tautulli returned incomplete history. Existing history has been retained.");
            foreach (var row in rows)
            {
                if (row.Text("media_type") != "movie" || row.Text("user_id") != settings.PlexUserId) continue;
                var id = row.Text("row_id", row.Text("id", row.Text("reference_id")));
                if (!seen.Add(id)) continue;
                var title = row.Text("title", row.Text("full_title"));
                var year = (int)row.Number("year");
                var key = row.Text("rating_key");
                var tmdbId = row.TmdbId();
                if (!tmdbId.HasValue && previousMatches.TryGetValue(key, out var remembered)) tmdbId = remembered;
                if (!tmdbId.HasValue)
                {
                    var matches = snapshot.Library.Where(x => x.Year == year && string.Equals(x.Title, title,
                        StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 1) tmdbId = matches[0].TmdbId;
                }
                if (!tmdbId.HasValue && key.Length > 0 && !previousMatches.ContainsKey(key))
                {
                    try { tmdbId = (await api.TautulliAsync(settings, "get_metadata", ct, ("rating_key", key))).TmdbId(); }
                    catch (UpstreamException) { /* Removed items can have no resolvable metadata. */ }
                }
                previousMatches[key] = tmdbId;
                var percent = Math.Clamp(row.Number("percent_complete"), 0, 100);
                history.Add(new(id, key, title, year, (long)row.Number("date"), percent,
                    tmdbId, percent >= 80 || row.Number("watched_status") >= 1));
            }
            offset += rows.Length;
            if (offset >= total) break;
            if (offset >= 100_000) throw new UpstreamException("History exceeds 100,000 plays. Existing history has been retained.");
            pageSize = 1000;
        }
        List<string> warnings = [];
        if (unreadable > 0) warnings.Add($"Tautulli could not return {unreadable} history plays. Other plays were imported, but watched filtering may be incomplete until those records can be read.");
        var unresolved = history.Count(x => x.Completed && !x.TmdbId.HasValue);
        if (unresolved > 0) warnings.Add($"{unresolved} completed Tautulli plays could not be matched to TMDB. They remain visible in history, but watched filtering may miss these movies.");
        return new(history.OrderByDescending(x => x.WatchedAt).ToList(), warnings);
    }

    /// <summary>Adds a movie once using verified Radarr destinations; searches only when explicitly requested.</summary>
    public async Task AddAsync(int tmdbId, bool searchNow, CancellationToken ct)
    {
        if (tmdbId <= 0) throw new ArgumentException("Choose a valid movie.");
        await Mutation.WaitAsync(ct);
        try
        {
            var settings = store.Settings;
            var existing = await api.RadarrAsync(settings, "movie", ct);
            if (existing.Items().Any(x => x.Number("tmdbId") == tmdbId))
                throw new ArgumentException("This movie is already in Radarr.");
            var profiles = await api.RadarrAsync(settings, "qualityprofile", ct);
            var folders = await api.RadarrAsync(settings, "rootfolder", ct);
            if (!profiles.Items().Any(x => x.Number("id") == settings.QualityProfileId)
                || !folders.Items().Any(x => x.Text("path") == settings.RootFolder))
                throw new ArgumentException("Select a current Radarr quality profile and root folder in Connections first.");
            var lookup = await api.RadarrAsync(settings, $"movie/lookup/tmdb?tmdbId={tmdbId}", ct);
            if (lookup.Number("tmdbId") != tmdbId) throw new UpstreamException("Radarr could not resolve this movie.");
            var body = JsonNode.Parse(lookup.GetRawText())!.AsObject();
            body.Remove("id");
            body["qualityProfileId"] = settings.QualityProfileId;
            body["rootFolderPath"] = settings.RootFolder;
            body["monitored"] = true;
            body["minimumAvailability"] = "released";
            body["addOptions"] = new JsonObject { ["searchForMovie"] = searchNow };
            var added = await api.RadarrAsync(settings, "movie", ct, body);
            var snapshot = store.Snapshot;
            var movie = new LibraryMovie(tmdbId, added.Text("imdbId"), added.Text("title", lookup.Text("title")),
                (int)added.Number("year"), added.Text("overview"), null, added.Flag("hasFile"), true);
            await store.SaveSnapshotAsync(snapshot with { Library = [.. snapshot.Library, movie] }, ct);
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Saves a verified explicit movie match for every play of that source item, including future synchronizations.</summary>
    public async Task<int> MatchHistoryAsync(HistoryMatchRequest request, CancellationToken ct)
    {
        if (request.RatingKey is null || request.RatingKey.Length is < 1 or > 300 || request.TmdbId <= 0)
            throw new ArgumentException("Choose a history item and a valid TMDB movie.");
        await Mutation.WaitAsync(ct);
        try
        {
            var snapshot = store.Snapshot;
            var plays = snapshot.History.Count(x => x.RatingKey == request.RatingKey);
            if (plays == 0) throw new ArgumentException("That history item is no longer in the current snapshot.");
            var movie = await api.TmdbAsync(store.Settings, $"movie/{request.TmdbId}", ct);
            if (movie.Number("id") != request.TmdbId) throw new ArgumentException("TMDB could not verify that movie.");
            var matches = new Dictionary<string, int>(store.HistoryMatches) { [store.MatchScope(snapshot.HistorySource) + request.RatingKey] = request.TmdbId };
            await store.SaveHistoryMatchesAsync(matches, ct);
            var history = snapshot.History.Select(x => x.RatingKey == request.RatingKey ? x with { TmdbId = request.TmdbId, Facts = null } : x).ToList();
            await store.SaveSnapshotAsync(snapshot with { History = history, Warnings = HistoryWarnings(snapshot.Warnings, history) }, ct);
            return plays;
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Removes every watch of one exact source item and excludes that item from both configured movie-history sources.</summary>
    public async Task<int> RemoveHistoryAsync(string ratingKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ratingKey) || ratingKey.Length > 300) throw new ArgumentException("Choose a movie history item.");
        await Mutation.WaitAsync(ct);
        try
        {
            var snapshot = store.Snapshot;
            var count = snapshot.History.Count(x => x.RatingKey == ratingKey);
            if (count == 0) throw new ArgumentException("This source item is no longer in movie history.");
            var excluded = new HashSet<string>(store.HistoryExclusions)
            { store.MatchScope("plex") + ratingKey, store.MatchScope("tautulli") + ratingKey };
            await store.SaveHistoryExclusionsAsync(excluded, ct);
            var history = snapshot.History.Where(x => x.RatingKey != ratingKey).ToList();
            await store.SaveSnapshotAsync(snapshot with { History = history, Warnings = HistoryWarnings(snapshot.Warnings, history) }, ct);
            return count;
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Explicitly allows previously removed source items to return on the next movie-history synchronization.</summary>
    public async Task RestoreHistoryAsync(CancellationToken ct)
    {
        await Mutation.WaitAsync(ct);
        try { await store.SaveHistoryExclusionsAsync([], ct); }
        finally { Mutation.Release(); }
    }

    /// <summary>Updates the actionable unmatched count while retaining unrelated source warnings.</summary>
    private static List<string> HistoryWarnings(IEnumerable<string> warnings, List<Watch> history)
    {
        var result = warnings.Where(x => !x.Contains("could not be matched to TMDB", StringComparison.Ordinal)).ToList();
        var missing = history.Count(x => !x.TmdbId.HasValue);
        if (missing > 0) result.Add($"{missing} history entries could not be matched to TMDB. Their history is visible, but watched filtering may miss these movies.");
        return result;
    }

    /// <summary>Adds a movie if necessary, then explicitly queues Radarr's MoviesSearch command and returns its acknowledgement.</summary>
    public async Task<SearchStatus> SearchAsync(int tmdbId, CancellationToken ct)
    {
        if (tmdbId <= 0) throw new ArgumentException("Choose a valid movie.");
        var existing = await api.RadarrAsync(store.Settings, "movie", ct);
        if (!existing.Items().Any(x => x.Number("tmdbId") == tmdbId)) await AddAsync(tmdbId, false, ct);
        await Mutation.WaitAsync(ct);
        try
        {
            existing = await api.RadarrAsync(store.Settings, "movie", ct);
            var movie = existing.Items().FirstOrDefault(x => x.Number("tmdbId") == tmdbId);
            var id = (int)movie.Number("id");
            if (id <= 0) throw new UpstreamException("Radarr did not return a movie ID for the search.");
            var result = await api.RadarrAsync(store.Settings, "command", ct,
                new JsonObject { ["name"] = "MoviesSearch", ["movieIds"] = new JsonArray(JsonValue.Create(id)) });
            var commandId = (int)result.Number("id");
            if (commandId <= 0) throw new UpstreamException("Radarr did not acknowledge the search command.");
            return new(commandId, result.Text("status", "queued"));
        }
        finally { Mutation.Release(); }
    }

    /// <summary>Reads a search command's current state without claiming a download has completed.</summary>
    public async Task<SearchStatus> SearchStatusAsync(int id, CancellationToken ct)
    {
        if (id <= 0) throw new ArgumentException("Choose a valid Radarr command.");
        var result = await api.RadarrAsync(store.Settings, "command/" + id, ct);
        return new(id, result.Text("status", "unknown"));
    }

    /// <summary>Restricts configurable endpoints to ordinary HTTP service base URLs, retaining reverse proxy paths.</summary>
    private static string BaseUrl(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
            throw new ArgumentException("Enter an HTTP or HTTPS base URL without credentials, a query, or a fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>Retains saved secrets when a password field is left blank.</summary>
    private static string Secret(string? supplied, string saved)
    {
        var value = string.IsNullOrWhiteSpace(supplied) ? saved : supplied.Trim();
        if (value.Length > 4096 || value.Any(char.IsControl)) throw new ArgumentException("An API credential is invalid.");
        return value;
    }
}
