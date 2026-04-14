namespace OmniRelay.UI.Models;

public static class GatewayBootstrapModes
{
    public const string Tunnel = "tunnel";
    public const string Direct = "direct";

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == Direct ? Direct : Tunnel;
    }
}
