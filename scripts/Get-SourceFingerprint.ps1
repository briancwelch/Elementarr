<# .SYNOPSIS Computes a source fingerprint excluding private data and build output. #>
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$files=@()
foreach ($folder in @('src','tests','tools','scripts','packaging')) {
    $files+=Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules)[\\/]' }
}
foreach ($name in @('Directory.Build.props','global.json','Dockerfile','compose.yaml','compose.radium.yaml','.dockerignore','package.json','package-lock.json')) { $files+=Get-Item -LiteralPath (Join-Path $projectRoot $name) }
$lines=$files | Sort-Object FullName | ForEach-Object { $_.FullName.Substring($projectRoot.Length).Replace('\','/')+' '+(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$algorithm=[Security.Cryptography.SHA256]::Create()
try { [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))).Replace('-','') }
finally { $algorithm.Dispose() }
