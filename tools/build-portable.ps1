param([string]$OutputDirectory,[switch]$Test)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$build=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $projectRoot 'build'}
& (Join-Path $projectRoot 'build.ps1') -SkipNative -OutputDirectory $build
& (Join-Path $projectRoot 'gateway\build-gateway.ps1') -OutputDirectory $build
& (Join-Path $projectRoot 'native\build-camera.ps1') -OutputDirectory $build -Test:$Test
& (Join-Path $projectRoot 'native\build-race-bridge.ps1') -OutputDirectory $build -Test:$Test
& (Join-Path $projectRoot 'native\build-assets.ps1') -OutputDirectory $build -Test:$Test
if($Test) {& (Join-Path $projectRoot 'gateway\test-offline.ps1')}
Write-Output "Portable launcher built in $build"
