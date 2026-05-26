param(
    [string]$ProjectPath = "src\OmniRelay.ConnectorCore",
    [string]$OutputDirectory = "build\connector-core",
    [ValidateSet("linux", "windows")]
    [string[]]$OperatingSystems = @("linux"),
    [string[]]$Architectures = @("amd64", "arm64"),
    [string]$Version
)

$ErrorActionPreference = "Stop"

function Resolve-ReleaseVersion {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [string]$ProvidedVersion
    )

    if (-not [string]::IsNullOrWhiteSpace($ProvidedVersion)) {
        return $ProvidedVersion.Trim()
    }

    try {
        $tag = git -C $RootPath describe --tags --always --dirty 2>$null
        if (-not [string]::IsNullOrWhiteSpace($tag)) {
            return $tag.Trim()
        }
    }
    catch {
        # fall back
    }

    return "dev"
}

function Test-SupportedArch {
    param([Parameter(Mandatory = $true)][string]$Arch)
    return $Arch -in @("amd64", "arm64")
}

$root = Split-Path -Parent $PSScriptRoot
$projectFullPath = Join-Path $root $ProjectPath
if (-not (Test-Path -LiteralPath $projectFullPath)) {
    throw "Connector core project not found: $projectFullPath"
}

$goBin = Get-Command go -ErrorAction SilentlyContinue
if ($null -eq $goBin) {
    throw "Go toolchain was not found on PATH."
}

$resolvedVersion = Resolve-ReleaseVersion -RootPath $root -ProvidedVersion $Version
Write-Host "Connector-core version: $resolvedVersion" -ForegroundColor Yellow

$outDir = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$originalGoos = $env:GOOS
$originalGoarch = $env:GOARCH
$originalCgoEnabled = $env:CGO_ENABLED

Push-Location $projectFullPath
try {
    Write-Host "Downloading Go modules..." -ForegroundColor Cyan
    & go mod download
    if ($LASTEXITCODE -ne 0) {
        throw "go mod download failed with exit code $LASTEXITCODE."
    }

    foreach ($osInput in $OperatingSystems) {
        $targetOs = ([string]$osInput).Trim().ToLowerInvariant()
        foreach ($archInput in $Architectures) {
            $archCandidate = if ($null -eq $archInput) { "" } else { [string]$archInput }
            $arch = $archCandidate.Trim().ToLowerInvariant()
            if (-not (Test-SupportedArch -Arch $arch)) {
                throw "Unsupported architecture '$arch'. Supported values: amd64, arm64."
            }

            $targetDir = if (($OperatingSystems.Count -eq 1) -and ($Architectures.Count -eq 1)) {
                $outDir
            } else {
                Join-Path $outDir "$targetOs-$arch"
            }
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

            $binaryName = if ($targetOs -eq "windows") { "connector-core.exe" } else { "connector-core" }
            $binaryPath = Join-Path $targetDir $binaryName

            Write-Host "Building connector-core for $targetOs/$arch..." -ForegroundColor Cyan
            $env:CGO_ENABLED = "0"
            $env:GOOS = $targetOs
            $env:GOARCH = $arch
            & go build -trimpath -ldflags "-s -w" -o $binaryPath ./cmd/connector-core
            if ($LASTEXITCODE -ne 0) {
                throw "go build failed for $targetOs/$arch with exit code $LASTEXITCODE."
            }

            $buildInfo = @{
                builtAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
                sourceProject = $ProjectPath
                version = $resolvedVersion
                os = $targetOs
                arch = $arch
            }
            $buildInfo | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $targetDir "connector-core.build.json") -Encoding UTF8

            $hash = (Get-FileHash -Path $binaryPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $fileSize = (Get-Item -LiteralPath $binaryPath).Length

            Write-Host "Connector-core binary created:" -ForegroundColor Green
            Write-Host "  Path: $binaryPath"
            Write-Host "  Size: $fileSize bytes"
            Write-Host "  SHA-256: $hash"
        }
    }
}
finally {
    Pop-Location
    $env:GOOS = $originalGoos
    $env:GOARCH = $originalGoarch
    $env:CGO_ENABLED = $originalCgoEnabled
}
