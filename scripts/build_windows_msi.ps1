param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("stable", "beta")]
    [string]$Channel = "stable",
    [string]$ConnectorCoreReleasePublicKeyPath,
    [switch]$RebuildOmniPanel
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "connector_core_release_helpers.ps1")
$installerProject = Join-Path $root "src\OmniRelay.Installer\OmniRelay.Installer.wixproj"
$productWxsPath = Join-Path $root "src\OmniRelay.Installer\Product.wxs"
$prepareOmniPanelAssetsScript = Join-Path $root "scripts\prepare_service_omnipanel_assets.ps1"
$gatewayAssetChannelMarkerPath = Join-Path $root "src\OmniRelay.UI\gateway_asset_channel.txt"
$connectorCorePublicKeyDestination = Join-Path $root "src\OmniRelay.UI\connector_core_release_public_key.pem"
$connectorCoreServiceDir = Join-Path $root "src\OmniRelay.Service\connector-core"
$connectorCoreProjectDir = Join-Path $root "src\OmniRelay.ConnectorCore"
$uiProject = Join-Path $root "src\OmniRelay.UI\OmniRelay.UI.csproj"
$serviceProject = Join-Path $root "src\OmniRelay.Service\OmniRelay.Service.csproj"

if ([string]::IsNullOrWhiteSpace($ConnectorCoreReleasePublicKeyPath)) {
    $ConnectorCoreReleasePublicKeyPath = (Ensure-ConnectorCoreSigningKeys -RepoRootPath $root).PublicKeyPath
}

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
    if (-not (Test-Path -LiteralPath $connectorCoreProjectDir)) {
        throw "Connector-core project directory not found: $connectorCoreProjectDir"
    }

    $goBin = Get-Command go -ErrorAction SilentlyContinue
    if ($null -eq $goBin) {
        throw "Go toolchain was not found on PATH."
    }

    New-Item -ItemType Directory -Path $connectorCoreServiceDir -Force | Out-Null
    $connectorCoreExe = Join-Path $connectorCoreServiceDir "connector-core.exe"

    $originalGoos = $env:GOOS
    $originalGoarch = $env:GOARCH
    $originalCgoEnabled = $env:CGO_ENABLED

    Push-Location $connectorCoreProjectDir
    try {
        Write-Host "Building connector-core Windows runtime (amd64)..." -ForegroundColor Cyan
        $env:CGO_ENABLED = "0"
        $env:GOOS = "windows"
        $env:GOARCH = "amd64"
        & go build -trimpath -ldflags "-s -w" -o $connectorCoreExe ./cmd/connector-core
        if ($LASTEXITCODE -ne 0) {
            throw "connector-core go build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
        $env:GOOS = $originalGoos
        $env:GOARCH = $originalGoarch
        $env:CGO_ENABLED = $originalCgoEnabled
    }

    Write-Host "Connector-core runtime ready in service payload: $connectorCoreExe" -ForegroundColor Green
}

function Write-GatewayAssetChannelMarker {
    param([Parameter(Mandatory = $true)][string]$ChannelValue)

    $normalized = $ChannelValue.Trim().ToLowerInvariant()
    if ($normalized -ne "beta" -and $normalized -ne "stable") {
        throw "Invalid channel marker value: $ChannelValue"
    }

    [System.IO.File]::WriteAllText(
        $gatewayAssetChannelMarkerPath,
        "$normalized`n",
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Gateway asset channel marker set to: $normalized" -ForegroundColor Yellow
}

function Install-ConnectorCoreReleasePublicKey {
    param([string]$SourcePath)

    if ([string]::IsNullOrWhiteSpace($SourcePath)) {
        if (Test-Path -LiteralPath $connectorCorePublicKeyDestination) {
            Remove-Item -LiteralPath $connectorCorePublicKeyDestination -Force
        }
        return
    }
    if (-not (Test-Path -LiteralPath $SourcePath)) {
        throw "Connector-core release public key not found: $SourcePath"
    }
    $content = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $SourcePath).Path)
    if (-not $content.Contains("-----BEGIN PUBLIC KEY-----")) {
        throw "Connector-core release public key must be a PEM public key."
    }
    [System.IO.File]::WriteAllText(
        $connectorCorePublicKeyDestination,
        $content.Replace("`r`n", "`n").Replace("`r", "`n"),
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Connector-core release public key embedded in UI payload." -ForegroundColor Yellow
}

function Assert-ConnectorCoreOnlyReleasePayload {
    param([Parameter(Mandatory = $true)][string]$RepoRootPath)

    $uiPayload = Join-Path $RepoRootPath "src\OmniRelay.Installer\payload\Release\ui"
    $gatewayScripts = Join-Path $uiPayload "GatewayScripts"
    $bootstrap = Join-Path $gatewayScripts "bootstrap_omnirelay_connector_core.sh"
    $publicKey = Join-Path $uiPayload "connector_core_release_public_key.pem"
    if (-not (Test-Path -LiteralPath $bootstrap)) {
        throw "Release MSI payload is missing connector-core bootstrap: $bootstrap"
    }
    if (-not (Test-Path -LiteralPath $publicKey)) {
        throw "Release MSI payload is missing connector-core trusted public key: $publicKey"
    }
    $unexpected = Get-ChildItem -LiteralPath $gatewayScripts -File |
        Where-Object { $_.Name -ne "bootstrap_omnirelay_connector_core.sh" }
    if ($unexpected) {
        throw "Release MSI payload contains legacy gateway scripts: $($unexpected.Name -join ', ')"
    }
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
Write-GatewayAssetChannelMarker -ChannelValue $Channel
Install-ConnectorCoreReleasePublicKey -SourcePath $ConnectorCoreReleasePublicKeyPath
Ensure-ConnectorCoreWindowsRuntime
Stop-LockingNodeProcesses -RepoRootPath $root
if (-not (Test-Path -LiteralPath $prepareOmniPanelAssetsScript)) {
    throw "OmniPanel asset preparation script not found: $prepareOmniPanelAssetsScript"
}

Write-Host "Preparing service OmniPanel assets..." -ForegroundColor Cyan
$env:OMNIRELAY_GATEWAY_ASSET_CHANNEL = $Channel
Write-Host "Using gateway asset channel: $Channel" -ForegroundColor Cyan
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
if ($Configuration -eq "Release") {
    Assert-ConnectorCoreOnlyReleasePayload -RepoRootPath $root
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
