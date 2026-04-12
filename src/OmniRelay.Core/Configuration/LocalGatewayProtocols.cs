namespace OmniRelay.Core.Configuration;

public static class LocalGatewayProtocols
{
    public const string VlessTcpPlain = "vless_local_tcp_plain";
    public const string Shadowsocks = "shadowsocks_local";
    public const string OpenVpnTcp = "openvpn_local_tcp";
    public const string OpenVpnComingSoon = "openvpn_local_coming_soon";

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, Shadowsocks, StringComparison.OrdinalIgnoreCase))
        {
            return Shadowsocks;
        }

        if (string.Equals(normalized, OpenVpnComingSoon, StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnTcp;
        }

        if (string.Equals(normalized, OpenVpnTcp, StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnTcp;
        }

        return VlessTcpPlain;
    }

    public static bool IsSupportedInV1(string? value)
    {
        var normalized = Normalize(value);
        return string.Equals(normalized, VlessTcpPlain, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, Shadowsocks, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, OpenVpnTcp, StringComparison.OrdinalIgnoreCase);
    }
}
