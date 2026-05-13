param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$RebuildOmniPanel
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$installerProject = Join-Path $root "src\OmniRelay.Installer\OmniRelay.Installer.wixproj"
$productWxsPath = Join-Path $root "src\OmniRelay.Installer\Product.wxs"
$prepareOmniPanelAssetsScript = Join-Path $root "scripts\prepare_service_omnipanel_assets.ps1"
$buildConnectorCoreScript = Join-Path $root "scripts\build_connector_core.ps1"
$connectorCoreServiceDir = Join-Path $root "src\OmniRelay.Service\connector-core"
$uiProject = Join-Path $root "src\OmniRelay.UI\OmniRelay.UI.csproj"
$serviceProject = Join-Path $root "src\OmniRelay.Service\OmniRelay.Service.csproj"

function Clean-ProjectArtifacts {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    $projectDir = Split-Path -Parent $ProjectPath
    $objDir = Join-Path $projectDir "obj"
    $binDir = Join-Path $projectDir "bin"

    if (Test-Path -LiteralPath $objDir) {
        Remove-Item -LiteralPath $objDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $binDir) {
        Remove-Item -LiteralPath $binDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Stop-LockingNodeProcesses {
    param([Parameter(Mandatory = $true)][string]$RepoRootPath)

    Write-Host "Stopping Node.js processes that may lock service OmniPanel artifacts..." -ForegroundColor Cyan
    $serviceBin = (Join-Path $RepoRootPath "src\OmniRelay.Service\bin").ToLowerInvariant()
    $panelRoot = (Join-Path $RepoRootPath "src\OmniRelay.GatewayPanel").ToLowerInvariant()

    Get-CimInstance Win32_Process -Filter "name = 'node.exe'" -ErrorAction SilentlyContinue | ForEach-Object {
        $cmdLine = $_.CommandLine
        if ($null -eq $cmdLine) { $cmdLine = "" }
        $cmd = $cmdLine.ToLowerInvariant()
        if ($cmd.Contains($serviceBin) -or $cmd.Contains($panelRoot)) {
            try {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
                Write-Host "Stopped node.exe PID=$($_.ProcessId)" -ForegroundColor DarkYellow
            }
            catch {
                Write-Host "Failed to stop node.exe PID=$($_.ProcessId): $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }
}

function Ensure-ConnectorCoreWindowsRuntime {
    if (-not (Test-Path -LiteralPath $buildConnectorCoreScript)) {
        throw "Connector-core build script not found: $buildConnectorCoreScript"
    }

    New-Item -ItemType Directory -Path $connectorCoreServiceDir -Force | Out-Null
    $connectorCoreExe = Join-Path $connectorCoreServiceDir "connector-core.exe"
    if (Test-Path -LiteralPath $connectorCoreExe) {
        Write-Host "Connector-core runtime already present: $connectorCoreExe" -ForegroundColor DarkGray
        return
    }

    Write-Host "Building connector-core Windows runtime (amd64)..." -ForegroundColor Cyan
    powershell -ExecutionPolicy Bypass -File $buildConnectorCoreScript -OperatingSystems @("windows") -Architectures @("amd64")
    $artifact = Join-Path $root "artifacts\connector-core\connector-core-windows-amd64.zip"
    if (-not (Test-Path -LiteralPath $artifact)) {
        throw "Connector-core Windows artifact not found after build: $artifact"
    }

    $tempExtract = Join-Path $root "artifacts\connector-core\extract-windows-amd64"
    if (Test-Path -LiteralPath $tempExtract) {
        Remove-Item -LiteralPath $tempExtract -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tempExtract -Force | Out-Null
    Expand-Archive -LiteralPath $artifact -DestinationPath $tempExtract -Force

    $builtExe = Join-Path $tempExtract "connector-core.exe"
    if (-not (Test-Path -LiteralPath $builtExe)) {
        throw "connector-core.exe not found inside artifact: $artifact"
    }

    Copy-Item -LiteralPath $builtExe -Destination $connectorCoreExe -Force
    Write-Host "Connector-core runtime copied to service payload: $connectorCoreExe" -ForegroundColor Green
}

function Increment-InstallerPatchVersion {
    param([Parameter(Mandatory = $true)][string]$FilePath)

    if (-not (Test-Path -LiteralPath $FilePath)) {
        throw "Installer Product.wxs not found: $FilePath"
    }

    $content = [System.IO.File]::ReadAllText($FilePath)
    $versionPattern = 'Version="(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)"'
    $match = [System.Text.RegularExpressions.Regex]::Match($content, $versionPattern)
    if (-not $match.Success) {
        throw "Could not find Package Version=`"x.y.z`" in $FilePath"
    }

    $major = [int]$match.Groups["major"].Value
    $minor = [int]$match.Groups["minor"].Value
    $patch = [int]$match.Groups["patch"].Value
    $oldVersion = "$major.$minor.$patch"
    $newVersion = "$major.$minor.$($patch + 1)"

    $updated = [System.Text.RegularExpressions.Regex]::Replace(
        $content,
        $versionPattern,
        "Version=""$newVersion""",
        1)

    [System.IO.File]::WriteAllText($FilePath, $updated, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Installer version bumped: $oldVersion -> $newVersion" -ForegroundColor Yellow
}

Increment-InstallerPatchVersion -FilePath $productWxsPath
Ensure-ConnectorCoreWindowsRuntime
Stop-LockingNodeProcesses -RepoRootPath $root
if (-not (Test-Path -LiteralPath $prepareOmniPanelAssetsScript)) {
    throw "OmniPanel asset preparation script not found: $prepareOmniPanelAssetsScript"
}

Write-Host "Preparing service OmniPanel assets..." -ForegroundColor Cyan
if ($RebuildOmniPanel) {
    powershell -ExecutionPolicy Bypass -File $prepareOmniPanelAssetsScript -BuildPanel
}
else {
    powershell -ExecutionPolicy Bypass -File $prepareOmniPanelAssetsScript
}

Write-Host "Cleaning UI/Service build artifacts..." -ForegroundColor Cyan
Clean-ProjectArtifacts -ProjectPath $uiProject
Clean-ProjectArtifacts -ProjectPath $serviceProject

Write-Host "Building OmniRelay MSI ($Configuration)..." -ForegroundColor Cyan
dotnet build $installerProject -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE."
}

$msiCandidates = Get-ChildItem -Path (Join-Path $root "src\OmniRelay.Installer\bin\$Configuration") -Filter *.msi -File -ErrorAction SilentlyContinue
if (-not $msiCandidates) {
    $msiCandidates = Get-ChildItem -Path (Join-Path $root "src\OmniRelay.Installer\bin") -Filter *.msi -File -Recurse
}

if (-not $msiCandidates) {
    throw "MSI not found under src\\OmniRelay.Installer\\bin."
}

$msiPath = ($msiCandidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName

Write-Host "MSI created:" -ForegroundColor Green
Write-Host $msiPath
