using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Elementarr;
using Elementarr.Services;
using Microsoft.Extensions.DependencyInjection;

internal static class TvChecks
{
    /// <summary>Proves TV/movie identity isolation, owner episode filtering, independent failures, persistence, and safe Sonarr writes.</summary>
    public static async Task RunAsync(HttpClient browser, MediaFixture fixture, IServiceProvider services, Action<bool, string> check)
    {
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);
        using var legacyBrowser = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = browser.BaseAddress };
        var identity = new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "legacy.owner")], "Cookies");
        var legacy = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(identity), "Cookies");
        legacyBrowser.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + options.TicketDataFormat.Protect(legacy));
        check((await legacyBrowser.GetAsync("/api/settings")).StatusCode == HttpStatusCode.Unauthorized, "Old password-authenticated cookies cannot access Plex-only installations");
        var store = services.GetRequiredService<StateStore>();
        var settings = new ConnectionUpdate(store.Settings.RadarrUrl, null, store.Settings.TautulliUrl, null, null,
            "42", "fixture.owner", 7, "/movies", "US", store.Settings.PlexUrl, true,
            "http://fixture-sonarr.local:8989", "sonarr-secret", 8, "/tv");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings));
        var safe = await browser.GetStringAsync("/api/settings");
        check(!safe.Contains("sonarr-secret") && JsonDocument.Parse(safe).RootElement.Flag("hasSonarrKey"), "Sonarr credentials remain server-side");
        var movieBefore = JsonSerializer.Serialize(store.Snapshot);
        var response = await browser.PostAsJsonAsync("/api/sync?source=sonarr", new { });
        await SuccessAsync(response);
        check((await response.Content.ReadFromJsonAsync<JsonElement>()).Flag("sonarrSynced") && store.Tv.Library.Count == 1,
            "Sonarr collection sync succeeds without changing movie data");
        check(JsonSerializer.Serialize(store.Snapshot) == movieBefore, "Sonarr sync cannot write the movie snapshot");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/sync?source=tv-history", new { }));
        check(store.Tv.History.Count == 2 && store.Tv.History.All(x => x.SeriesTitle == "Fixture Series"), "TV history imports only owner episodes and preserves rewatches");
        check(store.Tv.History.All(x => x.TmdbId == 1 && x.TvdbId == 101 && x.Season == 2 && x.Episode == 3),
            "TV matches use parent show identifiers, not the episode TMDB GUID");
        check(JsonSerializer.Serialize(store.Snapshot) == movieBefore, "TV watch history cannot modify watched movie history, even when numeric IDs collide");
        var tvHistory = await browser.GetFromJsonAsync<JsonElement>("/api/tv/browse?view=tv-history&pageSize=10&query=Fixture");
        check(tvHistory.Items("plays").Count() == 2 && !tvHistory.Items("series").Any(), "TV history has a separate searchable episode API");
        var movies = await browser.GetFromJsonAsync<JsonElement>("/api/history");
        check(movies.Items("plays").All(x => !x.Text("title").Contains("Episode")), "Movie history never exposes TV episode records");
        var tvLibrary = await browser.GetFromJsonAsync<JsonElement>("/api/tv/browse?view=tv-library&pageSize=10");
        check(tvLibrary.Items("series").Single().Flag("hasWatchedEpisodes"), "A watched episode is labeled as partial series history, not a fully watched series");
        var tvTrending = await browser.GetFromJsonAsync<JsonElement>("/api/tv/browse?view=tv-trending");
        check(tvTrending.Items("series").Any() && fixture.TvRequests > 0, "TV discovery uses TMDB's TV namespace");
        fixture.FailPlex = true;
        fixture.FailSonarr = true;
        check((await browser.PostAsJsonAsync("/api/sync?source=tv-history", new { })).StatusCode == HttpStatusCode.BadGateway, "TV history reports a Plex outage");
        check(store.Tv.History.Count == 2 && store.Tv.HistorySyncedAt.HasValue, "TV history retains its last successful data during an outage");
        fixture.FailPlex = false;
        var partial = await browser.PostAsJsonAsync("/api/sync", new { });
        await SuccessAsync(partial);
        var partialResult = await partial.Content.ReadFromJsonAsync<JsonElement>();
        check(!partialResult.Flag("sonarrSynced") && partialResult.Flag("tvHistorySynced") && partialResult.Flag("radarrSynced"),
            "Sonarr failure does not block Radarr or Plex TV history");
        fixture.FailSonarr = false;
        await SuccessAsync(await browser.PostAsJsonAsync("/api/sonarr/2", new AddRequest()));
        check(fixture.SonarrAdds == 1 && !fixture.LastTvSearchNow, "Adding to Sonarr verifies the TVDB ID and selected destination without starting a search");
        check((await browser.PostAsJsonAsync("/api/sonarr/2", new AddRequest())).StatusCode == HttpStatusCode.BadRequest && fixture.SonarrAdds == 1,
            "Sonarr duplicate checks prevent a second write");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/sonarr/3", new AddRequest(true)));
        check(fixture.LastTvSearchNow && fixture.SonarrAdds == 2, "Only an explicit Sonarr search choice requests missing-episode downloads");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings with { SonarrRootFolder = "/wrong" }));
        check((await browser.PostAsJsonAsync("/api/sonarr/4", new AddRequest())).StatusCode == HttpStatusCode.BadRequest && fixture.SonarrAdds == 2,
            "Stale Sonarr destinations are rejected before any upstream write");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/settings", settings));
        var reloaded = new StateStore(services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());
        check(reloaded.Tv.History.Count == 2 && reloaded.Tv.Library.Count == 3 && reloaded.Settings.SonarrKey == "sonarr-secret",
            "TV snapshots and protected Sonarr connections survive reload separately from movies");
        check(store.Tv.Library.All(x => x.Facts is not null && x.Poster is not null), "Sonarr records with existing TMDB IDs still receive full TV metadata and artwork");
        var posterResponse = await browser.GetAsync("/api/tv/art/sonarr/1");
        check(posterResponse.IsSuccessStatusCode && posterResponse.Content.Headers.ContentType?.MediaType == "image/png", "Sonarr TVDB-hosted posters are served through an authenticated local proxy without exposing credentials");
        fixture.PlexParentMode = 2;
        await SuccessAsync(await browser.PostAsJsonAsync("/api/sync?source=tv-history", new { }));
        check(store.Tv.History.All(x => x.TmdbId == 1 && x.TvdbId == 101), "Episode metadata repairs histories missing both numeric and path parent fields");
        fixture.PlexParentMode = 1;
        var tvBeforeRemoval = JsonSerializer.Serialize(store.Tv);
        await store.SaveSnapshotAsync(store.Snapshot with { History = [.. store.Snapshot.History, new Watch("repeat", "1", "Movie 1", 2020, 1760000000, null, 1, true), new Watch("fanedit", "separate-copy", "Separate copy", 2020, 1760000000, null, 1, true)] }, default);
        var removal = await browser.PostAsJsonAsync("/api/history/remove", new HistoryRemovalRequest("1"));
        await SuccessAsync(removal);
        check((await removal.Content.ReadFromJsonAsync<JsonElement>()).Number("removed") == 2 && store.Snapshot.History.All(x => x.RatingKey != "1"), "Removing a movie deletes every recorded play of that exact source item");
        check(store.Snapshot.History.Any(x => x.RatingKey == "separate-copy") && JsonSerializer.Serialize(store.Tv) == tvBeforeRemoval, "Movie history removal preserves other copies with the same TMDB ID and all TV history");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/sync?source=history", new { }));
        check(store.Snapshot.History.All(x => x.RatingKey != "1"), "Movie removals persist across subsequent history imports");
        var exclusionsReloaded = new StateStore(services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>());
        check(exclusionsReloaded.HistoryExclusions.Count > 0 && exclusionsReloaded.Snapshot.History.All(x => x.RatingKey != "1"), "Removed movie exclusions survive service reload");
        await SuccessAsync(await browser.PostAsJsonAsync("/api/history/restore-removed", new { }));
        await SuccessAsync(await browser.PostAsJsonAsync("/api/sync?source=history", new { }));
        check(store.Snapshot.History.Any(x => x.RatingKey == "1"), "Explicit restoration allows removed source items to return on the next sync");
        var server = await browser.PostAsJsonAsync("/api/server", new ServerSettings(8097));
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            check(server.StatusCode == HttpStatusCode.BadRequest, "Docker deployments require host-port changes through Compose");
        else
        {
            await SuccessAsync(server);
            check((await server.Content.ReadFromJsonAsync<JsonElement>()).Flag("restartRequired"), "Changing the operating port saves a restart-required preference");
        }
        check((await browser.PostAsJsonAsync("/api/server", new ServerSettings(0))).StatusCode == HttpStatusCode.BadRequest,
            "Invalid operating ports are rejected");
        check((await browser.PostAsJsonAsync("/api/server", new ServerSettings(8090, "https://evil.example"))).StatusCode == HttpStatusCode.BadRequest,
            "Server bind preferences cannot inject arbitrary listener URLs");
    }

    /// <summary>Reports sanitized server errors when an integration fixture fails.</summary>
    private static async Task SuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
    }
}
