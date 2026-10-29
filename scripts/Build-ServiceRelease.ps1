<# .SYNOPSIS Builds signed Windows service packages only from a matching successful Carbon Docker gate. #>
[CmdletBinding()]
param([string]$Compiler='', [string]$SigningKey='', [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if (-not $SigningKey) { $SigningKey=Join-Path $projectRoot '.local/release-signing.pk8' }
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $projectRoot artifacts }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$gateFile=Join-Path $projectRoot '.local/release-validation.json'
if (-not (Test-Path -LiteralPath $gateFile)) { throw 'Run scripts/Test-Local.ps1 before building a release.' }
$gate=Get-Content -LiteralPath $gateFile -Raw | ConvertFrom-Json
$fingerprint=& (Join-Path $PSScriptRoot Get-SourceFingerprint.ps1)
if (-not $gate.Passed -or $gate.Fingerprint -ne $fingerprint) { throw 'The source changed since validation. Run scripts/Test-Local.ps1 again.' }
if (-not (Test-Path -LiteralPath $SigningKey)) { throw 'Restore the original private publisher key before building updates.' }
[xml]$props=Get-Content -LiteralPath (Join-Path $projectRoot Directory.Build.props) -Raw
$version=[string]$props.Project.PropertyGroup.Version
$stage=Join-Path $OutputDirectory ('Elementarr-'+$version+'-win-x64')
if (Test-Path -LiteralPath $stage) {
    $resolved=[IO.Path]::GetFullPath($stage)
    if (-not $resolved.StartsWith($OutputDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -ne "Elementarr-$version-win-x64") { throw 'Invalid release staging path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null
foreach ($name in @('Elementarr','Elementarr.Tray','Elementarr.Updater')) {
    $sub=if ($name -eq 'Elementarr') { 'app' } elseif ($name -eq 'Elementarr.Tray') { 'tray' } else { 'updater' }
    $destination=Join-Path $stage $sub
    $arguments=@('publish',(Join-Path $projectRoot "src/$name/$name.csproj"),'-c','Release','-r','win-x64','--self-contained','true','-p:RuntimeFrameworkVersion=10.0.14','-p:DebugType=None','-p:DebugSymbols=false','-o',$destination)
    if ($sub -ne 'app') { $arguments+=@('-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true') }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw ("Publish failed: $name") }
    $frameworks=if ($sub -eq 'tray') { @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App') } elseif ($sub -eq 'app') { @('Microsoft.NETCore.App','Microsoft.AspNetCore.App') } else { @('Microsoft.NETCore.App') }
    $assets=Get-Content -LiteralPath (Join-Path $projectRoot "src/$name/obj/project.assets.json") -Raw | ConvertFrom-Json
    foreach ($framework in $frameworks) {
        $relative=$framework.ToLowerInvariant()+'.runtime.win-x64/10.0.14'
        $package=$assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $relative } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $package) { throw ('Runtime license package missing: '+$relative) }
        $licenses=Join-Path $destination ('licenses/'+$framework)
        New-Item -ItemType Directory -Force -Path $licenses | Out-Null
        $licenseFiles=Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -in @('LICENSE','LICENSE.TXT','THIRD-PARTY-NOTICES.TXT') }
        if (-not ($licenseFiles | Where-Object { $_.Name -in @('LICENSE','LICENSE.TXT') })) { throw ('Runtime license missing: '+$framework) }
        $licenseFiles | Copy-Item -Destination $licenses -Force
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot packaging/windows/Configure-Service.ps1) -Destination $stage
Copy-Item -LiteralPath (Join-Path $projectRoot docs/WINDOWS.md) -Destination (Join-Path $stage README.md)
Copy-Item -LiteralPath (Join-Path $projectRoot docs/THIRD_PARTY.md) -Destination (Join-Path $stage app/THIRD_PARTY.md)
$private=Get-ChildItem -LiteralPath $stage -Recurse -Force | Where-Object { $_.Name -like '.env*' -or $_.Name -match '\.(protected|pk8|p8|pfx|key)$' -or $_.Name -in @('data','keys','snapshot.json','tv-snapshot.json','history-exclusions.json') }
if ($private) { throw 'Private installation data or signing material was found in the release.' }
if ($fingerprint -ne (& (Join-Path $PSScriptRoot Get-SourceFingerprint.ps1))) { throw 'Source changed during packaging; revalidate and rebuild.' }
$payloadName="Elementarr-$version-win-x64-update.zip"
$payload=Join-Path $OutputDirectory $payloadName
Compress-Archive -Path (Join-Path $stage 'app/*') -DestinationPath $payload -Force
$manifest=[ordered]@{Version=$version;AssetName=$payloadName;Sha256=(Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant();Size=(Get-Item -LiteralPath $payload).Length;Platform='win-x64';MinimumUpdaterVersion=1}
$manifestPath=Join-Path $OutputDirectory elementarr-update.json
[IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
& dotnet run --project (Join-Path $projectRoot tools/Elementarr.ReleaseTools) -c Release -- sign $SigningKey $manifestPath (Join-Path $OutputDirectory elementarr-update.sig)
if ($LASTEXITCODE -ne 0) { throw 'Release signing failed.' }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $OutputDirectory "Elementarr-$version-win-x64.zip") -Force
if (-not $Compiler) { $found=Get-Command ISCC.exe -ErrorAction SilentlyContinue; if ($found) { $Compiler=$found.Source } }
if (-not $Compiler) { throw 'Pass the Inno Setup 7 compiler path with -Compiler.' }
& $Compiler /Qp "/DPublishDir=$stage" "/DOutputDir=$OutputDirectory" "/DAppVersion=$version" (Join-Path $projectRoot packaging/windows/Elementarr.Service.iss)
if ($LASTEXITCODE -ne 0) { throw 'Windows service installer compilation failed.' }
$files=Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object { $_.Name -like "Elementarr-$version-*" -or $_.Name -in @('elementarr-update.json','elementarr-update.sig') } | Sort-Object Name
$files | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+$_.Name } | Set-Content (Join-Path $OutputDirectory SHA256SUMS.txt) -Encoding ASCII
@{Version=$version;Fingerprint=$fingerprint;BuiltAt=[DateTimeOffset]::UtcNow.ToString('O');PayloadHash=$manifest.Sha256} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory release-build.json) -Encoding UTF8
Write-Output ("Signed Windows service release $version built from validated source.")
