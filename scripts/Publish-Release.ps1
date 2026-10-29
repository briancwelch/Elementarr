<#
.SYNOPSIS
Publishes locally validated source and signed artifacts only to the fixed private Elementarr repository.
#>
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    $repository='briancwelch/Elementarr'
    $repo=& gh repo view $repository --json visibility
    if ($LASTEXITCODE -ne 0 -or ($repo | ConvertFrom-Json).visibility -ne 'PRIVATE') { throw 'The release repository must be accessible and private.' }
    $build=Get-Content artifacts/release-build.json -Raw | ConvertFrom-Json
    if ($build.Fingerprint -ne (& scripts/Get-SourceFingerprint.ps1)) { throw 'Source changed after validation/build. Revalidate and rebuild before publishing.' }
    $native=Get-Content .local/native-package-validation.json -Raw | ConvertFrom-Json
    if (-not $native.Passed -or $native.Version -ne $build.Version) { throw 'Smoke-test the Windows package before publishing.' }
    $changes=& git -c "safe.directory=$projectRoot" status --porcelain
    if ($changes) { throw 'Commit the reviewed source before publishing; the working tree must be clean.' }
    & git -c "safe.directory=$projectRoot" -c 'url.https://github.com/.insteadOf=git@github.com:' -c credential.helper= -c 'credential.helper=!gh auth git-credential' push --set-upstream origin main
    if ($LASTEXITCODE -ne 0) { throw 'The private source push failed.' }
    $version=$build.Version
    $commit=(& git -c "safe.directory=$projectRoot" rev-parse HEAD).Trim()
    $assets=@("artifacts/Elementarr-$version-win-x64-Setup.exe","artifacts/Elementarr-$version-win-x64.zip","artifacts/Elementarr-$version-win-x64-update.zip",'artifacts/elementarr-update.json','artifacts/elementarr-update.sig','artifacts/SHA256SUMS.txt')
    & gh release create "v$version" @assets --repo $repository --target $commit --title "Elementarr $version" --notes-file docs/RELEASE.md
    if ($LASTEXITCODE -ne 0) { throw 'Release publication failed. Existing releases are never overwritten automatically.' }
    Write-Output ("Published private release v$version from $commit")
}
finally { Pop-Location }
