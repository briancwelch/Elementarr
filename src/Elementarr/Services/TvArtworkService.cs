using System.Security.Cryptography;
using System.Text;

namespace Elementarr.Services;

public sealed class TvArtworkService(StateStore store, MediaApi api, IConfiguration configuration)
{
    private readonly string directory = Path.Combine(configuration["ELEMENTARR_DATA"]!, "tv-artwork");
    private readonly SemaphoreSlim requests = new(4, 4);

    /// <summary>Serves bounded source-scoped artwork caches without trusting third-party URLs or leaking API credentials.</summary>
    public async Task<(byte[] Bytes, string ContentType)> GetAsync(string source, int id, CancellationToken ct)
    {
        if (id <= 0 || source is not ("sonarr" or "plex" or "plex-episode")) throw new ArgumentException("Choose a valid TV artwork item.");
        if (source == "sonarr" && !store.Tv.Library.Any(x => x.SonarrId == id)) throw new ArgumentException("This series is not in the current Sonarr collection.");
        if (source == "plex" && !store.Tv.History.Any(x => x.SeriesKey == id.ToString())) throw new ArgumentException("This series is not in the current Plex TV history.");
        if (source == "plex-episode" && !store.Tv.History.Any(x => x.RatingKey == id.ToString())) throw new ArgumentException("This episode is not in the current Plex TV history.");
        var settings = store.Settings;
        var scope = source == "sonarr" ? settings.SonarrUrl : settings.PlexServerId + ":" + settings.PlexOwnerId;
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + ":" + scope + ":" + id)));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await requests.WaitAsync(ct);
        try
        {
            foreach (var (extension, contentType) in new[] { ("jpg", "image/jpeg"), ("png", "image/png"), ("webp", "image/webp") })
                if (File.Exists(path + "." + extension) && File.GetLastWriteTimeUtc(path + "." + extension) > DateTime.UtcNow.AddDays(-7))
                    return (await File.ReadAllBytesAsync(path + "." + extension, ct), contentType);
            var result = await api.TvArtworkAsync(settings, source, id, ct);
            var suffix = result.ContentType == "image/jpeg" ? "jpg" : result.ContentType == "image/png" ? "png" : "webp";
            // A unique temporary name also supports simultaneous requests for the same poster safely.
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, result.Bytes, ct);
            File.Move(temporary, path + "." + suffix, true);
            return result;
        }
        finally { requests.Release(); }
    }
}
