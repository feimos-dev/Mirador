#Requires -Version 7.3
<#
.SYNOPSIS
    Signs files with signtool: Authenticode, SHA-256, timestamped.
.DESCRIPTION
    What signs Mirador's own programs and DLLs (tools\sign-bundle.ps1) and a Monado build (tools\sign-monado.ps1),
    and what Inno Setup runs for the uninstaller and the installer (tools\build-installer.ps1 -Sign). The environment
    says what signs:

      SIGN_DLIB, SIGN_METADATA   Azure Artifact Signing: its signtool library and the file that names the account
                                 (tools\get-signing-library.ps1 sets both). The Azure CLI must be logged in.
      SIGN_THUMBPRINT            instead: the SHA-1 thumbprint of a code-signing certificate in the user's store
      SIGNTOOL                   signtool.exe, if not the newest one of the Windows SDK
.EXAMPLE
    tools\sign.ps1 artifacts\bundle\Mirador.exe artifacts\bundle\Mirador.dll
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'
$timestampServer = 'http://timestamp.acs.microsoft.com'

$signtool = $env:SIGNTOOL
if (-not $signtool) {
    $signtool = Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\*\x64\signtool.exe') -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $signtool -or -not (Test-Path -LiteralPath $signtool)) {
    throw 'signtool.exe is missing: it comes with the Windows SDK.'
}

if ($env:SIGN_DLIB -or $env:SIGN_METADATA) {
    foreach ($file in $env:SIGN_DLIB, $env:SIGN_METADATA) {
        if (-not $file -or -not (Test-Path -LiteralPath $file)) {
            throw "SIGN_DLIB and SIGN_METADATA must both name a file ('$env:SIGN_DLIB', '$env:SIGN_METADATA')."
        }
    }
    $with = '/dlib', $env:SIGN_DLIB, '/dmdf', $env:SIGN_METADATA
} elseif ($env:SIGN_THUMBPRINT) {
    $with = '/sha1', $env:SIGN_THUMBPRINT
} else {
    throw 'Nothing to sign with: set SIGN_DLIB and SIGN_METADATA, or SIGN_THUMBPRINT.'
}

$files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })

# The signing service and the timestamp server are on the network: a failed call is tried again, three times in all.
for ($try = 1; ; $try++) {
    & $signtool sign /v /fd SHA256 /tr $timestampServer /td SHA256 @with @files
    if ($LASTEXITCODE -eq 0) {
        break
    }
    if ($try -ge 3) {
        throw "signtool failed (exit code $LASTEXITCODE) for: $($files -join ', ')"
    }
    Start-Sleep -Seconds 5
}
