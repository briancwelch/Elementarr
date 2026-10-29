using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Elementarr.Services;

public sealed partial class MetadataService
{
    public static readonly IReadOnlyDictionary<int, string> Genres = new Dictionary<int, string>
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation", [35] = "Comedy", [80] = "Crime",
        [99] = "Documentary", [18] = "Drama", [10751] = "Family", [14] = "Fantasy", [36] = "History",
        [27] = "Horror", [10402] = "Music", [9648] = "Mystery", [10749] = "Romance",
        [878] = "Science Fiction", [10770] = "TV Movie", [53] = "Thriller", [10752] = "War", [37] = "Western"
    };
    private readonly StateStore store;
    private readonly MediaApi api;
    private readonly string path;
    private readonly ConcurrentDictionary<int, Movie> movies;
    private readonly ConcurrentDictionary<int, LocalRating> ratings;
    private readonly SemaphoreSlim requests = new(4, 4);
    private readonly SemaphoreSlim writer = new(1, 1);
    private long revision;
    private long savedRevision;
    private sealed record Document(Dictionary<int, Movie> Movies, Dictionary<int, LocalRating> Ratings);

    /// <summary>Loads a persistent metadata cache without introducing another external ratings provider.</summary>
    public MetadataService(StateStore store, MediaApi api, IConfiguration configuration)
    {
        this.store = store;
        this.api = api;
        path = Path.Combine(Path.GetFullPath(configuration["ELEMENTARR_DATA"] ?? "data"), "metadata.json");
        Document? saved = null;
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            saved = JsonSerializer.Deserialize<Document>(stream);
        }
        movies = new(saved?.Movies ?? []);
        ratings = new(saved?.Ratings ?? []);
    }

    /// <summary>Combines cached metadata, current Radarr facts, and explicitly imported local ratings.</summary>
    public Movie Decorate(Movie movie)
    {
        movies.TryGetValue(movie.TmdbId, out var cached);
        var library = store.Snapshot.Library.FirstOrDefault(x => x.TmdbId == movie.TmdbId);
        var facts = Merge(cached?.Facts, Merge(movie.Facts, library?.Facts));
        if (ratings.TryGetValue(movie.TmdbId, out var supplied)) facts = (facts ?? new()) with
        {
            Imdb = supplied.Imdb ?? facts?.Imdb, RottenTomatoes = supplied.RottenTomatoes ?? facts?.RottenTomatoes,
            Letterboxd = supplied.Letterboxd ?? facts?.Letterboxd, Tmdb = supplied.Tmdb ?? facts?.Tmdb,
            RatingOrigin = "Local rating import; other values from Radarr / Plex / TMDB"
        };
        return movie with
        {
            Poster = movie.Poster ?? cached?.Poster ?? library?.Poster,
            Overview = movie.Overview.Length > 0 ? movie.Overview : cached?.Overview ?? library?.Overview ?? "",
            ReleaseDate = string.IsNullOrEmpty(movie.ReleaseDate) ? cached?.ReleaseDate ?? library?.ReleaseDate : movie.ReleaseDate,
            Facts = facts, Score = facts?.Tmdb ?? movie.Score
        };
    }

    /// <summary>Projects a Radarr library record without losing its availability flags.</summary>
    public Movie FromLibrary(LibraryMovie movie) => Decorate(new(movie.TmdbId, movie.Title, movie.Year,
        movie.Overview, movie.Poster, movie.ReleaseDate, movie.Facts?.Tmdb ?? 0, InRadarr: true,
        Available: movie.HasFile, Facts: movie.Facts));

    /// <summary>Gets bounded, cached movie data, requesting TMDB artwork and credits only for the detail drawer.</summary>
    public async Task<Movie> GetAsync(int id, bool detailed, CancellationToken ct, Movie? seed = null, bool force = false)
    {
        if (id <= 0) throw new ArgumentException("Choose a valid movie.");
        var library = store.Snapshot.Library.FirstOrDefault(x => x.TmdbId == id);
        var existing = Decorate(seed ?? (library is not null ? FromLibrary(library)
            : movies.GetValueOrDefault(id) ?? new(id, "", 0, "", null, null, 0)));
        var cached = movies.GetValueOrDefault(id)?.Facts;
        if (!force && (detailed ? cached?.DetailedAt : cached?.UpdatedAt) > DateTimeOffset.UtcNow.AddDays(-7)) return existing;
        await requests.WaitAsync(ct);
        try
        {
            if (!force && !detailed && library?.Facts is not null) return existing;
            if (!force && movies.TryGetValue(id, out var fresh)
                && (detailed ? fresh.Facts?.DetailedAt : fresh.Facts?.UpdatedAt) > DateTimeOffset.UtcNow.AddDays(-7))
                return Decorate(existing with { Facts = Merge(existing.Facts, fresh.Facts) });
            if (store.Settings.RadarrKey.Length > 0 && (existing.Facts?.Imdb is null || existing.Facts.RottenTomatoes is null))
            {
                try
                {
                    var lookup = await api.RadarrAsync(store.Settings, $"movie/lookup/tmdb?tmdbId={id}", ct);
                    if (lookup.Number("tmdbId") == id)
                    {
                        var found = ParseRadarr(lookup);
                        existing = existing with { Title = existing.Title.Length > 0 ? existing.Title : found.Title,
                            Year = existing.Year > 0 ? existing.Year : found.Year, Poster = existing.Poster ?? found.Poster,
                            Overview = existing.Overview.Length > 0 ? existing.Overview : found.Overview,
                            Facts = Merge(existing.Facts, found.Facts) };
                    }
                }
                catch (UpstreamException) { /* Movie discovery remains usable during a Radarr outage. */ }
            }
            if (detailed || existing.Title.Length == 0 || existing.Facts is null)
            {
                try
                {
                    // The compact parsed metadata is persisted below; retaining this large raw response would duplicate the index in RAM.
                    var json = await api.TmdbAsync(store.Settings, $"movie/{id}?append_to_response=credits,images,videos,release_dates&include_image_language=en,null", ct, cacheResponse: false);
                    if (json.Number("id") != id) throw new UpstreamException("TMDB did not return the requested movie.");
                    var parsed = ParseTmdb(json, detailed, store.Settings.Region);
                    existing = parsed with { Facts = Merge(existing.Facts, parsed.Facts) };
                }
                catch (UpstreamException) when (!detailed && existing.Title.Length > 0)
                { /* Retain real partial metadata instead of blocking a grid when enrichment is unavailable. */ }
            }
            var facts = existing.Facts ?? new();
            existing = Decorate(existing with { Facts = facts with { UpdatedAt = DateTimeOffset.UtcNow } });
            movies[id] = existing with { InRadarr = false, Available = false, Watched = false, Reason = "" };
            Interlocked.Increment(ref revision);
            return existing;
        }
        finally { requests.Release(); }
    }

    /// <summary>Enriches a displayed page concurrently and persists the shared cache once per batch.</summary>
    public async Task<List<Movie>> EnrichAsync(IEnumerable<Movie> source, CancellationToken ct, bool detailed = false)
    {
        var result = await Task.WhenAll(source.Select(async movie =>
        {
            var enriched = await GetAsync(movie.TmdbId, detailed, ct, movie);
            return enriched with { InRadarr = movie.InRadarr, Available = movie.Available, Watched = movie.Watched,
                Reason = movie.Reason, Title = movie.Title, Year = movie.Year > 0 ? movie.Year : enriched.Year };
        }));
        await SaveAsync(ct);
        return result.ToList();
    }

    /// <summary>Stores metadata atomically so cached ratings and artwork survive Docker updates.</summary>
    public async Task SaveAsync(CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try
        {
            var current = Volatile.Read(ref revision);
            if (current == savedRevision) return;
            await using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 65_536, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, new Document(new(movies), new(ratings)), cancellationToken: ct);
            File.Move(path + ".tmp", path, true);
            savedRevision = current;
        }
        finally { writer.Release(); }
    }

    /// <summary>Imports user-supplied scores with native source scales and strict limits, without scraping review sites.</summary>
    public async Task<int> ImportRatingsAsync(RatingImport import, CancellationToken ct)
    {
        if (import.Movies is null || import.Movies.Count is < 1 or > 100)
            throw new ArgumentException("Import between 1 and 100 movies at a time.");
        foreach (var item in import.Movies)
        {
            if (item.TmdbId <= 0) throw new ArgumentException("Every rating needs a valid TMDB movie ID.");
            foreach (var (value, scale) in new[] { (item.Imdb, 10d), (item.RottenTomatoes, 100d), (item.Letterboxd, 5d), (item.Tmdb, 10d) })
                if (value.HasValue && (!double.IsFinite(value.Value) || value < 0 || value > scale))
                    throw new ArgumentException("A rating is outside its source's scale.");
        }
        foreach (var item in import.Movies) ratings[item.TmdbId] = item;
        Interlocked.Increment(ref revision);
        await SaveAsync(ct);
        return import.Movies.Count;
    }

    /// <summary>Reports cached rating availability so unsupported sliders can explain missing source data.</summary>
    public object Coverage() => new { cachedMovies = movies.Count, localRatings = ratings.Count,
        letterboxdAvailable = ratings.Values.Any(x => x.Letterboxd.HasValue), provider = "Radarr / Plex / TMDB" };

    /// <summary>Recognizes Plex's rating image identifiers before assigning any score to a review source.</summary>
    public static MovieFacts ParsePlex(JsonElement json)
    {
        double? imdb = null, rt = null, tmdb = null;
        foreach (var (imageField, scoreField) in new[] { ("ratingImage", "rating"), ("audienceRatingImage", "audienceRating") })
            if (Score(json, scoreField, 10) is { } score)
                AssignPlexRating(json.Text(imageField), score, ref imdb, ref rt, ref tmdb);
        foreach (var rating in json.Items("Rating"))
            if (Score(rating, "value", 10) is { } score)
                AssignPlexRating(rating.Text("image"), score, ref imdb, ref rt, ref tmdb);
        return new() { Imdb = imdb, RottenTomatoes = rt, Tmdb = tmdb, ImdbId = json.ImdbId() ?? "",
            Genres = json.Items("Genre").Select(x => x.Text("tag")).Where(x => x.Length > 0).ToList(),
            Runtime = (int)(json.Number("duration") / 60_000), Certification = json.Text("contentRating"),
            RatingOrigin = "Plex" };
    }

    /// <summary>Converts Plex's 0-10 Rotten Tomatoes field to percent while preserving other source scales.</summary>
    private static void AssignPlexRating(string image, double value, ref double? imdb, ref double? rt, ref double? tmdb)
    {
        if (!double.IsFinite(value) || value is < 0 or > 10) return;
        if (image.StartsWith("imdb://", StringComparison.Ordinal)) imdb = value;
        else if (image.StartsWith("rottentomatoes://", StringComparison.Ordinal) && !image.Contains("audience")
            && !image.Contains("upright") && !image.Contains("spilled")) rt = value * 10;
        else if (image.StartsWith("themoviedb://", StringComparison.Ordinal)) tmdb = value;
    }

    /// <summary>Reads the documented Radarr rating sources using their original score scales.</summary>
    public static Movie ParseRadarr(JsonElement json)
    {
        var ratings = json.TryGetProperty("ratings", out var value) ? value : default;
        var poster = ImagePath(json.Items("images").FirstOrDefault(x => x.Text("coverType") == "poster").Text("remoteUrl"));
        var backdrop = ImagePath(json.Items("images").FirstOrDefault(x => x.Text("coverType") is "fanart" or "screenshot").Text("remoteUrl"));
        var language = json.TryGetProperty("originalLanguage", out var lang) ? lang.Text("name") : "";
        var facts = new MovieFacts
        {
            Genres = json.Items("genres").Select(x => x.ToString()).ToList(), Runtime = (int)json.Number("runtime"),
            Certification = json.Text("certification"), ImdbId = json.Text("imdbId"), Language = NormalizeLanguage(language),
            Imdb = ReadRating(ratings, "imdb", 10), Tmdb = ReadRating(ratings, "tmdb", 10),
            RottenTomatoes = ReadRating(ratings, "rottenTomatoes", 100),
            Votes = ratings.TryGetPropertySafe("tmdb", out var tmdb) ? (int)tmdb.Number("votes") : 0,
            Backdrop = backdrop, Popularity = json.Number("popularity"), RatingOrigin = "Radarr",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return new((int)json.Number("tmdbId"), json.Text("title"), (int)json.Number("year"), json.Text("overview"),
            poster, json.Text("inCinemas", json.Text("digitalRelease")), facts.Tmdb ?? 0, Facts: facts);
    }

    /// <summary>Reads TMDB artwork, credits, trailer links, certifications, and source-labeled vote averages.</summary>
    public static Movie ParseTmdb(JsonElement json, bool detailed = false, string region = "US")
    {
        var date = json.Text("release_date");
        _ = int.TryParse(date.Length >= 4 ? date[..4] : "", out var year);
        var genres = json.Items("genres").Select(x => x.Text("name")).ToList();
        if (genres.Count == 0) genres = json.Items("genre_ids").Select(x => Genres.GetValueOrDefault(x.GetInt32(), "")).Where(x => x.Length > 0).ToList();
        var credits = json.TryGetProperty("credits", out var c) ? c : default;
        var images = json.TryGetProperty("images", out var im) ? im : default;
        var videos = json.TryGetProperty("videos", out var v) ? v : default;
        var releases = json.TryGetProperty("release_dates", out var r) ? r : default;
        var release = releases.Items("results").FirstOrDefault(x => x.Text("iso_3166_1") == region);
        var trailer = videos.Items("results").FirstOrDefault(x => x.Text("site") == "YouTube" && x.Text("type") == "Trailer");
        var key = trailer.Text("key");
        var facts = new MovieFacts
        {
            Genres = genres, Runtime = (int)json.Number("runtime"), Language = json.Text("original_language"),
            ImdbId = json.Text("imdb_id"), Tmdb = json.Number("vote_count") > 0 ? Score(json, "vote_average", 10) : null,
            Votes = (int)json.Number("vote_count"), Backdrop = JsonFields.PosterPath(json.Text("backdrop_path")),
            Logo = JsonFields.PosterPath(images.Items("logos").OrderByDescending(x => x.Text("iso_639_1") == "en").FirstOrDefault().Text("file_path")),
            Tagline = json.Text("tagline"), Director = credits.Items("crew").FirstOrDefault(x => x.Text("job") == "Director").Text("name"),
            Cast = credits.Items("cast").Take(12).Select(x => new CastMember(x.Text("name"), x.Text("character"), JsonFields.PosterPath(x.Text("profile_path")))).ToList(),
            Countries = json.Items("production_countries").Select(x => x.Text("name")).ToList(),
            Country = json.Items("production_countries").FirstOrDefault().Text("iso_3166_1"),
            Studios = json.Items("production_companies").Select(x => x.Text("name")).ToList(),
            Certification = release.Items("release_dates").Select(x => x.Text("certification")).FirstOrDefault(x => x.Length > 0) ?? "",
            Trailer = YouTubeKey().IsMatch(key) ? "https://www.youtube.com/watch?v=" + key : null,
            Popularity = json.Number("popularity"), RatingOrigin = "TMDB", UpdatedAt = DateTimeOffset.UtcNow,
            Detailed = detailed, DetailedAt = detailed ? DateTimeOffset.UtcNow : null
        };
        return new((int)json.Number("id"), json.Text("title"), year, json.Text("overview"),
            JsonFields.PosterPath(json.Text("poster_path")), date, facts.Tmdb ?? 0, Facts: facts);
    }

    /// <summary>Retains real values from each provider when a later response omits that field.</summary>
    public static MovieFacts? Merge(MovieFacts? first, MovieFacts? next)
    {
        if (first is null) return next;
        if (next is null) return first;
        return next with
        {
            Genres = next.Genres.Count > 0 ? next.Genres : first.Genres,
            Runtime = next.Runtime > 0 ? next.Runtime : first.Runtime,
            Language = next.Language.Length > 0 ? next.Language : first.Language,
            Country = next.Country.Length > 0 ? next.Country : first.Country,
            Certification = next.Certification.Length > 0 ? next.Certification : first.Certification,
            ImdbId = next.ImdbId.Length > 0 ? next.ImdbId : first.ImdbId,
            Imdb = next.Imdb ?? first.Imdb, RottenTomatoes = next.RottenTomatoes ?? first.RottenTomatoes,
            Letterboxd = next.Letterboxd ?? first.Letterboxd, Tmdb = next.Tmdb ?? first.Tmdb,
            Votes = next.Votes > 0 ? next.Votes : first.Votes, Backdrop = next.Backdrop ?? first.Backdrop,
            Logo = next.Logo ?? first.Logo, Tagline = next.Tagline.Length > 0 ? next.Tagline : first.Tagline,
            Director = next.Director.Length > 0 ? next.Director : first.Director,
            Cast = next.Cast.Count > 0 ? next.Cast : first.Cast,
            Countries = next.Countries.Count > 0 ? next.Countries : first.Countries,
            Studios = next.Studios.Count > 0 ? next.Studios : first.Studios, Trailer = next.Trailer ?? first.Trailer,
            Popularity = next.Popularity > 0 ? next.Popularity : first.Popularity,
            Detailed = first.Detailed || next.Detailed,
            DetailedAt = next.DetailedAt ?? first.DetailedAt,
            RatingOrigin = string.Join(" / ", (first.RatingOrigin + "/" + next.RatingOrigin)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    /// <summary>Accepts artwork only from TMDB's image path format, including full Radarr TMDB URLs.</summary>
    private static string? ImagePath(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host == "image.tmdb.org")
            value = "/" + uri.AbsolutePath.Split('/').Last();
        return JsonFields.PosterPath(value);
    }

    /// <summary>Rejects missing or out-of-range ratings rather than presenting provider defaults as actual scores.</summary>
    private static double? ReadRating(JsonElement ratings, string source, double scale)
    {
        if (!ratings.TryGetPropertySafe(source, out var value)) return null;
        var score = Score(value, "value", scale);
        return score > 0 || (score == 0 && value.Number("votes") > 0) ? score : null;
    }

    /// <summary>Reads a real finite score, preserving valid zero values without inventing zero for absent or malformed data.</summary>
    private static double? Score(JsonElement json, string field, double scale) =>
        json.TryGetPropertySafe(field, out var raw)
            && double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
            && double.IsFinite(score) && score >= 0 && score <= scale ? score : null;

    /// <summary>Normalizes common Radarr language names to the TMDB language codes used by filters.</summary>
    private static string NormalizeLanguage(string value) => value.ToLowerInvariant() switch
    {
        "english" => "en", "french" => "fr", "spanish" => "es", "german" => "de", "italian" => "it",
        "japanese" => "ja", "korean" => "ko", "chinese" => "zh", "hindi" => "hi", "portuguese" => "pt",
        "russian" => "ru", "swedish" => "sv", "danish" => "da", "dutch" => "nl", _ => value.ToLowerInvariant()
    };

    /// <summary>Limits trailer links to a safe YouTube video identifier.</summary>
    [GeneratedRegex("^[a-zA-Z0-9_-]{6,40}$")]
    private static partial Regex YouTubeKey();
}

public static class SafeJson
{
    /// <summary>Reads object properties safely when an optional provider object is absent.</summary>
    public static bool TryGetPropertySafe(this JsonElement json, string name, out JsonElement value)
    {
        value = default;
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out value);
    }
}
