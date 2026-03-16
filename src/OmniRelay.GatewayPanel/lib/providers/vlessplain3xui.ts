import QRCode from "qrcode";
import { randomUUID } from "node:crypto";
import { type OmniSession } from "@/lib/session";
import { getInboundId, xuiJson } from "@/lib/xui";
import { applyXuiClientAccounting, fetchXuiClientUsage, XUI_ACCOUNTING_CAPABILITIES } from "@/lib/providers/xui-accounting";
import {
  type ClientConfigPayload,
  type GatewayClientCreateOptions,
  type GatewayClientRecord,
  type GatewayInboundSnapshot,
  type GatewayProtocolProvider,
  resolveGatewayHost,
  safeParseJson
} from "@/lib/providers/types";

function normalizeClientOptions(options?: GatewayClientCreateOptions): Required<GatewayClientCreateOptions> {
  const totalGB = Number(options?.totalGB ?? 0);
  const expiryTime = Number(options?.expiryTime ?? 0);
  return {
    totalGB: Number.isFinite(totalGB) && totalGB >= 0 ? totalGB : 0,
    expiryTime: Number.isFinite(expiryTime) && expiryTime >= 0 ? expiryTime : 0
  };
}

function generateClientPayload(email: string, options?: GatewayClientCreateOptions): GatewayClientRecord {
  const normalized = normalizeClientOptions(options);
  return {
    id: randomUUID(),
    email,
    limitIp: 0,
    totalGB: normalized.totalGB,
    expiryTime: normalized.expiryTime,
    enable: true,
    tgId: "",
    subId: randomUUID().replace(/-/g, "").slice(0, 16),
    comment: "",
    reset: 0
  };
}

export class VlessPlain3xuiProvider implements GatewayProtocolProvider {
  public readonly protocolId = "vless_plain_3xui";

  public async getInbound(session: OmniSession): Promise<GatewayInboundSnapshot> {
    const inboundId = getInboundId();
    const response = await xuiJson<Record<string, unknown>>(session, `/panel/api/inbounds/get/${encodeURIComponent(inboundId)}`);
    const inbound = response.obj ?? {};
    const settings = safeParseJson<{ clients?: GatewayClientRecord[] }>(inbound.settings, { clients: [] });
    const streamSettings = safeParseJson<Record<string, unknown>>(inbound.streamSettings, {});
    const usage = await fetchXuiClientUsage(session, inboundId, inbound);

    return {
      inbound: {
        id: Number(inbound.id ?? 0),
        protocol: String(inbound.protocol ?? "vless"),
        port: Number(inbound.port ?? 443),
        remark: String(inbound.remark ?? "OmniRelay Managed VLESS"),
        enable: Boolean(inbound.enable ?? true)
      },
      clients: applyXuiClientAccounting(settings.clients ?? [], usage.byId, usage.byEmail),
      streamSettings,
      capabilities: XUI_ACCOUNTING_CAPABILITIES
    };
  }

  public async addClient(session: OmniSession, email: string, options?: GatewayClientCreateOptions): Promise<GatewayClientRecord> {
    const inboundId = getInboundId();
    const client = generateClientPayload(email, options);
    const form = new URLSearchParams();
    form.set("id", inboundId);
    form.set("settings", JSON.stringify({ clients: [client] }));

    await xuiJson(session, "/panel/api/inbounds/addClient", {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
      },
      body: form
    });

    return client;
  }

  public async updateClient(session: OmniSession, client: GatewayClientRecord): Promise<void> {
    const inboundId = getInboundId();
    const clientId = String(client.id ?? "").trim();
    if (!clientId) {
      throw new Error("Client id is required.");
    }

    const form = new URLSearchParams();
    form.set("id", inboundId);
    form.set("settings", JSON.stringify({ clients: [{ ...client }] }));

    await xuiJson(session, `/panel/api/inbounds/updateClient/${encodeURIComponent(clientId)}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
      },
      body: form
    });
  }

  public async deleteClient(session: OmniSession, clientId: string): Promise<void> {
    const inboundId = getInboundId();
    await xuiJson(session, `/panel/api/inbounds/${encodeURIComponent(inboundId)}/delClient/${encodeURIComponent(clientId)}`, {
      method: "POST"
    });
  }

  public async buildClientConfig(session: OmniSession, request: Request, clientId: string): Promise<ClientConfigPayload> {
    const inboundId = getInboundId();
    const response = await xuiJson<Record<string, unknown>>(session, `/panel/api/inbounds/get/${encodeURIComponent(inboundId)}`);
    const inbound = response.obj ?? {};

    const settings = safeParseJson<{ clients?: Array<Record<string, unknown>> }>(inbound.settings, { clients: [] });
    const client = (settings.clients ?? []).find((item) => item.id === clientId);
    if (!client) {
      throw new Error("Client not found.");
    }

    const host = resolveGatewayHost(request);
    const port = Number(inbound.port ?? 443);
    const email = String(client.email ?? "OmniClient");
    const query = new URLSearchParams({
      type: "tcp",
      security: "none",
      encryption: "none"
    });

    const uri = `vless://${clientId}@${host}:${port}?${query.toString()}#${encodeURIComponent(email)}`;
    const qrCodeDataUrl = await QRCode.toDataURL(uri, {
      width: 320,
      margin: 1
    });

    return { mode: "qr", uri, qrCodeDataUrl };
  }
}
