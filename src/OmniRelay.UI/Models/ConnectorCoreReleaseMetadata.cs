namespace OmniRelay.UI.Models;

public sealed record ConnectorCoreReleaseMetadata(
    string Version,
    string Channel,
    string PanelArtifactUrl,
    string PanelArtifactSha256);
