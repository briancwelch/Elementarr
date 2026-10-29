# Carbon development and Radium service deployment

Develop and validate locally at `D:\Projects\Elementarr` on Carbon. Production runs as the **Elementarr Windows service** on Radium at `http://radium.local:8090`, with Plex-only sign-in and one owner per installation. Docker is retained for Carbon development and Radium rollback.

## Validate, package, and publish

Run these on Carbon, in order:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Local.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Windows.ps1 -Compiler C:\path\to\ISCC.exe
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Test-Windows.ps1
# Review and commit source, then publish the source and signed release assets:
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1
```

The build gate binds validation to a source fingerprint. Changed code must pass the Carbon Windows/Docker checks again. Keep the original `.local/release-signing.pk8` key in private backup; installed clients trust its public key. Never commit credentials, data, encryption keys, or release signing material. The repository and signed release assets must be public for anonymous updates.

Installed services download stable releases anonymously; users do not need a GitHub account or token. Automatic checks and installation are enabled by default, with checks after startup and every six hours. Application files and private data are backed up before applying newer signed payloads. See [Windows installation and updates](WINDOWS.md).

## Initial Radium migration

After the same-source package smoke test passes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Install-RadiumService.ps1
```

The script uses the dedicated ignored SSH key and pinned host key under `.local/ssh`. It verifies transferred hashes, preserves the Docker image, stops Docker for a consistent final data backup, and copies that data into `%PROGRAMDATA%\Elementarr\data`. The installer configures `radium.local:8090`, private-LAN access, a LocalService service, and the interactive tray task. Existing Plex ownership and encrypted source credentials must survive. On failure, the migration attempts to stop the native service and restart Docker.

This migration script is for the initial Docker-to-service cutover. Future normal service releases use the signed updater. Full installer upgrades replace the tray/updater bootstrap when necessary.

Backups are under `C:\Elementarr\backups\native-cutover-*`; the latest successful migration evidence is `C:\Elementarr\native-deployment.json`. Keep complete private data backups, including their `keys` directory. Do not replace current production data with an older Carbon QA copy.

## Docker rollback

If the initial native deployment must be rolled back, stop the Elementarr service before starting `elementarr-elementarr-1`. Its original volume and tagged image remain available. The retained Docker snapshot reflects cutover time; later native changes are separate. Verify the intended data copy before switching back.

## LAN and SSH

The service listens only locally unless LAN access is selected. Installer LAN access permits the selected TCP port on the Windows **Private** firewall profile from **LocalSubnet**. No router forwarding, public tunnel, or Docker API exposure is needed.

Unattended deployment uses Radium `10.0.0.14`, account `conta`, and a project-specific SSH key. `scripts/Prepare-RadiumKey.ps1` documents initial authorized-key setup; do not replace a pinned host key without verifying it directly on Radium. Carbon's observed address was `10.0.0.129` on October 28, 2026, so DHCP changes can affect the SSH firewall restriction. Source APIs remain server-side, and source credentials are never returned to browsers.
