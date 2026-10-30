# Elementarr 0.5.2 validation - October 30, 2026

- **189 integration checks passed on Carbon Windows and in Docker**, including distinct-episode progress and 88-percent rounding, cached season lists with live watch flags, combined extensive TV filters, malformed bounds, multi-provider-page TV filtering, snapshot isolation, and conservative movie match repair.
- **16 frontend DOM regressions passed**, including TV-specific filter state, lazy season expansion, automatic saved-connection checks without credential submission, and vertical versus Shift-horizontal timeline wheel behavior.
- Actual headless Edge checks used the production Bootstrap drawers and isolated upstream fixtures. Full-width TV artwork, season episode/watch rendering, equal connection cards, desktop/mobile layouts, filter animation with expanded/collapsed navigation, and vertical page scrolling over the timeline passed without page errors. Artwork in these browser screenshots was substituted fixture imagery, not live provider artwork.
- The circular SVG and multi-size ICO favicon were rendered and visually inspected with transparent corners. The application and tray retain their existing brand artwork.
- The self-contained Windows 0.5.2 payload passed fresh Plex-only startup, packaged assets, and shutdown/identity preservation. Release metadata was signed and verified with the existing publisher key; the actual update ZIP matches its signed hash/size and the validated TV, CSS and favicon assets.
- The source-bound Carbon Docker gate is healthy at localhost:8090. No upstream add, download search, or real-account history mutation was used for testing.

# Elementarr 0.5.0 interface validation - October 29, 2026

- **157 integration checks passed on Windows and in Docker** and cover new appearance defaults/persistence/bounds, authenticated release identity, bounded credential-free Plex avatar proxying and safe Plex-only redirects, fourteen-day Sonarr episode filtering/order/search, separate affected-TV-history browsing, and the shared branding assets.
- **Seven frontend DOM tests** execute production UI modules against a loopback fixture server. They cover grouped navigation/changelog/version, debounced AJAX searching, stale-response rejection and focus retention, cancel/confirm logout, escaped countdown notifications with keyboard pause and automatic dismissal, upcoming/affected TV navigation, and the new appearance controls. Native drawer/dialog/popover primitives are substituted in these DOM tests; they are not a visual rendering claim.
- Read-only live checks using the approved ignored QA configuration retrieved the actual Plex profile PNG through its asset redirect and seven upcoming Sonarr episodes with valid air times. No live collection addition, download search, or watch-history change was used for testing.
- The new atom-and-clapperboard vector was rendered and visually inspected as the shared application/favicon/Windows icon. All fixed app text sizes now derive from rem/base typography and category scales; badges use the shared radius setting.
- The self-contained Windows payload passed fresh Plex-only startup, packaged asset serving, and shutdown/identity checks. The actual signed 0.5.0 ZIP passed signature, hash/size, safe extraction, logo, and favicon verification.
- The exact full 0.5.0 installer was deployed to Radium with its current hostname, port, and LAN scope retained. Health and session/footer metadata report 0.5.0. The LocalService account, one interactive tray process, Plex owner, protected connections, appearance file, and movie-history count were preserved. Matching tray/favicon hashes and HTTP 200 brand/favicon responses passed; runtime appearance reports the 4 px badge and 5-second notification defaults.
- A fresh automatic public-feed check completed successfully after service startup without credentials. Private code/data backups remain on Radium, and the old Docker app remains stopped.
- Browser automation was unavailable, and native Edge remained minimized, so this revision's full browser rendering was not visually verified.

# Elementarr 0.4.1 public-update validation - October 29, 2026

- **137 integration checks passed on Carbon Windows and in Docker**, including a complete anonymous release/asset flow using real published signed metadata, a GitHub storage redirect, legacy token removal with preferences preserved, and safe rate-limit handling.
- The actual update client verified the live public repository's latest signed release without any supplied or saved GitHub credential. Fresh-install defaults enable both scheduled checks and automatic installation.
- The update UI no longer requests GitHub tokens. The user README now covers installation, Plex/TMDB setup, features, public automatic updates, and local backups; machine-specific development/deployment instructions remain in the separate developer guide.
- The same-source Windows installer and update ZIP use the existing pinned publisher signing key. Private application data, encryption keys, and signing credentials remain excluded from source and release artifacts.

