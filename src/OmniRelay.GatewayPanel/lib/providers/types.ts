import { type OmniSession } from "@/lib/session";

export interface GatewayClientRecord {
  id: string;
  email: string;
  enable: boolean;
  flow?: string;
  totalGB?: number;
  expiryTime?: number;
  usedBytes?: number | null;
  [key: string]: unknown;
}

export interface GatewayProtocolCapabilities {
  supportsTrafficLimit: boolean;
  supportsDurationLimit: boolean;
  supportsUsageAccounting: boolean;
}

export interface GatewayInboundSnapshot {
  inbound: {
    id: number | string;
    protocol: string;
    port: number;
    remark: string;
    enable: boolean;
  };
  clients: GatewayClientRecord[];
  streamSettings?: Record<string, unknown>;
  capabilities?: GatewayProtocolCapabilities;
}

export type ClientConfigPayload =
  | {
      mode: "qr";
      uri: string;
      qrCodeDataUrl: string;
      title?: string;
    }
  | {
      mode: "ipsec_manual";
      uri: string;
      title?: string;
      fields: {
        server: string;
        ports: string[];
        username: string;
        password: string;
        preSharedKey: string;
      };
      setupSteps: string[];
    }
  | {
      mode: "openvpn_bundle";
      uri: string;
      title?: string;
      username: string;
      password: string;
      privateKeyPassphrase: string;
      ovpnFileName: string;
      ovpnContent: string;
    };

export interface GatewayClientCreateOptions {
  totalGB?: number;
  expiryTime?: number;
}

export interface GatewayProtocolProvider {
  readonly protocolId: string;
  getInbound(session: OmniSession): Promise<GatewayInboundSnapshot>;
  addClient(session: OmniSession, email: string, options?: GatewayClientCreateOptions): Promise<GatewayClientRecord>;
  updateClient(session: OmniSession, client: GatewayClientRecord): Promise<void>;
  deleteClient(session: OmniSession, clientId: string): Promise<void>;
  buildClientConfig(session: OmniSession, request: Request, clientId: string): Promise<ClientConfigPayload>;
}

export function safeParseJson<T>(raw: unknown, fallback: T): T {
  if (typeof raw !== "string" || !raw.trim()) {
    return fallback;
  }

  try {
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
}

export function resolveGatewayHost(request: Request): string {
  const configuredHost = process.env.PANEL_PUBLIC_HOST?.trim();
  if (configuredHost) {
    return configuredHost;
  }

  const forwardedHost = request.headers.get("x-forwarded-host")?.split(",")[0]?.trim();
  if (forwardedHost) {
    return forwardedHost.split(":")[0];
  }

  const host = request.headers.get("host") ?? "localhost";
  return host.split(":")[0];
}
