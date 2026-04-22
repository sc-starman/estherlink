namespace OmniRelay.UI.Models;

public static class GatewayProtocols
{
    public const string VlessRealitySingbox = "vless_reality_singbox";
    public const string VlessPlainSingbox = "vless_plain_singbox";
    public const string ShadowsocksSingbox = "shadowsocks_singbox";
    public const string ShadowTlsV3ShadowsocksSingbox = "shadowtls_v3_shadowsocks_singbox";
    public const string OpenVpnTcpSingbox = "openvpn_tcp_singbox";
    public const string IpsecL2tpSingbox = "ipsec_l2tp_singbox";

    public static IReadOnlyList<(string Value, string Label)> All { get; } =
    [
        (VlessRealitySingbox, "VLESS Reality"),
        (VlessPlainSingbox, "VLESS (plain, no TLS)"),
        (ShadowsocksSingbox, "Shadowsocks"),
        (ShadowTlsV3ShadowsocksSingbox, "ShadowTLS v3 + Shadowsocks"),
        (OpenVpnTcpSingbox, "OpenVPN"),
        (IpsecL2tpSingbox, "IPSec/L2TP")
    ];

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return ShadowTlsV3ShadowsocksSingbox;
        }

        if (string.Equals(normalized, ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return ShadowsocksSingbox;
        }

        if (string.Equals(normalized, VlessPlainSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return VlessPlainSingbox;
        }

        if (string.Equals(normalized, OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnTcpSingbox;
        }

        if (string.Equals(normalized, IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return IpsecL2tpSingbox;
        }

        return VlessRealitySingbox;
    }

    public static string ToLabel(string? value)
    {
        var normalized = Normalize(value);
        foreach (var protocol in All)
        {
            if (string.Equals(protocol.Value, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return protocol.Label;
            }
        }

        return normalized;
    }
}
