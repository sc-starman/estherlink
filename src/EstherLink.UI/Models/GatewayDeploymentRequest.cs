using EstherLink.Core.Configuration;

namespace EstherLink.UI.Models;

public sealed class GatewayDeploymentRequest
{
    public required ServiceConfig Config { get; init; }
    public required string BundleLocalPath { get; init; }
    public required string BundleSha256 { get; init; }
    public int GatewayPublicPort { get; init; }
    public int GatewayPanelPort { get; init; }
}