- The published **0.4.1** update was downloaded anonymously on Radium and applied successfully by the installed signature-verifying updater, initiated once through the administrator deployment session. The service returned healthy, retained Plex ownership and byte-identical protected media connections, and preserved the movie-history count from its quiescent backup. The tray remained running and Docker remained stopped.
- The new LocalService process subsequently performed its own scheduled startup check and logged **Public update check: idle. Elementarr is up to date.** No GitHub credential/preferences file was present. LAN health independently reported **0.4.1**.
- Exact signed fixture bytes are preserved in Git for clean Windows/Linux checkouts. The native package startup/assets/shutdown checks passed for the released payload.

The following 0.4.0 evidence predates public hosting. Its private-token setup instructions describe the earlier version and are superseded by 0.4.1.

# Elementarr 0.4.0 validation - October 29, 2026

- **134 integration checks passed on Carbon Windows and in Docker.** Fixture coverage includes Plex-only ownership, independent movie/TV stores and ID namespaces, Plex parent-path recovery, Sonarr add destinations/acknowledgements, authenticated poster proxying, persistent exact-item movie-history removal, restoration, secret redaction, signed-update validation, unsafe archive rejection, and failed-startup rollback.
- Frontend JavaScript and all PowerShell scripts pass syntax checks. Carbon's Docker app is healthy at `http://localhost:8090`. The release gate records the source fingerprint and rejects packaging after source changes.
- An explicitly approved private Radium copy was tested in an isolated Carbon Docker volume. All **165 TV series** received expanded metadata; **164** have TMDB poster paths, and the remaining **one** returned valid authenticated Sonarr artwork, for **165/165 effective poster coverage**. **16,502 / 20,310** episode watches now resolve to series. The remaining **3,808** have no usable parent-series data and remain visible. These were read-only upstream calls; no live Sonarr/Radarr addition or download search was created for testing.
- The self-contained Windows payload starts with a fresh Plex-only owner flow, serves all packaged movie/TV/update modules, reports version **0.4.0**, and preserves its protected identity on shutdown. Runtime packages are **.NET/ASP.NET Core/Windows Desktop 10.0.14** with their license notices.
- Release metadata is signed with the local publisher key and verified against the pinned public key. Payload hash/size checks and rollback are exercised through fixtures. The full installer itself is not Authenticode-signed. No credentials, source snapshots, encryption keys, private QA data, or signing keys are included in Git or release artifacts.
- Radium's initial native cutover retained **4,097 movies**, **3,264 movie watches**, **165 series**, **20,310 episode watches**, the existing Plex owner, and byte-identical protected connection credentials. The service reports healthy **0.4.0** as **NT AUTHORITY\LocalService** on port **8090**; the Docker container is stopped with restart disabled. Production metadata repair independently reached the same **165** detailed series and **16,502** resolved episode watches, without source errors.
- The consistent final Docker data backup is `C:\Elementarr\backups\native-cutover-20261029-122512`. Original Docker data/image and previous release coordinates remain available for rollback.

Private automatic fetching requires an installation-specific GitHub token with Contents: read for the private repository. No developer token is bundled. Signature/hash/download and rollback checks do not by themselves establish a complete future-version production update cycle; that requires a subsequent newer published release. The exact final installer was installed successfully on Radium: service Running, one tray process in desktop session 1, tray task Running, and protected connections unchanged. The installed installer SHA-256 is `2dfd42af36e00a5d989fcaed5ac6728ee391087db6ad95f6547d134477b7ee8c`, matching `artifacts/SHA256SUMS.txt`. Both initial service installation and subsequent installer upgrade were exercised. The real signed update ZIP passed the shipped trust verifier, exact hash/size validation, and safe extraction of 394 application files. LAN health from Carbon reports 0.4.0.

