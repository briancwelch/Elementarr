using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Elementarr.Services;

public sealed class BrowseService(StateStore store, MetadataService metadata, DiscoveryService discovery)
{
    private readonly ConcurrentDictionary<string, RemoteFeed> feeds = new();
    private sealed class RemoteFeed
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly List<Movie> Movies = [];
        public int NextPage = 1;
        public int SourcePages = 500;
        public DateTimeOffset Created = DateTimeOffset.UtcNow;
        public bool Complete => NextPage > SourcePages;
    }

    /// <summary>Filters and sorts full local lists before paging; remote feeds accumulate server-side matches without page refreshes.</summary>
    public async Task<BrowsePage> BrowseAsync(BrowseOptions options, CancellationToken ct)
    {
        options.Validate();
        if (options.View is "history" or "issues") return await HistoryAsync(options, ct);
        if (options.View == "library")
        {
            var all = store.Snapshot.Library.Select(metadata.FromLibrary).Select(Annotate).ToList();
            var libraryNotice = all.Any(x => NeedsIndex(x, options)) ? "metadata" : null;
            var sorted = Sort(all.Where(x => Matches(x, options)), options).ToList();
            var page = await metadata.EnrichAsync(sorted.Skip((options.Page - 1) * options.PageSize).Take(options.PageSize), ct);
            return Page(page, [], options, sorted.Count, options.Page * options.PageSize < sorted.Count, libraryNotice);
        }
        foreach (var old in feeds.Where(x => x.Value.Created < DateTimeOffset.UtcNow.AddMinutes(-20)).Select(x => x.Key)) feeds.TryRemove(old, out _);
        if (feeds.Count >= 80) throw new ArgumentException("Too many active filter combinations. Try again in a few minutes.");
        var key = JsonSerializer.Serialize(options with { Page = 1, PageSize = 25 }) + store.Snapshot.SyncedAt;
        var feed = feeds.GetOrAdd(key, _ => new());
        await feed.Gate.WaitAsync(ct);
        try
        {
            var filtered = feed.Movies.Select(Annotate).Where(x => Matches(x, options)).ToList();
            var scan = 0;
            while (filtered.Count < options.Page * options.PageSize + 1 && !feed.Complete && scan++ < 5)
            {
                var source = await discovery.DiscoverAsync(options.View, options.Query, feed.NextPage, false, false, ct);
                feed.SourcePages = Math.Max(1, source.TotalPages);
                var known = feed.Movies.Select(x => x.TmdbId).ToHashSet();
                var fresh = source.Movies.Where(x => known.Add(x.TmdbId)).ToList();
                if (NeedsProviderFacts(options)) fresh = await metadata.EnrichAsync(fresh, ct, options.Country.Length > 0);
                feed.NextPage++;
                feed.Movies.AddRange(fresh);
                filtered = feed.Movies.Select(Annotate).Where(x => Matches(x, options)).ToList();
            }
            var sorted = Sort(filtered, options).ToList();
            var incomplete = !feed.Complete && sorted.Count < options.Page * options.PageSize;
            var notice = !feed.Complete
                ? "remote"
                : null;
            var page = await metadata.EnrichAsync(sorted.Skip((options.Page - 1) * options.PageSize).Take(options.PageSize), ct);
            return Page(page, [], options,
                sorted.Count, !feed.Complete || sorted.Count > options.Page * options.PageSize, notice, incomplete);
        }
        finally { feed.Gate.Release(); }
    }

    /// <summary>Requests additional provider facts only when filters or sorting need fields absent from TMDB list results.</summary>
    private static bool NeedsProviderFacts(BrowseOptions filter) => filter.ImdbMin.HasValue || filter.ImdbMax.HasValue
        || filter.RtMin.HasValue || filter.RtMax.HasValue || filter.LetterboxdMin.HasValue || filter.LetterboxdMax.HasValue
        || filter.RuntimeMin.HasValue || filter.RuntimeMax.HasValue || filter.Country.Length > 0 || filter.Certification.Length > 0
        || filter.Sort is "imdb" or "rt" or "letterboxd" or "runtime";

    /// <summary>Identifies missing indexed fields so local filtering does not imply complete metadata coverage.</summary>
    private static bool NeedsIndex(Movie movie, BrowseOptions filter) => filter.Country.Length > 0 && string.IsNullOrEmpty(movie.Facts?.Country)
        || filter.Certification.Length > 0 && string.IsNullOrEmpty(movie.Facts?.Certification)
        || (filter.Genres.Length > 0 || filter.ExcludeGenres.Length > 0) && (movie.Facts?.Genres.Count ?? 0) == 0
        || (filter.RuntimeMin.HasValue || filter.RuntimeMax.HasValue) && (movie.Facts?.Runtime ?? 0) == 0;

    /// <summary>Filters every stored play and enriches only the visible timeline page with cached artwork and ratings.</summary>
    private async Task<BrowsePage> HistoryAsync(BrowseOptions options, CancellationToken ct)
    {
        var all = store.Snapshot.History.Where(play => options.View != "issues" || !play.TmdbId.HasValue)
            .Where(play => options.Matched == "any" || (options.Matched == "yes") == play.TmdbId.HasValue)
            .Where(play => options.Completion == "any" || (options.Completion == "completed") == play.Completed)
            .Where(play => WatchDateMatches(play, options))
            .Select(play => new HistoryItem(play, play.TmdbId.HasValue ? Annotate(metadata.Decorate(new(play.TmdbId.Value,
                play.Title, play.Year, "", null, null, play.Facts?.Tmdb ?? 0, Facts: play.Facts))) : null))
            .Where(item => Matches(item.Movie ?? new(0, item.Play.Title, item.Play.Year, "", null, null, 0), options)).ToList();
        var field = options.Sort == "default" ? "watched" : options.Sort;
        var ordered = field == "watched" ? options.Order == "asc" ? all.OrderBy(x => x.Play.WatchedAt) : all.OrderByDescending(x => x.Play.WatchedAt)
            : Order(all, x => SortKey(x.Movie ?? new(0, x.Play.Title, x.Play.Year, "", null, null, 0), field), options.Order);
        var page = ordered.Skip((options.Page - 1) * options.PageSize).Take(options.PageSize).ToList();
        var enriched = (await metadata.EnrichAsync(page.Where(x => x.Movie is not null).Select(x => x.Movie!).DistinctBy(x => x.TmdbId), ct))
            .ToDictionary(x => x.TmdbId);
        page = page.Select(x => x.Movie is not null ? x with { Movie = enriched[x.Movie.TmdbId] } : x).ToList();
        return Page([], page, options, all.Count, options.Page * options.PageSize < all.Count);
    }

    /// <summary>Annotates every result from the current snapshot so cached pages reflect successful Radarr additions and corrections.</summary>
    public Movie Annotate(Movie movie)
    {
        var snapshot = store.Snapshot;
        var library = snapshot.Library.FirstOrDefault(x => x.TmdbId == movie.TmdbId);
        return metadata.Decorate(movie) with { InRadarr = library is not null, Available = library?.HasFile == true,
            Watched = snapshot.History.Any(x => x.Completed && x.TmdbId == movie.TmdbId) };
    }

    /// <summary>Applies each requested source-specific range, genre, language, date, and library-state filter.</summary>
    private static bool Matches(Movie movie, BrowseOptions filter)
    {
        var facts = movie.Facts;
        if (filter.Query.Length > 0 && filter.View is "library" or "history" or "issues"
            && !movie.Title.Contains(filter.Query, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.YearFrom.HasValue && movie.Year < filter.YearFrom || filter.YearTo.HasValue && movie.Year > filter.YearTo) return false;
        if (!Range(facts?.Imdb, filter.ImdbMin, filter.ImdbMax, filter.IncludeUnrated)
            || !Range(facts?.RottenTomatoes, filter.RtMin, filter.RtMax, filter.IncludeUnrated)
            || !Range(facts?.Letterboxd, filter.LetterboxdMin, filter.LetterboxdMax, filter.IncludeUnrated)
            || !Range(facts?.Tmdb, filter.TmdbMin, filter.TmdbMax, filter.IncludeUnrated)) return false;
        if (filter.VotesMin.HasValue && (facts?.Votes ?? 0) < filter.VotesMin) return false;
        if (filter.RuntimeMin.HasValue && (facts?.Runtime ?? 0) < filter.RuntimeMin
            || filter.RuntimeMax.HasValue && (facts?.Runtime is null or 0 || facts.Runtime > filter.RuntimeMax)) return false;
        var genres = (facts?.Genres ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var include = filter.Genres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exclude = filter.ExcludeGenres.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (include.Length > 0 && (filter.GenreMode == "all" ? !include.All(genres.Contains) : !include.Any(genres.Contains))) return false;
        if (exclude.Any(genres.Contains)) return false;
        if (filter.Language.Length > 0 && !string.Equals(facts?.Language, filter.Language, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.Country.Length > 0 && !string.Equals(facts?.Country, filter.Country, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.Certification.Length > 0 && !string.Equals(facts?.Certification, filter.Certification, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.ReleasedFrom.Length > 0 && string.CompareOrdinal(movie.ReleaseDate, filter.ReleasedFrom) < 0
            || filter.ReleasedTo.Length > 0 && (string.IsNullOrEmpty(movie.ReleaseDate) || string.CompareOrdinal(movie.ReleaseDate[..Math.Min(10, movie.ReleaseDate.Length)], filter.ReleasedTo) > 0)) return false;
        return State(movie.InRadarr, filter.Owned) && State(movie.Watched, filter.Watched) && State(movie.Available, filter.Available);
    }

    /// <summary>Excludes unknown scores only when a range is active and the user has not opted to include unrated items.</summary>
    private static bool Range(double? value, double? min, double? max, bool includeUnrated) =>
        !min.HasValue && !max.HasValue || (!value.HasValue ? includeUnrated : (!min.HasValue || value >= min) && (!max.HasValue || value <= max));

    /// <summary>Matches tri-state yes/no/all filters consistently.</summary>
    private static bool State(bool value, string filter) => filter == "any" || value == (filter == "yes");

    /// <summary>Filters calendar watch dates in the browser's requested UTC offset.</summary>
    private static bool WatchDateMatches(Watch watch, BrowseOptions filter)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(watch.WatchedAt).UtcDateTime.AddMinutes(filter.UtcOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return (filter.WatchedFrom.Length == 0 || string.CompareOrdinal(date, filter.WatchedFrom) >= 0)
            && (filter.WatchedTo.Length == 0 || string.CompareOrdinal(date, filter.WatchedTo) <= 0);
    }

    /// <summary>Chooses a typed sorting key while treating unavailable ratings as missing rather than zero.</summary>
    private static IComparable? SortKey(Movie movie, string field) => field switch
    {
        "title" => movie.Title.ToUpperInvariant(), "year" => movie.Year, "tmdb" => movie.Facts?.Tmdb,
        "imdb" => movie.Facts?.Imdb, "rt" => movie.Facts?.RottenTomatoes, "letterboxd" => movie.Facts?.Letterboxd,
        "runtime" => movie.Facts?.Runtime > 0 ? movie.Facts.Runtime : null, "votes" => movie.Facts?.Votes,
        "popularity" => movie.Facts?.Popularity, _ => null
    };

    /// <summary>Keeps missing sorting values at the end in both ascending and descending order.</summary>
    private static IOrderedEnumerable<T> Order<T>(IEnumerable<T> values, Func<T, IComparable?> key, string direction) =>
        direction == "asc" ? values.OrderBy(x => key(x) is null).ThenBy(key) : values.OrderBy(x => key(x) is null).ThenByDescending(key);

    /// <summary>Preserves provider ranking unless the user explicitly selects a list sorting option.</summary>
    private static IEnumerable<Movie> Sort(IEnumerable<Movie> movies, BrowseOptions filter) =>
        filter.Sort == "default" ? movies : Order(movies, x => SortKey(x, filter.Sort), filter.Order);

    /// <summary>Returns exact local counts and explicit partial-result state for progressively searched remote feeds.</summary>
    private static BrowsePage Page(List<Movie> movies, List<HistoryItem> plays, BrowseOptions filter, int count,
        bool more, string? notice = null, bool incomplete = false) => new(movies, plays, filter.Page, filter.PageSize,
            Math.Max(1, (int)Math.Ceiling(count / (double)filter.PageSize)), count, more, notice, incomplete);
}
