using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Elementarr.UpdateCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

namespace Elementarr.Services;

public sealed record UpdatePreferences(bool Enabled = true, bool AutoInstall = true, string Token = "");
public sealed record UpdatePreferenceRequest(bool Enabled, bool AutoInstall, string? Token = null, bool RemoveToken = false);
public sealed record UpdateStatus(string CurrentVersion, string? AvailableVersion = null, string Stage = "idle",
    string? Message = null, DateTimeOffset? CheckedAt = null, bool Busy = false);

public sealed class UpdateService : BackgroundService
{
    private readonly IHttpClientFactory clients;
    private readonly string directory;
    private readonly string preferencesPath;
    private readonly IDataProtector protector;
    private readonly SemaphoreSlim mutation = new(1, 1);
    private readonly Channel<bool> installs = Channel.CreateBounded<bool>(1);
    private UpdatePreferences preferences;
    private UpdateStatus status = new(CurrentVersion);
    private JsonElement release;
    private ReleaseManifest? manifest;
    private byte[]? manifestBytes;
    private byte[]? signature;
    public static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
    public UpdatePreferences Preferences => Volatile.Read(ref preferences);
    public UpdateStatus Status => Volatile.Read(ref status);
    public bool CanInstall => OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();

    /// <summary>Loads per-installation protected GitHub access without bundling developer credentials or exposing the token through API responses.</summary>
    public UpdateService(IHttpClientFactory clients, IConfiguration configuration, IDataProtectionProvider protection)
    {
        this.clients = clients;
        directory = configuration["ELEMENTARR_DATA"]!;
        preferencesPath = Path.Combine(directory, "updates.protected");
        protector = protection.CreateProtector("Elementarr.Updates.v1");
        preferences = File.Exists(preferencesPath) ? JsonSerializer.Deserialize<UpdatePreferences>(protector.Unprotect(File.ReadAllText(preferencesPath)))
            ?? throw new InvalidDataException("Stored update preferences are invalid.") : new();
    }

    /// <summary>Saves automatic-update preferences while keeping blank saved credentials and supporting explicit token removal.</summary>
    public async Task SaveAsync(UpdatePreferenceRequest request, CancellationToken ct)
    {
        await mutation.WaitAsync(ct);
        try
        {
            var token = request.RemoveToken ? "" : string.IsNullOrWhiteSpace(request.Token) ? preferences.Token : request.Token.Trim();
            if (token.Length > 4096 || token.Any(char.IsControl)) throw new ArgumentException("The GitHub access token is invalid.");
            var next = new UpdatePreferences(request.Enabled, request.AutoInstall, token);
            await File.WriteAllTextAsync(preferencesPath + ".tmp", protector.Protect(JsonSerializer.Serialize(next)), ct);
            File.Move(preferencesPath + ".tmp", preferencesPath, true);
            Volatile.Write(ref preferences, next);
        }
        finally { mutation.Release(); }
    }

