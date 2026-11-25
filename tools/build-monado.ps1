#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Build,
    [Parameter(Mandatory)] [string]$Install,
    [string[]]$ExtraCMakeOptions = @(),
    [string]$ExtraCMakeOptionsFile,
    [string]$Tools
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

function Stop-MissingTool([string]$message) {
    [Console]::Error.WriteLine($message)
    exit 3
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    $ErrorActionPreference = 'Continue'
    & $command
    if ($LASTEXITCODE -ne 0) {
        throw "$what failed (exit code $LASTEXITCODE)."
    }
}

if ($ExtraCMakeOptionsFile) {
    $ExtraCMakeOptions += @(Get-Content -LiteralPath $ExtraCMakeOptionsFile -Encoding UTF8 | Where-Object { $_.Trim() } | ForEach-Object { $_.Trim() })
}
foreach ($option in $ExtraCMakeOptions) {
    if ($option -notmatch '^-D[A-Za-z_][A-Za-z0-9_.+-]*(:[A-Z]+)?=') {
        throw "'$option' isn't a CMake setting of the form -DNAME=value."
    }
}

$Source = (Resolve-Path -LiteralPath $Source).Path
if (-not (Test-Path -LiteralPath (Join-Path $Source 'CMakeLists.txt'))) {
    throw "$Source has no CMakeLists.txt: is it a Monado source tree?"
}
$Build = [IO.Path]::GetFullPath($Build)
$Install = [IO.Path]::GetFullPath($Install)

if ($Tools) {
    $Tools = (Resolve-Path -LiteralPath $Tools).Path
    $paths = @('cmake\bin', 'ninja', 'git\cmd') | ForEach-Object { Join-Path $Tools $_ } | Where-Object { Test-Path -LiteralPath $_ }
    $env:PATH = (@($paths) + $env:PATH) -join [IO.Path]::PathSeparator
}
$vcpkg = if ($Tools -and (Test-Path -LiteralPath (Join-Path $Tools 'vcpkg\scripts\buildsystems\vcpkg.cmake'))) {
    Join-Path $Tools 'vcpkg'
}
elseif ($env:VCPKG_ROOT) { $env:VCPKG_ROOT }
elseif ($env:VCPKG_INSTALLATION_ROOT) { $env:VCPKG_INSTALLATION_ROOT }
else { $null }
if (-not $vcpkg -or -not (Test-Path -LiteralPath (Join-Path $vcpkg 'scripts\buildsystems\vcpkg.cmake'))) {
    Stop-MissingTool 'vcpkg is missing: give -Tools with a vcpkg folder, or set VCPKG_ROOT.'
}
$toolchain = Join-Path $vcpkg 'scripts\buildsystems\vcpkg.cmake'
$env:VCPKG_ROOT = $vcpkg
$env:VCPKG_DISABLE_METRICS = '1'

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        Stop-MissingTool 'Visual Studio or its Build Tools are missing (no vswhere.exe). Install them with the C++ x64 tools and the Windows 11 SDK.'
    }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        Stop-MissingTool 'Visual Studio has no C++ x64 tools: add "Desktop development with C++" (MSVC x64/x86 build tools and the Windows 11 SDK).'
    }
    $env:CI_COMMIT_DESCRIPTION = ''
    $env:CI_COMMIT_MESSAGE = ''
    Import-Module (Join-Path $vs 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll')
    Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64 -no_logo' | Out-Null
}
foreach ($tool in 'cmake', 'ninja', 'git') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Stop-MissingTool "$tool is missing: give -Tools, or put it on PATH."
    }
}

$vcpkgExe = Join-Path $vcpkg 'vcpkg.exe'
if (-not (Test-Path -LiteralPath $vcpkgExe)) {
    Invoke-Checked 'Bootstrapping vcpkg' { & (Join-Path $vcpkg 'bootstrap-vcpkg.bat') -disableMetrics }
}

if (-not (Get-Command glslangValidator -ErrorAction SilentlyContinue)) {
    $glslang = "$Build-glslang"
    $glslangTools = Join-Path $glslang 'x64-windows\tools\glslang'
    if (-not (Test-Path -LiteralPath (Join-Path $glslangTools 'glslangValidator.exe'))) {
        Write-Host 'Building glslangValidator with vcpkg'
        Push-Location ([IO.Path]::GetTempPath())
        try {
            Invoke-Checked 'Building glslangValidator' { & $vcpkgExe install 'glslang[tools]:x64-windows' --classic "--x-install-root=$glslang" --disable-metrics }
        }
        finally {
            Pop-Location
        }
    }
    $env:PATH = $glslangTools + [IO.Path]::PathSeparator + $env:PATH
}

