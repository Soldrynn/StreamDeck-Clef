$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$configuredDotnet = $env:APPLE_MUSIC_DOTNET
if (-not [string]::IsNullOrWhiteSpace($configuredDotnet) -and -not (Test-Path -LiteralPath $configuredDotnet)) {
    throw "APPLE_MUSIC_DOTNET does not point to a dotnet executable: $configuredDotnet"
}
$dotnet = if (-not [string]::IsNullOrWhiteSpace($configuredDotnet)) {
    $configuredDotnet
} elseif (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    'dotnet'
}
$project = Join-Path $projectRoot 'helper\AppleMusicBridge\AppleMusicBridge.csproj'
$output = Join-Path $projectRoot 'com.davedev.apple-music.sdPlugin\helper'

& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw "Helper publish failed with exit code $LASTEXITCODE." }

$pdb = Join-Path $output 'AppleMusicBridge.pdb'
if (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb -Force }
