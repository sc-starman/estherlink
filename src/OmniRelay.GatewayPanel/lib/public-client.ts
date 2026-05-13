import {
  type GatewayClientRecord,
  type GatewayProtocolCapabilities
} from "@/lib/providers/types";

export interface PublicClientViewModel {
  id: string;
  email: string;
  enable: boolean;
  totalGB?: number;
  expiryTime?: number;
  usedBytes?: number | null;
  speedLimitKbps?: number;
  isOnline?: boolean;
  lastSeenAtUnixMs?: number;
}

export const DEFAULT_PUBLIC_CAPABILITIES: GatewayProtocolCapabilities = {
  supportsTrafficLimit: false,
  supportsDurationLimit: false,
  supportsUsageAccounting: false,
  supportsSpeedLimit: false,
  supportsOnlineStatus: false,
  supportsClientLifecycle: false
};

export function isClientPubliclyAccessible(client: GatewayClientRecord, nowUnixMs: number): boolean {
  if (!client.enable) {
    return false;
  }

  const expiryTime = Number(client.expiryTime ?? 0);
  if (Number.isFinite(expiryTime) && expiryTime > 0 && expiryTime <= nowUnixMs) {
    return false;
  }

  return true;
}

export function toPublicClientViewModel(client: GatewayClientRecord): PublicClientViewModel {
  return {
    id: String(client.id ?? ""),
    email: String(client.email ?? ""),
    enable: Boolean(client.enable),
    totalGB: Number.isFinite(Number(client.totalGB)) ? Number(client.totalGB) : undefined,
    expiryTime: Number.isFinite(Number(client.expiryTime)) ? Number(client.expiryTime) : undefined,
    usedBytes: typeof client.usedBytes === "number" && Number.isFinite(client.usedBytes) ? client.usedBytes : null,
    speedLimitKbps: Number.isFinite(Number(client.speedLimitKbps)) ? Number(client.speedLimitKbps) : undefined,
    isOnline: typeof client.isOnline === "boolean" ? client.isOnline : undefined,
    lastSeenAtUnixMs: Number.isFinite(Number(client.lastSeenAtUnixMs)) ? Number(client.lastSeenAtUnixMs) : undefined
  };
}
