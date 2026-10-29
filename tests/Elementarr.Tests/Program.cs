using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Elementarr;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.DataProtection;
using Elementarr.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Report failures as an ordinary exit code so Windows Error Reporting cannot hold the test executable open.
try { if (args.Contains("--preview")) await PreviewHost.RunAsync(args); else await IntegrationChecks.RunAsync(); return 0; }
catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }

internal static class IntegrationChecks
{
    private static int checks;

    /// <summary>Exercises HTTP authentication, CSRF, sync, recommendation filtering, safe writes, and persistence offline.</summary>
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "elementarr-tests-" + Guid.NewGuid().ToString("N"));
        var fixture = new MediaFixture();
        var host = AppHost.Build([], builder =>
        {
            // Ephemeral state and in-process upstream fixtures prevent any live media-service writes.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ELEMENTARR_PASSWORD"] = "test-only-password-long-enough", ["ELEMENTARR_DATA"] = directory });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Environment.WebRootPath = Path.GetFullPath("src/Elementarr/wwwroot");
            builder.Environment.WebRootFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(builder.Environment.WebRootPath);
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Error);
            builder.Services.AddSingleton<IHttpClientFactory>(fixture);
        });
        try
        {
            await host.StartAsync();
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
            Check((await browser.GetAsync("/api/settings")).StatusCode == HttpStatusCode.Unauthorized, "Anonymous clients cannot read settings");
            Check((await browser.PostAsJsonAsync("/api/login", new { password = "test-only-password-long-enough" })).StatusCode == HttpStatusCode.BadRequest, "Login requires CSRF protection");
            await TokenAsync(browser);
            Check((await browser.PostAsJsonAsync("/api/login", new { password = "test-only-password-long-enough" })).StatusCode == HttpStatusCode.NotFound, "Password login has been removed");
            await SuccessAsync(await browser.PostAsJsonAsync("/api/plex/auth/start", new PlexAuthRequest()));
            fixture.PlexClaimed = true;
            await SuccessAsync(await browser.PostAsJsonAsync("/api/plex/auth/poll", new { }));
            await TokenAsync(browser);
            Check((await browser.GetAsync("/api/settings")).IsSuccessStatusCode, "Any verified Plex account can own a fresh installation without a password");
            var store = host.Services.GetRequiredService<StateStore>();
            await store.SaveSettingsAsync(store.Settings with { PlexToken = "" }, default);
            var settings = new ConnectionUpdate("http://radium.local:8082", "radarr-secret", "http://radium.local:8085", "tautulli-secret", "tmdb-secret", "", "fixture.owner", 7, "/movies", "US", TautulliEnabled: true);
            await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings));
            var saved = await browser.GetStringAsync("/api/settings");
            Check(!saved.Contains("radarr-secret") && !saved.Contains("tmdb-secret"), "Settings API never returns saved secrets");
            Check(!File.ReadAllText(Path.Combine(directory, "connections.protected")).Contains("radarr-secret"), "Saved connection credentials are protected");
            await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings with { RadarrKey = "", TautulliKey = "", TmdbToken = "" }));
            var connectionCheck = await browser.PostAsJsonAsync("/api/connections/check", new { });
            await SuccessAsync(connectionCheck);
            var connected = await connectionCheck.Content.ReadFromJsonAsync<JsonElement>();
            Check(connected.Flag("radarr") && connected.Flag("tautulli") && connected.Flag("tmdb"), "Saved credentials survive blank password inputs");
            await SuccessAsync(await browser.PostAsJsonAsync("/api/sync", new { }));
            var state = await browser.GetFromJsonAsync<JsonElement>("/api/state");
            Check(state.Number("historyCount") == 4, "Sync imports every history page and rejects other users");
            Check(state.Number("watchedCount") == 2, "Partial plays do not become watched movies and rewatches deduplicate");
            Check(fixture.FilteredHistoryRequests == 2, "All history requests include the resolved user ID and movie filter");
            Check(fixture.MetadataRequests == 1, "Modern Plex GUIDs resolve through metadata and reuse the match");
            var feed = await browser.GetFromJsonAsync<JsonElement>("/api/discover");
            var ids = feed.Items("movies").Select(x => (int)x.Number("tmdbId")).ToArray();
            Check(ids.SequenceEqual([3, 5]), "Personalized results exclude owned and completed watched movies");
            Check(feed.Items("movies").All(x => x.Text("reason").StartsWith("Because of ")), "Personalized results explain their seed");
            var upcoming = await browser.GetAsync("/api/discover?view=upcoming");
            await SuccessAsync(upcoming);
            Check(fixture.UpcomingRequestHasDates, "Upcoming discovery bounds future release dates");
            var malicious = await browser.PostAsJsonAsync("/api/settings", settings with { RadarrUrl = "file:///secret" });
            Check(malicious.StatusCode == HttpStatusCode.BadRequest, "Non-HTTP connection URLs are rejected");
            await SuccessAsync(await browser.PostAsJsonAsync("/api/radarr/5", new AddRequest()));
            Check(fixture.Adds == 1 && !fixture.LastSearchNow, "Add uses the selected destination without starting a download search");
            Check((await browser.PostAsJsonAsync("/api/radarr/5", new AddRequest())).StatusCode == HttpStatusCode.BadRequest && fixture.Adds == 1, "Duplicate add never issues a second upstream write");
            await SuccessAsync(await browser.PostAsJsonAsync("/api/radarr/3", new AddRequest(true)));
            Check(fixture.LastSearchNow, "Explicit search-now choice reaches Radarr");
            fixture.FailHistory = true;
            var partialResponse = await browser.PostAsJsonAsync("/api/sync", new { });
            await SuccessAsync(partialResponse);
            var partial = await partialResponse.Content.ReadFromJsonAsync<JsonElement>();
            Check(partial.Flag("radarrSynced") && !partial.Flag("historySynced"), "A failed history source does not block the Radarr scan");
            var retained = await browser.GetFromJsonAsync<JsonElement>("/api/state");
            Check(retained.Number("historyCount") == 4 && retained.Text("historySyncedAt") == state.Text("historySyncedAt"), "A failed sync preserves the last successful snapshot");
            var freshProvider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")),
                protection => protection.SetApplicationName("Elementarr"));
            var reloaded = new StateStore(freshProvider, host.Services.GetRequiredService<IConfiguration>());
            Check(reloaded.Settings.RadarrKey == "radarr-secret" && reloaded.Settings.PlexUserId == "42",
                "A fresh protection provider reloads saved credentials and the resolved user");
            Check(reloaded.Snapshot.History.Count == 4 && reloaded.Snapshot.Library.Count == 4,
                "Library, history, and successful Radarr additions survive a state-store reload");
            fixture.FailHistory = false;
            fixture.BrokenHistoryOffset = 1;
            await SuccessAsync(await browser.PostAsJsonAsync("/api/sync?source=history", new { }));
            var recovered = await browser.GetFromJsonAsync<JsonElement>("/api/state");
            Check(recovered.Number("historyCount") == 3 && recovered.Items("warnings").Any(x => x.ToString().Contains("could not return 1 history plays")),
                "An unreadable Tautulli record is isolated and reported while other history imports");
            fixture.BrokenHistoryOffset = null;
            fixture.FailHistory = true;
            fixture.HistoryFailureStatus = HttpStatusCode.InternalServerError;
            var requestsBeforeFailure = fixture.HistoryRequests;
            Check((await browser.PostAsJsonAsync("/api/sync?source=history", new { })).StatusCode == HttpStatusCode.BadGateway,
                "A persistent Tautulli outage fails the history operation explicitly");
            Check(fixture.HistoryRequests - requestsBeforeFailure < 20,
                "Repeated HTTP 500 responses are bounded instead of discarding arbitrary history");
            var outageState = await browser.GetFromJsonAsync<JsonElement>("/api/state");
            Check(outageState.Number("historyCount") == 3 && outageState.Number("libraryCount") == 4,
                "A history outage retains both prior history and the independent collection");
            fixture.FailHistory = false;
            fixture.HistoryFailureStatus = HttpStatusCode.ServiceUnavailable;
            fixture.FailRadarr = true;
            var historyOnly = await browser.PostAsJsonAsync("/api/sync", new { });
            await SuccessAsync(historyOnly);
            var historyOnlyResult = await historyOnly.Content.ReadFromJsonAsync<JsonElement>();
            Check(!historyOnlyResult.Flag("radarrSynced") && historyOnlyResult.Flag("historySynced"),
                "History sync succeeds independently when Radarr is unavailable");
            fixture.FailRadarr = false;
            await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings with { PlexUserId = "99", PlexUsername = "someone-else" }));
            var cleared = await browser.GetFromJsonAsync<JsonElement>("/api/state");
            Check(cleared.Number("historyCount") == 0 && cleared.Text("historySyncedAt") == "" && cleared.Number("libraryCount") == 4, "Changing the history user clears only previous-user history and preserves the collection");
            settings = settings with { PlexUserId = "42", TautulliEnabled = false };
            await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings));
            var historyRequestsBeforeScan = fixture.HistoryRequests;
            var collectionOnly = await browser.PostAsJsonAsync("/api/sync", new { });
            await SuccessAsync(collectionOnly);
            var collectionResult = await collectionOnly.Content.ReadFromJsonAsync<JsonElement>();
            Check(collectionResult.Flag("radarrSynced") && !collectionResult.Flag("historySynced") && fixture.HistoryRequests == historyRequestsBeforeScan,
                "Radarr scans work with Plex disconnected and optional Tautulli disabled");
            await PlexChecksAsync(browser, fixture, settings);
            await TvChecks.RunAsync(browser, fixture, host.Services, Check);
            await InterfaceChecks.RunAsync(browser, fixture, host.Services, Check);
            await UpdateChecks.RunAsync(browser, Check);
            await StudioChecks.RunAsync(browser, fixture, host.Services, Check);
            await SuccessAsync(await browser.PostAsJsonAsync("/api/logout", new { }));
            Check((await browser.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "Logout invalidates the authenticated session");
            Check(JsonFields.PosterPath("//evil.example/poster.jpg") is null, "Poster validation prevents arbitrary remote image paths");
            Check(JsonDocument.Parse("{\"guid\":\"com.plexapp.agents.imdb://tt1234567?lang=en\"}").RootElement.ImdbId() == "tt1234567",
                "Legacy Plex IMDb GUIDs remain matchable to movie discovery IDs");
            Console.WriteLine($"PASS: {checks} integration checks. Upstreams are fixtures; no live Radarr/Tautulli/TMDB calls.");
        }
        finally
        {
            await host.StopAsync();
            await host.DisposeAsync();
            // Remove only this uniquely created temporary test directory after verifying its location.
            var resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("elementarr-tests-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }

    /// <summary>Verifies browser-bound owner authorization, primary Plex history, optional fallback, and Plex sign-in.</summary>
    private static async Task PlexChecksAsync(HttpClient browser, MediaFixture fixture, ConnectionUpdate settings)
    {
        using var stranger = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
            { BaseAddress = browser.BaseAddress };
        await TokenAsync(stranger);
        fixture.PlexClaimed = true;
        fixture.PlexAccountId = "99";
        fixture.PlexUsername = "intruder";
        await SuccessAsync(await browser.PostAsJsonAsync("/api/plex/auth/start", new PlexAuthRequest(true)));
        Check((await stranger.PostAsJsonAsync("/api/plex/auth/poll", new { })).StatusCode == HttpStatusCode.BadRequest,
            "A different browser cannot redeem another browser's Plex challenge");
        Check((await browser.PostAsJsonAsync("/api/plex/auth/poll", new { })).StatusCode == HttpStatusCode.Forbidden,
            "An unrelated Plex account cannot replace an installation's linked owner");
        fixture.PlexAccountId = "42";
        fixture.PlexUsername = "fixture.owner";
        fixture.PlexClaimed = false;
        var start = await browser.PostAsJsonAsync("/api/plex/auth/start", new PlexAuthRequest(true));
        await SuccessAsync(start);
        var startJson = await start.Content.ReadFromJsonAsync<JsonElement>();
        Check(startJson.Text("authUrl").StartsWith("https://app.plex.tv/auth#?") && !startJson.GetRawText().Contains("plex-secret"),
            "Plex PIN start returns only the official authorization URL");
        var pending = await browser.PostAsJsonAsync("/api/plex/auth/poll", new { });
        await SuccessAsync(pending);
        Check((await pending.Content.ReadFromJsonAsync<JsonElement>()).Text("status") == "pending",
            "An unclaimed Plex PIN does not issue an authenticated authorization");
        fixture.PlexClaimed = true;
        var connected = await browser.PostAsJsonAsync("/api/plex/auth/poll", new { });
        await SuccessAsync(connected);
        await TokenAsync(browser);
        var safeSettings = await browser.GetStringAsync("/api/settings");
        Check(JsonDocument.Parse(safeSettings).RootElement.Flag("hasPlexToken") && !safeSettings.Contains("plex-secret"),
            "Plex authorization is saved without returning its token to the browser");
        Check((await browser.PostAsJsonAsync("/api/plex/auth/poll", new { })).StatusCode == HttpStatusCode.BadRequest,
            "A completed Plex authorization challenge cannot be replayed");
        var available = await browser.GetFromJsonAsync<JsonElement>("/api/session");
        Check(available.Flag("plexSignInAvailable"), "Plex-only sign-in remains available after ownership is established");
        fixture.PlexResourcesOwned = false;
        Check((await browser.PostAsJsonAsync("/api/plex/server", new PlexServerRequest("plex-server-1"))).StatusCode == HttpStatusCode.BadRequest,
            "Shared servers cannot be selected as the owner's history source");
        fixture.PlexResourcesOwned = true;
        await SuccessAsync(await browser.PostAsJsonAsync("/api/plex/server", new PlexServerRequest("plex-server-1")));
        Check((await browser.GetFromJsonAsync<JsonElement>("/api/settings")).Text("plexServerId") == "plex-server-1", "Owned Plex server selection verifies identity on any host");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings with { TautulliEnabled = true }));
        fixture.FailHistory = true;
        var requestsBeforePlex = fixture.HistoryRequests;
        var plexScan = await browser.PostAsJsonAsync("/api/sync?source=history", new { });
        await SuccessAsync(plexScan);
        var plexResult = await plexScan.Content.ReadFromJsonAsync<JsonElement>();
        var state = await browser.GetFromJsonAsync<JsonElement>("/api/state");
        Check(plexResult.Text("historySource") == "plex" && state.Number("historyCount") == 2 && fixture.HistoryRequests == requestsBeforePlex,
            "Plex history is primary and rejects injected foreign-profile entries without calling Tautulli");
        Check(!state.Items("warnings").Any(), "Normal TV episodes are excluded without reporting missing movie metadata");
        var history = await browser.GetFromJsonAsync<JsonElement>("/api/history");
        Check(history.Items("plays").All(x => x.GetProperty("percent").ValueKind == JsonValueKind.Null && x.Flag("completed") && x.Number("tmdbId") > 0),
            "Plex history resolves TMDB GUIDs without inventing a playback percentage");
        fixture.FailRadarr = true;
        var independent = await browser.PostAsJsonAsync("/api/sync", new { });
        await SuccessAsync(independent);
        var independentResult = await independent.Content.ReadFromJsonAsync<JsonElement>();
        Check(!independentResult.Flag("radarrSynced") && independentResult.Flag("historySynced"),
            "Plex history remains usable when Radarr fails");
        fixture.FailRadarr = false;
        fixture.FailHistory = false;
        fixture.FailPlex = true;
        var fallback = await browser.PostAsJsonAsync("/api/sync?source=history", new { });
        await SuccessAsync(fallback);
        var fallbackResult = await fallback.Content.ReadFromJsonAsync<JsonElement>();
        Check(fallbackResult.Text("historySource") == "tautulli", "Enabled Tautulli is used as a fallback when Plex is unavailable");
        var fallbackState = await browser.GetFromJsonAsync<JsonElement>("/api/state");
        Check(fallbackState.Items("warnings").Any(x => x.ToString().Contains("optional Tautulli source was used")),
            "Fallback history is clearly labeled instead of presented as a successful Plex import");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/logout", new { }));
        await TokenAsync(browser);
        fixture.PlexAccountId = "99";
        fixture.PlexUsername = "intruder";
        await SuccessAsync(await browser.PostAsJsonAsync("/api/plex/auth/start", new PlexAuthRequest()));
        Check((await browser.PostAsJsonAsync("/api/plex/auth/poll", new { })).StatusCode == HttpStatusCode.Forbidden,
            "Plex sign-in rejects a different account after ownership is pinned");
        fixture.PlexAccountId = "42";
        fixture.PlexUsername = "fixture.owner";
        await SuccessAsync(await browser.PostAsJsonAsync("/api/plex/auth/start", new PlexAuthRequest()));
        await SuccessAsync(await browser.PostAsJsonAsync("/api/plex/auth/poll", new { }));
        await TokenAsync(browser);
        Check((await browser.GetAsync("/api/state")).IsSuccessStatusCode,
            "The linked Plex owner can sign in while the local Plex server is unavailable");
        fixture.FailPlex = false;
    }

    /// <summary>Obtains an antiforgery request token associated with the client's current cookies.</summary>
    private static async Task TokenAsync(HttpClient browser)
    {
        var session = await browser.GetFromJsonAsync<JsonElement>("/api/session");
        browser.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session.Text("csrfToken"));
    }

    /// <summary>Includes the API error in a failed check to keep fixture failures actionable.</summary>
    private static async Task SuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Records a behavior check and fails immediately when the observed contract differs.</summary>
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        checks++;
        Console.WriteLine("PASS: " + message);
    }
}

