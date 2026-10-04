param(
    [switch]$ForceDownload
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Owner = 'Prokopiy8247'
$Repository = 'Opus5.5-4-projects'
$ReleaseTag = 'mudtrack-v1.0.0'
$AssetName = 'Mudtrack-Windows-x64.zip'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$CacheRoot = Join-Path $ProjectRoot '.game'

try {
    Write-Host 'Checking the Mudtrack release...'
    $headers = @{ 'User-Agent' = 'Mudtrack-Launcher'; 'Accept' = 'application/vnd.github+json' }
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Owner/$Repository/releases/tags/$ReleaseTag" `
        -Headers $headers

    $asset = $release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $asset) { throw "Release asset '$AssetName' was not found." }

    $VersionRoot = Join-Path $CacheRoot ([string]$asset.id)
    $GameRoot = Join-Path $VersionRoot 'Mudtrack-Windows-x64'
    $GameExe = Join-Path $GameRoot 'Mudtrack.exe'

    if ($ForceDownload -or -not (Test-Path -LiteralPath $GameExe)) {
        New-Item -ItemType Directory -Path $VersionRoot -Force | Out-Null
        $Archive = Join-Path $VersionRoot $AssetName
        Write-Host ("Downloading {0:N0} MB..." -f ($asset.size / 1MB))
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $Archive -UseBasicParsing
        Write-Host 'Extracting...'
        Expand-Archive -LiteralPath $Archive -DestinationPath $VersionRoot -Force
        Remove-Item -LiteralPath $Archive -Force
    }

    if (-not (Test-Path -LiteralPath $GameExe)) {
        throw "The archive was extracted, but Mudtrack.exe is missing: $GameExe"
    }

    Write-Host 'Starting Mudtrack...'
    Start-Process -FilePath $GameExe -WorkingDirectory $GameRoot
} catch {
    Write-Error $_.Exception.Message
    exit 1
}
