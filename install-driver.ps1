param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$driverPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'driver/flugelkranz'))
if (!(Test-Path -LiteralPath (Join-Path $driverPath 'driver.vrdrivermanifest')) -or
    !(Test-Path -LiteralPath (Join-Path $driverPath 'bin/win64/driver_flugelkranz.dll'))) {
    throw 'Run this script from the built windows-x64 package.'
}
$pathsFile = Join-Path $env:LOCALAPPDATA 'openvr/openvrpaths.vrpath'
$paths = Get-Content -LiteralPath $pathsFile -Raw | ConvertFrom-Json
$runtime = @($paths.runtime)[0]
$registrationTool = Join-Path $runtime 'bin/win64/vrpathreg.exe'
if (!(Test-Path -LiteralPath $registrationTool)) { throw 'SteamVR vrpathreg.exe was not found.' }
$operation = if ($Uninstall) { 'removedriver' } else { 'adddriver' }
& $registrationTool $operation $driverPath
if ($LASTEXITCODE -ne 0) { throw "SteamVR driver registration failed: $LASTEXITCODE" }
Write-Host 'Registration updated. Restart SteamVR to apply. This script does not stop SteamVR or change room setup.'