internal sealed class MediaFixture : HttpMessageHandler, IHttpClientFactory
{
    public int Adds { get; private set; }
    public bool LastSearchNow { get; private set; }
    public int FilteredHistoryRequests { get; private set; }
    public int MetadataRequests { get; private set; }
    public bool UpcomingRequestHasDates { get; private set; }
    public bool FailHistory { get; set; }
    public bool FailRadarr { get; set; }
    public bool FailPlex { get; set; }
    public bool FailSonarr { get; set; }
    public int PlexParentMode { get; set; } = 1;
    public int SonarrAdds { get; private set; }
    public int TvRequests { get; private set; }
    public bool LastTvSearchNow { get; private set; }
    private readonly List<object> tvAdded = [];
    public int? BrokenHistoryOffset { get; set; }
    public int HistoryRequests { get; private set; }
    public HttpStatusCode HistoryFailureStatus { get; set; } = HttpStatusCode.ServiceUnavailable;
    public bool PlexClaimed { get; set; }
    public string PlexAccountId { get; set; } = "42";
    public string PlexUsername { get; set; } = "fixture.owner";
    public bool PlexResourcesOwned { get; set; } = true;
    public string PlexThumb { get; set; } = "https://images.plex.tv/avatars/fixture.png";
    public int AvatarRequests { get; private set; }
    public int SearchCommands { get; private set; }
    public bool FailSearch { get; set; }
    public int MovieDetailRequests { get; private set; }
    private readonly List<object> added = [];

