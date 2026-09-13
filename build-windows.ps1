param([string]$Dotnet = 'dotnet', [string]$CMake = 'cmake')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $CMake -S native/driver -B artifacts/driver-build -A x64
    if ($LASTEXITCODE -ne 0) { throw 'Driver configure failed' }
    & $CMake --build artifacts/driver-build --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Driver build failed' }
    & $Dotnet publish src/FlugelKranz -c Release -r win-x64 --self-contained true -o artifacts/windows-x64
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed' }
    $driverPath = Join-Path $PSScriptRoot 'artifacts/windows-x64/driver/flugelkranz'
    New-Item -ItemType Directory -Force (Join-Path $driverPath 'bin/win64') | Out-Null
    Copy-Item -LiteralPath 'artifacts/driver-build/Release/driver_flugelkranz.dll' -Destination (Join-Path $driverPath 'bin/win64')
    Copy-Item -LiteralPath 'native/driver/driver.vrdrivermanifest' -Destination $driverPath
    Copy-Item -LiteralPath 'native/driver/vendor/openvr/LICENSE' -Destination (Join-Path $driverPath 'LICENSE-OpenVR')
    Copy-Item -LiteralPath 'native/driver/vendor/minhook/LICENSE.txt' -Destination (Join-Path $driverPath 'LICENSE-MinHook.txt')
    Copy-Item -LiteralPath 'install-driver.ps1' -Destination 'artifacts/windows-x64'
} finally { Pop-Location }
