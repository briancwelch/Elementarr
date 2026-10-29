using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elementarr;
using Elementarr.UpdateCore;
using Elementarr.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
            await CheckPublicFeedAsync(root, check);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("elementarr-update-tests-")) Directory.Delete(resolved, true);
        }
        var preferences = await browser.PostAsJsonAsync("/api/updates/settings", new { enabled = true, autoInstall = false });
        check(preferences.IsSuccessStatusCode, "The Plex owner can configure automatic updates without GitHub credentials");
        var safe = await browser.GetStringAsync("/api/updates");
        check(!safe.Contains("token", StringComparison.OrdinalIgnoreCase) && !JsonDocument.Parse(safe).RootElement.Flag("autoInstall"), "Public update preferences expose no credential configuration and retain the chosen install policy");
        check((await browser.PostAsJsonAsync("/api/updates/install", new { })).StatusCode == HttpStatusCode.BadRequest, "Portable/test/Docker processes cannot invoke native service installation");
        await browser.PostAsJsonAsync("/api/updates/settings", new { enabled = true, autoInstall = true });
    }

    /// <summary>Exercises the complete anonymous release/asset flow and migrates legacy credentials without changing saved policies.</summary>
    private static async Task CheckPublicFeedAsync(string root, Action<bool, string> check)
    {
        var data = Path.Combine(root, "public-feed");
        Directory.CreateDirectory(data);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(data, "keys")));
        var protector = protection.CreateProtector("Elementarr.Updates.v1");
        var path = Path.Combine(data, "updates.protected");
        await File.WriteAllTextAsync(path, protector.Protect("{\"Enabled\":true,\"AutoInstall\":false,\"Token\":\"legacy-fixture-token\"}"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ELEMENTARR_DATA"] = data }).Build();
        var fixture = new PublicReleaseFixture();
        using var updates = new UpdateService(fixture, configuration, protection, NullLogger<UpdateService>.Instance);
        var status = await updates.CheckAsync(default);
        check(status.Stage == "idle" && status.AvailableVersion is null && fixture.Requests == 4 && fixture.Anonymous,
            "Public updates verify a real publisher-signed release and follow asset redirects without authentication");
        check(updates.Preferences.Enabled && !updates.Preferences.AutoInstall && !protector.Unprotect(await File.ReadAllTextAsync(path)).Contains("Token"),
            "Legacy GitHub tokens are removed while existing automatic-update preferences survive migration");
        fixture.Limited = true;
        status = await updates.CheckAsync(default);
        check(status.Stage == "error" && status.Message!.Contains("limited") && status.AvailableVersion is null,
            "Anonymous GitHub rate limits leave an actionable retry status without advertising an unverified update");
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

internal sealed class PublicReleaseFixture : HttpMessageHandler, IHttpClientFactory
{
    public int Requests { get; private set; }
    public bool Anonymous { get; private set; } = true;
    public bool Limited { get; set; }

    /// <summary>Provides an in-process public release client without making network requests.</summary>
    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    /// <summary>Serves the published 0.4.0 signed metadata, including one GitHub storage redirect, and records authentication regressions.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests++;
        Anonymous &= request.Headers.Authorization is null;
        if (Limited) return new(HttpStatusCode.TooManyRequests);
        var url = request.RequestUri!.AbsoluteUri;
        if (url == $"https://api.github.com/repos/{ReleaseTrust.Repository}/releases/latest")
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { tag_name = "v0.4.0", draft = false, prerelease = false, assets = new[] {
                new { id = 1, name = "elementarr-update.json" }, new { id = 2, name = "elementarr-update.sig" } } }) };
        if (url.EndsWith("/releases/assets/1"))
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://release-assets.githubusercontent.com/elementarr/manifest");
            return redirect;
        }
        var name = url == "https://release-assets.githubusercontent.com/elementarr/manifest" ? "elementarr-update.json"
            : url.EndsWith("/releases/assets/2") ? "elementarr-update.sig" : throw new InvalidOperationException("Unexpected update request.");
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), ct)) };
    }
}
