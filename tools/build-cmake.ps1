# Copyright 2026, Finn Zeruhn
# SPDX-License-Identifier: BSL-1.0
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$BuildDir,
    [switch]$Configure,
    [switch]$Build,
    [switch]$Test,
    [string]$VsPath,
    [string]$Triplet = 'x64-windows-static',
    [string[]]$Extra = @()
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Source).Path
$buildDir = [IO.Path]::GetFullPath($BuildDir)
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
$env:PATH = "$installer;$env:PATH"
if ($VsPath) {
    $vs = $VsPath
} else {
    $vswhere = Join-Path $installer 'vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
        throw "vswhere.exe is not in ${installer}: install Visual Studio or its Build Tools, or give -VsPath"
    }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw ('no Visual Studio with the C++ x64 tools found (vswhere); install the "Desktop development with C++" ' +
            'workload or the Build Tools, or give -VsPath')
    }
}
$vcpkg = @(
    $env:VCPKG_ROOT,
    $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Mirador\tools\vcpkg' }),
    (Join-Path $vs 'VC\vcpkg'),
    $env:VCPKG_INSTALLATION_ROOT
) | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'scripts\buildsystems\vcpkg.cmake') -PathType Leaf) } |
    Select-Object -First 1
if (-not $vcpkg) {
    throw "no vcpkg found: set VCPKG_ROOT, or add the 'vcpkg package manager' component to $vs in the Visual Studio Installer"
}
$toolchain = Join-Path $vcpkg 'scripts\buildsystems\vcpkg.cmake'
$env:VCPKG_DISABLE_METRICS = '1'
$env:VCPKG_VISUAL_STUDIO_PATH = $vs
Import-Module (Join-Path $vs "Common7\Tools\Microsoft.VisualStudio.DevShell.dll")
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -no_logo -host_arch=amd64' | Out-Null
if ($Configure) {
    $cmakeArgs = @(
        "-S", $source, "-B", $buildDir, "-GNinja",
        "-DCMAKE_BUILD_TYPE=RelWithDebInfo",
        "-DCMAKE_EXE_LINKER_FLAGS_RELWITHDEBINFO=/debug /INCREMENTAL:NO /OPT:REF /OPT:NOICF",
        "-DCMAKE_SHARED_LINKER_FLAGS_RELWITHDEBINFO=/debug /INCREMENTAL:NO /OPT:REF /OPT:NOICF",
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
        "-DVCPKG_TARGET_TRIPLET=$Triplet"
    ) + $(if (Test-Path -LiteralPath (Join-Path $source 'triplets') -PathType Container) {
        @("-DVCPKG_OVERLAY_TRIPLETS=$(Join-Path $source 'triplets')")
    } else { @() }) + $Extra
    cmake @cmakeArgs
    if ($LASTEXITCODE) { throw "configure failed ($LASTEXITCODE)" }
}
if ($Build) {
    cmake --build $buildDir
    if ($LASTEXITCODE) { throw "build failed ($LASTEXITCODE)" }
}
if ($Test) {
    ctest --test-dir $buildDir --output-on-failure
    if ($LASTEXITCODE) { throw "tests failed ($LASTEXITCODE)" }
}
