using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Elementarr.Services;

public sealed class StateStore
{
    private readonly string directory;
    private readonly IDataProtector protector;
    private Connections settings;
    private LibrarySnapshot snapshot;
    private TvSnapshot tv;
    public SemaphoreSlim Mutation { get; } = new(1, 1);
    private Dictionary<string, int> historyMatches;
    private HashSet<string> historyExclusions;
    public Connections Settings => Volatile.Read(ref settings);
    public LibrarySnapshot Snapshot => Volatile.Read(ref snapshot);
    public TvSnapshot Tv => Volatile.Read(ref tv);
    public IReadOnlyDictionary<string, int> HistoryMatches => Volatile.Read(ref historyMatches);
    public IReadOnlySet<string> HistoryExclusions => Volatile.Read(ref historyExclusions);

    /// <summary>Loads persistent configuration and the last successful library snapshot.</summary>
    public StateStore(IDataProtectionProvider provider, IConfiguration configuration)
    {
        directory = Path.GetFullPath(configuration["ELEMENTARR_DATA"] ?? "data");
        Directory.CreateDirectory(directory);
        protector = provider.CreateProtector("Elementarr.Connections.v1");
        var settingsFile = Path.Combine(directory, "connections.protected");
        settings = File.Exists(settingsFile)
            ? JsonSerializer.Deserialize<Connections>(protector.Unprotect(File.ReadAllText(settingsFile)))
                ?? throw new InvalidDataException("Stored connections are invalid.")
            : new Connections();
        if (settings.PlexClientId.Length == 0)
        {
            // Plex device identity must survive restarts and remains protected with the connection settings.
            settings = settings with { PlexClientId = Guid.NewGuid().ToString("N") };
            File.WriteAllText(settingsFile + ".tmp", protector.Protect(JsonSerializer.Serialize(settings)));
            File.Move(settingsFile + ".tmp", settingsFile, overwrite: true);
        }
        var snapshotFile = Path.Combine(directory, "snapshot.json");
        snapshot = File.Exists(snapshotFile)
            ? JsonSerializer.Deserialize<LibrarySnapshot>(File.ReadAllText(snapshotFile))
                ?? throw new InvalidDataException("Stored snapshot is invalid.")
            : LibrarySnapshot.Empty;
        if (snapshot.SyncedAt.HasValue && snapshot.LibrarySyncedAt is null && snapshot.HistorySyncedAt is null)
            snapshot = snapshot with { LibrarySyncedAt = snapshot.SyncedAt, HistorySyncedAt = snapshot.SyncedAt,
                HistorySource = snapshot.History.Count > 0 ? "tautulli" : "" };
        var matchesFile = Path.Combine(directory, "history-matches.json");
        var tvFile = Path.Combine(directory, "tv-snapshot.json");
        tv = File.Exists(tvFile) ? JsonSerializer.Deserialize<TvSnapshot>(File.ReadAllText(tvFile))
            ?? throw new InvalidDataException("Stored TV snapshot is invalid.") : TvSnapshot.Empty;
        historyMatches = File.Exists(matchesFile) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(matchesFile))
            ?? throw new InvalidDataException("Stored history matches are invalid.") : [];
        var exclusionsFile = Path.Combine(directory, "history-exclusions.json");
        historyExclusions = File.Exists(exclusionsFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(exclusionsFile))
            ?? throw new InvalidDataException("Stored history exclusions are invalid.") : [];
        snapshot = snapshot with { History = snapshot.History.Where(x => !historyExclusions.Contains(MatchScope(snapshot.HistorySource) + x.RatingKey)).ToList() };
    }

    /// <summary>Persists protected secrets before making the new configuration visible.</summary>
    public async Task SaveSettingsAsync(Connections value, CancellationToken ct)
    {
        await WriteAsync("connections.protected", protector.Protect(JsonSerializer.Serialize(value)), ct);
        Volatile.Write(ref settings, value);
    }

    /// <summary>Atomically replaces the last complete synchronization snapshot.</summary>
    public async Task SaveSnapshotAsync(LibrarySnapshot value, CancellationToken ct)
    {
        await WriteAsync("snapshot.json", JsonSerializer.Serialize(value), ct);
        Volatile.Write(ref snapshot, value);
    }

    /// <summary>Atomically persists TV libraries and episode watches in a file separate from all movie history.</summary>
    public async Task SaveTvAsync(TvSnapshot value, CancellationToken ct)
    {
        await WriteAsync("tv-snapshot.json", JsonSerializer.Serialize(value), ct);
        Volatile.Write(ref tv, value);
    }

    /// <summary>Persists explicit history corrections separately from replaceable source snapshots.</summary>
    public async Task SaveHistoryMatchesAsync(Dictionary<string, int> value, CancellationToken ct)
    {
        await WriteAsync("history-matches.json", JsonSerializer.Serialize(value), ct);
        Volatile.Write(ref historyMatches, value);
    }

    /// <summary>Persists removed source items so a later sync or fallback does not silently reimport their movie watches.</summary>
    public async Task SaveHistoryExclusionsAsync(HashSet<string> value, CancellationToken ct)
    {
        await WriteAsync("history-exclusions.json", JsonSerializer.Serialize(value), ct);
        Volatile.Write(ref historyExclusions, value);
    }

    /// <summary>Scopes manual matches to a specific server and profile so unrelated history cannot inherit them.</summary>
    public string MatchScope(string source) => source == "plex"
        ? "plex:" + Settings.PlexServerId + ":" + Settings.PlexOwnerId + ":"
        : "tautulli:" + Settings.TautulliUrl + ":" + Settings.PlexUserId + ":";

    /// <summary>Writes in the same directory so interruption cannot leave a partially written live file.</summary>
    private async Task WriteAsync(string name, string content, CancellationToken ct)
    {
        var destination = Path.Combine(directory, name);
        var temporary = destination + ".tmp";
        await File.WriteAllTextAsync(temporary, content, ct);
        File.Move(temporary, destination, overwrite: true);
    }
}
