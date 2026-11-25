#Requires -Version 7.3
<#
.SYNOPSIS
    Signs Mirador's own programs and DLLs in the bundle (tools\build-bundle.ps1), before the installer packs them.
.DESCRIPTION
    Mirador's own are the .exe and .dll files named mirador*, and xr-check.exe. Everything else in the bundle is
    someone else's (the .NET runtime, the Windows App SDK, StereoKit, ...) and keeps its maker's signature, or none.
    tools\sign.ps1 says what signs.
.EXAMPLE
    tools\sign-bundle.ps1
#>
[CmdletBinding()]
param(
    [string]$Bundle = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\bundle')
)

$ErrorActionPreference = 'Stop'

# A bundle without one of these isn't whole: nothing is signed.
foreach ($needed in 'Mirador.exe', 'Mirador.dll', 'Mirador.Helper.exe', 'Mirador.Xr.exe', 'standin\mirador-standin.exe',
                    'layer\mirador_direct_mode.dll', 'layer\mirador-display-check.exe', 'tools\xr-check.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $Bundle $needed))) {
        throw "The bundle at $Bundle has no $needed. Run tools\build-bundle.ps1 first."
    }
}

$own = @(Get-ChildItem -LiteralPath $Bundle -Recurse -File |
    Where-Object { $_.Extension -in '.exe', '.dll' -and ($_.Name -like 'mirador*' -or $_.Name -eq 'xr-check.exe') } |
    ForEach-Object FullName)

& (Join-Path $PSScriptRoot 'sign.ps1') @own
& (Join-Path $PSScriptRoot 'check-signatures.ps1') @own
if ($LASTEXITCODE -ne 0) {
    throw 'The signatures of the bundle''s files didn''t pass the check.'
}