$options = @(
    '-DCMAKE_BUILD_TYPE=RelWithDebInfo'
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain"
    "-DCMAKE_INSTALL_PREFIX=$Install"
    '-DX_VCPKG_APPLOCAL_DEPS_INSTALL=ON'
    '-DXRT_SERVICE_EXECUTABLE=mirador-standin'
    '-DXRT_FEATURE_STEAMVR_PLUGIN=OFF'
    '-DCMAKE_EXE_LINKER_FLAGS_RELWITHDEBINFO=/debug /INCREMENTAL:NO /OPT:REF /OPT:NOICF'
    '-DCMAKE_SHARED_LINKER_FLAGS_RELWITHDEBINFO=/debug /INCREMENTAL:NO /OPT:REF /OPT:NOICF'
    '-DCMAKE_MODULE_LINKER_FLAGS_RELWITHDEBINFO=/debug /INCREMENTAL:NO /OPT:REF /OPT:NOICF'
) + $ExtraCMakeOptions

$env:PYTHONUTF8 = '1'
Write-Host "Configuring $Source in $Build"
Invoke-Checked 'Configuring Monado' { cmake -S $Source -B $Build -G Ninja @options }
Write-Host 'Building'
Invoke-Checked 'Building Monado' { cmake --build $Build }
if (Test-Path -LiteralPath $Install) {
    Remove-Item -LiteralPath $Install -Recurse -Force
}
Write-Host "Installing into $Install"
Invoke-Checked 'Installing Monado' { cmake --install $Build }

$bin = Join-Path $Install 'bin'
foreach ($file in 'openxr_monado.json', 'bin\openxr_monado.dll', 'bin\monado-service.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $Install $file))) {
        throw "The install has no $file."
    }
}

$crt = if ($env:VCToolsRedistDir) {
    Get-ChildItem -Path (Join-Path $env:VCToolsRedistDir 'x64') -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue | Select-Object -First 1
}
if ($crt) {
    Get-ChildItem -LiteralPath $crt.FullName -Filter '*.dll' | Copy-Item -Destination $bin -Force
}
else {
    Write-Warning 'The Visual C++ runtime DLLs weren''t found (no VCToolsRedistDir): this build needs the Visual C++ Redistributable on the PC.'
}

function Get-GitValue([string]$dir, [string[]]$gitArgs) {
    $ErrorActionPreference = 'Continue'
    $value = git -C $dir @gitArgs 2>$null
    if ($LASTEXITCODE -eq 0 -and $value) { "$value".Trim() } else { $null }
}
$compiler = & {
    $ErrorActionPreference = 'Continue'
    (& cl.exe 2>&1 | Select-Object -First 1 | ForEach-Object { "$_" } | Out-String).Trim()
}
$info = [ordered]@{
    format         = 1
    sourceUrl      = Get-GitValue $Source @('remote', 'get-url', 'origin')
    commit         = Get-GitValue $Source @('rev-parse', 'HEAD')
    describe       = Get-GitValue $Source @('describe', '--always', '--dirty')
    compiler       = $compiler
    msvcVersion    = $env:VCToolsVersion
    windowsSdk     = $env:WindowsSDKVersion -replace '\\$', ''
    cmakeVersion   = ((cmake --version | Select-Object -First 1) -replace '^cmake version ', '').Trim()
    vcpkgCommit    = Get-GitValue $vcpkg @('rev-parse', 'HEAD')
    buildType      = 'RelWithDebInfo'
    options        = @($options | Where-Object { $_ -notlike '-DCMAKE_TOOLCHAIN_FILE=*' -and $_ -notlike '-DCMAKE_INSTALL_PREFIX=*' })
    cxxFlags       = $env:CXXFLAGS
    builtAt        = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}
[IO.File]::WriteAllText((Join-Path $Install 'build-info.json'), ($info | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding $false))

Write-Host "Monado $($info.describe) installed in $Install"
