using System.Security.Cryptography;
using System.Text.Json;
using OmniRelay.Backend.Configuration;
using Microsoft.Extensions.Options;

namespace OmniRelay.Backend.Services.Installers;

public sealed class FileSystemInstallerStorageService : IInstallerStorageService
{
    private readonly InstallerStorageOptions _options;
    private readonly string _rootPath;

    public FileSystemInstallerStorageService(IWebHostEnvironment environment, IOptions<InstallerStorageOptions> options)
    {
        _options = options.Value ?? new InstallerStorageOptions();

        var configured = string.IsNullOrWhiteSpace(_options.RootPath)
            ? "data/installers"
            : _options.RootPath.Trim();

        _rootPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured);

        Directory.CreateDirectory(_rootPath);
    }

    public long MaxUploadBytes => Math.Max(1, _options.MaxUploadMb) * 1024L * 1024L;
    public string RootPath => _rootPath;

    public string GetWindowsInstallerPath(string channel, string version)
    {
        var safeChannel = SanitizeSegment(string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant());
        var safeVersion = SanitizeSegment(version.Trim());
        return Path.Combine(_rootPath, "windows", safeChannel, safeVersion, $"OmniRelay-{safeVersion}-windows-x64.msi");
    }

    public string GetWindowsDownloadFileName(string version)
    {
        var safeVersion = SanitizeSegment(version.Trim());
        return $"OmniRelay-{safeVersion}-windows-x64.msi";
    }

    public string GetOmniGatewayArtifactPath(string channel)
    {
        var safeChannel = SanitizeSegment(string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant());
        return Path.Combine(_rootPath, "omni-gateway", safeChannel, "latest", "omni-gateway.tar.gz");
    }

    public string GetOmniGatewayDownloadFileName()
    {
        return "omni-gateway.tar.gz";
    }

    public string GetOmniGatewayManifestPath(string channel)
    {
        var artifactPath = GetOmniGatewayArtifactPath(channel);
        return Path.Combine(Path.GetDirectoryName(artifactPath)!, "manifest.json");
    }

    public string GetOmniGatewayManifestDownloadFileName()
    {
        return "omni-gateway.manifest.json";
    }

    public string GetConnectorCoreArtifactPath(string channel, string os, string arch)
    {
        var safeChannel = SanitizeSegment(string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant());
        var safeOs = SanitizeSegment(string.IsNullOrWhiteSpace(os) ? "linux" : os.Trim().ToLowerInvariant());
        var safeArch = SanitizeSegment(string.IsNullOrWhiteSpace(arch) ? "amd64" : arch.Trim().ToLowerInvariant());
        return Path.Combine(_rootPath, "connector-core", safeChannel, safeOs, safeArch, $"connector-core-{safeOs}-{safeArch}.tar.gz");
    }

    public string GetConnectorCoreDownloadFileName(string os, string arch)
    {
        var safeOs = SanitizeSegment(string.IsNullOrWhiteSpace(os) ? "linux" : os.Trim().ToLowerInvariant());
        var safeArch = SanitizeSegment(string.IsNullOrWhiteSpace(arch) ? "amd64" : arch.Trim().ToLowerInvariant());
        return $"connector-core-{safeOs}-{safeArch}.tar.gz";
    }

    public string GetConnectorCoreManifestPath(string channel, string os, string arch)
    {
        return Path.Combine(GetConnectorCoreReleaseDirectory(channel, os, arch), "manifest.json");
    }

    public string GetConnectorCoreSignaturePath(string channel, string os, string arch)
    {
        return Path.Combine(GetConnectorCoreReleaseDirectory(channel, os, arch), "manifest.sig");
    }

    public string GetConnectorCoreManifestDownloadFileName(string os, string arch)
    {
        var safeOs = SanitizeSegment(string.IsNullOrWhiteSpace(os) ? "linux" : os.Trim().ToLowerInvariant());
        var safeArch = SanitizeSegment(string.IsNullOrWhiteSpace(arch) ? "amd64" : arch.Trim().ToLowerInvariant());
        return $"connector-core-{safeOs}-{safeArch}.manifest.json";
    }

    public string GetConnectorCoreSignatureDownloadFileName(string os, string arch)
    {
        var safeOs = SanitizeSegment(string.IsNullOrWhiteSpace(os) ? "linux" : os.Trim().ToLowerInvariant());
        var safeArch = SanitizeSegment(string.IsNullOrWhiteSpace(arch) ? "amd64" : arch.Trim().ToLowerInvariant());
        return $"connector-core-{safeOs}-{safeArch}.manifest.sig";
    }

    public async Task<InstallerSaveResult> SaveWindowsInstallerAsync(
        string sourceFilePath,
        string channel,
        string version,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Source installer file was not found.", sourceFilePath);
        }

        var destinationPath = GetWindowsInstallerPath(channel, version);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destination directory could not be determined.");

        Directory.CreateDirectory(destinationDirectory);

        var tempDestinationPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        long written = 0;
        string sha256;

        await using (var source = File.Open(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = File.Open(tempDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                written += read;
            }

            sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        File.Move(tempDestinationPath, destinationPath, overwrite: true);
        return new InstallerSaveResult(destinationPath, written, sha256);
    }

    public async Task<InstallerSaveResult> SaveOmniGatewayArtifactAsync(
        string sourceFilePath,
        string channel,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Source gateway artifact file was not found.", sourceFilePath);
        }

        var destinationPath = GetOmniGatewayArtifactPath(channel);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destination directory could not be determined.");

        Directory.CreateDirectory(destinationDirectory);
        var tempDestinationPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        long written = 0;
        string sha256;

        await using (var source = File.Open(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = File.Open(tempDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                written += read;
            }

            sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        var manifestPath = GetOmniGatewayManifestPath(channel);
        var manifestTempPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        var normalizedChannel = string.IsNullOrWhiteSpace(channel) ? "stable" : channel.Trim().ToLowerInvariant();
        var manifest = new OmniGatewayReleaseManifest(
            1,
            normalizedChannel,
            GetOmniGatewayDownloadFileName(),
            sha256,
            written);
        await File.WriteAllTextAsync(
            manifestTempPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            cancellationToken);

        // Publish the manifest last; it is the release commit marker.
        File.Move(tempDestinationPath, destinationPath, overwrite: true);
        File.Move(manifestTempPath, manifestPath, overwrite: true);
        return new InstallerSaveResult(destinationPath, written, sha256);
    }

    public async Task<InstallerSaveResult> SaveConnectorCoreArtifactAsync(
        string sourceFilePath,
        string channel,
        string os,
        string arch,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Source connector-core artifact file was not found.", sourceFilePath);
        }

        var destinationPath = GetConnectorCoreArtifactPath(channel, os, arch);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Destination directory could not be determined.");

        Directory.CreateDirectory(destinationDirectory);
        var tempDestinationPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        long written = 0;
        string sha256;

        await using (var source = File.Open(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = File.Open(tempDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                written += read;
            }

            sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        File.Move(tempDestinationPath, destinationPath, overwrite: true);
        return new InstallerSaveResult(destinationPath, written, sha256);
    }

    public async Task<ConnectorCoreReleaseSaveResult> SaveConnectorCoreReleaseAsync(
        string artifactSourceFilePath,
        string manifestSourceFilePath,
        string signatureSourceFilePath,
        string channel,
        string os,
        string arch,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in new[] { artifactSourceFilePath, manifestSourceFilePath, signatureSourceFilePath })
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Connector-core release source file was not found.", sourcePath);
            }
        }

        var artifactPath = GetConnectorCoreArtifactPath(channel, os, arch);
        var manifestPath = GetConnectorCoreManifestPath(channel, os, arch);
        var signaturePath = GetConnectorCoreSignaturePath(channel, os, arch);
        var destinationDirectory = Path.GetDirectoryName(artifactPath)
            ?? throw new InvalidOperationException("Connector-core release directory could not be determined.");
        Directory.CreateDirectory(destinationDirectory);

        var artifactTempPath = $"{artifactPath}.{Guid.NewGuid():N}.tmp";
        var manifestTempPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        var signatureTempPath = $"{signaturePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            var artifactResult = await CopyWithHashAsync(
                artifactSourceFilePath,
                artifactTempPath,
                cancellationToken);
            await CopyFileAsync(manifestSourceFilePath, manifestTempPath, cancellationToken);
            await CopyFileAsync(signatureSourceFilePath, signatureTempPath, cancellationToken);

            // The manifest is the release commit marker. Publish it only after both
            // files it authenticates have been replaced.
            File.Move(artifactTempPath, artifactPath, overwrite: true);
            File.Move(signatureTempPath, signaturePath, overwrite: true);
            File.Move(manifestTempPath, manifestPath, overwrite: true);

            return new ConnectorCoreReleaseSaveResult(
                artifactResult with { FilePath = artifactPath },
                manifestPath,
                signaturePath);
        }
        finally
        {
            DeleteIfExists(artifactTempPath);
            DeleteIfExists(manifestTempPath);
            DeleteIfExists(signatureTempPath);
        }
    }

    private string GetConnectorCoreReleaseDirectory(string channel, string os, string arch)
    {
        return Path.GetDirectoryName(GetConnectorCoreArtifactPath(channel, os, arch))
            ?? throw new InvalidOperationException("Connector-core release directory could not be determined.");
    }

    private static async Task<InstallerSaveResult> CopyWithHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        long written = 0;
        string sha256;
        await using (var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = File.Open(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hasher.AppendData(buffer, 0, read);
                written += read;
            }

            sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        return new InstallerSaveResult(destinationPath, written, sha256);
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var target = File.Open(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static string SanitizeSegment(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
