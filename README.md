# Elementarr

Private movie and TV tracking with Plex-only sign-in, independent watched histories, Radarr/Sonarr collections, and TMDB discovery. One Plex owner per installation; no personal Plex account is bundled.

Production runs as a self-contained Windows service with a tray companion and publisher-verified automatic updates from the private `briancwelch/Elementarr` GitHub repository. Development and testing remain on Carbon's local Docker containers before building/pushing releases. See [Windows installation, ports, metadata, and updates](docs/WINDOWS.md).

TMDB API access is required for full movie/TV posters and metadata. Obtain a developer credential from [TMDB API settings](https://www.themoviedb.org/settings/api) and paste the **API Read Access Token** into Connections. TMDB's developer API is [free for non-commercial use with attribution](https://developer.themoviedb.org/docs/faq), which Elementarr displays. Authenticated Sonarr/Plex artwork provides a TV fallback. TVDB IDs, TMDB TV IDs, and Plex parent-show paths resolve TV records without mixing them with movies.

**Watched Movie History** and **Watched TV History** use separate models, snapshots, metadata, and browsing routes. Movie history supports persistent removal of all plays of one source item, with deliberate restoration available in Administration. Plex retains its own source records. Sonarr additions use the same button pattern as Radarr, validate the destination, reject duplicates, and search for downloads only when explicitly selected.

## Local validation and release

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Local.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Windows.ps1 -Compiler C:\path\to\ISCC.exe
```

Builds require the original ignored `.local/release-signing.pk8` key and a passing source-matched local Docker validation gate. Private keys, Plex/API/GitHub tokens, watch history, data volumes, local probes, and build output are excluded from Git. Future release pushes must remain private.

The Windows installer lets users choose hostname, operating port, and local-only/LAN access. The native service runs as LocalService at boot, while the tray runs in the interactive session. Data is kept privately in `%PROGRAMDATA%\Elementarr\data`; backups include the complete directory and its keys. Automatic updates protect that data, verify signed metadata/checksums, and restore the previous application/data if startup fails. A read-only GitHub repository token must be configured per installation to activate private updates.

Docker remains the Carbon development environment. Its default URL is http://localhost:8090; `.env.example` controls host binding/port. `compose.radium.yaml` is an optional legacy service-host mapping overlay, not a production dependency. Previous Radium Docker deployments are retained as rollback history after migration to the native service.

See [validation evidence](docs/VALIDATION.md), [deployment guidance](docs/DEPLOYMENT.md), and [third-party notices](docs/THIRD_PARTY.md).
