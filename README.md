# Elementarr

Track your movies and TV shows, explore what to watch next, and manage your collection through Plex, Radarr, and Sonarr. Elementarr runs locally as a Windows service with a tray icon and automatic updates.

## Get started

1. Download the latest **Windows Setup.exe** from [Releases](https://github.com/briancwelch/Elementarr/releases/latest).
2. Run the installer and choose your hostname, port, and local-only or LAN access. The default address is **http://localhost:8090**.
3. Open Elementarr from the tray icon or Start menu and sign in with **Plex**. The first Plex account becomes the owner of that installation.
4. Open **Connections**, select your owned Plex server, and configure TMDB. Add Radarr and Sonarr if you use them, then sync your collection and watch history.

### TMDB posters and metadata

A TMDB **API Read Access Token** is required for full movie and TV posters, descriptions, ratings, cast, and discovery. Create a TMDB account and request developer API access in [TMDB API settings](https://www.themoviedb.org/settings/api), then paste the long API Read Access Token into **Connections > TMDB**.

TMDB's developer API is [free for non-commercial use with attribution](https://developer.themoviedb.org/docs/faq). Elementarr displays the required credit. Sonarr and Plex can also supply fallback TV artwork.

## Features

- Separate **Watched Movie History** and **Watched TV History**.
- Movie and TV discovery, recommendations, trending titles, and upcoming movies.
- Radarr and Sonarr collections, with confirmed add actions and optional download searches.
- Movie-history removal that stays removed after syncing, with restoration available in Administration. Plex keeps its own records.
- Windows tray controls to open, start, stop, restart, or exit Elementarr.
- Upcoming episodes from Sonarr and separate affected movie/TV history views.
- Live search, a built-in changelog, and version information in the navigation footer.
- Appearance controls for typography, themed scrollbars, badge corners, and timed notifications.
- Plex profile pictures and a confirmation before signing out.
- Configurable hostname, port, and LAN access.

## Automatic updates

Automatic updates are enabled by default in the Windows service. Elementarr checks for new public releases after startup and every six hours, verifies the publisher signature and download checksum, and installs newer versions automatically. **No GitHub account or token is needed.**

Use **Connections > Automatic updates** to change preferences or check manually. Updates preserve your settings and history, and failed startup restores the previous application and data.

## Your data

Plex is the only sign-in method, with one owner per installation. Settings, history, and credentials are stored locally in `%PROGRAMDATA%\Elementarr\data`. Back up the complete folder, including its keys. Uninstalling preserves this data.

See the [Windows guide](docs/WINDOWS.md) for installation, ports, tray controls, and backups. [Development and release instructions](docs/DEPLOYMENT.md) and [third-party notices](docs/THIRD_PARTY.md) are available separately.
