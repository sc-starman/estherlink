import { type GatewayProtocolCapabilities } from "@/lib/providers/types";

export type ProtocolAuthModel = "per_client" | "shared_credential";

export interface ProtocolCapabilityDescriptor extends GatewayProtocolCapabilities {
  authModel: ProtocolAuthModel;
  supportsClientLifecycle: boolean;
}

const PER_CLIENT_CAPABILITIES: ProtocolCapabilityDescriptor = {
  authModel: "per_client",
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true,
  supportsSpeedLimit: true,
  supportsOnlineStatus: true,
  supportsClientLifecycle: true
};

const SHARED_CREDENTIAL_CAPABILITIES: ProtocolCapabilityDescriptor = {
  authModel: "shared_credential",
  supportsTrafficLimit: false,
  supportsDurationLimit: false,
  supportsUsageAccounting: false,
  supportsSpeedLimit: false,
  supportsOnlineStatus: false,
  supportsClientLifecycle: false
};

const CAPABILITY_BY_PROTOCOL: Record<string, ProtocolCapabilityDescriptor> = {
  vless_tls_singbox: PER_CLIENT_CAPABILITIES,
  shadowsocks_singbox: PER_CLIENT_CAPABILITIES,
  shadowtls_v3_shadowsocks_singbox: PER_CLIENT_CAPABILITIES,
  trojan_singbox: PER_CLIENT_CAPABILITIES,
  openvpn_tcp_singbox: {
    ...PER_CLIENT_CAPABILITIES,
    authModel: "per_client"
  },
  ipsec_l2tp_singbox: {
    ...PER_CLIENT_CAPABILITIES,
    authModel: "per_client"
  },
  mixed_singbox: SHARED_CREDENTIAL_CAPABILITIES,
  socks_singbox: SHARED_CREDENTIAL_CAPABILITIES,
  http_singbox: SHARED_CREDENTIAL_CAPABILITIES,
  hysteria2_singbox: SHARED_CREDENTIAL_CAPABILITIES,
  naive_singbox: SHARED_CREDENTIAL_CAPABILITIES
};

export function getProtocolCapabilities(protocolId: string): ProtocolCapabilityDescriptor {
  const key = String(protocolId ?? "").trim().toLowerCase();
  return CAPABILITY_BY_PROTOCOL[key] ?? PER_CLIENT_CAPABILITIES;
}