    /// <summary>Creates clients that route all integration requests into this local fixture.</summary>
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    /// <summary>Simulates the upstream contracts, including pagination, foreign-user rows, and Radarr write options.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (uri.Host is "images.plex.tv" or "assets.plex.tv")
        {
            if (request.Headers.Contains("X-Plex-Token") || request.Headers.Authorization is not null) throw new InvalidOperationException("Avatar requests must not carry account credentials.");
            AvatarRequests++;
            if (uri.AbsolutePath.EndsWith("/redirect") || uri.AbsolutePath.EndsWith("/unsafe"))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(uri.AbsolutePath.EndsWith("/unsafe") ? "https://evil.example/avatar.png" : "https://assets.plex.tv/avatars/fixture.png");
                return redirect;
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl8sAAAAASUVORK5CYII=")) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") } } };
        }
        if (uri.AbsolutePath == "/api/v2")
        {
            var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            if (form["apikey"] != "tautulli-secret") throw new InvalidOperationException("Missing Tautulli body credential.");
            object data;
            switch (form["cmd"])
            {
                case "get_users": data = new[] { new { user_id = "42", username = "fixture.owner" } }; break;
                case "get_metadata": MetadataRequests++; data = new { guid = "plex://movie/abc", guids = new[] { "tmdb://4" } }; break;
                case "get_history":
                    HistoryRequests++;
                    if (FailHistory) return new HttpResponseMessage(HistoryFailureStatus);
                    var offset = int.Parse(form["start"]);
                    var length = int.Parse(form["length"]);
                    if (BrokenHistoryOffset.HasValue && offset <= BrokenHistoryOffset && offset + length > BrokenHistoryOffset)
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    if (form["user_id"] == "42" && form["media_type"] == "movie") FilteredHistoryRequests++;
                    var allRows = new[]
                    { Play("1", "42", "1", "Watched Movie", "tmdb://1", 90), Play("2", "42", "2", "Incomplete Movie", "tmdb://2", 30), Play("3", "99", "3", "Other User Movie", "tmdb://3", 100),
                      Play("4", "42", "55", "External Movie", "plex://movie/abc", 95), Play("5", "42", "55", "External Movie", "plex://movie/abc", 95) };
                    var rows = allRows.Skip(offset).Take(Math.Min(length, 3)).ToArray();
                    data = new { recordsFiltered = 5, data = rows }; break;
                default: throw new InvalidOperationException("Unexpected Tautulli command.");
            }
            return Json(new { response = new { result = "success", data } });
        }
        if (uri.Host == "plex.tv")
        {
            if (!request.Headers.Contains("X-Plex-Client-Identifier")) throw new InvalidOperationException("Missing stable Plex client identifier.");
            if (uri.AbsolutePath.EndsWith("/pins")) return Json(new { id = 12345, code = "test-authorization-code" });
            if (uri.AbsolutePath.Contains("/pins/")) return Json(new { id = 12345, authToken = PlexClaimed ? "plex-secret" : null });
            if (!request.Headers.TryGetValues("X-Plex-Token", out var tokens) || tokens.Single() != "plex-secret") throw new InvalidOperationException("Missing Plex cloud token.");
            if (uri.AbsolutePath.EndsWith("/user")) return Json(new { id = PlexAccountId, username = PlexUsername, thumb = PlexThumb });
            if (uri.AbsolutePath.EndsWith("/resources")) return Json(new[] { new { owned = PlexResourcesOwned, provides = "server", clientIdentifier = "plex-server-1",
                connections = new[] { new { local = true, relay = false, address = "10.0.0.14", uri = "http://radium.local:32400" } } } });
        }
        if (uri.Port == 32400)
        {
            if (!request.Headers.TryGetValues("X-Plex-Token", out var tokens) || tokens.Single() != "plex-secret") throw new InvalidOperationException("Missing Plex server token.");
            if (FailPlex) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (uri.AbsolutePath == "/identity") return Json(new { MediaContainer = new { machineIdentifier = "plex-server-1" } });
            if (uri.AbsolutePath == "/accounts") return Json(new { MediaContainer = new { Account = new[] { new { id = 1, name = "fixture.owner" } } } });
            if (uri.AbsolutePath == "/status/sessions/history/all")
            {
                if (!uri.Query.Contains("accountID=1") || uri.Query.Contains("type=")) throw new InvalidOperationException("Plex history must use the documented owner filter without an unsupported type filter.");
                var first = uri.Query.Contains("X-Plex-Container-Start=0");
                var rows = first
                    ? new[] { PlexPlay("1", "1"), PlexPlay("3", "99"), PlexPlay("5", "1", "episode", PlexParentMode), PlexPlay("6", "99", "episode", PlexParentMode) }
                    : new[] { PlexPlay("4", "1"), PlexPlay("7", "1", "episode", PlexParentMode) };
                return Json(new { MediaContainer = new { totalSize = 6, offset = first ? 0 : 4, Metadata = rows } });
            }
            if (uri.AbsolutePath.StartsWith("/library/metadata/"))
            {
                var keys = uri.AbsolutePath.Split('/').Last().Split(',');
                return Json(new { MediaContainer = new { Metadata = keys.Select(key => new { ratingKey = key, type = key == "100" ? "show" : key is "5" or "7" ? "episode" : "movie", grandparentRatingKey = key is "5" or "7" ? "100" : "", grandparentTitle = "Fixture Series", parentIndex = 2, index = 3, title = key == "100" ? "Fixture Series" : "Movie " + key, year = 2020, Guid = new[] { new { id = "tmdb://" + (key == "100" ? "1" : key) }, new { id = key == "100" ? "tvdb://101" : "imdb://tt1234567" } } }) } });
            }
        }
        if (uri.Port == 8989 && uri.AbsolutePath.StartsWith("/MediaCover/"))
        {
            if (!request.Headers.TryGetValues("X-Api-Key", out var imageKeys) || imageKeys.Single() != "sonarr-secret") throw new InvalidOperationException("Missing Sonarr artwork header credential.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl8sAAAAASUVORK5CYII=")) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png") } } };
        }
        if (uri.Port == 8989 && uri.AbsolutePath.StartsWith("/api/v3/"))
        {
            if (FailSonarr) return new(HttpStatusCode.ServiceUnavailable);
            if (!request.Headers.TryGetValues("X-Api-Key", out var sonarrKeys) || sonarrKeys.Single() != "sonarr-secret") throw new InvalidOperationException("Missing Sonarr header credential.");
            if (uri.AbsolutePath.EndsWith("/qualityprofile")) return Json(new[] { new { id = 8, name = "TV HD" } });
            if (uri.AbsolutePath.EndsWith("/calendar"))
            {
                if (!uri.Query.Contains("includeSeries=true") || !uri.Query.Contains("start=") || !uri.Query.Contains("end=")) throw new InvalidOperationException("Calendar requires a bounded window and series data.");
                return Json(new[] {
                    new { seriesId = 1, seasonNumber = 2, episodeNumber = 10, title = "Later Episode", airDateUtc = DateTimeOffset.UtcNow.AddDays(2) },
                    new { seriesId = 1, seasonNumber = 2, episodeNumber = 9, title = "Next Episode", airDateUtc = DateTimeOffset.UtcNow.AddDays(1) },
                    new { seriesId = 1, seasonNumber = 1, episodeNumber = 1, title = "Past Episode", airDateUtc = DateTimeOffset.UtcNow.AddDays(-1) },
                    new { seriesId = 1, seasonNumber = 3, episodeNumber = 1, title = "Outside Window", airDateUtc = DateTimeOffset.UtcNow.AddDays(16) }
                });
            }
            if (uri.AbsolutePath.EndsWith("/rootfolder")) return Json(new[] { new { path = "/tv" } });
            if (uri.AbsolutePath.EndsWith("/lookup"))
            {
                var id = int.Parse(Uri.UnescapeDataString(uri.Query.Split(':').Last()));
                return Json(new[] { new { tvdbId = id, tmdbId = id - 100, title = "Series " + (id - 100), year = 2020, titleSlug = "series-" + id } });
            }
            if (uri.AbsolutePath.EndsWith("/series") && request.Method == HttpMethod.Get)
                return Json(new object[] { new { id = 1, tvdbId = 101, tmdbId = 1, title = "Fixture Series", year = 2020,
                    statistics = new { episodeFileCount = 3, episodeCount = 8 } } }.Concat(tvAdded));
            if (uri.AbsolutePath.EndsWith("/series") && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                if (body.Number("qualityProfileId") != 8 || body.Text("rootFolderPath") != "/tv" || !body.Flag("monitored")) throw new InvalidOperationException("Wrong Sonarr destination.");
                LastTvSearchNow = body.GetProperty("addOptions").Flag("searchForMissingEpisodes");
                SonarrAdds++; tvAdded.Add(body); return Json(body);
            }
            throw new InvalidOperationException("Unexpected Sonarr request.");
        }
        if (uri.AbsolutePath.StartsWith("/api/v3/"))
        {
            if (FailRadarr) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (!request.Headers.TryGetValues("X-Api-Key", out var keys) || keys.Single() != "radarr-secret") throw new InvalidOperationException("Missing Radarr header credential.");
            if (uri.AbsolutePath.EndsWith("/qualityprofile")) return Json(new[] { new { id = 7, name = "HD" } });
            if (uri.AbsolutePath.EndsWith("/rootfolder")) return Json(new[] { new { path = "/movies" } });
            if (uri.AbsolutePath.EndsWith("/lookup/tmdb")) return Json(new { tmdbId = int.Parse(uri.Query.Split('=').Last()), title = "A New Movie", year = 2026, titleSlug = "a-new-movie",
                imdbId = "tt1234567", runtime = 121, genres = new[] { "Drama" }, ratings = new { imdb = new { value = 8.4, votes = 1000 }, rottenTomatoes = new { value = 82, votes = 100 } } });
            if (uri.AbsolutePath.EndsWith("/movie") && request.Method == HttpMethod.Get)
                return Json(new object[] { new { id = 11, tmdbId = 1, title = "Watched Movie", year = 2020, hasFile = true }, new { id = 12, tmdbId = 2, title = "Incomplete Movie", year = 2020, hasFile = false } }.Concat(added));
            if (uri.AbsolutePath.EndsWith("/movie") && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                if (body.Number("qualityProfileId") != 7 || body.Text("rootFolderPath") != "/movies" || !body.Flag("monitored")) throw new InvalidOperationException("Wrong Radarr destination.");
                LastSearchNow = body.GetProperty("addOptions").Flag("searchForMovie"); Adds++;
                var stored = JsonNode.Parse(body.GetRawText())!.AsObject(); stored["id"] = 100 + Adds;
                var item = JsonSerializer.SerializeToElement(stored); added.Add(item); return Json(item);
            }
            if (uri.AbsolutePath.EndsWith("/command"))
            {
                if (FailSearch) return new(HttpStatusCode.ServiceUnavailable);
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
                if (body.Text("name") != "MoviesSearch" || !body.Items("movieIds").Any(x => x.GetInt32() > 0)) throw new InvalidOperationException("Invalid Radarr search command.");
                SearchCommands++; return Json(new { id = 900 + SearchCommands, status = "queued" });
            }
            if (uri.AbsolutePath.Contains("/command/")) return Json(new { id = 901, status = "completed" });
        }
        if (uri.Host == "api.themoviedb.org")
        {
            if (request.Headers.Authorization?.Parameter != "tmdb-secret") throw new InvalidOperationException("Missing TMDB bearer token.");
            if (uri.AbsolutePath.StartsWith("/3/tv/") && int.TryParse(uri.AbsolutePath.Split('/').Last(), out var tvId))
            {
                TvRequests++;
                return Json(new { id = tvId, name = "Series " + tvId, first_air_date = "2020-01-01", overview = "TV overview", poster_path = "/poster.jpg", vote_average = 8.1, vote_count = 100,
                    external_ids = new { tvdb_id = 100 + tvId } });
            }
            if (uri.AbsolutePath.StartsWith("/3/trending/tv") || uri.AbsolutePath.StartsWith("/3/search/tv") || uri.AbsolutePath.Contains("/recommendations") && uri.AbsolutePath.StartsWith("/3/tv/"))
            {
                TvRequests++;
                return Json(new { total_pages = 1, total_results = 2, results = new[] { new { id = 1, name = "Fixture Series", first_air_date = "2020-01-01" }, new { id = 8, name = "Another Series", first_air_date = "2021-02-01" } } });
            }
            if (uri.AbsolutePath.EndsWith("/configuration")) return Json(new { images = new { } });
            if (int.TryParse(uri.AbsolutePath.Split('/').Last(), out var movieId))
            {
                MovieDetailRequests++;
                if (movieId == 4040) return new(HttpStatusCode.NotFound);
                return Json(new { id = movieId, title = "Rich Movie " + movieId, overview = "A real fixture synopsis.", release_date = "2001-06-15",
                    runtime = 125, imdb_id = "tt1234567", original_language = "en", vote_average = 7.7, vote_count = 5000,
                    poster_path = "/poster.jpg", backdrop_path = "/backdrop.jpg", tagline = "A fixture tagline.",
                    genres = new[] { new { id = 18, name = "Drama" } }, production_countries = new[] { new { iso_3166_1 = "US", name = "United States" } },
                    production_companies = new[] { new { name = "Fixture Studio" } },
                    credits = new { cast = new[] { new { name = "Fixture Actor", character = "A Character", profile_path = "/actor.jpg" } }, crew = new[] { new { name = "Fixture Director", job = "Director" } } },
                    images = new { logos = new[] { new { file_path = "/logo.png", iso_639_1 = "en" } } },
                    videos = new { results = new[] { new { site = "YouTube", type = "Trailer", key = "abcdefghi12" } } },
                    release_dates = new { results = new[] { new { iso_3166_1 = "US", release_dates = new[] { new { certification = "PG-13" } } } } } });
            }
            UpcomingRequestHasDates |= uri.Query.Contains("primary_release_date.gte") && uri.Query.Contains("primary_release_date.lte");
            return Json(new { total_pages = 3, results = Enumerable.Range(1, 5).Select(id => new { id, title = "Movie " + id, release_date = "2026-11-01", vote_average = 7.5, vote_count = 1000, poster_path = "/poster.jpg" }) });
        }
        throw new InvalidOperationException("Unexpected upstream request: " + uri.AbsolutePath);
    }

    /// <summary>Creates representative history rows with string identifiers, as returned by Tautulli.</summary>
    private static object Play(string id, string userId, string key, string title, string guid, int percent) =>
        new { id, user_id = userId, rating_key = key, title, guid, percent_complete = percent, year = "2020", date = "1760000000", media_type = "movie" };

    /// <summary>Creates Plex history with a server-local account ID and no unsupported progress field.</summary>
    private static object PlexPlay(string key, string accountId, string type = "movie", int parentMode = 0) => new
    { historyKey = "/status/sessions/history/" + key, ratingKey = key, title = (type == "episode" ? "Episode " : "Movie ") + key,
        originallyAvailableAt = "2020-01-01", viewedAt = 1760000000, accountID = accountId, type, grandparentRatingKey = type == "episode" && parentMode == 0 ? "100" : "", grandparentKey = type == "episode" && parentMode == 1 ? "/library/metadata/100" : "", grandparentTitle = "Fixture Series", parentIndex = 2, index = 3 };

    /// <summary>Serializes fixture resources through the normal JSON HTTP content implementation.</summary>
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = null }) };
}
