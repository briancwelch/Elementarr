using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace Elementarr.Services;

/// <summary>Reports a sanitized upstream error with its status for narrowly scoped recovery.</summary>
public sealed class UpstreamException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class MediaApi(IHttpClientFactory clients) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 48 * 1024 * 1024 });

    /// <summary>Releases the explicitly bounded upstream-response cache when the application stops.</summary>
    public void Dispose() => cache.Dispose();

    /// <summary>Fetches artwork from an authenticated source without exposing source credentials or arbitrary image URLs to the browser.</summary>
    public async Task<(byte[] Bytes, string ContentType)> TvArtworkAsync(Connections settings, string source, int id, CancellationToken ct)
    {
        if (id <= 0 || source is not ("sonarr" or "plex" or "plex-episode")) throw new ArgumentException("Choose a valid TV artwork item.");
        var url = source == "sonarr" ? settings.SonarrUrl.TrimEnd('/') + $"/MediaCover/{id}/poster.jpg"
            : settings.PlexUrl.TrimEnd('/') + $"/library/metadata/{id}/thumb";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (source == "sonarr") request.Headers.Add("X-Api-Key", settings.SonarrKey);
        else PlexHeaders(request, settings, settings.PlexToken);
        try
        {
            using var response = await clients.CreateClient("media").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new UpstreamException("TV artwork is unavailable.", response.StatusCode);
            if (response.Content.Headers.ContentLength > 10 * 1024 * 1024) throw new UpstreamException("TV artwork exceeds the size limit.");
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/jpeg" or "image/png" or "image/webp")) throw new UpstreamException("The TV source returned unsupported artwork.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[16_384];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (output.Length + read > 10 * 1024 * 1024) throw new UpstreamException("TV artwork exceeds the size limit.");
                output.Write(buffer, 0, read);
            }
            return (output.ToArray(), type);
        }
        catch (HttpRequestException) { throw new UpstreamException("The TV artwork source could not be reached."); }
    }

    /// <summary>Calls Sonarr v3/v4 with the API credential in a header and preserves reverse-proxy base paths.</summary>
    public Task<JsonElement> SonarrAsync(Connections settings, string path, CancellationToken ct, JsonNode? body = null)
    {
        if (string.IsNullOrWhiteSpace(settings.SonarrKey)) throw new UpstreamException("Add your Sonarr API key in Connections.");
        var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            settings.SonarrUrl.TrimEnd('/') + "/api/v3/" + path);
        request.Headers.Add("X-Api-Key", settings.SonarrKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return SendAsync(request, "Sonarr", ct);
    }
    /// <summary>Requests Radarr resources with the API key kept in an HTTP header.</summary>
    public Task<JsonElement> RadarrAsync(Connections settings, string path, CancellationToken ct,
        JsonNode? body = null)
    {
        if (string.IsNullOrWhiteSpace(settings.RadarrKey)) throw new UpstreamException("Add your Radarr API key in Connections.");
        var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,
            settings.RadarrUrl.TrimEnd('/') + "/api/v3/" + path);
        request.Headers.Add("X-Api-Key", settings.RadarrKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return SendAsync(request, "Radarr", ct);
    }

    /// <summary>Uses a form body to keep the Tautulli API key out of URL and access logs.</summary>
    public async Task<JsonElement> TautulliAsync(Connections settings, string command, CancellationToken ct,
        params (string Key, string Value)[] parameters)
    {
        if (string.IsNullOrWhiteSpace(settings.TautulliKey)) throw new UpstreamException("Add your Tautulli API key in Connections.");
        var fields = new Dictionary<string, string> { ["apikey"] = settings.TautulliKey, ["cmd"] = command };
        foreach (var (key, value) in parameters) fields[key] = value;
        var request = new HttpRequestMessage(HttpMethod.Post, settings.TautulliUrl.TrimEnd('/') + "/api/v2")
            { Content = new FormUrlEncodedContent(fields) };
        var json = await SendAsync(request, "Tautulli", ct);
        if (!json.TryGetProperty("response", out var response) || response.Text("result") != "success")
            throw new UpstreamException("Tautulli rejected the request. Check the API key, selected user, and server version.");
        return response.GetProperty("data").Clone();
    }

    /// <summary>Caches movie metadata briefly and authenticates TMDB requests on the server.</summary>
    public async Task<JsonElement> TmdbAsync(Connections settings, string path, CancellationToken ct, bool cacheResponse = true)
    {
        if (string.IsNullOrWhiteSpace(settings.TmdbToken)) throw new UpstreamException("Add a TMDB API Read Access Token in Connections to discover movies.");
        var cacheKey = (settings.TmdbToken, path);
        if (cacheResponse && cache.TryGetValue<JsonElement>(cacheKey, out var cached)) return cached;
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.TmdbToken);
        var result = await SendAsync(request, "TMDB", ct);
        if (cacheResponse) cache.Set(cacheKey, result, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            Size = Math.Max(512L, result.GetRawText().Length * 3L)
        });
        return result;
    }

    /// <summary>Calls Plex's documented PIN/account APIs without putting access tokens in request URLs.</summary>
    public Task<JsonElement> PlexCloudAsync(Connections settings, string path, CancellationToken ct,
        bool createPin = false, string? token = null)
    {
        var request = new HttpRequestMessage(createPin ? HttpMethod.Post : HttpMethod.Get,
            "https://plex.tv/api/v2/" + path);
        PlexHeaders(request, settings, token);
        if (createPin) request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["strong"] = "true" });
        return SendAsync(request, "Plex account", ct);
    }

    /// <summary>Reads the configured local Plex server with the linked account token kept in a header.</summary>
    public Task<JsonElement> PlexAsync(Connections settings, string path, CancellationToken ct)
    {
        if (settings.PlexToken.Length == 0) throw new UpstreamException("Connect Plex in Connections to import your Plex history.");
        var request = new HttpRequestMessage(HttpMethod.Get, settings.PlexUrl.TrimEnd('/') + "/" + path);
        PlexHeaders(request, settings, settings.PlexToken);
        return SendAsync(request, "Plex server", ct);
    }

    /// <summary>Identifies this stable Plex device and requests JSON for both cloud and server APIs.</summary>
    private static void PlexHeaders(HttpRequestMessage request, Connections settings, string? token)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Plex-Product", "Elementarr");
        request.Headers.Add("X-Plex-Version", "0.3");
        request.Headers.Add("X-Plex-Client-Identifier", settings.PlexClientId);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Add("X-Plex-Token", token);
    }

    /// <summary>Checks bounded upstream responses and translates failures without disclosing credentials or response bodies.</summary>
    private async Task<JsonElement> SendAsync(HttpRequestMessage request, string service, CancellationToken ct)
    {
        using (request)
        {
            try
            {
                using var response = await clients.CreateClient("media").SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    throw new UpstreamException($"{service} returned HTTP {(int)response.StatusCode}.", response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                return document.RootElement.Clone();
            }
            catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.NameResolutionError)
            { throw new UpstreamException($"Elementarr cannot resolve the {service} hostname. Check the container's host mapping and service URL."); }
            catch (HttpRequestException) { throw new UpstreamException($"Cannot reach {service} from Elementarr. Check its address and firewall."); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new UpstreamException($"{service} timed out. Try again shortly."); }
            catch (JsonException) { throw new UpstreamException($"{service} returned an unexpected response. Check the base URL."); }
        }
    }
}
