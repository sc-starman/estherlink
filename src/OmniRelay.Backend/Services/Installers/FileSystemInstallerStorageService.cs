using System.Security.Cryptography;
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

        File.Move(tempDestinationPath, destinationPath, overwrite: true);
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

    private static string SanitizeSegment(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = input.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
