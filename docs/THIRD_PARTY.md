# Dependencies and API references

Frontend assets are vendored under `src/Elementarr/wwwroot/vendor` and served from the container. npm is only needed when updating these assets; it is not needed to build or run Elementarr.

| Dependency | Version | License / origin |
| --- | --- | --- |
| Bootstrap | 6.0.0-alpha.1 | MIT, [official release](https://blog.getbootstrap.com/2026/10/08/bootstrap-6-alpha/), npm `bootstrap@6.0.0-alpha.1` |
| Font Awesome Free | 7.3.1 | Fonts SIL OFL 1.1, code MIT, icons CC BY 4.0; npm `@fortawesome/fontawesome-free@7.3.1` |
| ASP.NET Core / .NET | 10 LTS | Microsoft official SDK and chiseled runtime images, [support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) |

The original Bootstrap and Font Awesome license files accompany the vendored assets. The Dockerfile uses the .NET 10 image channel so rebuilds can receive servicing updates. The installed SDK is selected by `global.json`.

API contracts consulted:

- [Plex Media Server API](https://developer.plex.tv/pms/): server identity, owner accounts, paginated playback history, and metadata GUIDs. History uses the documented account filter and client-side movie filtering; the unsupported `type` query parameter is deliberately omitted.
- [Plex's authorization guide](https://forums.plex.tv/t/authenticating-with-plex/609370): strong PIN creation, official Plex authorization page, server-side PIN polling, and stable client identifiers. Account tokens never pass through Elementarr's browser API.
- [Radarr v3 OpenAPI specification](https://github.com/Radarr/Radarr/blob/develop/src/Radarr.Api.V3/openapi.json): movies, lookup by TMDB, quality profiles, root folders, and movie creation.
- [Tautulli API reference](https://github.com/Tautulli/Tautulli/wiki/Tautulli-API-Reference): user enumeration, paginated movie history, metadata and GUIDs.
- [TMDB movie recommendations](https://developer.themoviedb.org/reference/movie-recommendations), [discover movies](https://developer.themoviedb.org/reference/discover-movie), and [attribution requirements](https://developer.themoviedb.org/docs/faq).
- [Bootstrap 6 migration reference](https://getbootstrap.com/docs/6.0/guides/migration/): v6 theme/button classes, responsive class prefixes, and form controls.
- [Bootstrap 6 Drawer](https://getbootstrap.com/docs/6.0/components/drawer/) and [badges](https://getbootstrap.com/docs/6.0/components/badge/): native-dialog drawers, source/action labels, keyboard handling, and focus behavior.
- [Google Fonts CSS API](https://developers.google.com/fonts/docs/css2): optional named-family stylesheets with `display=swap`. No Google API key is required; no external font request occurs unless enabled in Administration.

TMDB movie data and artwork remain subject to TMDB's terms. Elementarr displays the required notice and an approved, unmodified TMDB logo in its Credits footer. The logo was downloaded from the [official attribution page](https://www.themoviedb.org/about/logos-attribution).

TV and Windows packaging references:

- [Sonarr v3/v4 OpenAPI specification](https://github.com/Sonarr/Sonarr/blob/develop/src/Sonarr.Api.V3/openapi.json): series, TVDB lookup, profiles, root folders, add options, and episode statistics.
- [TMDB TV search](https://developer.themoviedb.org/reference/search-tv), [TV recommendations](https://developer.themoviedb.org/reference/tv-series-recommendations), and TVDB external-ID lookup. Movie and TV namespaces remain independent.
- [Inno Setup](https://jrsoftware.org/isinfo.php): installer built locally using the Inno Setup 7.1.0 compiler distributed by the Tools.InnoSetup NuGet package. The compiler is a build tool and is not redistributed with Elementarr.
- The self-contained Windows application includes the official Microsoft .NET/ASP.NET Core runtime and its published license/third-party notices.

Windows service release 0.4 uses Microsoft.Extensions.Hosting.WindowsServices and System.ServiceProcess.ServiceController 10.0.14, the official self-contained .NET/ASP.NET Core/Windows Desktop 10.0.14 runtimes, and Inno Setup 7. Runtime licenses accompany each native component. The tray/installer icon derives from the bundled Font Awesome Free 7.3.1 `layer-group` SVG (Font Awesome, CC BY 4.0).

Private update assets follow [GitHub release-asset authentication](https://docs.github.com/en/rest/releases/assets); signing and checksums use .NET's ECDSA P-256 and SHA-256 implementations. Windows hosting follows [Microsoft's Windows Service guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0).
