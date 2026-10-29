<# .SYNOPSIS Builds the validated native Windows service release. #>
[CmdletBinding()]
param([string]$Compiler='', [string]$SigningKey='', [string]$OutputDirectory='')
& (Join-Path $PSScriptRoot Build-ServiceRelease.ps1) -Compiler $Compiler -SigningKey $SigningKey -OutputDirectory $OutputDirectory
