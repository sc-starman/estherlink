import QRCode from "qrcode";
import { randomUUID } from "node:crypto";
import { type OmniSession } from "@/lib/session";
import {
  type ClientConfigPayload,
  type GatewayClientCreateOptions,
  type GatewayClientRecord,
  type GatewayInboundSnapshot,
  type GatewayProtocolProvider,
  resolveGatewayHost
} from "@/lib/providers/types";
import {
  SINGBOX_ACCOUNTING_CAPABILITIES,
  normalizeClientOptions,
  readClientFile,
  readUsageByClientIds,
  runGatewaySync,
  writeClientFile
} from "@/lib/providers/singbox-shared";

function getClientsFilePath(): string {
  return process.env.SINGBOX_VLESS_REALITY_CLIENTS_FILE?.trim() || "/opt/omnirelay/omni-gateway/vless_reality_clients.json";
}

function getPublicPort(): number {
  const parsed = Number.parseInt((process.env.SINGBOX_PUBLIC_PORT ?? "443").trim(), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 443;
}

export class VlessRealitySingboxProvider implements GatewayProtocolProvider {
  public readonly protocolId = "vless_reality_singbox";

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await readClientFile(getClientsFilePath());
    const usage = await readUsageByClientIds(clients.map((item) => item.id));
    return {
      inbound: {
        id: 1,
        protocol: "vless",
        port: getPublicPort(),
        remark: "OmniRelay Managed VLESS Reality",
        enable: true
      },
      clients: clients.map((item) => ({
        id: item.id,
        email: item.email,
        enable: item.enable,
        flow: "xtls-rprx-vision",
        totalGB: item.totalGB,
        expiryTime: item.expiryTime,
        usedBytes: usage.get(item.id) ?? 0
      })),
      capabilities: SINGBOX_ACCOUNTING_CAPABILITIES
    };
  }

  public async addClient(_session: OmniSession, email: string, options?: GatewayClientCreateOptions): Promise<GatewayClientRecord> {
    const normalizedEmail = String(email ?? "").trim();
    if (!normalizedEmail) {
      throw new Error("Email is required.");
    }

    const normalized = normalizeClientOptions(options);
    const clients = await readClientFile(getClientsFilePath());
    const client = {
      id: randomUUID(),
      email: normalizedEmail,
      enable: true,
      flow: "xtls-rprx-vision",
      totalGB: normalized.totalGB,
      expiryTime: normalized.expiryTime
    };

    clients.push(client);
    await writeClientFile(getClientsFilePath(), clients);
    await runGatewaySync();
    return { ...client, usedBytes: 0 };
  }

  public async updateClient(_session: OmniSession, client: GatewayClientRecord): Promise<void> {
    const clientId = String(client.id ?? "").trim();
    if (!clientId) {
      throw new Error("Client id is required.");
    }

    const clients = await readClientFile(getClientsFilePath());
    const index = clients.findIndex((item) => item.id === clientId);
    if (index < 0) {
      throw new Error("Client not found.");
    }

    clients[index] = {
      ...clients[index],
      email: String(client.email ?? clients[index].email).trim() || clients[index].email,
      enable: Boolean(client.enable),
      flow: "xtls-rprx-vision",
      totalGB: Number(client.totalGB ?? clients[index].totalGB) || 0,
      expiryTime: Number(client.expiryTime ?? clients[index].expiryTime) || 0
    };
    await writeClientFile(getClientsFilePath(), clients);
    await runGatewaySync();
  }

  public async deleteClient(_session: OmniSession, clientId: string): Promise<void> {
    const trimmed = String(clientId ?? "").trim();
    if (!trimmed) {
      throw new Error("Client id is required.");
    }

    const clients = await readClientFile(getClientsFilePath());
    const filtered = clients.filter((item) => item.id !== trimmed);
    if (filtered.length === clients.length) {
      throw new Error("Client not found.");
    }

    await writeClientFile(getClientsFilePath(), filtered);
    await runGatewaySync();
  }

  public async buildClientConfig(_session: OmniSession, request: Request, clientId: string): Promise<ClientConfigPayload> {
    const clients = await readClientFile(getClientsFilePath());
    const client = clients.find((item) => item.id === clientId);
    if (!client) {
      throw new Error("Client not found.");
    }

    const host = resolveGatewayHost(request);
    const port = getPublicPort();
    const sni = process.env.SINGBOX_REALITY_SERVER_NAME?.trim() || process.env.GATEWAY_SNI?.trim() || "www.cloudflare.com";
    const sid = process.env.SINGBOX_REALITY_SHORT_ID?.trim() || "";
    const pbk = process.env.SINGBOX_REALITY_PUBLIC_KEY?.trim() || "";
    if (!sid || !pbk) {
      throw new Error("REALITY runtime metadata is missing.");
    }

    const query = new URLSearchParams({
      type: "tcp",
      security: "reality",
      flow: "xtls-rprx-vision",
      fp: "chrome",
      sni,
      pbk,
      sid,
      spx: "/",
      encryption: "none"
    });

    const uri = `vless://${client.id}@${host}:${port}?${query.toString()}#${encodeURIComponent(client.email)}`;
    const qrCodeDataUrl = await QRCode.toDataURL(uri, { width: 320, margin: 1 });
    return { mode: "qr", uri, qrCodeDataUrl };
  }
}