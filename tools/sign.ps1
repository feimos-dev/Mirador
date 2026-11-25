#Requires -Version 7.3
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
