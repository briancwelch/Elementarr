Elementarr 0.4.0 adds a native Windows service, tray controls, installer hostname/port setup, and publisher-verified automatic updates from this private repository. Production data remains separate from application files, and failed update startup restores the previous application/data.

TV collections now load persistent TMDB metadata even when Sonarr already supplies a TMDB ID. TVDB-hosted Sonarr artwork has an authenticated local fallback, Plex parent-show paths are resolved correctly, and Add to Sonarr uses the Radarr button pattern and a working confirmed action.

Movie history supports removing all plays of an exact source item, with persistent sync exclusions and explicit restoration. Onboarding explains TMDB's free non-commercial API access, links to signup/terms, and identifies the required API Read Access Token.

Configure a per-installation read-only GitHub Contents token under Connections > Automatic updates to activate private release access. The installer is not Authenticode-signed; automatic update manifests and payload hashes have independent publisher verification.
