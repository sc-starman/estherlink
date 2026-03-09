param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$AdminApiKey,

    [string]$UploadBaseUrl,
    [switch]$InsecureSkipTlsVerify,
    [string]$ArtifactPath,
    [string]$BuildOutputDirectory = "artifacts\omni-gateway"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http

function Normalize-BaseUrl {
    param([Parameter(Mandatory = $true)][string]$Value)

    $normalized = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        throw "BaseUrl is required."
    }

    if ($normalized -notmatch '^https?://') {
        $normalized = "https://$normalized"
    }

    return $normalized.TrimEnd("/")
}

function Resolve-ArtifactPath {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [string]$ProvidedPath,
        [Parameter(Mandatory = $true)][string]$OutputDirectory
    )

    $candidate = if ($null -eq $ProvidedPath) { "" } else { $ProvidedPath.Trim() }
    if ($candidate -eq '""' -or $candidate -eq "''") {
        $candidate = string.Empty
    }

    if (-not [string]::IsNullOrWhiteSpace($candidate)) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            throw "Artifact path not found: $candidate"
        }

        return (Resolve-Path -LiteralPath $candidate).Path
    }

    $buildScript = Join-Path $RootPath "scripts\build_omni_gateway_panel.ps1"
    if (-not (Test-Path -LiteralPath $buildScript)) {
        throw "Build script not found: $buildScript"
    }

    Write-Host "No -ArtifactPath provided. Building OmniPanel artifact first..." -ForegroundColor Yellow
    & $buildScript -OutputDirectory $OutputDirectory 2>&1 | ForEach-Object {
        Write-Host $_
    }

    $artifactRoot = Join-Path $RootPath $OutputDirectory
    $candidates = Get-ChildItem -Path $artifactRoot -Filter *.tar.gz -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending
    if (-not $candidates -or $candidates.Count -eq 0) {
        throw "Gateway artifact not found after build in: $artifactRoot"
    }

    return $candidates[0].FullName
}

function Invoke-Upload {
    param(
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [switch]$SkipTlsValidation
    )

    $handler = New-Object System.Net.Http.HttpClientHandler
    if ($SkipTlsValidation) {
        $handler.ServerCertificateCustomValidationCallback = { $true }
    }

    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(20)
    $client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", $ApiKey)

    $multipart = New-Object System.Net.Http.MultipartFormDataContent
    $fileStream = [System.IO.File]::OpenRead($PackagePath)
    $fileContent = New-Object System.Net.Http.StreamContent($fileStream)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/gzip")
    $multipart.Add($fileContent, "artifact", [System.IO.Path]::GetFileName($PackagePath))

    try {
        $response = $client.PostAsync($Endpoint, $multipart).GetAwaiter().GetResult()
        $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        if (-not $response.IsSuccessStatusCode) {
            throw "Upload failed with status $($response.StatusCode): $payload"
        }

        return ($payload | ConvertFrom-Json)
    }
    finally {
        $fileStream.Dispose()
        $multipart.Dispose()
        $client.Dispose()
        $handler.Dispose()
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$normalizedBaseUrl = Normalize-BaseUrl -Value $BaseUrl
$normalizedUploadBaseUrl = if ([string]::IsNullOrWhiteSpace($UploadBaseUrl)) {
    $normalizedBaseUrl
}
else {
    Normalize-BaseUrl -Value $UploadBaseUrl
}

$uploadUrl = "$normalizedUploadBaseUrl/api/installer/upload-omni-gateway"
$artifact = Resolve-ArtifactPath -RootPath $repoRoot -ProvidedPath $ArtifactPath -OutputDirectory $BuildOutputDirectory
if ([string]::IsNullOrWhiteSpace($artifact)) {
    throw "Artifact path resolved to empty value."
}

if (-not $artifact.ToLowerInvariant().EndsWith(".tar.gz")) {
    throw "Artifact must be .tar.gz: $artifact"
}

if (-not (Test-Path -LiteralPath $artifact)) {
    throw "Artifact file does not exist: $artifact"
}

$hash = (Get-FileHash -Path $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
$fileSize = (Get-Item -LiteralPath $artifact).Length

Write-Host "Uploading OmniPanel artifact..." -ForegroundColor Cyan
Write-Host "  File: $artifact"
Write-Host "  Size: $fileSize bytes"
Write-Host "  SHA-256: $hash"
Write-Host "  Upload Endpoint: $uploadUrl"

$response = Invoke-Upload -Endpoint $uploadUrl -ApiKey $AdminApiKey -PackagePath $artifact -SkipTlsValidation:$InsecureSkipTlsVerify

Write-Host ""
Write-Host "Upload complete." -ForegroundColor Green
Write-Host "  Server SHA-256: $($response.sha256)"
Write-Host "  Download URL: $normalizedBaseUrl/download/omni-gateway"

if ($response.sha256 -ne $hash) {
    Write-Warning "Local and server SHA-256 differ. Verify upload path and file consistency."
}
