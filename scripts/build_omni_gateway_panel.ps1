param(
    [string]$ProjectPath = "src\OmniRelay.GatewayPanel",
    [string]$OutputDirectory = "artifacts\omni-gateway"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projectFullPath = Join-Path $root $ProjectPath
if (-not (Test-Path -LiteralPath $projectFullPath)) {
    throw "Gateway panel project not found: $projectFullPath"
}

Push-Location $projectFullPath
try {
    if (-not (Test-Path -LiteralPath (Join-Path $projectFullPath "package-lock.json"))) {
        throw "package-lock.json not found. Run npm install once in $projectFullPath."
    }

    Write-Host "Installing dependencies (npm ci)..." -ForegroundColor Cyan
    npm ci
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed with exit code $LASTEXITCODE."
    }

    Write-Host "Building OmniPanel (Next standalone)..." -ForegroundColor Cyan
    npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$outDir = Join-Path $root $OutputDirectory
$stageDir = Join-Path $outDir "stage"
$artifactPath = Join-Path $outDir "omni-gateway.tar.gz"

if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}

New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

$standaloneDir = Join-Path $projectFullPath ".next\standalone"
$staticDir = Join-Path $projectFullPath ".next\static"
$publicDir = Join-Path $projectFullPath "public"
$envExamplePath = Join-Path $projectFullPath ".env.example"

if (-not (Test-Path -LiteralPath (Join-Path $standaloneDir "server.js"))) {
    throw "Next standalone output missing server.js in $standaloneDir"
}

Copy-Item -Path (Join-Path $standaloneDir "*") -Destination $stageDir -Recurse -Force

$newStaticDir = Join-Path $stageDir ".next\static"
New-Item -ItemType Directory -Path $newStaticDir -Force | Out-Null
Copy-Item -Path (Join-Path $staticDir "*") -Destination $newStaticDir -Recurse -Force

if (Test-Path -LiteralPath $publicDir) {
    $targetPublicDir = Join-Path $stageDir "public"
    New-Item -ItemType Directory -Path $targetPublicDir -Force | Out-Null
    Copy-Item -Path (Join-Path $publicDir "*") -Destination $targetPublicDir -Recurse -Force
}

if (Test-Path -LiteralPath $envExamplePath) {
    Copy-Item -LiteralPath $envExamplePath -Destination (Join-Path $stageDir ".env.example") -Force
}

$buildInfo = @{
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString("o")
    sourceProject = $ProjectPath
}
$buildInfo | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $stageDir "omni-gateway.build.json") -Encoding UTF8

New-Item -ItemType Directory -Path $outDir -Force | Out-Null
if (Test-Path -LiteralPath $artifactPath) {
    Remove-Item -LiteralPath $artifactPath -Force
}

tar -czf $artifactPath -C $stageDir .
if ($LASTEXITCODE -ne 0) {
    throw "tar packaging failed with exit code $LASTEXITCODE."
}

$hash = (Get-FileHash -Path $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$fileSize = (Get-Item -LiteralPath $artifactPath).Length

Write-Host "OmniPanel artifact created:" -ForegroundColor Green
Write-Host "  Path: $artifactPath"
Write-Host "  Size: $fileSize bytes"
Write-Host "  SHA-256: $hash"
