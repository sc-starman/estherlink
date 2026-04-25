namespace OmniRelay.Backend.Services.Installers;

public interface IInstallerStorageService
{
    long MaxUploadBytes { get; }
    string RootPath { get; }
    string GetWindowsInstallerPath(string channel, string version);
    string GetWindowsDownloadFileName(string version);

    string GetOmniGatewayArtifactPath();
    string GetOmniGatewayDownloadFileName();
    string GetConnectorCoreArtifactPath(string os, string arch);
    string GetConnectorCoreDownloadFileName(string os, string arch);
    Task<InstallerSaveResult> SaveWindowsInstallerAsync(string sourceFilePath, string channel, string version, CancellationToken cancellationToken);
    Task<InstallerSaveResult> SaveOmniGatewayArtifactAsync(string sourceFilePath, CancellationToken cancellationToken);
    Task<InstallerSaveResult> SaveConnectorCoreArtifactAsync(string sourceFilePath, string os, string arch, CancellationToken cancellationToken);
}

public sealed record InstallerSaveResult(string FilePath, long FileSizeBytes, string Sha256);
