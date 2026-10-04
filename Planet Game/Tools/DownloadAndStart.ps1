$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$localBuild = Join-Path $projectRoot "Build\Windows\SableReach.exe"

if (Test-Path -LiteralPath $localBuild) {
    Start-Process -FilePath $localBuild -WorkingDirectory (Split-Path -Parent $localBuild)
    exit 0
}

$version = "0.1.0"
$archiveName = "Sable-Reach-Windows-v$version.zip"
$downloadUrl = "https://github.com/Prokopiy8247/Opus5.5-4-projects/releases/download/planet-game-v$version/$archiveName"
$installParent = Join-Path $projectRoot ".downloaded-game"
$installRoot = Join-Path $installParent "Sable-Reach-Windows-v$version"
$gameExe = Join-Path $installRoot "SableReach.exe"

if (-not (Test-Path -LiteralPath $gameExe)) {
    New-Item -ItemType Directory -Path $installParent -Force | Out-Null
    $archivePath = Join-Path ([System.IO.Path]::GetTempPath()) ("Sable-Reach-" + [guid]::NewGuid().ToString("N") + ".zip")

    try {
        Write-Host "Downloading Sable Reach v$version..."
        Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
        Write-Host "Extracting the game..."
        Expand-Archive -LiteralPath $archivePath -DestinationPath $installParent -Force
        Get-ChildItem -LiteralPath $installRoot -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    }
    finally {
        if (Test-Path -LiteralPath $archivePath) {
            Remove-Item -LiteralPath $archivePath -Force
        }
    }
}

if (-not (Test-Path -LiteralPath $gameExe)) {
    throw "The game executable was not found after extraction: $gameExe"
}

Write-Host "Starting Sable Reach..."
Start-Process -FilePath $gameExe -WorkingDirectory $installRoot
