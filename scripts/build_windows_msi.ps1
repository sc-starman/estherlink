param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$installerProject = Join-Path $root "src\OmniRelay.Installer\OmniRelay.Installer.wixproj"
$productWxsPath = Join-Path $root "src\OmniRelay.Installer\Product.wxs"

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
