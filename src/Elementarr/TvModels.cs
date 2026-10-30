namespace Elementarr;

// TV identifiers and episode watches deliberately have no dependency on the movie snapshot or movie matching index.
public sealed record TvSeries(int? TmdbId, int TvdbId, string Title, int Year, string Overview,
    string? Poster, double? Score = null, bool InSonarr = false, int EpisodeFileCount = 0,
    int EpisodeCount = 0, bool HasWatchedEpisodes = false, string Reason = "", int SonarrId = 0,
    TvFacts? Facts = null, DateTimeOffset? MetadataUpdatedAt = null, TvAiringEpisode? AiringEpisode = null,
    int WatchedEpisodeCount = 0, int? CompletionPercent = null);
public sealed record TvAiringEpisode(int Season, int Episode, string Title, DateTimeOffset AirsAt);
public sealed record TvWatch(string Id, string RatingKey, string SeriesKey, string SeriesTitle,
    string EpisodeTitle, int? Season, int? Episode, long WatchedAt, int? TmdbId, int? TvdbId, string? Poster = null, int SonarrId = 0,
    TvEpisode? Details = null, TvSeries? Series = null);
public sealed record TvFacts(List<string> Genres, string Status, int Seasons, int Episodes,
    int Runtime, string Language, string? Backdrop, List<CastMember> Cast)
{
    public string? Logo { get; init; }
    public string Tagline { get; init; } = "";
    public string Certification { get; init; } = "";
    public string Director { get; init; } = "";
    public string ImdbId { get; init; } = "";
    public double? Imdb { get; init; }
    public double? RottenTomatoes { get; init; }
    public double? Tmdb { get; init; }
    public int Votes { get; init; }
    public string RatingOrigin { get; init; } = "";
    public List<string> Countries { get; init; } = [];
    public List<string> Studios { get; init; } = [];
    public string? Trailer { get; init; }
    public int MetadataVersion { get; init; }
    public string FirstAirDate { get; init; } = "";
    public string Country { get; init; } = "";
    public double Popularity { get; init; }
    public List<TvSeason> SeasonList { get; init; } = [];
}
public sealed record TvSeason(int Number, string Name, int EpisodeCount, string AirDate = "");
public sealed record TvSeasonEpisode(int Season, int Episode, string Title, string AirDate,
    string Overview = "", string? Still = null, bool Watched = false, long? WatchedAt = null);
public sealed record TvSeasonPage(List<TvSeasonEpisode> Episodes, DateTimeOffset UpdatedAt);
public sealed record TvEpisode(string Title, string Overview, string AirDate, MovieFacts Facts,
    DateTimeOffset? UpdatedAt = null);
public sealed record TvSnapshot(List<TvSeries> Library, List<TvWatch> History,
    DateTimeOffset? LibrarySyncedAt = null, DateTimeOffset? HistorySyncedAt = null,
    string? LibraryError = null, string? HistoryError = null)
{
    public static TvSnapshot Empty { get; } = new([], []);
}
public sealed record TvPage(List<TvSeries> Series, List<TvWatch> Plays, int Page, int TotalPages,
    int TotalResults, bool HasMore, string? Notice = null, bool Incomplete = false);
public sealed record PlexServerRequest(string ServerId);
