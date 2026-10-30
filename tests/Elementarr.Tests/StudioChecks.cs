using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Elementarr;
using Elementarr.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class StudioChecks
{
    /// <summary>Verifies the new browse, rating, rich metadata, manual correction, search, and administration contracts offline.</summary>
    public static async Task RunAsync(HttpClient browser, MediaFixture fixture, IServiceProvider services, Action<bool, string> check)
    {
        var store = services.GetRequiredService<StateStore>();
        var library = Enumerable.Range(1, 32).Select(id => new LibraryMovie(id, "tt" + id, "Movie " + id.ToString("D2"),
            1980 + id, "Synopsis", "/poster.jpg", id % 2 == 0, true,
            new MovieFacts { Genres = id % 2 == 1 ? ["Action", "Drama"] : ["Comedy"], Runtime = 80 + id * 2,
                Imdb = id == 32 ? null : 5 + id / 10d, RottenTomatoes = 40 + id, Tmdb = 6 + id % 10 / 10d,
                Votes = id * 100, Language = "en", Country = "US", Certification = "PG-13", UpdatedAt = DateTimeOffset.UtcNow })).ToList();
        var noon = DateTimeOffset.Parse("2026-10-28T12:00:00Z").ToUnixTimeSeconds();
        await store.SaveSnapshotAsync(store.Snapshot with { Library = library, HistorySource = "plex", History =
            [new("p1", "3", "Movie 03", 1983, noon, null, 3, true), new("p2", "4", "Unknown Movie", 2001, noon - 86400, null, null, true),
             new("p3", "5", "Movie 05", 1985, noon - 86400 * 2, 30, 5, false)] }, default);
        var page = await GetAsync(browser, "browse?view=library&page=2&pageSize=10&sort=year&order=asc");
        check(page.Number("totalResults") == 32 && page.Items("movies").Count() == 10
            && page.Items("movies").First().Number("tmdbId") == 11, "Collection pagination follows global sorting and supports ten-item pages");
        var years = await GetAsync(browser, "browse?view=library&pageSize=25&yearFrom=2000&yearTo=2005");
        check(years.Number("totalResults") == 6 && years.Items("movies").All(x => x.Number("year") is >= 2000 and <= 2005),
            "Year ranges filter the whole collection before pagination");
        var genres = await GetAsync(browser, "browse?view=library&pageSize=25&genres=Action,Drama&genreMode=all&excludeGenres=Comedy");
        check(genres.Number("totalResults") == 16, "Genre inclusion, all-of matching, and exclusions combine correctly");
        var ratings = await GetAsync(browser, "browse?view=library&pageSize=25&imdbMin=7&rtMin=60&tmdbMin=6.4&runtimeMin=120&votesMin=2000");
        check(ratings.Items("movies").Any() && ratings.Items("movies").All(x => x.GetProperty("facts").Number("imdb") >= 7
            && x.GetProperty("facts").Number("rottenTomatoes") >= 60), "Native-scale rating, runtime, and vote filters combine without unlabeled scores");
        var unknown = await GetAsync(browser, "browse?view=library&pageSize=100&imdbMin=8&includeUnrated=true&sort=imdb&order=desc");
        check(unknown.Items("movies").Last().Number("tmdbId") == 32 && unknown.Items("movies").First().Number("tmdbId") == 31,
            "Unknown ratings can be included and always sort last");
        var descending = await GetAsync(browser, "browse?view=library&pageSize=50&sort=title&order=desc");
        check(descending.Items("movies").First().Text("title") == "Movie 32", "Title sorting supports descending order across the full list");
        var state = await GetAsync(browser, "browse?view=library&pageSize=100&available=yes&watched=no");
        check(state.Items("movies").Count() == 16 && state.Items("movies").All(x => x.Flag("available")),
            "Availability and watched filters use current local state");
        var international = await GetAsync(browser, "browse?view=library&pageSize=100&country=US&language=en&certification=PG-13");
        check(international.Number("totalResults") == 32, "Language, production country, and certification filters work together");
        foreach (var size in new[] { 10, 25, 50, 100 })
            check((await browser.GetAsync($"/api/browse?view=library&pageSize={size}")).IsSuccessStatusCode,
                $"The {size}-item page size is accepted");
        check((await browser.GetAsync("/api/browse?view=library&pageSize=20")).StatusCode == HttpStatusCode.BadRequest,
            "Unsupported page sizes are rejected");
        check((await browser.GetAsync("/api/browse?view=library&imdbMin=11")).StatusCode == HttpStatusCode.BadRequest
            && (await browser.GetAsync("/api/browse?view=library&sort=invalid")).StatusCode == HttpStatusCode.BadRequest,
            "Malformed rating and sorting filters are rejected");
        var history = await GetAsync(browser, "browse?view=history&pageSize=10&sort=watched&order=asc&completion=completed");
        check(history.Items("plays").Count() == 2 && history.Items("plays").First().GetProperty("play").Text("id") == "p2",
            "Timeline sorting and play-completion filters preserve chronological order");
        var watchedDate = await GetAsync(browser, "browse?view=history&pageSize=10&watchedFrom=2026-10-28&watchedTo=2026-10-28&utcOffset=-420");
        check(watchedDate.Items("plays").Single().GetProperty("play").Text("id") == "p1", "Watch-date filtering respects the browser UTC offset");
        var affected = await GetAsync(browser, "browse?view=issues&pageSize=10");
        check(affected.Number("totalResults") == 1 && affected.Items("plays").Single().GetProperty("play").Text("ratingKey") == "4",
            "Affected-items view identifies the actual unmatched history item");
        var detail = await GetAsync(browser, "movies/42");
        var facts = detail.GetProperty("facts");
        check(detail.Text("poster") == "/poster.jpg" && facts.Text("backdrop") == "/backdrop.jpg" && facts.Text("logo") == "/logo.png"
            && facts.Items("cast").Count() == 1 && facts.Text("director") == "Fixture Director", "Movie detail metadata contains poster, backdrop, logo, cast, and director");
        check(facts.Number("imdb") == 8.4 && facts.Number("rottenTomatoes") == 82 && facts.Number("tmdb") == 7.7
            && facts.GetProperty("letterboxd").ValueKind == JsonValueKind.Null, "Scores keep their true source scales and missing Letterboxd stays missing");
        var requestsBeforeRefresh = fixture.MovieDetailRequests;
        var metadata = services.GetRequiredService<MetadataService>();
        await metadata.GetAsync(42, true, default, force: true);
        await metadata.GetAsync(42, true, default, force: true);
        check(fixture.MovieDetailRequests == requestsBeforeRefresh + 2,
            "Detailed metadata refreshes do not retain duplicate raw movie responses in memory");
        var plex = MetadataService.ParsePlex(JsonDocument.Parse("{\"rating\":8.2,\"ratingImage\":\"rottentomatoes://image.rating.ripe\",\"audienceRating\":7.3,\"audienceRatingImage\":\"imdb://image.rating\"}").RootElement);
        check(plex.RottenTomatoes == 82 && plex.Imdb == 7.3, "Plex review-source identifiers are recognized before converting percentages");
        var mixedPlex = MetadataService.ParsePlex(JsonDocument.Parse("{\"rating\":8.2,\"ratingImage\":\"rottentomatoes://image.rating.ripe\",\"audienceRating\":9.1,\"audienceRatingImage\":\"rottentomatoes://image.rating.upright\"}").RootElement);
        check(mixedPlex.RottenTomatoes == 82, "Rotten Tomatoes audience scores cannot overwrite the critics score");
        var zeroScore = MetadataService.ParseRadarr(JsonDocument.Parse("{\"tmdbId\":1,\"ratings\":{\"rottenTomatoes\":{\"value\":0,\"votes\":10}}}").RootElement);
        var invalidScore = MetadataService.ParseRadarr(JsonDocument.Parse("{\"tmdbId\":1,\"ratings\":{\"rottenTomatoes\":{\"value\":\"NaN\",\"votes\":10}}}").RootElement);
        check(zeroScore.Facts?.RottenTomatoes == 0 && invalidScore.Facts?.RottenTomatoes is null,
            "Genuine zero-percent critics scores stay valid while malformed scores remain unavailable");
        check(JsonDocument.Parse("{\"value\":\"NaN\"}").RootElement.Number("value") == 0,
            "Non-finite upstream numeric values cannot poison persisted metadata");
        var imported = await browser.PostAsJsonAsync("/api/ratings/import", new RatingImport([new(1, null, null, 4.8, null)]));
        check(imported.IsSuccessStatusCode, "Explicit local rating imports do not require an additional external provider");
        var letterboxd = await GetAsync(browser, "browse?view=library&pageSize=25&letterboxdMin=4.5");
        check(letterboxd.Items("movies").Single().Number("tmdbId") == 1, "Letterboxd filtering becomes functional for imported scores");
        check((await browser.PostAsJsonAsync("/api/ratings/import", new RatingImport([new(1, null, null, 6, null)]))).StatusCode == HttpStatusCode.BadRequest,
            "Imported ratings outside a source scale are rejected");
        var match = await browser.PostAsJsonAsync("/api/history/match", new HistoryMatchRequest("4", 42));
        check(match.IsSuccessStatusCode && store.Snapshot.History.Single(x => x.RatingKey == "4").TmdbId == 42,
            "Manual matching corrects the actual history item");
        var afterMatch = await GetAsync(browser, "state");
        check(afterMatch.Number("unmatchedCount") == 0, "Affected-item counts update immediately after a correction");
        var sync = await browser.PostAsJsonAsync("/api/sync?source=history", new { });
        check(sync.IsSuccessStatusCode && store.Snapshot.History.Single(x => x.RatingKey == "4").TmdbId == 42,
            "Explicit history corrections survive a subsequent source sync");
        var adds = fixture.Adds;
        var search = await browser.PostAsJsonAsync("/api/radarr/1/search", new { });
        var command = await search.Content.ReadFromJsonAsync<JsonElement>();
        check(search.IsSuccessStatusCode && command.Number("commandId") > 0 && fixture.Adds == adds,
            "Immediate search for an existing Radarr movie queues a command without another add");
        var commandStatus = await GetAsync(browser, "radarr/commands/" + command.Number("commandId"));
        check(commandStatus.Text("status") == "completed", "Radarr command status can be reported independently of download completion");
        var addSearch = await browser.PostAsJsonAsync("/api/radarr/6/search", new { });
        check(addSearch.IsSuccessStatusCode && fixture.Adds == adds + 1 && !fixture.LastSearchNow && fixture.SearchCommands == 2,
            "Immediate search adds an absent movie once and then explicitly sends MoviesSearch");
        fixture.FailSearch = true;
        check((await browser.PostAsJsonAsync("/api/radarr/1/search", new { })).StatusCode == HttpStatusCode.BadGateway,
            "A rejected Radarr search is reported as an error rather than success");
        fixture.FailSearch = false;
        var appearance = new UiSettings { Accent = "#ff8800", BodyFont = "Inter", GoogleFonts = true, PageSize = 50,
            Texts = new() { ["nav.history"] = "My movie timeline" } };
        check((await browser.PostAsJsonAsync("/api/appearance", appearance)).IsSuccessStatusCode, "Appearance and wording can be saved separately from API credentials");
        check((await browser.PostAsJsonAsync("/api/appearance", appearance with { BodyFont = "x;url(https://invalid.example)" })).StatusCode == HttpStatusCode.BadRequest,
            "Font configuration cannot inject CSS or arbitrary stylesheet URLs");
        var config = services.GetRequiredService<IConfiguration>();
        check(new UiService(config).Settings.Texts["nav.history"] == "My movie timeline", "Appearance and wording survive a service reload");
        var reloadedMetadata = new MetadataService(store, services.GetRequiredService<MediaApi>(), config);
        check((await reloadedMetadata.GetAsync(42, true, default)).Facts?.Logo == "/logo.png", "Rich metadata cache survives a service reload");
        var reloadedLibrary = reloadedMetadata.FromLibrary(library[0]);
        check(reloadedLibrary.Facts?.Letterboxd == 4.8, "Explicit local ratings survive a service reload");
        await store.SaveSnapshotAsync(store.Snapshot with { History = [], Library =
            [new(4040, "", "Unavailable Fixture", 2000, "", null, false, true)] }, default);
        await browser.PostAsJsonAsync("/api/metadata/refresh", new { });
        JsonElement index = default;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            index = await GetAsync(browser, "metadata/status");
            if (!index.Flag("running")) break;
            await Task.Delay(20);
        }
        check(!index.Flag("running") && index.Number("failed") == 1
            && index.Items("unavailable").Single().Text("name") == "Unavailable Fixture",
            "Metadata refresh failures identify the actual affected movie and complete without discarding its library record");
        var beforeRepair = store.Snapshot;
        var beforeTvRepair = JsonSerializer.Serialize(store.Tv);
        await store.SaveSnapshotAsync(beforeRepair with { Library = [new(11, "", "Repair me", 2020, "", null, true, true),
            new(12, "", "Ambiguous", 2020, "", null, true, true), new(13, "", "Ambiguous", 2020, "", null, true, true)],
            History = [new("repair", "repair", "Repair me", 2020, 1760000000, null, null, true),
                new("explicit", "explicit", "Repair me", 2020, 1760000000, null, 42, true),
                new("ambiguous", "ambiguous", "Ambiguous", 2020, 1760000000, null, null, true)] }, default);
        check((await browser.PostAsJsonAsync("/api/metadata/refresh-matches", new { })).IsSuccessStatusCode, "Movie metadata and match repair can be queued from Connections");
        for (var attempt = 0; attempt < 50 && (await GetAsync(browser, "metadata/status")).Flag("running"); attempt++) await Task.Delay(20);
        check(store.Snapshot.History.Single(x => x.Id == "repair").TmdbId == 11 && store.Snapshot.History.Single(x => x.Id == "explicit").TmdbId == 42
            && store.Snapshot.History.Single(x => x.Id == "ambiguous").TmdbId is null && store.Snapshot.History.Count == 3
            && JsonSerializer.Serialize(store.Tv) == beforeTvRepair, "Movie match refresh repairs unique title/year matches while preserving explicit matches, ambiguous plays, counts and TV history");
        await store.SaveSnapshotAsync(beforeRepair, default);
        using var anonymous = new HttpClient { BaseAddress = browser.BaseAddress };
        check((await anonymous.GetAsync("/api/browse?view=history")).StatusCode == HttpStatusCode.Unauthorized
            && (await anonymous.PostAsJsonAsync("/api/appearance", appearance)).StatusCode == HttpStatusCode.Unauthorized,
            "History browsing and appearance writes require an authenticated session");
    }

    /// <summary>Reads a JSON result and fails with the sanitized response when an HTTP contract is broken.</summary>
    private static async Task<JsonElement> GetAsync(HttpClient browser, string path)
    {
        var response = await browser.GetAsync("/api/" + path);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
