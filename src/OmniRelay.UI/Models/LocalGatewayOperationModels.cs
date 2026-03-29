using OmniRelay.Ipc;

namespace OmniRelay.UI.Models;

public sealed record LocalGatewayClientsResult(
    bool Success,
    string Message,
    IReadOnlyList<LocalGatewayClientRecord> Clients);

public sealed record LocalGatewayClientConfigBuildResult(
    bool Success,
    string Message,
    string Uri,
    string Title);
