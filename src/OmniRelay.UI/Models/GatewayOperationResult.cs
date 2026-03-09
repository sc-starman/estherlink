namespace OmniRelay.UI.Models;

public sealed record GatewayOperationResult(
    bool Success,
    string Message,
    string? PanelUrl = null,
    string? PanelUsername = null,
    string? InitialPanelPassword = null);
