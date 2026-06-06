namespace OmniRelay.Backend.Services.Installers;

public interface IInstallerStorageService
{
    long MaxUploadBytes { get; }
    string RootPath { get; }
    string GetWindowsInstallerPath(string channel, string version);
    string GetWindowsDownloadFileName(string version);

    string GetOmniGatewayArtifactPath(string channel);
    string GetOmniGatewayManifestPath(string channel);
    string GetOmniGatewayDownloadFileName();
    string GetOmniGatewayManifestDownloadFileName();
    string GetConnectorCoreArtifactPath(string channel, string os, string arch);
    string GetConnectorCoreManifestPath(string channel, string os, string arch);
    string GetConnectorCoreSignaturePath(string channel, string os, string arch);
    string GetConnectorCoreDownloadFileName(string os, string arch);
    string GetConnectorCoreManifestDownloadFileName(string os, string arch);
    string GetConnectorCoreSignatureDownloadFileName(string os, string arch);
    Task<InstallerSaveResult> SaveWindowsInstallerAsync(string sourceFilePath, string channel, string version, CancellationToken cancellationToken);
    Task<InstallerSaveResult> SaveOmniGatewayArtifactAsync(string sourceFilePath, string channel, CancellationToken cancellationToken);
    Task<InstallerSaveResult> SaveConnectorCoreArtifactAsync(string sourceFilePath, string channel, string os, string arch, CancellationToken cancellationToken);
    Task<ConnectorCoreReleaseSaveResult> SaveConnectorCoreReleaseAsync(
        string artifactSourceFilePath,
        string manifestSourceFilePath,
        string signatureSourceFilePath,
        string channel,
        string os,
        string arch,
        CancellationToken cancellationToken);
}

public sealed record InstallerSaveResult(string FilePath, long FileSizeBytes, string Sha256);
public sealed record ConnectorCoreReleaseSaveResult(
    InstallerSaveResult Artifact,
    string ManifestPath,
    string SignaturePath);