Browser visual inspection of this final revision could not complete: the browser access host was unavailable and the native Edge window was minimized. Earlier 0.3 visual results below are historical evidence, not a claim about this revision.

## Historical 0.3 and earlier evidence

The following record predates the native-service cutover. Its Docker production, password login, and publication statements describe those earlier deployments.

# TV, Plex-only ownership, and Windows package validation - October 29, 2026

Current local verification for Elementarr 0.3.0:

- **112 integration checks passed**, using local Plex/Radarr/Sonarr/Tautulli/TMDB fixtures. Coverage includes first-owner Plex sign-in without a password, rejection of unrelated accounts and legacy password cookies, protected credentials, verified owned-server selection, movie/TV numeric-ID collisions, episode-versus-show identifiers, foreign-profile rejection, rewatches, separate snapshot reloads, independent source failures, duplicate protection, Sonarr destination validation, opt-in search, and saved/invalid operating ports.
- **4 native Windows package checks passed** (`tests/Test-Windows.ps1`): self-contained startup on a chosen loopback port; explicit occupied-port failure without accepting another instance's health response; restart on a changed port with the same protected installation identity; and process-scoped stop with data retained. The native startup path now uses console/file logs without requiring Windows Event Log permissions and returns normal failure codes instead of leaving crash-reporting processes open.
- All frontend JavaScript modules and PowerShell scripts passed syntax checks. Generic and Radium-overlay Compose definitions passed configuration validation. These local checks preceded the Radium deployment recorded below.
- Browser validation used the explicit test executable's loopback fixture preview on port 18091. Verified separate Watched Movie History / Watched TV History navigation, the episode-only timeline, show/season/episode details, TV-specific Sonarr actions, unchecked immediate-search opt-in, Sonarr connection fields, and operating-port preferences. Account and hostname labels display the current installation rather than a bundled personal identity.
- Self-contained Windows x64 ZIP and Inno Setup 7 installer compiled successfully, bundling .NET/ASP.NET Core 10.0.13 and runtime license notices. The installer wizard was not installed onto the host during verification. Native executable/launcher behavior was tested from the published package.
- Package inventory checks exclude private credentials, data snapshots, Data Protection keys, .env files, and fixture sources. SHA-256 hashes accompany the packages in `artifacts/SHA256SUMS.txt`. Packages are unsigned and remain local.

The local test phase did not perform live Sonarr requests, real-account Plex authorization, GitHub publication, or an automatic update release. Radium was subsequently deployed with explicit approval, as recorded below. TV history currently uses Plex only; optional Tautulli fallback is movie-only. Remote TV discovery uses provider pagination; movie filters do not apply to it.

## Radium deployment - October 29, 2026

Deployed with explicit user authorization using `scripts/Deploy-Radium.ps1`. The active release is `C:\Elementarr\releases\20261029-104506`, serving **http://radium.local:8090** through `10.0.0.14:8090 ? 8080`. Docker rebuilt the application successfully, and the replacement container reports running/healthy, non-root user 1654, and a read-only root filesystem.

Before switching containers, the prior image was preserved as `elementarr:pre-tv-20261029-104454`. A consistent paused-container copy of private data and the existing host configuration was saved in `C:\Elementarr\backups\pre-tv-20261029-104454`. The previous release path is recorded there for rollback. Backups remain private on Radium.

Post-deployment verification confirmed:

- The protected connection file and movie snapshot have the same SHA-256 hashes as the pre-deployment backup.
- All **4,097 collection movies** and **3,264 movie-history entries** are preserved; the existing linked Plex owner is recognized rather than treated as a fresh installation.
- `/health` returns `ok`, the TV frontend module returns HTTP 200, and both Watched Movie History and Watched TV History navigation entries are served.
- Anonymous settings access returns HTTP 401. The removed password-login endpoint returns HTTP 404 with a valid anonymous CSRF token.
- The existing data volume, LAN port, and Radium service-host mapping are retained.

