param(
    [string]$PanelProjectPath = "src\OmniRelay.GatewayPanel",
    [string]$ServiceProjectPath = "src\OmniRelay.Service",
    [switch]$BuildPanel
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$serviceRoot = Join-Path $root $ServiceProjectPath
$panelRoot = Join-Path $root $PanelProjectPath
$standaloneRoot = Join-Path $panelRoot ".next\standalone"
$staticRoot = Join-Path $panelRoot ".next\static"
$publicRoot = Join-Path $panelRoot "public"
$serviceOmniPanelDir = Join-Path $serviceRoot "omnipanel"
$serviceNodeDir = Join-Path $serviceRoot "nodejs"

if ($BuildPanel) {
    Push-Location $panelRoot
    try {
        npm run build
        if ($LASTEXITCODE -ne 0) {
            throw "npm run build failed."
        }
    }
    finally {
        Pop-Location
    }
}   

if (-not (Test-Path -LiteralPath (Join-Path $standaloneRoot "server.js"))) {
    throw "Missing OmniPanel standalone server.js at $standaloneRoot. Run panel build first."
}

$nodeCmd = Get-Command node -ErrorAction SilentlyContinue
if ($null -eq $nodeCmd) {
    throw "Node.js was not found on PATH. Cannot prepare service nodejs runtime."
}

$nodeExe = $nodeCmd.Source
$nodeHome = Split-Path -Parent $nodeExe
New-Item -ItemType Directory -Path $serviceNodeDir -Force | Out-Null
Copy-Item -LiteralPath $nodeExe -Destination (Join-Path $serviceNodeDir "node.exe") -Force
Get-ChildItem -LiteralPath $nodeHome -Filter "*.dll" -File -ErrorAction SilentlyContinue | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $serviceNodeDir $_.Name) -Force
}

if (Test-Path -LiteralPath $serviceOmniPanelDir) {
    Remove-Item -LiteralPath $serviceOmniPanelDir -Recurse -Force
}

New-Item -ItemType Directory -Path $serviceOmniPanelDir -Force | Out-Null
Copy-Item -Path (Join-Path $standaloneRoot "*") -Destination $serviceOmniPanelDir -Recurse -Force

if (Test-Path -LiteralPath $staticRoot) {
    $destStatic = Join-Path $serviceOmniPanelDir ".next\static"
    New-Item -ItemType Directory -Path $destStatic -Force | Out-Null
    Copy-Item -Path (Join-Path $staticRoot "*") -Destination $destStatic -Recurse -Force
}

if (Test-Path -LiteralPath $publicRoot) {
    $destPublic = Join-Path $serviceOmniPanelDir "public"
    New-Item -ItemType Directory -Path $destPublic -Force | Out-Null
    Copy-Item -Path (Join-Path $publicRoot "*") -Destination $destPublic -Recurse -Force
}

Write-Host "Prepared service Node runtime at: $serviceNodeDir" -ForegroundColor Green
Write-Host "Prepared service OmniPanel runtime at: $serviceOmniPanelDir" -ForegroundColor Green
