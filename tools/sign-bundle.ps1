#Requires -Version 7.3
[CmdletBinding()]
param(
    [string]$Bundle = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\bundle')
)

$ErrorActionPreference = 'Stop'

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
