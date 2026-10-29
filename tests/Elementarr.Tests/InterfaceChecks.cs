using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Elementarr;
using Elementarr.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class InterfaceChecks
{
    /// <summary>Checks persisted UI policies, source-scoped episode views, avatar isolation, and installation release identity.</summary>
    internal static async Task RunAsync(HttpClient browser, MediaFixture fixture, IServiceProvider services, Action<bool, string> check)
    {
        var defaults = await browser.GetFromJsonAsync<UiSettings>("/api/appearance/defaults");
        check(defaults!.BadgeRadius == 4 && defaults.ToastDuration == 5 && defaults.NavigationFontScale == 100,
            "Fresh installations use four-pixel badges, five-second notifications, and unmodified typography scales");
        var original = services.GetRequiredService<UiService>().Settings;
        var changed = original with { BadgeRadius = 0, ToastDuration = 12, NavigationFontScale = 150, CaptionFontScale = 125, ToastFontScale = 175 };
        check((await browser.PostAsJsonAsync("/api/appearance", changed)).IsSuccessStatusCode,
            "The Plex owner can save independent typography, square badges, and notification duration");
        var reloaded = new UiService(services.GetRequiredService<IConfiguration>()).Settings;
        check(reloaded.BadgeRadius == 0 && reloaded.ToastDuration == 12 && reloaded.NavigationFontScale == 150 && reloaded.CaptionFontScale == 125,
            "New appearance preferences survive service reload without losing valid zero-radius values");
        foreach (var invalid in new[] { changed with { ToastDuration = 0 }, changed with { ToastDuration = 61 }, changed with { BadgeRadius = -1 }, changed with { CardFontScale = 176 } })
            check((await browser.PostAsJsonAsync("/api/appearance", invalid)).StatusCode == HttpStatusCode.BadRequest,
                "Appearance policies reject unsupported duration, radius, or typography values");
        await browser.PostAsJsonAsync("/api/appearance", original);
        var session = await browser.GetFromJsonAsync<JsonElement>("/api/session");
        var changes = await browser.GetFromJsonAsync<JsonElement>("/api/changelog");
        check(changes.Text("version") == session.Text("version") && changes.Items("releases").First().Text("version") == UpdateService.CurrentVersion,
            "Sidebar version and bundled changelog use the running installation release");
        using var anonymous = new HttpClient { BaseAddress = browser.BaseAddress };
        check((await anonymous.GetAsync("/api/plex/avatar")).StatusCode == HttpStatusCode.Unauthorized
            && (await anonymous.GetAsync("/api/changelog")).StatusCode == HttpStatusCode.Unauthorized,
            "Anonymous clients cannot access the owner's avatar or authenticated changelog");
        var avatar = await browser.GetAsync("/api/plex/avatar");
        check(avatar.IsSuccessStatusCode && avatar.Content.Headers.ContentType?.MediaType == "image/png" && (await avatar.Content.ReadAsByteArrayAsync()).Length > 0,
            "Plex profile pictures are served through the credential-free authenticated avatar proxy");
        var requests = fixture.AvatarRequests;
        await browser.GetAsync("/api/plex/avatar");
        check(fixture.AvatarRequests == requests, "Avatar caching avoids repeated account/image requests");
        fixture.PlexThumb = "https://evil.example/avatar.png";
        var avatars = new PlexAvatarService(services.GetRequiredService<StateStore>(), services.GetRequiredService<MediaApi>(), fixture);
        check(await avatars.GetAsync(default) is null && fixture.AvatarRequests == requests,
            "The Plex image proxy rejects non-Plex destinations instead of fetching arbitrary URLs");
        fixture.PlexThumb = "https://images.plex.tv/avatars/redirect";
        avatars = new PlexAvatarService(services.GetRequiredService<StateStore>(), services.GetRequiredService<MediaApi>(), fixture);
        check(await avatars.GetAsync(default) is not null, "Plex avatar redirects to its HTTPS asset hosts work without account-token forwarding");
        fixture.PlexThumb = "https://images.plex.tv/avatars/unsafe";
        avatars = new PlexAvatarService(services.GetRequiredService<StateStore>(), services.GetRequiredService<MediaApi>(), fixture);
        check(await avatars.GetAsync(default) is null, "Avatar redirects cannot escape the Plex host allowlist");
        fixture.PlexThumb = "https://images.plex.tv/avatars/fixture.png";

        var store = services.GetRequiredService<StateStore>();
        var movies = JsonSerializer.Serialize(store.Snapshot);
        var upcoming = await browser.GetFromJsonAsync<JsonElement>("/api/tv/browse?view=tv-upcoming&pageSize=10");
        var episodes = upcoming.Items("series").ToArray();
        check(episodes.Length == 2 && episodes[0].GetProperty("airingEpisode").Number("episode") == 9
            && episodes.All(x => x.GetProperty("airingEpisode").Text("airsAt").Length > 0),
            "The TV calendar lists actual future Sonarr episodes in air-time order and excludes past/out-of-window rows");
        var searched = await browser.GetFromJsonAsync<JsonElement>("/api/tv/browse?view=tv-upcoming&pageSize=10&query=Later");
        check(searched.Items("series").Count() == 1, "Upcoming episode search includes episode titles");
        var originalTv = store.Tv;
        try
        {
            await store.SaveTvAsync(originalTv with { History = [.. originalTv.History, new TvWatch("unmatched-ui", "999", "", "Lost Series", "Lost Episode", 1, 2, 100, null, null)] }, default);
            var issues = await browser.GetFromJsonAsync<JsonElement>("/api/tv/browse?view=tv-issues&pageSize=10&query=Lost");
            check(issues.Items("plays").Single().Text("id") == "unmatched-ui" && !issues.Items("series").Any(),
                "Affected TV history is independently searchable and contains only unresolved episode watches");
            check(JsonSerializer.Serialize(store.Snapshot) == movies, "TV calendar and affected-history browsing never modify movie history");
        }
        finally { await store.SaveTvAsync(originalTv, default); }
        foreach (var asset in new[] { "brand.svg", "favicon.ico" })
            check((await browser.GetAsync("/" + asset)).IsSuccessStatusCode, "Shared brand and favicon assets are served by the packaged application");
    }
}