    /// <summary>Checks only the fixed private repository's stable release and verifies the pinned publisher signature before advertising an update.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        await mutation.WaitAsync(ct);
        try
        {
            if (Preferences.Token.Length == 0) throw new ArgumentException("Add a read-only GitHub token for the private Elementarr repository to activate updates.");
            if (Status.Busy && Status.Stage is "downloading" or "installing") return Status;
            status = status with { Busy = true, Stage = "checking", Message = null };
            using var response = await SendAsync($"https://api.github.com/repos/{ReleaseTrust.Repository}/releases/latest", true, false, ct);
            await EnsureSuccessAsync(response);
            var bytes = await ReadBoundedAsync(response, 1024 * 1024, ct);
            release = JsonDocument.Parse(bytes).RootElement.Clone();
            if (release.Flag("draft") || release.Flag("prerelease")) throw new InvalidDataException("Only stable published releases can be installed.");
            manifestBytes = await AssetAsync("elementarr-update.json", 65536, ct);
            signature = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(await AssetAsync("elementarr-update.sig", 1024, ct)).Trim());
            manifest = ReleaseTrust.Verify(manifestBytes, signature);
            if (release.Text("tag_name") != "v" + manifest.Version) throw new InvalidDataException("The release tag does not match the signed update version.");
            var newer = Version.Parse(manifest.Version) > Version.Parse(CurrentVersion);
            status = new(CurrentVersion, newer ? manifest.Version : null, "idle", newer ? "A verified update is available." : "Elementarr is up to date.", DateTimeOffset.UtcNow);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or FormatException or ArgumentException or TaskCanceledException)
        {
            manifest = null;
            status = status with { Busy = false, AvailableVersion = null, Stage = "error", Message = SafeError(exception), CheckedAt = DateTimeOffset.UtcNow };
        }
        finally { mutation.Release(); }
        return Status;
    }

    /// <summary>Queues a Windows-service update without tying installation to the browser request or exposing arbitrary executable paths.</summary>
    public void Install()
    {
        if (!CanInstall) throw new ArgumentException("Automatic installation is available in the Windows service. Docker development uses local source builds.");
        if (Status.Busy || Status.AvailableVersion is null) throw new ArgumentException("Check for a newer verified release first.");
        if (!installs.Writer.TryWrite(true)) throw new ArgumentException("An update is already queued.");
        status = status with { Busy = true, Stage = "queued" };
    }

    /// <summary>Checks at startup and every six hours, applying updates automatically only when enabled in an installed Windows service.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!CanInstall) return;
        var nextCheck = DateTimeOffset.UtcNow.AddSeconds(30);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (Preferences.Enabled && Preferences.Token.Length > 0 && DateTimeOffset.UtcNow >= nextCheck)
                {
                    await CheckAsync(stoppingToken);
                    nextCheck = DateTimeOffset.UtcNow.AddHours(6);
                    if (Preferences.AutoInstall && Status.AvailableVersion is not null) Install();
                }
                if (installs.Reader.TryRead(out _)) await PrepareInstallAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { /* Normal service stop/update. */ }
    }

    /// <summary>Downloads and authenticates the complete release into private staging, then starts the fixed installed updater helper.</summary>
    private async Task PrepareInstallAsync(CancellationToken ct)
    {
        await mutation.WaitAsync(ct);
        try
        {
            if (!OperatingSystem.IsWindows() || manifest is null || manifestBytes is null || signature is null) throw new InvalidDataException("Check for a verified update again.");
            var install = NativeInstallation.Read();
            if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.Combine(install.Root, "app"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The service is not running from its registered installation.");
            var job = Path.Combine(directory, "updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(job);
            status = status with { Stage = "downloading", Message = "Downloading the signed Windows service update." };
            await File.WriteAllBytesAsync(Path.Combine(job, "elementarr-update.json"), manifestBytes, ct);
            await File.WriteAllTextAsync(Path.Combine(job, "elementarr-update.sig"), Convert.ToBase64String(signature), ct);
            var asset = FindAsset(manifest.AssetName);
            using var response = await SendAsync($"https://api.github.com/repos/{ReleaseTrust.Repository}/releases/assets/{(long)asset.Number("id")}", true, true, ct);
            await EnsureSuccessAsync(response);
            await using (var file = File.Create(Path.Combine(job, "update.zip")))
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > manifest.Size) throw new InvalidDataException("Update download exceeds its signed size.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            await ReleaseTrust.VerifyPayloadAsync(Path.Combine(job, "update.zip"), manifest, ct);
            status = status with { Stage = "installing", Message = "Installing the verified update. Elementarr will restart shortly." };
            var start = new ProcessStartInfo(Path.Combine(install.Root, "updater", "Elementarr.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(job);
            _ = Process.Start(start) ?? throw new IOException("The updater helper could not be started.");
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { status = status with { Busy = false, Stage = "error", Message = SafeError(exception) }; }
        finally { mutation.Release(); }
    }

    /// <summary>Reads a bounded GitHub release asset using its authenticated API ID rather than a public browser download URL.</summary>
    private async Task<byte[]> AssetAsync(string name, int limit, CancellationToken ct)
    {
        var asset = FindAsset(name);
        using var response = await SendAsync($"https://api.github.com/repos/{ReleaseTrust.Repository}/releases/assets/{(long)asset.Number("id")}", true, true, ct);
        await EnsureSuccessAsync(response);
        return await ReadBoundedAsync(response, limit, ct);
    }

    /// <summary>Accepts exactly one expected asset from the selected stable release.</summary>
    private JsonElement FindAsset(string name)
    {
        var matches = release.Items("assets").Where(x => x.Text("name") == name && x.Number("id") > 0).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidDataException("The release is missing a required signed update asset.");
    }

    /// <summary>Follows GitHub's HTTPS asset redirects without forwarding the private-repository token to storage hosts.</summary>
    private async Task<HttpResponseMessage> SendAsync(string url, bool authenticated, bool binary, CancellationToken ct)
    {
        var client = clients.CreateClient("updates");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
        if (authenticated) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Preferences.Token);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var target = response.Headers.Location;
            response.Dispose();
            if (!binary || target is null || !target.IsAbsoluteUri || target.Scheme != "https" || target.UserInfo.Length > 0
                || target.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("GitHub returned an unexpected update redirect.");
            using var redirected = new HttpRequestMessage(HttpMethod.Get, target);
            return await client.SendAsync(redirected, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return response;
    }

    /// <summary>Rejects unauthenticated or failed requests without exposing GitHub response bodies, tokens, or signed asset URLs.</summary>
    private static Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new InvalidDataException("GitHub denied access. Check the token's repository access, Contents: read permission, and expiry.");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException("No published update was found, or this token cannot read the private repository.");
        if (!response.IsSuccessStatusCode) throw new InvalidDataException($"GitHub returned HTTP {(int)response.StatusCode}.");
        return Task.CompletedTask;
    }

    /// <summary>Limits release JSON and signature buffering independently of upstream Content-Length headers.</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("GitHub returned an oversized update response.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary>Returns actionable verification errors while sanitizing network and local exception details.</summary>
    private static string SafeError(Exception exception) => exception is InvalidDataException or ArgumentException ? exception.Message : "The update could not be completed. Check connectivity and the private update log.";
}
