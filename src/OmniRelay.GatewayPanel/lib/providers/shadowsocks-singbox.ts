import QRCode from "qrcode";
import { randomBytes, randomUUID } from "node:crypto";
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

const DEFAULT_METHOD = "2022-blake3-aes-128-gcm";

function getClientsFilePath(): string {
  return process.env.SINGBOX_SHADOWSOCKS_CLIENTS_FILE?.trim() || "/opt/omnirelay/omni-gateway/shadowsocks_clients.json";
}

function getPublicPort(): number {
  const parsed = Number.parseInt((process.env.SINGBOX_PUBLIC_PORT ?? "443").trim(), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 443;
}

function getServerPassword(): string {
  return process.env.SINGBOX_SHADOWSOCKS_SERVER_PASSWORD?.trim() ?? "";
}

function random2022Key(): string {
  return randomBytes(16).toString("base64");
}

function toSsUriUserInfo(method: string, password: string): string {
  // Keep base64 padding for better client compatibility with parsers that do not auto-pad base64url.
  return Buffer.from(`${method}:${password}`).toString("base64").replace(/\+/g, "-").replace(/\//g, "_");
}

export class ShadowsocksSingboxProvider implements GatewayProtocolProvider {
  public readonly protocolId = "shadowsocks_singbox";

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await readClientFile(getClientsFilePath());
    const usage = await readUsageByClientIds(clients.map((item) => item.id));
    return {
      inbound: {
        id: 1,
        protocol: "shadowsocks",
        port: getPublicPort(),
        remark: "OmniRelay Managed Shadowsocks",
        enable: true
      },
      clients: clients.map((item) => ({
        id: item.id,
        email: item.email,
        enable: item.enable,
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
      method: DEFAULT_METHOD,
      password: random2022Key(),
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

    const method = String(client.method ?? DEFAULT_METHOD).trim() || DEFAULT_METHOD;
    const userPassword = String(client.password ?? "").trim();
    if (!userPassword) {
      throw new Error("Client password is missing.");
    }
    const serverPassword = getServerPassword();
    const is2022 = method.startsWith("2022-");
    if (is2022 && !serverPassword) {
      throw new Error("Shadowsocks runtime server password is missing.");
    }
    const password = is2022 ? `${serverPassword}:${userPassword}` : userPassword;

    const host = resolveGatewayHost(request);
    const port = getPublicPort();
    const userInfo = toSsUriUserInfo(method, password);
    const uri = `ss://${userInfo}@${host}:${port}#${encodeURIComponent(client.email)}`;
    const qrCodeDataUrl = await QRCode.toDataURL(uri, { width: 320, margin: 1 });
    return { mode: "qr", uri, qrCodeDataUrl };
  }
}
