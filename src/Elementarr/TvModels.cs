namespace Elementarr;

// TV identifiers and episode watches deliberately have no dependency on the movie snapshot or movie matching index.
public sealed record TvSeries(int? TmdbId, int TvdbId, string Title, int Year, string Overview,
    string? Poster, double? Score = null, bool InSonarr = false, int EpisodeFileCount = 0,
    int EpisodeCount = 0, bool HasWatchedEpisodes = false, string Reason = "", int SonarrId = 0,
    TvFacts? Facts = null, DateTimeOffset? MetadataUpdatedAt = null);
public sealed record TvWatch(string Id, string RatingKey, string SeriesKey, string SeriesTitle,
    string EpisodeTitle, int? Season, int? Episode, long WatchedAt, int? TmdbId, int? TvdbId, string? Poster = null, int SonarrId = 0);
public sealed record TvFacts(List<string> Genres, string Status, int Seasons, int Episodes,
    int Runtime, string Language, string? Backdrop, List<CastMember> Cast);
public sealed record TvSnapshot(List<TvSeries> Library, List<TvWatch> History,
    DateTimeOffset? LibrarySyncedAt = null, DateTimeOffset? HistorySyncedAt = null,
    string? LibraryError = null, string? HistoryError = null)
{
    public static TvSnapshot Empty { get; } = new([], []);
}
public sealed record TvPage(List<TvSeries> Series, List<TvWatch> Plays, int Page, int TotalPages,
    int TotalResults, bool HasMore, string? Notice = null);
public sealed record PlexServerRequest(string ServerId);
