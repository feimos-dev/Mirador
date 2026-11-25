param(
    [switch]$Test,
    [switch]$SkipHardware
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

Push-Location $repo
try {
    try {
        & (Join-Path $repo 'standin\build.ps1')
    }
    catch {
        Write-Warning "The stand-in wasn't built: $_"
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $cppTools = (Test-Path -LiteralPath $vswhere) -and
        [bool](& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($cppTools) {
        $layerArgs = @('-Configure', '-Build') + $(if ($Test) { @('-Test') } else { @() })
        & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'layer') `
            -BuildDir (Join-Path $repo 'artifacts\layer-build') @layerArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'tools\xr-check') `
            -BuildDir (Join-Path $repo 'artifacts\xr-check-build') -Configure -Build
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        & (Get-Process -Id $PID).Path -NoProfile -File (Join-Path $repo 'tools\build-cmake.ps1') -Source (Join-Path $repo 'tools\openxr-loader') `
            -BuildDir (Join-Path $repo 'artifacts\openxr-loader-build') -Triplet x64-windows-dll-static-crt -Configure -Build
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    elseif ($env:CI) {
        throw "The layer can't be built on CI: Visual Studio's C++ x64 tools are missing."
    }
    else {
        Write-Warning "The layer wasn't built: Visual Studio's C++ x64 tools are missing."
    }

    dotnet build Mirador.slnx -c Release -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Test) {
        $filter = if ($SkipHardware) { @('--filter', 'Category!=Hardware') } else { @() }
        dotnet test Mirador.slnx -c Release -p:Platform=x64 --no-build --logger trx @filter
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