This deployment did not enter Sonarr credentials or perform a live Sonarr collection import. Sonarr must be configured in Connections. TV history can be synced independently using the retained Plex connection; no manual TV-history import was initiated as part of deployment verification. Existing browser sessions created through password login must sign in again through Plex.

## Historical foundation and deployment evidence

The following record describes the earlier movie-only build and is not evidence of a deployment of 0.3.0.

### Foundation validation - October 28, 2026

Elementarr runs at **http://radium.local:8090** on Radium and **http://localhost:8090** on Carbon. Production and development have separate configuration and data volumes.

## Automated checks

- `dotnet run --project tests/Elementarr.Tests --no-launch-profile`: **90 passing integration checks** using local upstream fixtures, with no live Radarr writes.
- Coverage includes password authentication, CSRF, protected settings, browser-bound Plex PIN authorization, owner-only Plex sign-in, one-use challenges, server ownership, user-scoped pagination, independent collection/history imports, optional fallback, mixed TV/movie history, watched filtering, recommendation reasons, and preservation of good data on failure.
- Radarr addition fixtures verify destination selection, explicit search-now behavior, and duplicate protection.
- State-store reload checks restore credentials, library, history, and confirmed fixture additions.
- `node --check src/Elementarr/wwwroot/app.js`: passed.
- PowerShell parser checks for the local startup and deployment scripts: passed.
- Compose configuration validation and Docker builds: passed. Both containers are healthy.
- Local HTTP smoke checks served the application, Bootstrap 6 assets, Font Awesome 7 Free font, and TMDB attribution assets successfully.

## Live Radium checks

- The user completed the official Plex authorization window successfully as **brian.welch**. Elementarr saved the token privately and reports Plex sign-in available. The linked identity and credentials survived a subsequent deployment.
- A Radarr-only scan succeeded with optional Tautulli disabled: **4,074 movies** imported. Discovery generated recommendations from collection data before any successful history import.
- The complete direct Plex history import succeeded in approximately **30 seconds**: **3,264 movie history entries**, representing **1,397 distinct watched movies** in the stored snapshot. The independent Radarr collection remained at **4,074 movies**.
- The final import reported `historySource=plex` even with optional Tautulli enabled. Plex has priority, and normal TV history is excluded without a false missing-metadata warning.
- **112 history entries** could not be matched reliably to TMDB. They remain visible with a warning; watched filtering may miss these movies. This is an explicit limitation rather than a successful mapping claim.
- The personalized feed returned **41 recommendations** after history import. Counts can change as TMDB data or the local snapshot changes.
- Saved integration credentials were retained. Live authenticated API connection checks passed after the container hostname fix.
- The full history endpoint was verified against the official Plex API contract. The undocumented `type=1` parameter reduced this server's history while still including episodes. It was removed; the importer now paginates owner history and filters movies explicitly. Pagination totals and offsets are checked before committing a replacement snapshot.

## Timeline, filtering, details, and administration

