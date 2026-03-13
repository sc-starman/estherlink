import { type GatewayProtocolProvider } from "@/lib/providers/types";
import { VlessReality3xuiProvider } from "@/lib/providers/vless3xui";
import { VlessPlain3xuiProvider } from "@/lib/providers/vlessplain3xui";
import { Shadowsocks3xuiProvider } from "@/lib/providers/ss3xui";
import { ShadowTlsShadowsocksProvider } from "@/lib/providers/shadowtls";
import { OpenVpnProvider } from "@/lib/providers/openvpn";
import { IpsecL2tpProvider } from "@/lib/providers/ipsec-l2tp";

const PROTOCOL_VLESS = "vless_reality_3xui";
const PROTOCOL_VLESS_PLAIN = "vless_plain_3xui";
const PROTOCOL_SHADOWSOCKS_3XUI = "shadowsocks_3xui";
const PROTOCOL_SHADOWTLS = "shadowtls_v3_shadowsocks_singbox";
const PROTOCOL_OPENVPN = "openvpn_tcp_relay";
const PROTOCOL_IPSEC_L2TP = "ipsec_l2tp_hwdsl2";

const vlessProvider = new VlessReality3xuiProvider();
const vlessPlainProvider = new VlessPlain3xuiProvider();
const shadowsocks3xuiProvider = new Shadowsocks3xuiProvider();
const shadowTlsProvider = new ShadowTlsShadowsocksProvider();
const openVpnProvider = new OpenVpnProvider();
const ipsecL2tpProvider = new IpsecL2tpProvider();

export function getActiveProtocol(): string {
  const raw = process.env.OMNIRELAY_ACTIVE_PROTOCOL?.trim().toLowerCase();
  if (raw === PROTOCOL_SHADOWTLS) {
    return PROTOCOL_SHADOWTLS;
  }
  if (raw === PROTOCOL_VLESS_PLAIN) {
    return PROTOCOL_VLESS_PLAIN;
  }
  if (raw === PROTOCOL_SHADOWSOCKS_3XUI) {
    return PROTOCOL_SHADOWSOCKS_3XUI;
  }
  if (raw === PROTOCOL_OPENVPN) {
    return PROTOCOL_OPENVPN;
  }
  if (raw === PROTOCOL_IPSEC_L2TP) {
    return PROTOCOL_IPSEC_L2TP;
  }

  return PROTOCOL_VLESS;
}

export function getGatewayProvider(): GatewayProtocolProvider {
  const active = getActiveProtocol();
  if (active === PROTOCOL_SHADOWTLS) {
    return shadowTlsProvider;
  }
  if (active === PROTOCOL_VLESS_PLAIN) {
    return vlessPlainProvider;
  }
  if (active === PROTOCOL_SHADOWSOCKS_3XUI) {
    return shadowsocks3xuiProvider;
  }
  if (active === PROTOCOL_OPENVPN) {
    return openVpnProvider;
  }
  if (active === PROTOCOL_IPSEC_L2TP) {
    return ipsecL2tpProvider;
  }
  return vlessProvider;
}
