# Elementarr Windows service

Run `Elementarr-0.4.0-win-x64-Setup.exe` on Windows 10/11 x64. The installer includes .NET 10 and requires administrator approval to register the service. Docker, Node.js, a separate .NET installation, and Git are not required on the production machine.

The installer asks for a **hostname**, **operating port**, and **local-only or private LAN access**. The default is `http://localhost:8090`. For LAN access, use an existing reachable hostname such as your computer name and select private LAN access. This adds a firewall rule restricted to the Private profile and local subnet. It does not create a DNS record. The finish page and tray menu show the configured URL.

The Windows service starts at boot as **LocalService**, using its own service SID. The optional tray companion starts in the installing user's interactive Windows session. It offers Open Elementarr, Start service, Stop service, Restart service, Updates and settings, Stop service and exit, and Exit tray while keeping the service running. The Start menu also opens the web interface. Closing a browser or exiting only the tray does not stop the service.

Plex is the only application sign-in. The first verified Plex account owns a fresh installation; existing data retains its linked owner. Service credentials and data live in `%PROGRAMDATA%\Elementarr\data`, separately from `%ProgramFiles%\Elementarr\app`. Administrator-writable tray/updater bootstrap programs are outside the service's writable application directory. Uninstall removes the service, tray task, and app-owned firewall rule while preserving private data.

## Metadata setup

TMDB supplies movie and TV posters, descriptions, ratings, genres, credits, and discovery. Its developer API is **free for non-commercial use with attribution**, as explained in the [official FAQ](https://developer.themoviedb.org/docs/faq). Elementarr displays TMDB's required credit. Create/sign in to TMDB, request API access in [Settings > API](https://www.themoviedb.org/settings/api), and copy the **API Read Access Token** into Connections > TMDB. Use that long token rather than the shorter v3 API key.

Configure your own Radarr/Sonarr URL and API key, check connections, and choose each service's root folder and quality profile. Movie and TV histories remain separate. TV artwork can fall back to authenticated Sonarr/Plex image proxies; source credentials are never placed in image URLs. Existing incomplete TV snapshots repair in the background, with an explicit Refresh TV metadata & matches action available in Connections.

## Private automatic updates

In **Connections > Automatic updates**, enter a fine-grained GitHub token scoped only to the private `briancwelch/Elementarr` repository with **Contents: read**. The token stays protected on this installation and is never bundled with software. Private repository access is required to read private release assets; it is independent of Plex sign-in. Tokens must be renewed if they expire or are revoked.

Automatic checks run after startup and every six hours. Automatic installation can be disabled independently, and Check for updates / Install update remain explicit actions. The installed Windows service downloads stable releases using GitHub's authenticated release-asset API. It verifies a publisher-signed manifest and the complete ZIP's SHA-256 and byte count before stopping anything. Storage redirects do not receive the GitHub token.

The fixed updater stops Elementarr, waits for its executable to exit, backs up application files and private data, installs only the application payload, and requires the new local health endpoint to report the signed version. Failed startup restores the previous application and data. Data, encryption keys, and history are outside the update payload. Tray/updater bootstrap upgrades use the full installer when a newer bootstrap is required; normal automatic updates cover the service and web UI.

Change host/port in Connections > Server & port, save, and restart the service from the tray menu. Local Docker development changes its host port through Compose instead. Private service logs are in `%PROGRAMDATA%\Elementarr\data\logs`. Back up the entire data directory including keys. Do not share it or commit it: it contains personal history, protected credentials, and the keys needed to read those credentials.

## Movie-history cleanup

In Watched Movie History or Affected history, choose **Remove movie from history**. Confirming removes all plays of that exact source item and persists exclusions for future imports. Other library copies sharing the same TMDB ID and all TV history are independent. This changes Elementarr's records, not Plex's own history. Administration can allow removed items to return on the next sync.

## Local development and publishing

Develop and validate on Carbon using local Docker containers before building or pushing Windows releases. `scripts/Test-Local.ps1` runs Windows and Docker fixture checks and records a fingerprint of validated source. `scripts/Build-Windows.ps1` refuses changed or unvalidated source and requires the original private publisher key. The signing key stays in ignored `.local` storage and must be privately backed up; replacing it breaks trust for existing installations.

Full installer ZIPs contain app, tray, updater, licenses, and service configuration. The separate update ZIP contains only service/web application files. Release JSON/signature/checksums accompany private GitHub releases. No application data or access tokens enter GitHub or release packages. Windows installers are currently not Authenticode-signed; update payloads have independent publisher-signature verification.
