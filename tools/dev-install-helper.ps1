#Requires -Version 7.3
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$isAdministrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    [Console]::Error.WriteLine('The helper service can only be installed from an administrator PowerShell. Open PowerShell as administrator and run tools\dev-install-helper.ps1 again.')
    exit 1
}

$repo = Split-Path -Parent $PSScriptRoot
$serviceName = 'MiradorHelper'
$appDir = Join-Path $env:ProgramFiles 'Mirador'
$stagingDir = Join-Path $appDir 'helper.new'
$helperExe = Join-Path $appDir 'Mirador.Helper.exe'

if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
dotnet publish (Join-Path $repo 'src\Mirador.Helper\Mirador.Helper.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $stagingDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (exit code $LASTEXITCODE). The installed helper, if any, was not touched."
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName
    $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

if (Get-Process -Name 'Mirador' -ErrorAction SilentlyContinue) {
    throw 'Mirador is running: quit it (tray icon > Quit), then run this again.'
}
$clash = Get-ChildItem -LiteralPath $stagingDir -Recurse -File | Where-Object {
    $installed = Join-Path $appDir ([IO.Path]::GetRelativePath($stagingDir, $_.FullName))
    $_.Name -notlike 'Mirador.Helper.*' -and (Test-Path -LiteralPath $installed) -and
        (Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash
}
if ($clash) {
    throw "The installed Mirador has other copies of $(($clash | ForEach-Object Name) -join ', '): build the helper from the installed Mirador's commit, or install Mirador from this one. Nothing was copied."
}
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
robocopy $stagingDir $appDir /E /R:0 /W:0 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "Copying the helper into $appDir failed (robocopy exit code $LASTEXITCODE)."
}
Remove-Item -LiteralPath $stagingDir -Recurse -Force

if (-not $service) {
    sc.exe create $serviceName binPath= "`"$helperExe`"" start= auto DisplayName= 'Mirador helper'
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe create failed (exit code $LASTEXITCODE)."
    }
}
Start-Service -Name $serviceName

sc.exe query $serviceName
Write-Host "The Mirador helper service is installed from $appDir."
