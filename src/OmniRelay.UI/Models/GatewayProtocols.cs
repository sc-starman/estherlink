namespace OmniRelay.UI.Models;

public static class GatewayProtocols
{
    public const string VlessReality3xui = "vless_reality_3xui";
    public const string VlessPlain3xui = "vless_plain_3xui";
    public const string Shadowsocks3xui = "shadowsocks_3xui";
    public const string ShadowTlsV3ShadowsocksSingbox = "shadowtls_v3_shadowsocks_singbox";
    public const string OpenVpnTcpRelay = "openvpn_tcp_relay";
    public const string IpsecL2tpHwdsl2 = "ipsec_l2tp_hwdsl2";

    public static IReadOnlyList<(string Value, string Label)> All { get; } =
    [
        (VlessReality3xui, "VLESS Reality (3x-ui)"),
        (VlessPlain3xui, "VLESS (3x-ui, no TLS)"),
        (Shadowsocks3xui, "Shadowsocks (3x-ui)"),
        (ShadowTlsV3ShadowsocksSingbox, "ShadowTLS v3 + Shadowsocks (sing-box)"),
        (OpenVpnTcpRelay, "OpenVPN (TCP, cert + user/pass)"),
        (IpsecL2tpHwdsl2, "IPSec/L2TP (hwdsl2)")
    ];

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return ShadowTlsV3ShadowsocksSingbox;
        }

        if (string.Equals(normalized, Shadowsocks3xui, StringComparison.OrdinalIgnoreCase))
        {
            return Shadowsocks3xui;
        }

        if (string.Equals(normalized, VlessPlain3xui, StringComparison.OrdinalIgnoreCase))
        {
            return VlessPlain3xui;
        }

        if (string.Equals(normalized, OpenVpnTcpRelay, StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnTcpRelay;
        }

        if (string.Equals(normalized, IpsecL2tpHwdsl2, StringComparison.OrdinalIgnoreCase))
        {
            return IpsecL2tpHwdsl2;
        }

        return VlessReality3xui;
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
