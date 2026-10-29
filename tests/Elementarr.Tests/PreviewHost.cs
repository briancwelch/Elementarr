using System.Security.Claims;
using Elementarr;
using Elementarr.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

internal static class PreviewHost
{
    /// <summary>Runs a loopback-only UI preview against the integration fixtures, without using real accounts or upstream services.</summary>
    public static async Task RunAsync(string[] args)
    {
        var root = Path.GetFullPath("src/Elementarr/wwwroot");
        var directory = Path.GetFullPath(".local/fixture-preview");
        var port = 18091;
        var portIndex = Array.IndexOf(args, "--preview-port");
        if (portIndex >= 0 && (portIndex + 1 >= args.Length || !int.TryParse(args[portIndex + 1], out port) || port is < 0 or > 65535))
            throw new ArgumentException("Choose a valid loopback preview port.");
        var dataIndex = Array.IndexOf(args, "--preview-data");
        if (dataIndex >= 0)
        {
            directory = Path.GetFullPath(args[dataIndex + 1]);
            if (!directory.StartsWith(Path.GetFullPath(".local") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Preview data must stay under the ignored local development directory.");
        }
        var fixture = new MediaFixture { PlexClaimed = true };
        var host = AppHost.Build([], builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ELEMENTARR_DATA"] = directory });
            builder.WebHost.UseUrls("http://127.0.0.1:" + port);
            builder.Environment.WebRootPath = root;
            builder.Environment.WebRootFileProvider = new PhysicalFileProvider(root);
            builder.Services.AddSingleton<IHttpClientFactory>(fixture);
        });
        var store = host.Services.GetRequiredService<StateStore>();
        await store.SaveSettingsAsync(store.Settings with { PlexOwnerId = "42", PlexOwnerName = "fixture.owner", PlexUsername = "fixture.owner",
            PlexServerId = "plex-server-1", PlexUrl = "http://fixture-media.local:32400", PlexToken = "plex-secret",
            RadarrUrl = "http://fixture-radarr.local:7878", RadarrKey = "radarr-secret", TmdbToken = "tmdb-secret",
            QualityProfileId = 7, RootFolder = "/movies", SonarrUrl = "http://fixture-sonarr.local:8989", SonarrKey = "sonarr-secret",
            SonarrQualityProfileId = 8, SonarrRootFolder = "/tv" }, default);
        await host.Services.GetRequiredService<MediaService>().SyncAsync(default);
        // This authentication helper exists only in the test executable's explicit loopback preview mode.
        host.MapGet("/__fixture/sign-in", async (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Name, "fixture.owner")], CookieAuthenticationDefaults.AuthenticationScheme)));
            context.Response.Redirect("/");
        });
        await host.StartAsync();
        var addresses = host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        Console.WriteLine("PREVIEW_URL=" + addresses.Addresses.Single());
        await host.WaitForShutdownAsync();
    }
}
