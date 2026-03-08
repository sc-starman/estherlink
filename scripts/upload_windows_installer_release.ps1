param(
    [Parameter(Mandatory = $true)]
    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$AdminApiKey,

    [string]$UploadBaseUrl,
    [switch]$InsecureSkipTlsVerify,
    [string]$MsiPath,
    [string]$Version,
    [string]$Channel = "stable",
    [string]$MinSupportedVersion,
    [string]$Notes = "",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

function Test-SemVerBasic {
    param([Parameter(Mandatory = $true)][string]$Value)
    return $Value -match '^\d+\.\d+\.\d+([\-+][0-9A-Za-z\.-]+)?$'
}

function Resolve-LatestMsiPath {
    param([Parameter(Mandatory = $true)][string]$RootPath, [Parameter(Mandatory = $true)][string]$BuildConfiguration)

    $buildScriptPath = Join-Path $RootPath "scripts\build_windows_msi.ps1"
    if (-not (Test-Path -LiteralPath $buildScriptPath)) {
        throw "Build script not found: $buildScriptPath"
    }

    # Consume build script pipeline output so this function returns only the MSI path.
    & $buildScriptPath -Configuration $BuildConfiguration 2>&1 | ForEach-Object {
        Write-Host $_
    }

    $installerBin = Join-Path $RootPath "src\OmniRelay.Installer\bin\$BuildConfiguration"
    $candidates = Get-ChildItem -Path $installerBin -Filter *.msi -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending

    if (-not $candidates -or $candidates.Count -eq 0) {
        throw "No MSI files found after build in $installerBin"
    }

    return $candidates[0].FullName
}

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

function Get-MsiProductVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "MSI file not found: $Path"
    }

    $installer = $null
    $database = $null
    $view = $null
    $record = $null

    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @($Path, 0))
        $view = $database.GetType().InvokeMember(
            "OpenView",
            "InvokeMethod",
            $null,
            $database,
            @("SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'")
        )
        $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null
        $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)

        if ($null -eq $record) {
            throw "MSI ProductVersion is missing."
        }

        $resolved = $record.GetType().InvokeMember("StringData", "GetProperty", $null, $record, @(1))
        if ([string]::IsNullOrWhiteSpace($resolved)) {
            throw "MSI ProductVersion is empty."
        }

        return $resolved.Trim()
    }
    finally {
        foreach ($obj in @($record, $view, $database, $installer)) {
            if ($null -ne $obj -and [System.Runtime.InteropServices.Marshal]::IsComObject($obj)) {
                [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($obj)
            }
        }
    }
}

