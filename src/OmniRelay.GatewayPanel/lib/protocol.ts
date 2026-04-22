import { type GatewayProtocolProvider } from "@/lib/providers/types";
import { VlessRealitySingboxProvider } from "@/lib/providers/vless-reality-singbox";
import { VlessPlainSingboxProvider } from "@/lib/providers/vless-plain-singbox";
import { ShadowsocksSingboxProvider } from "@/lib/providers/shadowsocks-singbox";
import { ShadowTlsShadowsocksProvider } from "@/lib/providers/shadowtls";
import { OpenVpnProvider } from "@/lib/providers/openvpn";
import { IpsecL2tpProvider } from "@/lib/providers/ipsec-l2tp";

const PROTOCOL_VLESS_REALITY = "vless_reality_singbox";
const PROTOCOL_VLESS_PLAIN = "vless_plain_singbox";
const PROTOCOL_SHADOWSOCKS = "shadowsocks_singbox";
const PROTOCOL_SHADOWTLS = "shadowtls_v3_shadowsocks_singbox";
const PROTOCOL_OPENVPN = "openvpn_tcp_singbox";
const PROTOCOL_IPSEC_L2TP = "ipsec_l2tp_singbox";

const vlessProvider = new VlessRealitySingboxProvider();
const vlessPlainProvider = new VlessPlainSingboxProvider();
const shadowsocksProvider = new ShadowsocksSingboxProvider();
const shadowTlsProvider = new ShadowTlsShadowsocksProvider();
const openVpnProvider = new OpenVpnProvider();
const ipsecL2tpProvider = new IpsecL2tpProvider();

export function getActiveProtocol(): string {
  const raw = process.env.OMNIRELAY_ACTIVE_PROTOCOL?.trim().toLowerCase();
  if (raw === PROTOCOL_SHADOWTLS) return PROTOCOL_SHADOWTLS;
  if (raw === PROTOCOL_VLESS_PLAIN) return PROTOCOL_VLESS_PLAIN;
  if (raw === PROTOCOL_SHADOWSOCKS) return PROTOCOL_SHADOWSOCKS;
  if (raw === PROTOCOL_OPENVPN) return PROTOCOL_OPENVPN;
  if (raw === PROTOCOL_IPSEC_L2TP) return PROTOCOL_IPSEC_L2TP;
  return PROTOCOL_VLESS_REALITY;
}

export function getGatewayProvider(): GatewayProtocolProvider {
  const active = getActiveProtocol();
  if (active === PROTOCOL_SHADOWTLS) return shadowTlsProvider;
  if (active === PROTOCOL_VLESS_PLAIN) return vlessPlainProvider;
  if (active === PROTOCOL_SHADOWSOCKS) return shadowsocksProvider;
  if (active === PROTOCOL_OPENVPN) return openVpnProvider;
  if (active === PROTOCOL_IPSEC_L2TP) return ipsecL2tpProvider;
  return vlessProvider;
}
