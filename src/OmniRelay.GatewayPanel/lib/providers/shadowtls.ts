import { promises as fs } from "node:fs";
import { dirname } from "node:path";
import { randomBytes, randomUUID } from "node:crypto";
import QRCode from "qrcode";
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
  readUsageByClientIds,
  runGatewaySync
} from "@/lib/providers/singbox-shared";

interface ShadowTlsClientRecord extends GatewayClientRecord {
  ssPassword: string;
  shadowTlsPassword: string;
  totalGB: number;
  expiryTime: number;
}

function getClientsFilePath(): string {
  return process.env.SHADOWTLS_CLIENTS_FILE?.trim() || "/opt/omnirelay/omni-gateway/shadowtls_clients.json";
}

function getCamouflageServer(): string {
  return process.env.SHADOWTLS_CAMOUFLAGE_SERVER?.trim() || "www.apple.com:443";
}

function getPublicPort(): number {
  const parsed = Number.parseInt((process.env.SHADOWTLS_PUBLIC_PORT ?? process.env.SINGBOX_PUBLIC_PORT ?? "443").trim(), 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 443;
}

function randomToken(length: number): string {
  return randomBytes(length).toString("base64").replace(/[^a-zA-Z0-9]/g, "").slice(0, length);
}

async function readClients(): Promise<ShadowTlsClientRecord[]> {
  const filePath = getClientsFilePath();
  try {
    const raw = await fs.readFile(filePath, "utf8");
    const payload = JSON.parse(raw) as unknown;
    if (!Array.isArray(payload)) {
      return [];
    }
    return payload
      .map((item) => ({
        id: String((item as Record<string, unknown>).id ?? ""),
        email: String((item as Record<string, unknown>).email ?? ""),
        enable: Boolean((item as Record<string, unknown>).enable ?? true),
        ssPassword: String((item as Record<string, unknown>).ssPassword ?? ""),
        shadowTlsPassword: String((item as Record<string, unknown>).shadowTlsPassword ?? ""),
        totalGB: Number((item as Record<string, unknown>).totalGB ?? 0) || 0,
        expiryTime: Number((item as Record<string, unknown>).expiryTime ?? 0) || 0
      }))
      .filter((item) => item.id && item.email && item.ssPassword && item.shadowTlsPassword);
  } catch {
    return [];
  }
}

async function writeClients(clients: ShadowTlsClientRecord[]): Promise<void> {
  const filePath = getClientsFilePath();
  await fs.mkdir(dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.tmp`;
  const sorted = [...clients].sort((left, right) => left.email.localeCompare(right.email));
  await fs.writeFile(tempPath, `${JSON.stringify(sorted, null, 2)}\n`, { encoding: "utf8", mode: 0o640 });
  await fs.rename(tempPath, filePath);
}

export class ShadowTlsShadowsocksProvider implements GatewayProtocolProvider {
  public readonly protocolId = "shadowtls_v3_shadowsocks_singbox";

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await readClients();
    const usage = await readUsageByClientIds(clients.map((item) => item.id));
    return {
      inbound: {
        id: 1,
        protocol: this.protocolId,
        port: getPublicPort(),
        remark: "OmniRelay Managed ShadowTLS v3 + Shadowsocks",
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
      throw new Error("Client email is required.");
    }

    const normalized = normalizeClientOptions(options);
    const clients = await readClients();
    const client: ShadowTlsClientRecord = {
      id: randomUUID(),
      email: normalizedEmail,
      enable: true,
      ssPassword: randomToken(24),
      shadowTlsPassword: randomToken(32),
      totalGB: normalized.totalGB,
      expiryTime: normalized.expiryTime
    };

    clients.push(client);
    await writeClients(clients);
    await runGatewaySync();
    return { ...client, usedBytes: 0 };
  }

  public async updateClient(_session: OmniSession, client: GatewayClientRecord): Promise<void> {
    const clientId = String(client.id ?? "").trim();
    if (!clientId) {
      throw new Error("Client payload is required.");
    }

    const clients = await readClients();
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

    await writeClients(clients);
    await runGatewaySync();
  }

  public async deleteClient(_session: OmniSession, clientId: string): Promise<void> {
    const trimmed = String(clientId ?? "").trim();
    if (!trimmed) {
      throw new Error("Client id is required.");
    }

    const clients = await readClients();
    const filtered = clients.filter((item) => item.id !== trimmed);
    if (filtered.length === clients.length) {
      throw new Error("Client not found.");
    }

    await writeClients(filtered);
    await runGatewaySync();
  }

  public async buildClientConfig(_session: OmniSession, request: Request, clientId: string): Promise<ClientConfigPayload> {
    const clients = await readClients();
    const client = clients.find((item) => item.id === clientId);
    if (!client) {
      throw new Error("Client not found.");
    }

    const server = resolveGatewayHost(request);
    const publicPort = getPublicPort();
    const camouflage = getCamouflageServer();
    const camouflageHost = camouflage.includes(":") ? camouflage.slice(0, camouflage.lastIndexOf(":")) : camouflage;

    const config = {
      log: { level: "warn" },
      inbounds: [{ type: "socks", tag: "socks-in", listen: "127.0.0.1", listen_port: 10808 }],
      outbounds: [
        {
          type: "shadowsocks",
          tag: "proxy",
          method: "2022-blake3-aes-128-gcm",
          password: client.ssPassword,
          server,
          server_port: publicPort,
          detour: "shadowtls"
        },
        {
          type: "shadowtls",
          tag: "shadowtls",
          server,
          server_port: publicPort,
          version: 3,
          password: client.shadowTlsPassword,
          tls: { enabled: true, server_name: camouflageHost }
        },
        { type: "direct", tag: "direct" }
      ],
      route: { final: "proxy" }
    };

    const uri = JSON.stringify(config, null, 2);
    const qrCodeDataUrl = await QRCode.toDataURL(uri, { width: 320, margin: 1 });
    return { mode: "qr", uri, qrCodeDataUrl };
  }
}