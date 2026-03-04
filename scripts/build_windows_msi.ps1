param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$installerProject = Join-Path $root "src\EstherLink.Installer\EstherLink.Installer.wixproj"

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
