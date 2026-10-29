using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Elementarr;

public sealed record Connections
{
    public string RadarrUrl { get; init; } = "http://localhost:7878";
    public string RadarrKey { get; init; } = "";
    public string TautulliUrl { get; init; } = "http://localhost:8181";
    public string TautulliKey { get; init; } = "";
    public string TmdbToken { get; init; } = "";
    public string PlexUsername { get; init; } = "";
    public string PlexUserId { get; init; } = "";
    public int QualityProfileId { get; init; }
    public string RootFolder { get; init; } = "";
    public string Region { get; init; } = "US";
    public string PlexUrl { get; init; } = "http://localhost:32400";
    public string PlexToken { get; init; } = "";
    public string PlexClientId { get; init; } = "";
    public string PlexOwnerId { get; init; } = "";
    public string PlexOwnerName { get; init; } = "";
    public string PlexServerId { get; init; } = "";
    public bool TautulliEnabled { get; init; }
    public string SonarrUrl { get; init; } = "http://localhost:8989";
    public string SonarrKey { get; init; } = "";
    public int SonarrQualityProfileId { get; init; }
    public string SonarrRootFolder { get; init; } = "";
}

public sealed record ConnectionUpdate(string RadarrUrl, string? RadarrKey, string TautulliUrl,
    string? TautulliKey, string? TmdbToken, string PlexUserId, string PlexUsername,
    int QualityProfileId, string RootFolder, string Region,
    string PlexUrl = "http://localhost:32400", bool TautulliEnabled = false,
    string SonarrUrl = "http://localhost:8989", string? SonarrKey = null,
    int SonarrQualityProfileId = 0, string SonarrRootFolder = "");
public sealed record HistoryRemovalRequest(string RatingKey);
public sealed record AddRequest(bool SearchNow = false);
public sealed record Choice(string Id, string Name);
public sealed record ConnectionCheck(bool Radarr, bool Tautulli, bool Tmdb,
    List<Choice> Users, List<Choice> Profiles, List<Choice> Folders, List<string> Errors, bool Plex = false,
    bool Sonarr = false, List<Choice>? SonarrProfiles = null, List<Choice>? SonarrFolders = null);
public sealed record PlexAuthRequest(bool Connect = false);
public sealed record PlexAuthorization(bool Connected, bool Connect = false, string Token = "",
    string OwnerId = "", string Username = "", string ServerId = "", string ServerUrl = "");
public sealed record SyncResult(bool RadarrSynced, bool HistorySynced, string HistorySource, List<string> Errors,
    bool SonarrSynced = false, bool TvHistorySynced = false);
public sealed record HistoryImport(List<Watch> History, List<string> Warnings);

public sealed record Movie(int TmdbId, string Title, int Year, string Overview, string? Poster,
    string? ReleaseDate, double Score, string Reason = "", bool InRadarr = false,
    bool Available = false, bool Watched = false, MovieFacts? Facts = null);
public sealed record LibraryMovie(int TmdbId, string ImdbId, string Title, int Year,
    string Overview, string? Poster, bool HasFile, bool Monitored, MovieFacts? Facts = null, string? ReleaseDate = null);
public sealed record Watch(string Id, string RatingKey, string Title, int Year,
    long WatchedAt, double? Percent, int? TmdbId, bool Completed, MovieFacts? Facts = null);
public sealed record LibrarySnapshot(DateTimeOffset? SyncedAt, string UserId,
    List<LibraryMovie> Library, List<Watch> History, List<string> Warnings,
    DateTimeOffset? LibrarySyncedAt = null, DateTimeOffset? HistorySyncedAt = null,
    string? LibraryError = null, string? HistoryError = null, string HistorySource = "")
{
    public static LibrarySnapshot Empty { get; } = new(null, "", [], [], []);
}
public sealed record MoviePage(List<Movie> Movies, int Page, int TotalPages, string? Notice = null);

public static partial class JsonFields
{
    /// <summary>Reads optional upstream properties without depending on their numeric representation.</summary>
    public static string Text(this JsonElement json, string field, string fallback = "") =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(field, out var value)
            && value.ValueKind is not JsonValueKind.Null ? value.ToString() : fallback;

    /// <summary>Reads numeric fields supplied as either JSON numbers or strings.</summary>
    public static double Number(this JsonElement json, string field) =>
        double.TryParse(json.Text(field), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value) ? value : 0;

    /// <summary>Reads the boolean conventions used by Radarr and Tautulli.</summary>
    public static bool Flag(this JsonElement json, string field) => json.Text(field) is "true" or "True" or "1";

    /// <summary>Enumerates an optional JSON array.</summary>
    public static IEnumerable<JsonElement> Items(this JsonElement json, string? field = null)
    {
        if (field is not null && (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(field, out json)))
            return [];
        return json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().ToArray() : [];
    }

    /// <summary>Extracts stable TMDB identifiers from legacy and modern Plex GUIDs.</summary>
    public static int? TmdbId(this JsonElement json)
    {
        var guids = json.Items("guids").Concat(json.Items("Guid")).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.Text("id"))
            .Append(json.Text("guid"));
        foreach (var guid in guids)
        {
            var match = TmdbGuid().Match(guid);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var id) && id > 0) return id;
        }
        return null;
    }

    /// <summary>Recognizes TMDB GUID formats without treating unrelated numeric GUIDs as movies.</summary>
    [GeneratedRegex(@"(?:^tmdb|^com\.plexapp\.agents\.themoviedb)://(\d+)(?:\?|$)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbGuid();

    /// <summary>Reads IMDb identifiers from legacy Plex agents and current external GUID lists.</summary>
    public static string? ImdbId(this JsonElement json)
    {
        var guids = json.Items("guids").Concat(json.Items("Guid"))
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.Text("id"))
            .Append(json.Text("guid"));
        foreach (var guid in guids)
        {
            var match = ImdbGuid().Match(guid);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Accepts the IMDb movie-ID shape used by Plex's supported GUID formats.</summary>
    [GeneratedRegex(@"(?:^imdb|^com\.plexapp\.agents\.imdb)://(tt\d+)(?:\?|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbGuid();

    /// <summary>Accepts only known TMDB poster paths for browser image URLs.</summary>
    public static string? PosterPath(string? path) => path is not null && SafePoster().IsMatch(path) ? path : null;

    /// <summary>Validates the filename shape used by TMDB image assets.</summary>
    [GeneratedRegex(@"^/[a-zA-Z0-9_-]+\.(jpg|png|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex SafePoster();
}
