using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Elementarr.Services;

public sealed class PlexService(StateStore store, MediaApi api, IMemoryCache cache)
{
    private const string ChallengeCookie = "elementarr.plex.challenge";
    private sealed record Challenge(string PinId, bool Connect, string OwnerId);

    /// <summary>Discards only this browser's pending authorization without changing the linked account.</summary>
    public void Cancel(HttpContext context)
    {
        var nonce = context.Request.Cookies[ChallengeCookie];
        if (nonce is not null) cache.Remove("plex-pin:" + nonce);
        context.Response.Cookies.Delete(ChallengeCookie, new CookieOptions { Path = "/api/plex" });
    }

    /// <summary>Starts a browser-bound Plex PIN flow; the first verified account becomes this installation's owner.</summary>
    public async Task<string> StartAsync(HttpContext context, bool connect, CancellationToken ct)
    {
        var settings = store.Settings;
        var pin = await api.PlexCloudAsync(settings, "pins", ct, createPin: true);
        var pinId = pin.Text("id");
        var code = pin.Text("code");
        if (!long.TryParse(pinId, out _) || code.Length is < 4 or > 128)
            throw new UpstreamException("Plex did not return a valid authorization PIN.");
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        cache.Set("plex-pin:" + nonce, new Challenge(pinId, connect, settings.PlexOwnerId), TimeSpan.FromMinutes(10));
        context.Response.Cookies.Append(ChallengeCookie, nonce, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(10), Path = "/api/plex"
        });
        return "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(settings.PlexClientId)
            + "&code=" + Uri.EscapeDataString(code) + "&context%5Bdevice%5D%5Bproduct%5D=Elementarr";
    }

    /// <summary>Polls only this browser's challenge and verifies the returned account before accepting its token.</summary>
    public async Task<PlexAuthorization> PollAsync(HttpContext context, CancellationToken ct)
    {
        var nonce = context.Request.Cookies[ChallengeCookie];
        if (nonce is null || !cache.TryGetValue<Challenge>("plex-pin:" + nonce, out var challenge) || challenge is null)
            throw new ArgumentException("Plex authorization expired. Please try again.");
        var settings = store.Settings;
        if (challenge.OwnerId != settings.PlexOwnerId)
            throw new UnauthorizedAccessException("The linked Plex account changed. Please start again.");
        var pin = await api.PlexCloudAsync(settings, "pins/" + challenge.PinId, ct);
        var token = pin.Text("authToken");
        if (token.Length == 0) return new(false);
        var account = await api.PlexCloudAsync(settings, "user", ct, token: token);
        var id = account.Text("id");
        var username = account.Text("username");
        if (id.Length == 0 || username.Length == 0)
            throw new UpstreamException("Plex did not return a valid account identity.");
        var allowed = settings.PlexOwnerId.Length == 0 || settings.PlexOwnerId == id;
        if (!allowed)
        {
            cache.Remove("plex-pin:" + nonce);
            context.Response.Cookies.Delete(ChallengeCookie, new CookieOptions { Path = "/api/plex" });
            throw new UnauthorizedAccessException("This Plex account is not authorized for this Elementarr installation.");
        }

        var serverId = settings.PlexServerId;
        var serverUrl = settings.PlexUrl;
        // Authentication must remain usable while the local media server is offline or unconfigured.
        // Server discovery is an explicit authenticated action after sign-in.
        cache.Remove("plex-pin:" + nonce);
        context.Response.Cookies.Delete(ChallengeCookie, new CookieOptions { Path = "/api/plex" });
        return new(true, challenge.Connect, token, id, username, serverId, serverUrl);
    }

    /// <summary>Lists owned Plex servers without exposing account or per-server access tokens.</summary>
    public async Task<List<Choice>> ServersAsync(CancellationToken ct)
    {
        var resources = await api.PlexCloudAsync(store.Settings, "resources?includeHttps=1&includeRelay=0", ct, token: store.Settings.PlexToken);
        return resources.Items().Where(IsOwnedServer)
            .Select(x => new Choice(x.Text("clientIdentifier"), x.Text("name", x.Text("clientIdentifier")))).ToList();
    }

    /// <summary>Checks ownership and identity before accepting an advertised connection on any installation's network.</summary>
    public async Task<PlexAuthorization> ResolveServerAsync(Connections settings, string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) throw new ArgumentException("Choose a Plex server.");
        var resources = await api.PlexCloudAsync(settings, "resources?includeHttps=1&includeRelay=0", ct, token: settings.PlexToken);
        var server = resources.Items().FirstOrDefault(x => IsOwnedServer(x) && x.Text("clientIdentifier") == id);
        if (server.ValueKind != JsonValueKind.Object) throw new ArgumentException("Choose a server owned by your linked Plex account.");
        var connections = server.Items("connections").Where(x => !x.Flag("relay"))
            .OrderByDescending(x => x.Flag("local")).ThenByDescending(x => x.Text("uri").StartsWith("https://", StringComparison.Ordinal)).Take(12);
        foreach (var connection in connections)
        {
            var url = connection.Text("uri");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) continue;
            try
            {
                var identity = await api.PlexAsync(settings with { PlexUrl = url }, "identity", ct);
                if (Container(identity).Text("machineIdentifier") == id)
                    return new(true, true, settings.PlexToken, settings.PlexOwnerId, settings.PlexOwnerName, id, url.TrimEnd('/'));
            }
            catch (UpstreamException) { /* Try the next advertised connection with normal TLS validation. */ }
        }
        throw new UpstreamException("Plex authorized your account, but this server could not be reached. Check its network and firewall.");
    }

    /// <summary>Accepts only Plex resources explicitly owned by the authenticated account.</summary>
    private static bool IsOwnedServer(JsonElement item) => item.Flag("owned") && item.Text("provides").Split(',').Contains("server");

    /// <summary>Validates Plex's envelope so malformed upstream responses retain the previous source snapshot.</summary>
    internal static JsonElement Container(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("MediaContainer", out var container)
            || container.ValueKind != JsonValueKind.Object) throw new UpstreamException("Plex returned an invalid media response. Previous history retained.");
        return container;
    }

    /// <summary>Confirms the local server identity and the linked owner's server-side profile without importing other users.</summary>
    public async Task CheckAsync(Connections settings, CancellationToken ct)
    {
        if (settings.PlexServerId.Length == 0) throw new UpstreamException("Select your owned Plex server in Connections before syncing history.");
        var identity = await api.PlexAsync(settings, "identity", ct);
        if (!identity.TryGetProperty("MediaContainer", out var container)
            || settings.PlexServerId.Length == 0 || container.Text("machineIdentifier") != settings.PlexServerId)
            throw new UpstreamException("The configured URL does not match your linked Plex server. Reconnect Plex in Connections.");
        var accounts = await api.PlexAsync(settings, "accounts", ct);
        if (!Container(accounts).Items("Account").Any(x => x.Text("id") == "1"))
            throw new UpstreamException("Plex did not return the server owner's history profile.");
    }

    /// <summary>Imports the linked server owner's movie history, using batched metadata to resolve stable TMDB IDs.</summary>
    public async Task<HistoryImport> ReadHistoryAsync(Connections settings, LibrarySnapshot snapshot, CancellationToken ct)
    {
        await CheckAsync(settings, ct);
        var previous = snapshot.HistorySource == "plex" ? snapshot.History.Where(x => x.TmdbId.HasValue)
            .GroupBy(x => x.RatingKey).ToDictionary(x => x.Key, x => x.First().TmdbId) : new Dictionary<string, int?>();
        var metadata = new Dictionary<string, JsonElement>();
        var history = new List<Watch>();
        var seen = new HashSet<string>();
        var offset = 0;
        var unavailable = 0;
        while (true)
        {
            var response = await api.PlexAsync(settings,
                $"status/sessions/history/all?accountID=1&sort=viewedAt:desc&X-Plex-Container-Start={offset}&X-Plex-Container-Size=500", ct);
            var container = Container(response);
            var rows = container.Items("Metadata").ToArray();
            if (!int.TryParse(container.Text("totalSize"), out var total) || total < 0
                || container.Number("offset") != offset)
                throw new UpstreamException("Plex returned invalid history pagination. The previous history has been retained.");
            if (total > 100_000)
                throw new UpstreamException("Plex history exceeds 100,000 plays. The previous history has been retained.");
            if (rows.Length == 0 && offset < total)
                throw new UpstreamException("Plex returned an incomplete history page. The previous history has been retained.");
            // History mixes media types; only fetch metadata for this owner's movies.
            var keys = rows.Where(x => x.Text("accountID") == "1" && x.Text("type") == "movie")
                .Select(x => x.Text("ratingKey")).Where(x => long.TryParse(x, out var number) && number > 0)
                .Where(x => !metadata.ContainsKey(x) && !previous.ContainsKey(x)).Distinct().ToArray();
            if (keys.Length > 0)
            {
                try
                {
                    var details = await api.PlexAsync(settings, "library/metadata/" + string.Join(',', keys) + "?includeGuids=1", ct);
                    foreach (var item in Container(details).Items("Metadata")) metadata[item.Text("ratingKey")] = item;
                }
                catch (UpstreamException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                { /* Removed library items can retain valid history; match their title/year when unambiguous. */ }
            }
            foreach (var row in rows)
            {
                if (row.Text("accountID") != "1") continue;
                if (row.Text("type").Length == 0) unavailable++;
                if (row.Text("type") != "movie") continue;
                var key = row.Text("ratingKey");
                var watchedAt = (long)row.Number("viewedAt");
                var id = "plex:" + row.Text("historyKey", key + ":" + watchedAt);
                if (!seen.Add(id)) continue;
                var title = row.Text("title");
                _ = int.TryParse(row.Text("originallyAvailableAt").Split('-')[0], out var year);
                var tmdbId = row.TmdbId();
                var imdbId = row.ImdbId();
                var facts = MetadataService.ParsePlex(row);
                if (metadata.TryGetValue(key, out var detail))
                {
                    tmdbId ??= detail.TmdbId();
                    imdbId ??= detail.ImdbId();
                    if (detail.Number("year") > 0) year = (int)detail.Number("year");
                    facts = MetadataService.Merge(facts, MetadataService.ParsePlex(detail))!;
                }
                if (!tmdbId.HasValue && previous.TryGetValue(key, out var remembered)) tmdbId = remembered;
                if (!tmdbId.HasValue && imdbId is not null)
                    tmdbId = snapshot.Library.FirstOrDefault(x => x.ImdbId == imdbId)?.TmdbId;
                if (!tmdbId.HasValue)
                {
                    var matches = snapshot.Library.Where(x => x.Year == year && string.Equals(x.Title, title,
                        StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matches.Length == 1) tmdbId = matches[0].TmdbId;
                }
                if (!tmdbId.HasValue && imdbId is not null && settings.TmdbToken.Length > 0)
                {
                    try
                    {
                        var found = await api.TmdbAsync(settings, "find/" + imdbId + "?external_source=imdb_id", ct);
                        var movies = found.Items("movie_results").ToArray();
                        if (movies.Length == 1 && movies[0].Number("id") > 0) tmdbId = (int)movies[0].Number("id");
                    }
                    catch (UpstreamException) { /* Discovery outages do not block an otherwise readable Plex history. */ }
                }
                if (key.Length > 0) previous[key] = tmdbId;
                // Plex history records watched/scrobbled items; it does not provide the playback percentage.
                history.Add(new(id, key.Length > 0 ? key : id, title, year, watchedAt, null, tmdbId, true, facts));
            }
            offset += rows.Length;
            if (offset >= total || rows.Length == 0) break;
            if (offset >= 100_000) throw new UpstreamException("Plex history exceeds 100,000 plays. The previous history has been retained.");
        }
        List<string> warnings = [];
        if (unavailable > 0) warnings.Add($"{unavailable} Plex history entries have no media type and could not be identified as movies.");
        var unmatched = history.Count(x => !x.TmdbId.HasValue);
        if (unmatched > 0) warnings.Add($"{unmatched} Plex history entries could not be matched to TMDB. Their history is visible, but watched filtering may miss these movies.");
        return new(history.OrderByDescending(x => x.WatchedAt).ToList(), warnings);
    }
}
