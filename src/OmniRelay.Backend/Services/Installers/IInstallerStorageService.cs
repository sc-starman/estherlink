namespace OmniRelay.Backend.Services.Installers;

public interface IInstallerStorageService
{
    long MaxUploadBytes { get; }
    string RootPath { get; }
    string GetWindowsInstallerPath(string channel, string version);
    string GetWindowsDownloadFileName(string version);

    string GetOmniGatewayArtifactPath();
    string GetOmniGatewayDownloadFileName();
    Task<InstallerSaveResult> SaveWindowsInstallerAsync(string sourceFilePath, string channel, string version, CancellationToken cancellationToken);
    Task<InstallerSaveResult> SaveOmniGatewayArtifactAsync(string sourceFilePath, CancellationToken cancellationToken);
}

public sealed record InstallerSaveResult(string FilePath, long FileSizeBytes, string Sha256);
