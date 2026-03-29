namespace OmniRelay.Core.Configuration;

public static class GatewayTypes
{
    public const string Remote = "remote";
    public const string Local = "local";

    public static string Normalize(string? value)
    {
        return string.Equals((value ?? string.Empty).Trim(), Local, StringComparison.OrdinalIgnoreCase)
            ? Local
            : Remote;
    }
}
