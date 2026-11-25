#Requires -Version 7.3
[CmdletBinding()]
param(
    [string]$Out = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\bundle')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Need([string]$path, [string]$what) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "$what is missing: $path"
    }
}

function Invoke-Checked([scriptblock]$command, [string]$what) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (exit code $LASTEXITCODE)."
    }
}

if (Test-Path -LiteralPath $Out) {
    Remove-Item -LiteralPath $Out -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null

$standinOut = Join-Path $repo 'artifacts\standin'
& (Join-Path $repo 'standin\build.ps1') -Out $standinOut
$standin = Join-Path $standinOut 'mirador-standin.exe'
Need $standin 'The stand-in'

$layerBuild = Join-Path $repo 'artifacts\layer-build'
Invoke-Checked { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'layer') -Configure -Build -BuildDir $layerBuild } 'Building the layer'
$layerFiles = (Join-Path $layerBuild 'layer\mirador_direct_mode.json'), (Join-Path $layerBuild 'layer\mirador_direct_mode.dll'),
    (Join-Path $layerBuild 'check\mirador-display-check.exe')
foreach ($file in $layerFiles) {
    Need $file 'The layer''s build'
}

$xrCheckBuild = Join-Path $repo 'artifacts\xr-check-build'
Invoke-Checked { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'tools\xr-check') -BuildDir $xrCheckBuild -Configure -Build } 'Building xr-check'
$xrCheck = Join-Path $xrCheckBuild 'xr-check.exe'
Need $xrCheck 'xr-check''s build'

$loaderBuild = Join-Path $repo 'artifacts\openxr-loader-build'
Invoke-Checked { & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'tools\openxr-loader') -BuildDir $loaderBuild -Triplet x64-windows-dll-static-crt -Configure -Build } 'Building the OpenXR loader'
Need (Join-Path $loaderBuild 'openxr_loader.dll') 'the OpenXR loader''s build'

Invoke-Checked { dotnet publish (Join-Path $repo 'src\Mirador.App\Mirador.App.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $Out } 'Publishing the app'
$helperOut = "$Out.helper"
if (Test-Path -LiteralPath $helperOut) { Remove-Item -LiteralPath $helperOut -Recurse -Force }
Invoke-Checked { dotnet publish (Join-Path $repo 'src\Mirador.Helper\Mirador.Helper.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $helperOut } 'Publishing the helper'
$clash = Get-ChildItem -LiteralPath $helperOut -Recurse -File | Where-Object {
    $app = Join-Path $Out ([IO.Path]::GetRelativePath($helperOut, $_.FullName))
    (Test-Path -LiteralPath $app) -and (Get-FileHash -LiteralPath $app).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash
}
if ($clash) { throw "The helper and the app ship different copies of: $(($clash | ForEach-Object Name) -join ', ')." }
Copy-Item -Path (Join-Path $helperOut '*') -Destination $Out -Recurse -Force
Remove-Item -LiteralPath $helperOut -Recurse -Force
$xrOut = "$Out.xr"
if (Test-Path -LiteralPath $xrOut) { Remove-Item -LiteralPath $xrOut -Recurse -Force }
Invoke-Checked { dotnet publish (Join-Path $repo 'src\Mirador.Xr\Mirador.Xr.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $xrOut } 'Publishing the home and the dashboard'
Remove-Item -LiteralPath (Join-Path $xrOut 'StereoKitC.pdb') -Force -ErrorAction SilentlyContinue
$clash = Get-ChildItem -LiteralPath $xrOut -Recurse -File | Where-Object {
    $app = Join-Path $Out ([IO.Path]::GetRelativePath($xrOut, $_.FullName))
    (Test-Path -LiteralPath $app) -and (Get-FileHash -LiteralPath $app).Hash -ne (Get-FileHash -LiteralPath $_.FullName).Hash
}
if ($clash) { throw "The home and dashboard and the app ship different copies of: $(($clash | ForEach-Object Name) -join ', ')." }
Copy-Item -Path (Join-Path $xrOut '*') -Destination $Out -Recurse -Force
Remove-Item -LiteralPath $xrOut -Recurse -Force
Need (Join-Path $Out 'Mirador.Xr.exe') 'the home and dashboard''s publish'
Need (Join-Path $Out 'xr\grid.hlsl.sks') 'the home''s shaders'
Need (Join-Path $Out 'xr\power.png') 'the dashboard''s icons'
Need (Join-Path $Out 'xr\controllers\valve-index\left.glb') 'the controllers'' models'
Need (Join-Path $Out 'openxr_loader.dll') 'the desktop program''s OpenXR loader'
Need (Join-Path $Out 'xr\openxr-loader-LICENSE.txt') 'the OpenXR loader''s licence'
Need (Join-Path $Out 'xr\jsoncpp-LICENSE.txt') 'the licence of JsonCpp, in the OpenXR loader'
Copy-Item -LiteralPath $standin -Destination (New-Item -ItemType Directory -Force -Path (Join-Path $Out 'standin')).FullName -Force
$toolsDir = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'tools')
Copy-Item -LiteralPath (Join-Path $repo 'tools\build-monado.ps1'), (Join-Path $repo 'tools\vcpkg-commit.txt'), $xrCheck -Destination $toolsDir.FullName

$layerOut = New-Item -ItemType Directory -Force -Path (Join-Path $Out 'layer')
Copy-Item -LiteralPath $layerFiles -Destination $layerOut.FullName

$standinSize = (Get-Item -LiteralPath $standin).Length
Write-Host "Bundle: $Out"
Write-Host "  stand-in $([math]::Round($standinSize / 1KB)) KB"
Write-Host '  the layer'
