namespace Elementarr.Services;

public sealed class DiscoveryService(StateStore store, MediaApi api)
{
    /// <summary>Builds movie feeds from TMDB and annotates them with the current local library and watch history.</summary>
    public async Task<MoviePage> DiscoverAsync(string view, string? query, int page, bool hideOwned,
        bool hideWatched, CancellationToken ct)
    {
        if (page is < 1 or > 500) throw new ArgumentException("Choose a page between 1 and 500.");
        if (query?.Length > 200) throw new ArgumentException("Search must be 200 characters or fewer.");
        var settings = store.Settings;
        var snapshot = store.Snapshot;
        var watched = snapshot.History.Where(x => x.Completed && x.TmdbId.HasValue).Select(x => x.TmdbId!.Value).ToHashSet();
        var owned = snapshot.Library.Select(x => x.TmdbId).ToHashSet();
        var available = snapshot.Library.Where(x => x.HasFile).Select(x => x.TmdbId).ToHashSet();
        var result = new List<Movie>();
        int totalPages = 1;
        string? notice = null;
        if (view == "for-you" && string.IsNullOrWhiteSpace(query))
        {
            var seeds = snapshot.History.Where(x => x.Completed && x.TmdbId.HasValue)
                .Select(x => (Id: x.TmdbId!.Value, x.Title))
                .Concat(snapshot.Library.Select(x => (Id: x.TmdbId, x.Title))).DistinctBy(x => x.Id).Take(6).ToList();
            if (seeds.Count == 0)
            {
                notice = "Trending movies are shown until your first library and watch history sync.";
                view = "trending";
            }
            else
            {
                foreach (var seed in seeds)
                {
                    var recommendations = await api.TmdbAsync(settings, $"movie/{seed.Id}/recommendations?include_adult=false&page={page}", ct);
                    totalPages = Math.Max(totalPages, Math.Min(500, (int)recommendations.Number("total_pages")));
                    result.AddRange(recommendations.Items("results").Where(x => !x.Flag("adult"))
                        .Select(x => ConvertMovie(x, $"Because of {seed.Title}")));
                }
            }
        }
        if (result.Count == 0 && (view != "for-you" || !string.IsNullOrWhiteSpace(query)))
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var end = DateTime.UtcNow.AddMonths(12).ToString("yyyy-MM-dd");
            var path = !string.IsNullOrWhiteSpace(query)
                ? $"search/movie?query={Uri.EscapeDataString(query.Trim())}&include_adult=false&page={page}"
                : view switch
                {
                    "trending" => $"trending/movie/week?page={page}",
                    "upcoming" => $"discover/movie?include_adult=false&region={settings.Region}&primary_release_date.gte={today}&primary_release_date.lte={end}&sort_by=popularity.desc&page={page}",
                    "top-rated" => $"discover/movie?include_adult=false&vote_count.gte=300&sort_by=vote_average.desc&page={page}",
                    _ => throw new ArgumentException("Choose a valid discovery feed.")
                };
            var json = await api.TmdbAsync(settings, path, ct);
            totalPages = Math.Max(1, Math.Min(500, (int)json.Number("total_pages")));
            result.AddRange(json.Items("results").Where(x => !x.Flag("adult")).Select(x => ConvertMovie(x, "")));
        }
        result = result.DistinctBy(x => x.TmdbId).Where(x => x.TmdbId > 0)
            .Where(x => !hideOwned || !owned.Contains(x.TmdbId)).Where(x => !hideWatched || !watched.Contains(x.TmdbId))
            .Select(x => x with { InRadarr = owned.Contains(x.TmdbId), Available = available.Contains(x.TmdbId), Watched = watched.Contains(x.TmdbId) }).ToList();
        return new(result, page, totalPages, notice);
    }

    /// <summary>Projects only displayable movie fields from TMDB, including a validated poster path.</summary>
    private static Movie ConvertMovie(System.Text.Json.JsonElement json, string reason)
    {
        return MetadataService.ParseTmdb(json) with { Reason = reason };
    }
}
