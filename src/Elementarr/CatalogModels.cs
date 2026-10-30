using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Elementarr;

public sealed record MovieFacts
{
    public List<string> Genres { get; init; } = [];
    public int Runtime { get; init; }
    public string Language { get; init; } = "";
    public string Country { get; init; } = "";
    public string Certification { get; init; } = "";
    public string ImdbId { get; init; } = "";
    public double? Imdb { get; init; }
    public double? RottenTomatoes { get; init; }
    public double? Letterboxd { get; init; }
    public double? Tmdb { get; init; }
    public int Votes { get; init; }
    public string? Backdrop { get; init; }
    public string? Logo { get; init; }
    public string Tagline { get; init; } = "";
    public string Director { get; init; } = "";
    public List<CastMember> Cast { get; init; } = [];
    public List<string> Countries { get; init; } = [];
    public List<string> Studios { get; init; } = [];
    public string? Trailer { get; init; }
    public double Popularity { get; init; }
    public string RatingOrigin { get; init; } = "";
    public DateTimeOffset? UpdatedAt { get; init; }
    public bool Detailed { get; init; }
    public DateTimeOffset? DetailedAt { get; init; }
}

public sealed record CastMember(string Name, string Character, string? Photo);
public sealed record HistoryItem(Watch Play, Movie? Movie);
public sealed record BrowsePage(List<Movie> Movies, List<HistoryItem> Plays, int Page, int PageSize,
    int TotalPages, int TotalResults, bool HasMore, string? Notice = null, bool Incomplete = false);
public sealed record HistoryMatchRequest(string RatingKey, int TmdbId);
public sealed record LocalRating(int TmdbId, double? Imdb, double? RottenTomatoes, double? Letterboxd, double? Tmdb);
public sealed record RatingImport(List<LocalRating> Movies);
public sealed record SearchStatus(int CommandId, string Status);

public sealed record BrowseOptions
{
    public string View { get; init; } = "for-you";
    public string Query { get; init; } = "";
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
    public string Sort { get; init; } = "default";
    public string Order { get; init; } = "desc";
    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }
    public string Genres { get; init; } = "";
    public string ExcludeGenres { get; init; } = "";
    public string GenreMode { get; init; } = "any";
    public double? ImdbMin { get; init; }
    public double? ImdbMax { get; init; }
    public double? RtMin { get; init; }
    public double? RtMax { get; init; }
    public double? LetterboxdMin { get; init; }
    public double? LetterboxdMax { get; init; }
    public double? TmdbMin { get; init; }
    public double? TmdbMax { get; init; }
    public int? VotesMin { get; init; }
    public int? RuntimeMin { get; init; }
    public int? RuntimeMax { get; init; }
    public string Language { get; init; } = "";
    public string Country { get; init; } = "";
    public string Certification { get; init; } = "";
    public string Owned { get; init; } = "any";
    public string Watched { get; init; } = "any";
    public string Available { get; init; } = "any";
    public string Completion { get; init; } = "any";
    public string Matched { get; init; } = "any";
    public string WatchedFrom { get; init; } = "";
    public string WatchedTo { get; init; } = "";
    public string ReleasedFrom { get; init; } = "";
    public string ReleasedTo { get; init; } = "";
    public bool IncludeUnrated { get; init; }
    public int UtcOffset { get; init; }

    /// <summary>Binds only declared filter properties and rejects malformed scalar query values.</summary>
    public static BrowseOptions FromQuery(IQueryCollection query)
    {
        var json = new JsonObject();
        foreach (var property in typeof(BrowseOptions).GetProperties())
        {
            if (!query.TryGetValue(property.Name, out var supplied)) continue;
            if (supplied.Count != 1) throw new ArgumentException("Use one value per filter.");
            var value = supplied.ToString();
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (value.Length == 0 && type != typeof(string)) continue;
            if (type == typeof(string)) json[property.Name] = value;
            else if (type == typeof(bool) && bool.TryParse(value, out var boolean)) json[property.Name] = boolean;
            else if (type == typeof(int) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) json[property.Name] = integer;
            else if (type == typeof(double) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) json[property.Name] = number;
            else throw new ArgumentException("A filter has an invalid value.");
        }
        return json.Deserialize<BrowseOptions>() ?? new();
    }

    /// <summary>Validates bounded query fields before they reach metadata providers or pagination.</summary>
    public void Validate()
    {
        if (Page is < 1 or > 50_000 || PageSize is not (10 or 25 or 50 or 100) || UtcOffset is < -840 or > 840)
            throw new ArgumentException("Choose a valid page and 10, 25, 50, or 100 items per page.");
        if (View is not ("for-you" or "trending" or "upcoming" or "top-rated" or "library" or "history" or "issues"))
            throw new ArgumentException("Choose a valid movie view.");
        if (Query.Length > 200 || Genres.Length > 400 || ExcludeGenres.Length > 400
            || Language.Length > 400 || Country.Length > 400 || Certification.Length > 400)
            throw new ArgumentException("A filter value is too long.");
        ValidateSelections(Language, 20);
        ValidateSelections(Country, 2);
        ValidateSelections(Certification, 12);
        if (YearFrom is < 1870 or > 2200 || YearTo is < 1870 or > 2200 || YearFrom > YearTo
            || RuntimeMin is < 0 or > 1000 || RuntimeMax is < 0 or > 1000 || RuntimeMin > RuntimeMax
            || VotesMin is < 0 or > 100_000_000)
            throw new ArgumentException("Check the year, runtime, and vote ranges.");
        foreach (var (min, max, scale) in new[] { (ImdbMin, ImdbMax, 10d), (RtMin, RtMax, 100d),
            (LetterboxdMin, LetterboxdMax, 5d), (TmdbMin, TmdbMax, 10d) })
            if ((min.HasValue && (!double.IsFinite(min.Value) || min < 0 || min > scale))
                || (max.HasValue && (!double.IsFinite(max.Value) || max < 0 || max > scale)) || min > max)
                throw new ArgumentException("Check the rating ranges.");
        foreach (var date in new[] { WatchedFrom, WatchedTo, ReleasedFrom, ReleasedTo })
            if (date.Length > 0 && !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) throw new ArgumentException("Use a valid date filter.");
        foreach (var state in new[] { Owned, Watched, Available, Matched })
            if (state is not ("any" or "yes" or "no")) throw new ArgumentException("Choose a valid status filter.");
        if (Completion is not ("any" or "completed" or "partial") || GenreMode is not ("any" or "all")
            || Order is not ("asc" or "desc") || Sort is not ("default" or "title" or "year" or "tmdb" or "imdb"
                or "rt" or "letterboxd" or "runtime" or "votes" or "popularity" or "watched"))
            throw new ArgumentException("Choose valid filter and sorting options.");
    }

    /// <summary>Accepts bounded comma-separated chip selections while retaining the previous per-value limits.</summary>
    private static void ValidateSelections(string value, int maximumLength)
    {
        if (value.Length == 0) return;
        var selections = value.Split(',', StringSplitOptions.TrimEntries);
        if (selections.Length > 32 || selections.Any(item => item.Length == 0 || item.Length > maximumLength))
            throw new ArgumentException("Choose up to 32 valid values per filter.");
    }
}
