namespace OmniRelay.UI.Models;

public static class GatewayProtocols
{
    public const string VlessPlainSingbox = "vless_plain_singbox";
    public const string VlessTlsSingbox = "vless_tls_singbox";
    public const string MixedSingbox = "mixed_singbox";
    public const string SocksSingbox = "socks_singbox";
    public const string HttpSingbox = "http_singbox";
    public const string Hysteria2Singbox = "hysteria2_singbox";
    public const string TrojanSingbox = "trojan_singbox";
    public const string NaiveSingbox = "naive_singbox";
    public const string ShadowsocksSingbox = "shadowsocks_singbox";
    public const string ShadowTlsV3ShadowsocksSingbox = "shadowtls_v3_shadowsocks_singbox";
    public const string OpenVpnTcpSingbox = "openvpn_tcp_singbox";
    public const string IpsecL2tpSingbox = "ipsec_l2tp_singbox";

    public static IReadOnlyList<(string Value, string Label)> All { get; } =
    [
        (VlessTlsSingbox, "VLESS"),
        (MixedSingbox, "Mixed (HTTP+SOCKS)"),
        (SocksSingbox, "SOCKS"),
        (HttpSingbox, "HTTP"),
        (Hysteria2Singbox, "Hysteria2"),
        (TrojanSingbox, "Trojan"),
        (NaiveSingbox, "Naive"),
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

        if (string.Equals(normalized, VlessTlsSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return VlessTlsSingbox;
        }

        if (string.Equals(normalized, VlessPlainSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return VlessTlsSingbox;
        }

        if (string.Equals(normalized, MixedSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return MixedSingbox;
        }

        if (string.Equals(normalized, SocksSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return SocksSingbox;
        }

        if (string.Equals(normalized, HttpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return HttpSingbox;
        }

        if (string.Equals(normalized, Hysteria2Singbox, StringComparison.OrdinalIgnoreCase))
        {
            return Hysteria2Singbox;
        }

        if (string.Equals(normalized, TrojanSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return TrojanSingbox;
        }

        if (string.Equals(normalized, NaiveSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return NaiveSingbox;
        }

        if (string.Equals(normalized, OpenVpnTcpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return OpenVpnTcpSingbox;
        }

        if (string.Equals(normalized, IpsecL2tpSingbox, StringComparison.OrdinalIgnoreCase))
        {
            return IpsecL2tpSingbox;
        }

        return VlessTlsSingbox;
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
