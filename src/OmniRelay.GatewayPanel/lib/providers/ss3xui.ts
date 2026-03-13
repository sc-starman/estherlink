import QRCode from "qrcode";
import { randomBytes, randomUUID } from "node:crypto";
import { type OmniSession } from "@/lib/session";
import { getInboundId, xuiJson } from "@/lib/xui";
import { type ClientConfigPayload, type GatewayClientRecord, type GatewayInboundSnapshot, type GatewayProtocolProvider, resolveGatewayHost, safeParseJson } from "@/lib/providers/types";

const DEFAULT_SS_METHOD = "2022-blake3-aes-128-gcm";

function randomBase64(length: number): string {
  return randomBytes(length).toString("base64");
}

function randomSubId(): string {
  return randomUUID().replace(/-/g, "").slice(0, 16);
}

function toRecord(value: unknown): Record<string, unknown> {
  return (value ?? {}) as Record<string, unknown>;
}

function toSsUriUserInfo(method: string, password: string): string {
  return Buffer.from(`${method}:${password}`)
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/g, "");
}

function generateClientPayload(email: string): GatewayClientRecord {
  return {
    id: randomUUID(),
    email,
    enable: true,
    method: "",
    password: randomBase64(16),
    limitIp: 0,
    totalGB: 0,
    expiryTime: 0,
    tgId: "",
    subId: randomSubId(),
    comment: "",
    reset: 0
  };
}

export class Shadowsocks3xuiProvider implements GatewayProtocolProvider {
  public readonly protocolId = "shadowsocks_3xui";

  public async getInbound(session: OmniSession): Promise<GatewayInboundSnapshot> {
    const inboundId = getInboundId();
    const response = await xuiJson<Record<string, unknown>>(session, `/panel/api/inbounds/get/${encodeURIComponent(inboundId)}`);
    const inbound = response.obj ?? {};
    const settings = safeParseJson<{ clients?: GatewayClientRecord[] }>(inbound.settings, { clients: [] });

    return {
      inbound: {
        id: Number(inbound.id ?? 0),
        protocol: String(inbound.protocol ?? "shadowsocks"),
        port: Number(inbound.port ?? 443),
        remark: String(inbound.remark ?? "OmniRelay Managed Shadowsocks"),
        enable: Boolean(inbound.enable ?? true)
      },
      clients: settings.clients ?? []
    };
  }

  public async addClient(session: OmniSession, email: string): Promise<GatewayClientRecord> {
    const inboundId = getInboundId();
    const normalizedEmail = (email ?? "").trim();
    if (!normalizedEmail) {
      throw new Error("Email is required.");
    }

    const client = generateClientPayload(normalizedEmail);
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

    return {
      id: String(client.id),
      email: String(client.email),
      enable: Boolean(client.enable)
    };
  }

  public async updateClient(session: OmniSession, client: GatewayClientRecord): Promise<void> {
    const inboundId = getInboundId();
    const clientId = String(client.id ?? "").trim();
    if (!clientId) {
      throw new Error("Client id is required.");
    }

    const payload: GatewayClientRecord = {
      ...client,
      method: ""
    };
    const form = new URLSearchParams();
    form.set("id", inboundId);
    form.set("settings", JSON.stringify({ clients: [payload] }));

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
    const inboundRecord = toRecord(inbound);
    const settings = safeParseJson<Record<string, unknown>>(inboundRecord.settings, {});
    const clients = (settings.clients as unknown[] | undefined) ?? [];
    const client = clients
      .map((item) => toRecord(item))
      .find((item) => String(item.id ?? "").trim() === clientId);

    if (!client) {
      throw new Error("Client not found.");
    }

    const email = String(client.email ?? "OmniClient");
    const clientMethod = String(client.method ?? "").trim();
    const settingsMethod = String(settings.method ?? DEFAULT_SS_METHOD).trim();
    const method = clientMethod || settingsMethod || DEFAULT_SS_METHOD;
    const password = String(client.password ?? settings.password ?? "").trim();
    if (!method || !password) {
      throw new Error("Inbound Shadowsocks settings are incomplete.");
    }

    const host = resolveGatewayHost(request);
    const port = Number(inboundRecord.port ?? 443);
    const userInfo = toSsUriUserInfo(method, password);
    const uri = `ss://${userInfo}@${host}:${port}#${encodeURIComponent(email)}`;
    const qrCodeDataUrl = await QRCode.toDataURL(uri, {
      width: 320,
      margin: 1
    });

    return { uri, qrCodeDataUrl };
  }
}
