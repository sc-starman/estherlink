import { type GatewayProtocolProvider } from "@/lib/providers/types";
import { VlessPlainSingboxProvider } from "@/lib/providers/vless-plain-singbox";
import { ShadowsocksSingboxProvider } from "@/lib/providers/shadowsocks-singbox";
import { ShadowTlsShadowsocksProvider } from "@/lib/providers/shadowtls";
import { TrojanSingboxProvider } from "@/lib/providers/trojan-singbox";
import { OpenVpnProvider } from "@/lib/providers/openvpn";
import { IpsecL2tpProvider } from "@/lib/providers/ipsec-l2tp";
import { SharedCredentialSingboxProvider } from "@/lib/providers/shared-credential-singbox";
import { getProtocolCapabilities } from "@/lib/protocol-capabilities";

const PROTOCOL_VLESS_TLS = "vless_tls_singbox";
const PROTOCOL_MIXED = "mixed_singbox";
const PROTOCOL_SOCKS = "socks_singbox";
const PROTOCOL_HTTP = "http_singbox";
const PROTOCOL_HYSTERIA2 = "hysteria2_singbox";
const PROTOCOL_TROJAN = "trojan_singbox";
const PROTOCOL_NAIVE = "naive_singbox";
const PROTOCOL_SHADOWSOCKS = "shadowsocks_singbox";
const PROTOCOL_SHADOWTLS = "shadowtls_v3_shadowsocks_singbox";
const PROTOCOL_OPENVPN = "openvpn_tcp_singbox";
const PROTOCOL_IPSEC_L2TP = "ipsec_l2tp_singbox";
const PROTOCOL_LOCAL_VLESS = "vless_local_tcp_plain";
const PROTOCOL_LOCAL_SHADOWSOCKS = "shadowsocks_local";
const PROTOCOL_LOCAL_OPENVPN = "openvpn_local_tcp";

const vlessPlainProvider = new VlessPlainSingboxProvider();
const shadowsocksProvider = new ShadowsocksSingboxProvider();
const shadowTlsProvider = new ShadowTlsShadowsocksProvider();
const trojanProvider = new TrojanSingboxProvider();
const mixedProvider = new SharedCredentialSingboxProvider(PROTOCOL_MIXED, "mixed", "OmniRelay Managed Mixed", getProtocolCapabilities(PROTOCOL_MIXED));
const socksProvider = new SharedCredentialSingboxProvider(PROTOCOL_SOCKS, "socks", "OmniRelay Managed SOCKS", getProtocolCapabilities(PROTOCOL_SOCKS));
const httpProvider = new SharedCredentialSingboxProvider(PROTOCOL_HTTP, "http", "OmniRelay Managed HTTP", getProtocolCapabilities(PROTOCOL_HTTP));
const hysteria2Provider = new SharedCredentialSingboxProvider(PROTOCOL_HYSTERIA2, "hysteria2", "OmniRelay Managed Hysteria2", getProtocolCapabilities(PROTOCOL_HYSTERIA2));
const naiveProvider = new SharedCredentialSingboxProvider(PROTOCOL_NAIVE, "naive", "OmniRelay Managed Naive", getProtocolCapabilities(PROTOCOL_NAIVE));
const openVpnProvider = new OpenVpnProvider();
const ipsecL2tpProvider = new IpsecL2tpProvider();

export function getActiveProtocol(): string {
  const raw = process.env.OMNIRELAY_ACTIVE_PROTOCOL?.trim().toLowerCase();
  if (raw === PROTOCOL_LOCAL_VLESS) return PROTOCOL_VLESS_TLS;
  if (raw === PROTOCOL_LOCAL_SHADOWSOCKS) return PROTOCOL_SHADOWSOCKS;
  if (raw === PROTOCOL_LOCAL_OPENVPN) return PROTOCOL_OPENVPN;
  if (raw === PROTOCOL_SHADOWTLS) return PROTOCOL_SHADOWTLS;
  if (raw === PROTOCOL_VLESS_TLS) return PROTOCOL_VLESS_TLS;
  if (raw === PROTOCOL_MIXED) return PROTOCOL_MIXED;
  if (raw === PROTOCOL_SOCKS) return PROTOCOL_SOCKS;
  if (raw === PROTOCOL_HTTP) return PROTOCOL_HTTP;
  if (raw === PROTOCOL_HYSTERIA2) return PROTOCOL_HYSTERIA2;
  if (raw === PROTOCOL_TROJAN) return PROTOCOL_TROJAN;
  if (raw === PROTOCOL_NAIVE) return PROTOCOL_NAIVE;
  if (raw === PROTOCOL_SHADOWSOCKS) return PROTOCOL_SHADOWSOCKS;
  if (raw === PROTOCOL_OPENVPN) return PROTOCOL_OPENVPN;
  if (raw === PROTOCOL_IPSEC_L2TP) return PROTOCOL_IPSEC_L2TP;
  return PROTOCOL_VLESS_TLS;
}

export function getGatewayProvider(): GatewayProtocolProvider {
  const active = getActiveProtocol();
  if (active === PROTOCOL_SHADOWTLS) return shadowTlsProvider;
  if (active === PROTOCOL_VLESS_TLS) return vlessPlainProvider;
  if (active === PROTOCOL_TROJAN) return trojanProvider;
  if (active === PROTOCOL_MIXED) return mixedProvider;
  if (active === PROTOCOL_SOCKS) return socksProvider;
  if (active === PROTOCOL_HTTP) return httpProvider;
  if (active === PROTOCOL_HYSTERIA2) return hysteria2Provider;
  if (active === PROTOCOL_NAIVE) return naiveProvider;
  if (active === PROTOCOL_SHADOWSOCKS) return shadowsocksProvider;
  if (active === PROTOCOL_OPENVPN) return openVpnProvider;
  if (active === PROTOCOL_IPSEC_L2TP) return ipsecL2tpProvider;
  return vlessPlainProvider;
}
