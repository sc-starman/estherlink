namespace OmniRelay.Service.Runtime;

public sealed class LocalGatewayClientConfigPayload
{
    public static LocalGatewayClientConfigPayload Empty { get; } = new();

    public string Mode { get; set; } = "uri";
    public string Uri { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string OvpnFileName { get; set; } = string.Empty;
    public string OvpnContent { get; set; } = string.Empty;
}
