namespace Elementarr.Services;

public sealed class PlexAvatarService(StateStore store, MediaApi api, IHttpClientFactory clients)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string owner = "";
    private DateTimeOffset refreshAt;
    private (byte[] Bytes, string ContentType)? cached;

    /// <summary>Proxies the verified owner's bounded Plex profile image, with a cached fallback and no browser-visible account credentials.</summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetAsync(CancellationToken ct)
    {
        var settings = store.Settings;
        if (settings.PlexOwnerId.Length == 0 || settings.PlexToken.Length == 0) return null;
        await gate.WaitAsync(ct);
        try
        {
            if (owner != settings.PlexOwnerId) { cached = null; refreshAt = default; owner = settings.PlexOwnerId; }
            if (DateTimeOffset.UtcNow < refreshAt) return cached;
            refreshAt = DateTimeOffset.UtcNow.AddMinutes(5);
            var account = await api.PlexCloudAsync(settings, "user", ct, token: settings.PlexToken);
            if (account.Text("id") != settings.PlexOwnerId || !SafeAddress(account.Text("thumb"), out var address)) return cached;
            // Plex redirects avatar endpoints to its asset hosts. Revalidate each hop and never forward account credentials.
            for (var hop = 0; hop < 3; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address);
                using var response = await clients.CreateClient("media").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location is null || !SafeAddress(new Uri(address!, location).AbsoluteUri, out address)) return cached;
                    continue;
                }
                var type = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode || type is not ("image/png" or "image/jpeg" or "image/webp")
                    || response.Content.Headers.ContentLength > 2 * 1024 * 1024) return cached;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                using var output = new MemoryStream();
                var buffer = new byte[16_384]; int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (output.Length + read > 2 * 1024 * 1024) return cached;
                    output.Write(buffer, 0, read);
                }
                if (output.Length == 0) return cached;
                cached = (output.ToArray(), type);
                refreshAt = DateTimeOffset.UtcNow.AddHours(6);
                return cached;
            }
            return cached;
        }
        catch (Exception exception) when (exception is UpstreamException or HttpRequestException or IOException or TaskCanceledException)
        { return cached; }
        finally { gate.Release(); }
    }

    /// <summary>Restricts upstream image requests to HTTPS Plex hosts, excluding credentials, nonstandard ports, and other services.</summary>
    private static bool SafeAddress(string value, out Uri? address)
    {
        address = null;
        return value.Length <= 2048 && Uri.TryCreate(value, UriKind.Absolute, out address)
            && address.Scheme == "https" && address.Port == 443 && address.UserInfo.Length == 0
            && (address.Host == "plex.tv" || address.Host.EndsWith(".plex.tv", StringComparison.OrdinalIgnoreCase));
    }
}
