param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$installerProject = Join-Path $root "src\EstherLink.Installer\EstherLink.Installer.wixproj"
$bundleTar = Join-Path $root "src\EstherLink.UI\Assets\GatewayBundle\omnirelay-vps-bundle-x64.tar.gz"
$bundleSha = "$bundleTar.sha256"

if ($Configuration -eq "Release") {
    if (-not (Test-Path $bundleTar)) {
        throw "Release MSI build requires gateway bundle at $bundleTar. Run scripts/build_omnirelay_vps_bundle.sh first."
    }

    if (-not (Test-Path $bundleSha)) {
        throw "Release MSI build requires gateway bundle checksum at $bundleSha. Run scripts/build_omnirelay_vps_bundle.sh first."
    }
}

Write-Host "Building OmniRelay MSI ($Configuration)..." -ForegroundColor Cyan
dotnet build $installerProject -c $Configuration

$msiCandidates = Get-ChildItem -Path (Join-Path $root "src\EstherLink.Installer\bin\$Configuration") -Filter *.msi -File -ErrorAction SilentlyContinue
if (-not $msiCandidates) {
    $msiCandidates = Get-ChildItem -Path (Join-Path $root "src\EstherLink.Installer\bin") -Filter *.msi -File -Recurse
}

if (-not $msiCandidates) {
    throw "MSI not found under src\\EstherLink.Installer\\bin."
}

$msiPath = ($msiCandidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).FullName

Write-Host "MSI created:" -ForegroundColor Green
Write-Host $msiPath
