#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Install
)

$ErrorActionPreference = 'Stop'
$Install = (Resolve-Path -LiteralPath $Install).Path

$unsigned = @()
$kept = 0
foreach ($file in Get-ChildItem -LiteralPath $Install -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' }) {
    $status = (Get-AuthenticodeSignature -LiteralPath $file.FullName).Status
    if ($status -eq 'NotSigned') {
        $unsigned += $file.FullName
    } elseif ($status -eq 'Valid') {
        $kept++
    } else {
        throw "$($file.FullName) has a signature Windows doesn't trust ($status)."
    }
}

foreach ($needed in 'bin\monado-service.exe', 'bin\openxr_monado.dll') {
    if ((Join-Path $Install $needed) -notin $unsigned) {
        throw "$needed is missing from $Install, or signed already: this takes a build as tools\build-monado.ps1 leaves it."
    }
}

& (Join-Path $PSScriptRoot 'sign.ps1') @unsigned
& (Join-Path $PSScriptRoot 'check-signatures.ps1') @unsigned
if ($LASTEXITCODE -ne 0) {
    throw 'The signatures of the build''s files didn''t pass the check.'
}
Write-Host "Signed $($unsigned.Count) files of the build; $kept kept their makers' signatures."
