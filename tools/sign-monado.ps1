#Requires -Version 7.3
<#
.SYNOPSIS
    Signs a Monado build (the install folder of tools\build-monado.ps1) before the weekly workflow publishes it.
.DESCRIPTION
    Signs every .exe and .dll in the folder that has no signature: what the build compiled, Monado's programs and the
    libraries vcpkg built for it. Files that came signed (Microsoft's C++ runtime) keep their signature. Afterwards
    every program and DLL in the folder carries a signature Windows trusts. tools\sign.ps1 says what signs.
.EXAMPLE
    tools\sign-monado.ps1 -Install C:\src\monado\install
#>
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
        # Never signed over: a signature Windows doesn't trust is a file that isn't what its maker shipped.
        throw "$($file.FullName) has a signature Windows doesn't trust ($status)."
    }
}

# The runtime and the service are what Mirador runs: a folder where they aren't waiting to be signed isn't a fresh build.
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
