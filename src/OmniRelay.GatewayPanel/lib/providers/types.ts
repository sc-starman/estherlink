import { type OmniSession } from "@/lib/session";

export interface GatewayClientRecord {
  id: string;
  email: string;
  enable: boolean;
  flow?: string;
  [key: string]: unknown;
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
}

export interface ClientConfigPayload {
  uri: string;
  qrCodeDataUrl: string;
}

export interface GatewayProtocolProvider {
  readonly protocolId: string;
  getInbound(session: OmniSession): Promise<GatewayInboundSnapshot>;
  addClient(session: OmniSession, email: string): Promise<GatewayClientRecord>;
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
