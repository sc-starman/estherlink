namespace OmniRelay.UI.Models;

public sealed class ConnectorCoreGatewaySpecOptions
{
    public required string ConnectorCoreVersion { get; init; }
    public string ReleaseChannel { get; init; } = "stable";
    public string PanelPublicHost { get; init; } = string.Empty;
    public string PanelCertRemotePath { get; init; } = string.Empty;
    public string PanelKeyRemotePath { get; init; } = string.Empty;
    public string PanelArtifactRemotePath { get; init; } = string.Empty;
    public string PanelArtifactUrl { get; init; } = string.Empty;
    public string PanelArtifactSha256 { get; init; } = string.Empty;
    public string PanelUsername { get; init; } = string.Empty;
    public string PanelPassword { get; init; } = string.Empty;
    public string ProtocolCertRemotePath { get; init; } = string.Empty;
    public string ProtocolKeyRemotePath { get; init; } = string.Empty;
    public string OpenVpnSharedCaCertRemotePath { get; init; } = string.Empty;
    public string OpenVpnSharedClientCertRemotePath { get; init; } = string.Empty;
    public string OpenVpnSharedClientKeyRemotePath { get; init; } = string.Empty;
    public string OpenVpnSharedTlsCryptKeyRemotePath { get; init; } = string.Empty;
    public string ShadowsocksServerPassword { get; init; } = string.Empty;
}
