using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Elementarr.UpdateCore;

public sealed record ReleaseManifest(string Version, string AssetName, string Sha256, long Size,
    string Platform = "win-x64", int MinimumUpdaterVersion = 1);

public static class ReleaseTrust
{
    public const int UpdaterVersion = 1;
    public const string Repository = "briancwelch/Elementarr";
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEg3g0SVvG/7z3XAa3qxE0kjx/vWcKF/o/LqRb9OkoApV1c6gvJsbf0sf3QfD4dMq/pvDLbRM+wMAdpX5AkzeaWg==";

    /// <summary>Authenticates exact manifest bytes with the pinned publisher key before trusting any release metadata.</summary>
    public static ReleaseManifest Verify(byte[] json, byte[] signature, string publicKey = PublicKey)
    {
        if (json.Length is < 2 or > 65536 || signature.Length != 64) throw new InvalidDataException("Release signature is invalid.");
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!verifier.VerifyData(json, signature, HashAlgorithmName.SHA256)) throw new InvalidDataException("Release signature could not be verified.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Release metadata is invalid.");
        if (!System.Version.TryParse(manifest.Version, out var version) || version.Major < 0 || version.Build < 0 || version.ToString(3) != manifest.Version || manifest.Platform != "win-x64"
            || manifest.MinimumUpdaterVersion > UpdaterVersion || manifest.MinimumUpdaterVersion < 1
            || manifest.AssetName != $"Elementarr-{manifest.Version}-win-x64-update.zip"
            || manifest.Size is < 1 or > 300 * 1024 * 1024 || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("This release requires a compatible Windows installer or has invalid metadata.");
        return manifest;
    }

    /// <summary>Verifies the complete downloaded payload before extraction or stopping the installed service.</summary>
    public static async Task VerifyPayloadAsync(string file, ReleaseManifest manifest, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        if (stream.Length != manifest.Size) throw new InvalidDataException("Update download is incomplete.");
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update checksum does not match the signed release.");
    }

    /// <summary>Extracts only ordinary application files into an empty staging directory, preventing traversal, links, duplicates, and oversized archives.</summary>
    public static void Extract(string archive, string destination)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidDataException("Update staging must be empty.");
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        if (zip.Entries.Count > 5000) throw new InvalidDataException("The update contains too many files.");
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (name.StartsWith('/') || segments.Length == 0 || segments.Any(x => x is "." or ".." || x.Contains(':') || x.EndsWith('.') || x.EndsWith(' '))
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || !names.Add(name.TrimEnd('/')))
                throw new InvalidDataException("The update contains an unsafe file path.");
            var path = Path.GetFullPath(Path.Combine(destination, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update escapes its staging directory.");
            if (segments.Any(x => x.Equals("data", StringComparison.OrdinalIgnoreCase) || x.Equals("keys", StringComparison.OrdinalIgnoreCase))
                || Path.GetFileName(path) is "connections.protected" or "snapshot.json" or "tv-snapshot.json" or "server.json"
                || Path.GetFileName(path).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update cannot contain installation data.");
            expanded += entry.Length;
            if (expanded > 1024L * 1024 * 1024 || entry.Length > 300L * 1024 * 1024) throw new InvalidDataException("The expanded update is too large.");
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
        foreach (var required in new[] { "Elementarr.exe", "Elementarr.dll", "wwwroot/index.html" })
            if (!File.Exists(Path.Combine(destination, required))) throw new InvalidDataException("The update is missing required application files.");
    }
}
