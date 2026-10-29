using System.Security.Claims;
using System.Threading.RateLimiting;
using Elementarr.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Elementarr;

public static class AppHost
{
    /// <summary>Creates the LAN application with persistent sessions, protected settings, and authenticated API routes.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (OperatingSystem.IsWindows()) builder.Host.UseWindowsService(options => options.ServiceName = "Elementarr");
        // Native installations run without administrator rights or access to Windows Event Log sources.
        builder.Logging.ClearProviders().AddSimpleConsole();
        configure?.Invoke(builder);
        var dataDirectory = Path.GetFullPath(builder.Configuration["ELEMENTARR_DATA"] ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elementarr", "data")
            : Path.Combine(builder.Environment.ContentRootPath, "data")));
        Directory.CreateDirectory(dataDirectory);
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()) builder.Logging.AddProvider(new ServiceFileLogger(Path.Combine(dataDirectory, "logs")));
        builder.Configuration["ELEMENTARR_DATA"] = dataDirectory;
        var server = new ServerSettingsStore(dataDirectory);
        builder.Services.AddSingleton(server);
        if (!server.Container && string.IsNullOrWhiteSpace(builder.Configuration["urls"]) && string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
            builder.WebHost.UseUrls($"http://{server.Settings.BindAddress}:{server.Settings.Port}");
        builder.Configuration["AllowedHosts"] ??= "*";
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        builder.Services.AddDataProtection().SetApplicationName("Elementarr")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            // API clients receive status codes and the browser handles the login view.
            options.Cookie.Name = "elementarr.session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(12);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = context =>
            {
                // Invalidate legacy password sessions and cookies for an installation whose owner has changed.
                var owner = context.HttpContext.RequestServices.GetRequiredService<StateStore>().Settings.PlexOwnerId;
                if (owner.Length == 0 || context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) != owner)
                    context.RejectPrincipal();
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "elementarr.csrf";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = 429;
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("plex-poll", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 90, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("plex-start", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient("media", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(25);
            client.MaxResponseContentBufferSize = 32 * 1024 * 1024;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Elementarr/0.3");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddHttpClient("updates", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Elementarr/0.4");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<UpdateService>());
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddSingleton<MediaApi>();
        builder.Services.AddSingleton<PlexService>();
        builder.Services.AddSingleton<PlexAvatarService>();
        builder.Services.AddSingleton<TvService>();
        builder.Services.AddSingleton<TvRefreshWorker>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<TvRefreshWorker>());
        builder.Services.AddSingleton<TvMetadataService>();
        builder.Services.AddSingleton<TvArtworkService>();
        builder.Services.AddSingleton<MediaService>();
        builder.Services.AddSingleton<DiscoveryService>();
        builder.Services.AddSingleton<MetadataService>();
        builder.Services.AddSingleton<BrowseService>();
        builder.Services.AddSingleton<UiService>();
        builder.Services.AddSingleton<MetadataIndexer>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<MetadataIndexer>());
        builder.Services.AddHostedService<SyncWorker>();
        var app = builder.Build();
        // Load state on startup so invalid persistent data fails visibly rather than resetting silently.
        _ = app.Services.GetRequiredService<StateStore>();
        app.Use(async (context, next) =>
        {
            // Block embedding and external script execution while allowing the selected movie image provider.
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' https://image.tmdb.org data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
            try { await next(context); }
            catch (AntiforgeryValidationException) { await ErrorAsync(context, 400, "Your session changed. Reload the page and try again."); }
            catch (UnauthorizedAccessException exception) { await ErrorAsync(context, 403, exception.Message); }
            catch (ArgumentException exception) { await ErrorAsync(context, 400, exception.Message); }
            catch (UpstreamException exception) { await ErrorAsync(context, 502, exception.Message); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { /* The browser disconnected. */ }
            catch (Exception exception)
            {
                app.Logger.LogError("Request failed with {ExceptionType}", exception.GetType().Name);
                await ErrorAsync(context, 500, "Elementarr could not complete the request. Check the application logs.");
            }
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            // Validate every state-changing API request, including anonymous login.
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api"))
                await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            await next(context);
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok", version = UpdateService.CurrentVersion, instance = app.Configuration["ELEMENTARR_INSTANCE"] ?? "" }));
        app.MapGet("/api/session", Session);
        app.MapGet("/api/appearance", (UiService ui) => Results.Ok(ui.Settings));
        app.MapGet("/api/appearance/defaults", () => Results.Ok(new UiSettings()));
        app.MapPost("/api/plex/auth/start", PlexStartAsync).RequireRateLimiting("plex-start");
        app.MapPost("/api/plex/auth/poll", PlexPollAsync).RequireRateLimiting("plex-poll");
        app.MapPost("/api/plex/auth/cancel", (HttpContext context, PlexService plex) => { plex.Cancel(context); return Results.Ok(); });
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/logout", (Delegate)LogoutAsync);
        api.MapGet("/settings", Settings);
        api.MapGet("/changelog", () => Results.Ok(new { version = UpdateService.CurrentVersion, releases = ReleaseNotes.All }));
        api.MapGet("/plex/avatar", async (PlexAvatarService avatars, CancellationToken ct) =>
        {
            var value = await avatars.GetAsync(ct);
            return value is { } image ? (IResult)Results.File(image.Bytes, image.ContentType) : Results.NotFound();
        });
        api.MapGet("/updates", (UpdateService updates) => Results.Ok(new { updates.Status, updates.CanInstall,
            enabled = updates.Preferences.Enabled, autoInstall = updates.Preferences.AutoInstall,
            repository = Elementarr.UpdateCore.ReleaseTrust.Repository }));
        api.MapPost("/updates/settings", async (UpdatePreferenceRequest request, UpdateService updates, CancellationToken ct) =>
        { await updates.SaveAsync(request, ct); return Results.Ok(); });
        api.MapPost("/updates/check", async (UpdateService updates, CancellationToken ct) => Results.Ok(await updates.CheckAsync(ct)));
        api.MapPost("/updates/install", (UpdateService updates) => { updates.Install(); return Results.Accepted(); });
        api.MapGet("/server", (ServerSettingsStore server) => Results.Ok(new { server.Settings, server.Running, server.Container, restartRequired = server.Settings != server.Running }));
        api.MapPost("/server", async (ServerSettings value, ServerSettingsStore server, CancellationToken ct) =>
        { await server.SaveAsync(value, ct); return Results.Ok(new { restartRequired = server.Settings != server.Running }); });
        api.MapPost("/settings", async (ConnectionUpdate value, MediaService service, CancellationToken ct) =>
        {
            // Saving credentials does not call or modify upstream services.
            await service.SaveAsync(value, ct);
            return Results.Ok();
        });
        api.MapPost("/connections/check", async (MediaService service, CancellationToken ct) => Results.Ok(await service.CheckAsync(ct)));
        api.MapGet("/state", State);
        api.MapGet("/plex/servers", async (PlexService plex, CancellationToken ct) => Results.Ok(await plex.ServersAsync(ct)));
        api.MapPost("/plex/server", async (PlexServerRequest request, MediaService media, CancellationToken ct) =>
        { await media.SelectPlexServerAsync(request.ServerId, ct); return Results.Ok(); });
        api.MapGet("/tv/browse", async (HttpRequest request, TvService tv, CancellationToken ct) =>
            Results.Ok(await tv.BrowseAsync(request.Query, ct)));
        api.MapGet("/tv/metadata/status", (TvRefreshWorker refresh) => Results.Ok(refresh.Status));
        api.MapPost("/tv/metadata/refresh", (TvRefreshWorker refresh) => { refresh.Start(); return Results.Ok(refresh.Status); });
        api.MapGet("/tv/sonarr/{id:int}", async (int id, TvService tv, CancellationToken ct) => Results.Ok(await tv.ManagedDetailAsync(id, ct)));
        api.MapGet("/tv/art/{source}/{id:int}", async (string source, int id, TvArtworkService artwork, CancellationToken ct) =>
        { var result = await artwork.GetAsync(source, id, ct); return Results.File(result.Bytes, result.ContentType); });
        api.MapGet("/tv/{id:int}", async (int id, TvService tv, CancellationToken ct) => Results.Ok(await tv.DetailAsync(id, ct)));
        api.MapGet("/tv/history", (StateStore store) => Results.Ok(store.Tv.History));
        api.MapPost("/sonarr/{id:int}", async (int id, AddRequest request, TvService tv, CancellationToken ct) =>
        { await tv.AddAsync(id, request.SearchNow, ct); return Results.Ok(); });
        api.MapGet("/browse", async (HttpRequest request, BrowseService browse, CancellationToken ct) =>
            Results.Ok(await browse.BrowseAsync(BrowseOptions.FromQuery(request.Query), ct)));
        api.MapGet("/movies/{id:int}", async (int id, MetadataService metadata, BrowseService browse, CancellationToken ct) =>
        {
            var movie = await metadata.GetAsync(id, true, ct);
            await metadata.SaveAsync(ct);
            return Results.Ok(browse.Annotate(movie));
        });
        api.MapGet("/filter-options", (MetadataService metadata) => Results.Ok(new
        {
            genres = MetadataService.Genres.Select(x => new Choice(x.Key.ToString(), x.Value)),
            ratings = metadata.Coverage()
        }));
        api.MapPost("/appearance", async (UiSettings value, UiService ui, CancellationToken ct) =>
        { await ui.SaveAsync(value, ct); return Results.Ok(ui.Settings); });
        api.MapPost("/ratings/import", async (RatingImport value, MetadataService metadata, CancellationToken ct) =>
            Results.Ok(new { imported = await metadata.ImportRatingsAsync(value, ct) }));
        api.MapPost("/history/remove", async (HistoryRemovalRequest request, MediaService media, CancellationToken ct) => Results.Ok(new { removed = await media.RemoveHistoryAsync(request.RatingKey, ct) }));
        api.MapPost("/history/restore-removed", async (MediaService media, CancellationToken ct) => { await media.RestoreHistoryAsync(ct); return Results.Ok(); });
        api.MapPost("/history/match", async (HistoryMatchRequest value, MediaService media, CancellationToken ct) =>
            Results.Ok(new { matched = await media.MatchHistoryAsync(value, ct) }));
        api.MapGet("/metadata/status", (MetadataIndexer indexer) => Results.Ok(indexer.Status));
        api.MapPost("/metadata/refresh", (MetadataIndexer indexer) => { indexer.Start(); return Results.Ok(indexer.Status); });
        api.MapPost("/radarr/{tmdbId:int}/search", async (int tmdbId, MediaService media, CancellationToken ct) =>
            Results.Ok(await media.SearchAsync(tmdbId, ct)));
        api.MapGet("/radarr/commands/{id:int}", async (int id, MediaService media, CancellationToken ct) =>
            Results.Ok(await media.SearchStatusAsync(id, ct)));
        api.MapPost("/sync", async (MediaService service, string? source, CancellationToken ct) =>
        {
            var result = await service.SyncAsync(ct, source ?? "all");
            return result.RadarrSynced || result.HistorySynced || result.SonarrSynced || result.TvHistorySynced ? Results.Ok(result)
                : Results.Json(new { error = string.Join(" ", result.Errors), result }, statusCode: 502);
        });
        api.MapGet("/discover", async (DiscoveryService service, string? view, string? query, int? page,
            bool? hideOwned, bool? hideWatched, CancellationToken ct) =>
            Results.Ok(await service.DiscoverAsync(view ?? "for-you", query, page ?? 1, hideOwned ?? true, hideWatched ?? true, ct)));
        api.MapGet("/library", Library);
        api.MapGet("/history", History);
        api.MapPost("/radarr/{tmdbId:int}", async (int tmdbId, AddRequest request, MediaService service, CancellationToken ct) =>
        {
            // Radarr receives a write only after the user confirms this movie in the browser.
            await service.AddAsync(tmdbId, request.SearchNow, ct);
            return Results.Ok();
        });
        return app;
    }

    /// <summary>Returns an antiforgery token bound to the current anonymous or authenticated browser session.</summary>
    private static IResult Session(HttpContext context, IAntiforgery antiforgery, StateStore store) => Results.Ok(new
    { authenticated = context.User.Identity?.IsAuthenticated == true, csrfToken = antiforgery.GetAndStoreTokens(context).RequestToken,
        plexSignInAvailable = true, firstRun = store.Settings.PlexOwnerId.Length == 0, version = UpdateService.CurrentVersion });

    /// <summary>Starts a server-managed Plex authorization without exposing PIN tokens or accepting arbitrary callback URLs.</summary>
    private static async Task<IResult> PlexStartAsync(HttpContext context, PlexAuthRequest request, PlexService plex, CancellationToken ct) =>
        Results.Ok(new { authUrl = await plex.StartAsync(context, request.Connect, ct) });

    /// <summary>Accepts the first verified Plex owner or the existing linked owner, saves the token server-side, and issues a normal local session cookie.</summary>
    private static async Task<IResult> PlexPollAsync(HttpContext context, PlexService plex, MediaService media, CancellationToken ct)
    {
        var authorization = await plex.PollAsync(context, ct);
        if (!authorization.Connected) return Results.Ok(new { status = "pending" });
        await media.LinkPlexAsync(authorization, ct);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, authorization.Username), new Claim(ClaimTypes.NameIdentifier, authorization.OwnerId)], CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Ok(new { status = authorization.Connect ? "connected" : "signed-in" });
    }

    /// <summary>Ends the current authenticated session.</summary>
    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync();
        return Results.Ok();
    }

    /// <summary>Returns connection preferences and credential-presence flags, never saved API secrets.</summary>
    private static IResult Settings(StateStore store)
    {
        var value = store.Settings;
        return Results.Ok(new { value.RadarrUrl, value.TautulliUrl, value.PlexUsername, value.PlexUserId,
            value.QualityProfileId, value.RootFolder, value.Region, value.SonarrUrl, value.SonarrQualityProfileId, value.SonarrRootFolder, value.PlexServerId, value.PlexUrl, value.PlexOwnerName, value.TautulliEnabled,
            hasRadarrKey = value.RadarrKey.Length > 0, hasSonarrKey = value.SonarrKey.Length > 0, hasTautulliKey = value.TautulliKey.Length > 0,
            hasTmdbToken = value.TmdbToken.Length > 0, hasPlexToken = value.PlexToken.Length > 0 });
    }

    /// <summary>Summarizes the last successful sync without presenting missing data as a successful import.</summary>
    private static IResult State(StateStore store)
    {
        var value = store.Snapshot;
        var tv = store.Tv;
        var warnings = value.Warnings.Concat(new[] {
            value.LibraryError is null ? null : "Radarr sync failed: " + value.LibraryError,
            value.HistoryError is null ? null : "Movie history sync failed: " + value.HistoryError,
            tv.LibraryError is null ? null : "Sonarr sync failed: " + tv.LibraryError,
            tv.HistoryError is null ? null : "TV history sync failed: " + tv.HistoryError
        }.OfType<string>()).ToList();
        return Results.Ok(new { value.SyncedAt, warnings, value.LibrarySyncedAt, value.HistorySyncedAt,
            tvLibraryCount = tv.Library.Count, tvHistoryCount = tv.History.Count,
            tvLibrarySyncedAt = tv.LibrarySyncedAt, tvHistorySyncedAt = tv.HistorySyncedAt,
            tvUnmatchedCount = tv.History.Count(x => x.TmdbId is null && x.TvdbId is null),
            value.HistorySource, value.LibraryError, value.HistoryError, unmatchedCount = value.History.Count(x => !x.TmdbId.HasValue), libraryCount = value.Library.Count,
            availableCount = value.Library.Count(x => x.HasFile), historyCount = value.History.Count,
            watchedCount = value.History.Where(x => x.Completed).Select(x => x.TmdbId?.ToString() ?? $"plex:{x.RatingKey}").Distinct().Count(),
            username = store.Settings.PlexOwnerName.Length > 0 ? store.Settings.PlexOwnerName : store.Settings.PlexUsername });
    }

    /// <summary>Provides paginated local library movies, including availability and completed-watch status.</summary>
    private static IResult Library(StateStore store, string? query, int? page)
    {
        var number = Math.Max(1, page ?? 1);
        var snapshot = store.Snapshot;
        var watched = snapshot.History.Where(x => x.Completed && x.TmdbId.HasValue).Select(x => x.TmdbId!.Value).ToHashSet();
        var movies = snapshot.Library.Where(x => string.IsNullOrWhiteSpace(query) || x.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Title).ToList();
        return Results.Ok(new MoviePage(movies.Skip((number - 1) * 40).Take(40).Select(x =>
            new Movie(x.TmdbId, x.Title, x.Year, x.Overview, x.Poster, null, 0, "", true, x.HasFile, watched.Contains(x.TmdbId))).ToList(),
            number, Math.Max(1, (int)Math.Ceiling(movies.Count / 40d))));
    }

    /// <summary>Provides paginated plays for the configured Plex user, preserving partial plays and unmatched movies.</summary>
    private static IResult History(StateStore store, int? page)
    {
        var number = Math.Max(1, page ?? 1);
        var history = store.Snapshot.History;
        return Results.Ok(new { plays = history.Skip((number - 1) * 50).Take(50), page = number,
            totalPages = Math.Max(1, (int)Math.Ceiling(history.Count / 50d)) });
    }

    /// <summary>Writes concise errors without exposing upstream payloads or local exception details.</summary>
    private static async Task ErrorAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
