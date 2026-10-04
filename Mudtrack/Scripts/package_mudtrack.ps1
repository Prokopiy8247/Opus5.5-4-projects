param(
    [ValidateSet('Development', 'Shipping')]
    [string]$Configuration = 'Shipping',
    [string]$EngineRoot = '',
    [string]$OutputDir = '',
    [switch]$CleanCook
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$UProject = Join-Path $ProjectRoot 'Mudtrack.uproject'

function Find-UnrealEngine {
    param([string]$RequestedRoot)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($RequestedRoot) { $candidates.Add($RequestedRoot) }
    if ($env:UE_ENGINE_ROOT) { $candidates.Add($env:UE_ENGINE_ROOT) }

    $association = (Get-Content -LiteralPath $UProject -Raw | ConvertFrom-Json).EngineAssociation
    if ($association) {
        $launcherKey = "HKLM:\SOFTWARE\EpicGames\Unreal Engine\$association"
        $wowKey = "HKLM:\SOFTWARE\WOW6432Node\EpicGames\Unreal Engine\$association"
        foreach ($key in @($launcherKey, $wowKey)) {
            if (Test-Path $key) {
                $installed = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstalledDirectory
                if ($installed) { $candidates.Add($installed) }
            }
        }

        $sourceKey = 'HKCU:\Software\Epic Games\Unreal Engine\Builds'
        if (Test-Path $sourceKey) {
            $props = Get-ItemProperty -Path $sourceKey
            if ($props.PSObject.Properties.Name -contains $association) {
                $candidates.Add([string]$props.$association)
            }
        }

        $candidates.Add((Join-Path $env:ProgramFiles "Epic Games\UE_$association"))
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $tool = Join-Path $candidate 'Engine\Binaries\DotNET\AutomationTool\AutomationTool.exe'
        if (Test-Path -LiteralPath $tool) { return (Resolve-Path -LiteralPath $candidate).Path }
    }

    throw 'Unreal Engine was not found. Pass -EngineRoot "C:\path\to\UE_5.8".'
}

if (-not (Test-Path -LiteralPath $UProject)) { throw "Project not found: $UProject" }
$EngineRoot = Find-UnrealEngine $EngineRoot
$AutomationTool = Join-Path $EngineRoot 'Engine\Binaries\DotNET\AutomationTool\AutomationTool.exe'

if (-not $OutputDir) { $OutputDir = Join-Path $ProjectRoot 'Packaged' }
$LogDir = Join-Path $ProjectRoot '.build_logs'
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$Log = Join-Path $LogDir ("package_{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

$arguments = @(
    'BuildCookRun'
    "-project=$UProject"
    '-noP4'
    '-platform=Win64'
    "-clientconfig=$Configuration"
    '-build'
    '-cook'
    '-stage'
    '-pak'
    '-iostore'
    '-compressed'
    '-prereqs'
    '-utf8output'
    '-UbtArgs=-NoHotReloadFromIDE'
)
if ($CleanCook) { $arguments += '-clean' }
if ($Configuration -eq 'Shipping') {
    $arguments += @('-distribution', '-nodebuginfo')
}

Write-Host "Project : $UProject"
Write-Host "Engine  : $EngineRoot"
Write-Host "Output  : $OutputDir"
Write-Host "Log     : $Log"

& $AutomationTool @arguments 2>&1 | Tee-Object -FilePath $Log
if ($LASTEXITCODE -ne 0) { throw "Packaging failed. See $Log" }

$StagedGame = Join-Path $ProjectRoot 'Saved\StagedBuilds\Windows'
if (-not (Test-Path -LiteralPath (Join-Path $StagedGame 'Mudtrack.exe'))) {
    throw "Staged build not found: $StagedGame"
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
& robocopy $StagedGame $OutputDir /MIR /R:3 /W:1 /NFL /NDL /NP /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }

$Exe = Join-Path $OutputDir 'Mudtrack.exe'
if (-not (Test-Path -LiteralPath $Exe)) { throw "Packaged executable is missing: $Exe" }
$SizeMB = [math]::Round((Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "PACKAGED OK: $Exe ($SizeMB MB)" -ForegroundColor Green