function Invoke-Upload {
    param(
        [Parameter(Mandatory = $true)][string]$Endpoint,
        [Parameter(Mandatory = $true)][string]$ApiKey,
        [Parameter(Mandatory = $true)][string]$InstallerPath,
        [Parameter(Mandatory = $true)][string]$InstallerVersion,
        [Parameter(Mandatory = $true)][string]$InstallerChannel,
        [Parameter(Mandatory = $true)][string]$InstallerMinSupportedVersion,
        [switch]$SkipTlsValidation,
        [string]$InstallerNotes
    )

    $handler = New-Object System.Net.Http.HttpClientHandler
    if ($SkipTlsValidation) {
        $handler.ServerCertificateCustomValidationCallback = { $true }
    }

    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    $client.DefaultRequestHeaders.Add("X-ADMIN-API-KEY", $ApiKey)

    $multipart = New-Object System.Net.Http.MultipartFormDataContent
    $fileStream = [System.IO.File]::OpenRead($InstallerPath)
    $fileContent = New-Object System.Net.Http.StreamContent($fileStream)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/x-msi")
    $multipart.Add($fileContent, "installer", [System.IO.Path]::GetFileName($InstallerPath))
    $multipart.Add((New-Object System.Net.Http.StringContent($InstallerVersion)), "version")
    $multipart.Add((New-Object System.Net.Http.StringContent($InstallerChannel)), "channel")
    $multipart.Add((New-Object System.Net.Http.StringContent($InstallerMinSupportedVersion)), "minSupportedVersion")

    if (-not [string]::IsNullOrWhiteSpace($InstallerNotes)) {
        $multipart.Add((New-Object System.Net.Http.StringContent($InstallerNotes)), "notes")
    }

    try {
        try {
            $response = $client.PostAsync($Endpoint, $multipart).GetAwaiter().GetResult()
        }
        catch {
            $ex = $_.Exception
            $chain = @()
            while ($null -ne $ex) {
                $chain += ("{0}: {1}" -f $ex.GetType().FullName, $ex.Message)
                $ex = $ex.InnerException
            }

            $chainText = ($chain -join " | ")
            $tlsHint = if ($SkipTlsValidation) { "" } else { " If this is an origin host with self-signed or mismatched TLS cert, retry with -InsecureSkipTlsVerify for diagnosis only." }
            throw "Upload request failed before HTTP response. Endpoint=$Endpoint. Details: $chainText.$tlsHint"
        }

        $payload = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

        if (-not $response.IsSuccessStatusCode) {
            $statusCode = [int]$response.StatusCode
            if ($statusCode -eq 524) {
                throw "Upload failed with status 524 (Cloudflare timeout). Upload path likely timed out behind proxy. Use -UploadBaseUrl with a non-proxied origin host (DNS-only) and retry. Raw response: $payload"
            }

            throw "Upload failed with status $($response.StatusCode): $payload"
        }

        if ([string]::IsNullOrWhiteSpace($payload)) {
            throw "Upload endpoint returned empty response."
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

$uploadUrl = "$normalizedUploadBaseUrl/api/installer/upload-windows"

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    Write-Host "No -MsiPath provided. Building MSI first..." -ForegroundColor Yellow
    $MsiPath = Resolve-LatestMsiPath -RootPath $repoRoot -BuildConfiguration $Configuration
}
else {
    $MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
}

if (-not (Test-Path -LiteralPath $MsiPath)) {
    throw "MSI path not found: $MsiPath"
}

$resolvedVersion = $Version
if ([string]::IsNullOrWhiteSpace($resolvedVersion)) {
    $resolvedVersion = Get-MsiProductVersion -Path $MsiPath
}

if (-not (Test-SemVerBasic -Value $resolvedVersion)) {
    throw "Resolved version '$resolvedVersion' is not valid semver."
}

if ([string]::IsNullOrWhiteSpace($MinSupportedVersion)) {
    $MinSupportedVersion = $resolvedVersion
}

if (-not (Test-SemVerBasic -Value $MinSupportedVersion)) {
    throw "MinSupportedVersion '$MinSupportedVersion' is not valid semver."
}

$localHash = (Get-FileHash -Path $MsiPath -Algorithm SHA256).Hash.ToLowerInvariant()
$fileSizeBytes = (Get-Item -LiteralPath $MsiPath).Length

Write-Host "Uploading MSI release..." -ForegroundColor Cyan
Write-Host "  File: $MsiPath"
Write-Host "  Size: $fileSizeBytes bytes"
Write-Host "  Version: $resolvedVersion"
Write-Host "  Channel: $Channel"
Write-Host "  MinSupportedVersion: $MinSupportedVersion"
Write-Host "  Local SHA-256: $localHash"
Write-Host "  Upload Endpoint: $uploadUrl"
if ($InsecureSkipTlsVerify) {
    Write-Warning "TLS certificate validation is disabled for this upload request."
}

$response = Invoke-Upload `
    -Endpoint $uploadUrl `
    -ApiKey $AdminApiKey `
    -InstallerPath $MsiPath `
    -InstallerVersion $resolvedVersion `
    -InstallerChannel $Channel `
    -InstallerMinSupportedVersion $MinSupportedVersion `
    -SkipTlsValidation:$InsecureSkipTlsVerify `
    -InstallerNotes $Notes

Write-Host ""
Write-Host "Upload complete." -ForegroundColor Green
Write-Host "  Version: $($response.version)"
Write-Host "  Channel: $($response.channel)"
Write-Host "  PublishedAt: $($response.publishedAt)"
Write-Host "  Server SHA-256: $($response.sha256)"
Write-Host "  Download URL: $normalizedBaseUrl/download/windows"

if ($response.sha256 -ne $localHash) {
    Write-Warning "Local and server SHA-256 differ. Verify upload path and file consistency."
}
