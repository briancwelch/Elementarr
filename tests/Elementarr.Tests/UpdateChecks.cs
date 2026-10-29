using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elementarr;
using Elementarr.UpdateCore;

internal static class UpdateChecks
{
    /// <summary>Verifies signature/hash enforcement, safe extraction, version/platform limits, rollback, and protected update configuration.</summary>
    internal static async Task RunAsync(HttpClient browser, Action<bool, string> check)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var manifest = new ReleaseManifest("0.5.0", "Elementarr-0.5.0-win-x64-update.zip", new string('A', 64), 10);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var signature = key.SignData(json, HashAlgorithmName.SHA256);
        check(ReleaseTrust.Verify(json, signature, publicKey).Version == "0.5.0", "Valid publisher signatures authenticate release metadata");
        var altered = json.ToArray(); altered[8] ^= 1;
        check(Rejected(() => ReleaseTrust.Verify(altered, signature, publicKey)), "Tampered release metadata is rejected before trusting its version or payload");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        check(Rejected(() => ReleaseTrust.Verify(json, stranger.SignData(json, HashAlgorithmName.SHA256), publicKey)), "Another signing key cannot publish accepted updates");
        var incompatible = JsonSerializer.SerializeToUtf8Bytes(manifest with { Platform = "other", MinimumUpdaterVersion = 99 });
        check(Rejected(() => ReleaseTrust.Verify(incompatible, key.SignData(incompatible, HashAlgorithmName.SHA256), publicKey)), "Incompatible platform/bootstrap releases require a compatible installer");
        var root = Path.Combine(Path.GetTempPath(), "elementarr-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var payload = Path.Combine(root, "payload.zip");
            MakeZip(payload, new Dictionary<string, string> { ["Elementarr.exe"] = "new exe", ["Elementarr.dll"] = "new dll", ["wwwroot/index.html"] = "new UI" });
            var real = manifest with { Size = new FileInfo(payload).Length, Sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(payload))) };
            await ReleaseTrust.VerifyPayloadAsync(payload, real, default);
            check(true, "Complete update payloads must match the signed byte count and SHA-256");
            await File.AppendAllTextAsync(payload, "corrupt");
            var rejected = false;
            try { await ReleaseTrust.VerifyPayloadAsync(payload, real, default); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Incomplete or modified update downloads cannot be installed");
            var traversal = Path.Combine(root, "unsafe.zip");
            MakeZip(traversal, new Dictionary<string, string> { ["../escaped.txt"] = "escape" });
            check(Rejected(() => ReleaseTrust.Extract(traversal, Path.Combine(root, "unsafe"))) && !File.Exists(Path.Combine(root, "escaped.txt")), "Update extraction rejects directory traversal");
            var privateData = Path.Combine(root, "private.zip");
            MakeZip(privateData, new Dictionary<string, string> { ["data/connections.protected"] = "private" });
            check(Rejected(() => ReleaseTrust.Extract(privateData, Path.Combine(root, "private"))), "Update archives cannot contain installation data or encryption keys");
            var duplicate = Path.Combine(root, "duplicate.zip");
            MakeZip(duplicate, new Dictionary<string, string> { ["Elementarr.dll"] = "one", ["ELEMENTARR.DLL"] = "two" });
            check(Rejected(() => ReleaseTrust.Extract(duplicate, Path.Combine(root, "duplicate"))), "Case-insensitive duplicate update files are rejected on Windows");
            var staged = Path.Combine(root, "staged"); var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(staged); Directory.CreateDirectory(installed);
            await File.WriteAllTextAsync(Path.Combine(staged, "Elementarr.dll"), "new");
            await File.WriteAllTextAsync(Path.Combine(installed, "Elementarr.dll"), "old");
            await File.WriteAllTextAsync(Path.Combine(root, "private-history.json"), "history stays intact");
            int stops = 0, starts = 0;
            rejected = false;
            try { await UpdateInstaller.ApplyAsync(staged, installed, Path.Combine(root, "rollback"), () => { stops++; return Task.CompletedTask; }, () => { starts++; return Task.CompletedTask; }, () => Task.FromResult(false)); }
            catch (IOException) { rejected = true; }
            check(rejected && stops == 2 && starts == 2 && await File.ReadAllTextAsync(Path.Combine(installed, "Elementarr.dll")) == "old", "Failed update health restores the previous application and restarts its service");
            check(await File.ReadAllTextAsync(Path.Combine(root, "private-history.json")) == "history stays intact", "Application rollback leaves persistent watch history outside the binary directory untouched");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("elementarr-update-tests-")) Directory.Delete(resolved, true);
        }
        var preferences = await browser.PostAsJsonAsync("/api/updates/settings", new { enabled = true, autoInstall = false, token = "fixture-github-token" });
        check(preferences.IsSuccessStatusCode, "The Plex owner can configure private update access and automatic-install preferences");
        var safe = await browser.GetStringAsync("/api/updates");
        check(!safe.Contains("fixture-github-token") && JsonDocument.Parse(safe).RootElement.Flag("hasToken"), "Update APIs expose credential presence without returning the saved GitHub token");
        check((await browser.PostAsJsonAsync("/api/updates/install", new { })).StatusCode == HttpStatusCode.BadRequest, "Portable/test/Docker processes cannot invoke native service installation");
        await browser.PostAsJsonAsync("/api/updates/settings", new { enabled = true, autoInstall = true, removeToken = true });
    }

    /// <summary>Builds synthetic archives containing no application or user secrets.</summary>
    private static void MakeZip(string path, Dictionary<string, string> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, contents) in entries) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8); writer.Write(contents); }
    }

    /// <summary>Recognizes an expected signed-update validation failure.</summary>
    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception exception) when (exception is InvalidDataException or CryptographicException or JsonException) { return true; }
    }
}