- The 90-check suite includes full-list sorting before pagination, year/genre/rating/runtime/vote/status filters, all four supported page sizes, browser-offset watch dates, source rating scales, metadata persistence, persistent manual matching, server-side appearance validation, and Radarr search acknowledgements/failures.
- Live library/history data survived deployment: 4,074 collection movies, 3,264 plays, and 112 unresolved history entries.
- A fresh independent Radarr scan populated runtime, genre, release, and review fields without changing Plex history.
- The live 1990-1999 / IMDb >= 7 collection filter returned 278 movies. Ten-item paging returned ten movies, and the timeline's ten-item page had ten posters.
- The affected-items endpoint returned the same 112 unresolved records counted by the source notice. No live matching decision was made on the user's behalf.
- A live movie detail request returned a poster, backdrop, logo, director metadata, and twelve cast members.
- All seven application JavaScript/CSS modules returned HTTP 200. Bootstrap 6 Drawer and badge components remain vendored; Google Fonts is optional.
- The native Edge inspection displayed the horizontal timeline, poster/thumbnail/rating controls, source-labeled score overlays, the filter panel beside navigation, and the movie drawer with separate add/search buttons.
- Radarr search/add writes were exercised against fixtures only. No live movie or download search was created for testing.
- Metadata indexing exposed memory pressure from retaining raw, expanded TMDB responses. The fix bounds the response cache, bypasses raw detail-response caching, streams metadata reads/writes, coalesces unchanged writes, and checkpoints index progress in batches.
- The full metadata pass subsequently processed **4,074 / 4,074 movies**, with **one unavailable expanded record: Riddick: Furya (TMDB 374771)**. Existing Radarr data is retained, and Administration identifies the affected movie with a TMDB link and retry guidance.
- Container memory after the complete pass was approximately **149 MiB / 512 MiB**, and health remained **healthy**. No memory-limit increase was needed.
- Additional score checks preserve real zero-percent ratings, reject malformed/non-finite scores, and keep Rotten Tomatoes audience ratings from replacing its critics score.

## Tautulli history diagnosis

The installed Tautulli v2.18.1 accepted its API key and user/info requests but returned HTTP 500 on specific history pages. Read-only log inspection showed a `TypeError` in `get_datatables_history`: a null playback percentage was compared with a numeric watched threshold. Elementarr did not change Tautulli's installation, configuration, or database.

Tautulli is now optional and disabled by default for new installations. A saved key can remain without being used. When explicitly enabled, it is a fallback if Plex is unavailable. Fixture checks verify isolated corrupt-row recovery with visible omissions and bounded failure during a persistent outage. A successful complete live Tautulli history import has not been claimed.

## Deployment and network

- Unattended SSH works as `radium\conta` with the dedicated Carbon key and pinned host key.
- Radium runs Docker Server 29.9.2 in Linux container mode with Compose 5.6.1.
- `scripts/Deploy-Radium.ps1 -UserName conta` transfers source to a fresh release directory, builds on Radium, updates the stable Compose project, preserves the data volume, and waits for health.
- Production publishes **10.0.0.14:8090 -> container 8080**. Carbon development publishes **127.0.0.1:8090 -> container 8080**.
- Radium's `Elementarr-LAN-8090` firewall rule allows TCP 8090 only on the Private profile, local address `10.0.0.14`, remote subnet `10.0.0.0/24`.
- LAN health, application access, password sign-in, and authenticated settings requests passed from Carbon.
- Compose maps `radium.local` explicitly to `10.0.0.14`. The original Radarr hostname is necessary here: raw-IP and `host.docker.internal` requests returned HTTP 400, while the server accepted the `radium.local` Host header.
- Production configuration remains at `C:\Elementarr\.env`, with an ACL-protected private copy at `.local/radium.env` on Carbon. Development uses `.local/development.env`. Credentials and private keys are excluded from source archives and Docker builds.
- No public source hosting, image upload, router forwarding, tunnel, or Docker daemon exposure was introduced. Git is not initialized; no commit or push occurred.

## Evidence limits

The real Connect Plex flow passed with the user's browser. Repeated Plex sign-in, account rejection, challenge isolation, and sign-in while the local Plex server is unavailable passed against fixtures; a separate manual logout/sign-in cycle was not recorded.

No movie was added to live Radarr solely for testing. Addition behavior is verified through fixtures and still requires explicit movie confirmation in the UI.

The browser automation host was unavailable. Native Edge fallback successfully verified the exact Radium URL and displayed the redesigned timeline, source-labeled ratings, filter drawer beside navigation, and rich movie drawer. Full automated DOM interaction and mobile visual QA remain unverified. UI input stopped when the tool detected the user interacting with the window.



