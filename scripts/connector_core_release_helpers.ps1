$ErrorActionPreference = "Stop"

function Get-OmniRelayInstallerVersion {
    param([Parameter(Mandatory = $true)][string]$RepoRootPath)

    $productWxsPath = Join-Path $RepoRootPath "src\OmniRelay.Installer\Product.wxs"
    if (-not (Test-Path -LiteralPath $productWxsPath)) {
        throw "Installer Product.wxs not found: $productWxsPath"
    }

    $content = [System.IO.File]::ReadAllText($productWxsPath)
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $content,
        '<Package\b[^>]*\bVersion\s*=\s*"(?<version>\d+\.\d+\.\d+)"',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) {
        throw "Could not find Package Version=`"x.y.z`" in $productWxsPath"
    }

    return $match.Groups["version"].Value
}

function Get-ConnectorCoreSigningPaths {
    param([Parameter(Mandatory = $true)][string]$RepoRootPath)

    $certsRoot = Join-Path $RepoRootPath "certs"
    return @{
        Root = $certsRoot
        PrivateKeyPath = Join-Path $certsRoot "connector-core-private.pem"
        PublicKeyPath = Join-Path $certsRoot "connector-core-public.pem"
    }
}

function Invoke-ConnectorCoreSigningHelper {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        throw "dotnet SDK is required to generate/sign connector-core release keys."
    }

    $helperRoot = Join-Path ([System.IO.Path]::GetTempPath()) "omnirelay-connector-core-signing-helper"
    $projectRoot = Join-Path $helperRoot "src"
    New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
    $projectPath = Join-Path $projectRoot "ConnectorCoreSigningHelper.csproj"
    $programPath = Join-Path $projectRoot "Program.cs"

    [System.IO.File]::WriteAllText(
        $projectPath,
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
"@,
        [System.Text.UTF8Encoding]::new($false))

    [System.IO.File]::WriteAllText(
        $programPath,
        @'
using System.Security.Cryptography;
using System.Text;

static string Pem(string label, byte[] der)
{
    var base64 = Convert.ToBase64String(der);
    var builder = new StringBuilder();
    builder.AppendLine("-----BEGIN " + label + "-----");
    for (var index = 0; index < base64.Length; index += 64)
    {
        builder.AppendLine(base64.Substring(index, Math.Min(64, base64.Length - index)));
    }
    builder.AppendLine("-----END " + label + "-----");
    return builder.ToString();
}

static RSA LoadPrivateKey(string path)
{
    var rsa = RSA.Create();
    rsa.ImportFromPem(File.ReadAllText(path));
    return rsa;
}

if (args.Length < 1)
{
    throw new ArgumentException("command is required");
}

switch (args[0])
{
    case "ensure-keys":
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("ensure-keys requires private and public key paths");
        }
        var privatePath = args[1];
        var publicPath = args[2];
        Directory.CreateDirectory(Path.GetDirectoryName(privatePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publicPath)!);
        if (!File.Exists(privatePath))
        {
            using var rsa = RSA.Create(3072);
            File.WriteAllText(privatePath, Pem("PRIVATE KEY", rsa.ExportPkcs8PrivateKey()), new UTF8Encoding(false));
            File.WriteAllText(publicPath, Pem("PUBLIC KEY", rsa.ExportSubjectPublicKeyInfo()), new UTF8Encoding(false));
            return;
        }
        using (var rsa = LoadPrivateKey(privatePath))
        {
            if (!File.Exists(publicPath))
            {
                File.WriteAllText(publicPath, Pem("PUBLIC KEY", rsa.ExportSubjectPublicKeyInfo()), new UTF8Encoding(false));
            }
        }
        using (var publicRsa = RSA.Create())
        {
            publicRsa.ImportFromPem(File.ReadAllText(publicPath));
        }
        return;
    }
    case "sign":
    {
        if (args.Length != 4)
        {
            throw new ArgumentException("sign requires private key, input, and output paths");
        }
        using var rsa = LoadPrivateKey(args[1]);
        var signature = rsa.SignData(File.ReadAllBytes(args[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Directory.CreateDirectory(Path.GetDirectoryName(args[3])!);
        File.WriteAllBytes(args[3], signature);
        return;
    }
    case "export-public":
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("export-public requires private and public key paths");
        }
        using var rsa = LoadPrivateKey(args[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(args[2])!);
        File.WriteAllText(args[2], Pem("PUBLIC KEY", rsa.ExportSubjectPublicKeyInfo()), new UTF8Encoding(false));
        return;
    }
    default:
        throw new ArgumentException("unsupported command: " + args[0]);
}
'@,
        [System.Text.UTF8Encoding]::new($false))

    $dotnetArgs = @("run", "--project", $projectPath, "--") + $Arguments
    & dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) {
        throw "connector-core signing helper failed with exit code $LASTEXITCODE."
    }
}

function Ensure-ConnectorCoreSigningKeys {
    param([Parameter(Mandatory = $true)][string]$RepoRootPath)

    $paths = Get-ConnectorCoreSigningPaths -RepoRootPath $RepoRootPath
    Invoke-ConnectorCoreSigningHelper -Arguments @("ensure-keys", $paths.PrivateKeyPath, $paths.PublicKeyPath)
    return $paths
}

function Sign-ConnectorCoreManifest {
    param(
        [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$SignaturePath
    )

    Invoke-ConnectorCoreSigningHelper -Arguments @("sign", $PrivateKeyPath, $ManifestPath, $SignaturePath)
}

function Export-ConnectorCorePublicKey {
    param(
        [Parameter(Mandatory = $true)][string]$PrivateKeyPath,
        [Parameter(Mandatory = $true)][string]$PublicKeyPath
    )

    Invoke-ConnectorCoreSigningHelper -Arguments @("export-public", $PrivateKeyPath, $PublicKeyPath)
}
