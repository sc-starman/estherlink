param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$AdminApiKey,

    [string]$UploadBaseUrl,
    [switch]$InsecureSkipTlsVerify,
    [string]$ArtifactPath,
    [string]$BuildOutputDirectory = "build\connector-core",
    [ValidateSet("stable", "beta")]
    [string]$Channel = "stable",

    [ValidateSet("linux","windows")]
    [string]$Os = "linux",

    [ValidateSet("amd64", "arm64")]
    [string]$Arch = "amd64"
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
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$TargetOs,
        [Parameter(Mandatory = $true)][string]$TargetArch
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

    $buildScript = Join-Path $RootPath "scripts\build_connector_core.ps1"
    if (-not (Test-Path -LiteralPath $buildScript)) {
        throw "Build script not found: $buildScript"
    }

    Write-Host "No -ArtifactPath provided. Building connector-core binary first..." -ForegroundColor Yellow
    & $buildScript -OutputDirectory $OutputDirectory -OperatingSystems @($TargetOs) -Architectures @($TargetArch) 2>&1 | ForEach-Object {
        Write-Host $_
    }

    $binaryRoot = Join-Path $RootPath $OutputDirectory
    $binaryName = if ($TargetOs -eq "windows") { "connector-core.exe" } else { "connector-core" }
    $directPath = Join-Path $binaryRoot $binaryName
    if (Test-Path -LiteralPath $directPath) {
        return (Resolve-Path -LiteralPath $directPath).Path
    }

    $nestedPath = Join-Path (Join-Path $binaryRoot "$TargetOs-$TargetArch") $binaryName
    if (Test-Path -LiteralPath $nestedPath) {
        return (Resolve-Path -LiteralPath $nestedPath).Path
    }

    throw "Connector-core binary not found after build. Checked: $directPath and $nestedPath"
}

function New-UploadPackageFromBinary {
    param(
        [Parameter(Mandatory = $true)][string]$BinaryPath,
        [Parameter(Mandatory = $true)][string]$TargetOs,
        [Parameter(Mandatory = $true)][string]$TargetArch
    )

    if (-not (Test-Path -LiteralPath $BinaryPath)) {
        throw "Binary file not found: $BinaryPath"
    }

    $tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "omnirelay-connector-upload"
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $stageDir = Join-Path $tempRoot ("stage-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

    $binaryName = if ($TargetOs -eq "windows") { "connector-core.exe" } else { "connector-core" }
    $stagedBinary = Join-Path $stageDir $binaryName
    Copy-Item -LiteralPath $BinaryPath -Destination $stagedBinary -Force

    $packageName = if ($TargetOs -eq "windows") {
        "connector-core-$TargetOs-$TargetArch.zip"
    } else {
        "connector-core-$TargetOs-$TargetArch.tar.gz"
    }
    $packagePath = Join-Path $tempRoot $packageName

    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }

    if ($TargetOs -eq "windows") {
        Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $packagePath -CompressionLevel Optimal -Force
    } else {
        & tar -czf $packagePath -C $stageDir .
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to create tar.gz package for $TargetOs/$TargetArch (exit code $LASTEXITCODE)."
        }
    }

    if (-not (Test-Path -LiteralPath $packagePath)) {
        throw "Failed to create upload package: $packagePath"
    }

    return (Resolve-Path -LiteralPath $packagePath).Path
}

function Invoke-Upload {
    param(
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$TargetOs,
        [Parameter(Mandatory = $true)][string]$TargetArch,
        [Parameter(Mandatory = $true)][string]$ReleaseChannel,
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
    $contentType = if ($TargetOs -eq "windows") { "application/zip" } else { "application/gzip" }
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($contentType)
    $multipart.Add($fileContent, "artifact", [System.IO.Path]::GetFileName($PackagePath))
    $multipart.Add((New-Object System.Net.Http.StringContent($TargetOs)), "os")
    $multipart.Add((New-Object System.Net.Http.StringContent($TargetArch)), "arch")
    $multipart.Add((New-Object System.Net.Http.StringContent($ReleaseChannel)), "channel")

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

$uploadUrl = "$normalizedUploadBaseUrl/api/installer/upload-connector-core"
$resolvedInput = Resolve-ArtifactPath -RootPath $repoRoot -ProvidedPath $ArtifactPath -OutputDirectory $BuildOutputDirectory -TargetOs $Os -TargetArch $Arch
if ([string]::IsNullOrWhiteSpace($resolvedInput)) {
    throw "Resolved input path is empty."
}

$lowerInput = $resolvedInput.ToLowerInvariant()
$isArchive = $lowerInput.EndsWith(".zip") -or $lowerInput.EndsWith(".tar.gz")
$artifact = if ($isArchive) {
    $resolvedInput
} else {
    New-UploadPackageFromBinary -BinaryPath $resolvedInput -TargetOs $Os -TargetArch $Arch
}

$hash = (Get-FileHash -Path $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
$fileSize = (Get-Item -LiteralPath $artifact).Length

Write-Host "Uploading connector-core artifact..." -ForegroundColor Cyan
Write-Host "  File: $artifact"
Write-Host "  Size: $fileSize bytes"
Write-Host "  SHA-256: $hash"
Write-Host "  Channel: $Channel"
Write-Host "  OS/Arch: $Os/$Arch"
Write-Host "  Upload Endpoint: $uploadUrl"

$response = Invoke-Upload -Endpoint $uploadUrl -ApiKey $AdminApiKey -PackagePath $artifact -TargetOs $Os -TargetArch $Arch -ReleaseChannel $Channel -SkipTlsValidation:$InsecureSkipTlsVerify

$downloadPath = if ($Channel -eq "beta") {
    "/download/connector-core/beta/$Os/$Arch"
}
else {
    "/download/connector-core/$Os/$Arch"
}

Write-Host ""
Write-Host "Upload complete." -ForegroundColor Green
Write-Host "  Server SHA-256: $($response.sha256)"
Write-Host "  Download URL: $normalizedBaseUrl$downloadPath"

if ($response.sha256 -ne $hash) {
    Write-Warning "Local and server SHA-256 differ. Verify upload path and file consistency."
}
