param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$ServiceName = "OmniRelay.Service",
    [string]$ServiceDisplayName = "OmniRelay Service",
    [switch]$SkipUiLaunch,
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script as Administrator."
    }
}

function Ensure-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return
    }

    if ($Elevated) {
        throw "Failed to elevate to Administrator."
    }

    $argList = @(
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-Configuration", $Configuration,
        "-ServiceName", "`"$ServiceName`"",
        "-ServiceDisplayName", "`"$ServiceDisplayName`"",
        "-Elevated"
    )

    if ($SkipUiLaunch) {
        $argList += "-SkipUiLaunch"
    }

    Write-Host "Requesting Administrator permission (UAC)..." -ForegroundColor Yellow
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList $argList | Out-Null
    exit 0
}

function Stop-UiProcesses {
    Write-Step "Stopping running UI processes"
    Get-Process -Name "OmniRelay.UI" -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Remove-ServiceIfExists {
    param([string]$Name)

    $svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $svc) {
        return
    }

    if ($svc.Status -ne "Stopped") {
        Write-Host "Stopping service $Name..."
        sc.exe stop $Name | Out-Null
        Start-Sleep -Seconds 2
    }

    Write-Host "Deleting service $Name..."
    sc.exe delete $Name | Out-Null
    Start-Sleep -Seconds 2
}

function New-RelayService {
    param(
        [string]$Name,
        [string]$DisplayName,
        [string]$ExePath
    )

    $binPath = '"' + $ExePath + '"'
    Write-Host "Creating service $Name..."
    sc.exe create $Name binPath= $binPath start= auto DisplayName= $DisplayName | Out-Null
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$serviceProj = Join-Path $repoRoot "src\OmniRelay.Service\OmniRelay.Service.csproj"
$uiProj = Join-Path $repoRoot "src\OmniRelay.UI\OmniRelay.UI.csproj"
$serviceExe = Join-Path $repoRoot "src\OmniRelay.Service\bin\$Configuration\net8.0-windows\OmniRelay.Service.exe"
$uiExe = Join-Path $repoRoot "src\OmniRelay.UI\bin\$Configuration\net8.0-windows\OmniRelay.UI.exe"

Ensure-Elevated
Assert-Admin
Stop-UiProcesses

Write-Step "Stopping and reinstalling Windows relay service"
Remove-ServiceIfExists -Name $ServiceName

Write-Step "Building relay service"
dotnet build $serviceProj -c $Configuration

Write-Step "Building UI"
dotnet build $uiProj -c $Configuration

if (-not (Test-Path $serviceExe)) {
    throw "Service executable not found: $serviceExe"
}

if (-not (Test-Path $uiExe)) {
    throw "UI executable not found: $uiExe"
}

Write-Step "Creating and starting relay service"
New-RelayService -Name $ServiceName -DisplayName $ServiceDisplayName -ExePath $serviceExe
sc.exe start $ServiceName | Out-Null

if (-not $SkipUiLaunch) {
    Write-Step "Launching UI"
    Start-Process -FilePath $uiExe
}

Write-Host "`nDone." -ForegroundColor Green
