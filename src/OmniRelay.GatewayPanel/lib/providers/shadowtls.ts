import { randomBytes, randomUUID } from "node:crypto";
import QRCode from "qrcode";
import { type OmniSession } from "@/lib/session";
import {
  type ClientConfigPayload,
  type GatewayClientCreateOptions,
  type GatewayClientRecord,
  type GatewayInboundSnapshot,
  type GatewayProtocolProvider,
  type ProtocolBackupInput,
  type ProtocolBackupPayload,
  resolveGatewayHost
} from "@/lib/providers/types";
import {
  SINGBOX_PER_CLIENT_CAPABILITIES,
  normalizeClientOptions,
  normalizeExpiryTime,
  normalizeSpeedLimitKbps,
  normalizeTotalGB,
  readRuntimeStatsByClientIds,
  runGatewaySync,
  listProtocolClientsFromDb,
  upsertProtocolClientToDb,
  deleteProtocolClientFromDb
} from "@/lib/providers/singbox-shared";
import { createJsonClientBackup, readJsonClientBackup } from "@/lib/providers/backup";
import { resolveProtocolConfigPort, resolveProtocolConfigString } from "@/lib/protocol-config";

interface ShadowTlsClientRecord extends GatewayClientRecord {
  ssPassword: string;
  shadowTlsPassword: string;
  totalGB: number;
  expiryTime: number;
  speedLimitKbps: number;
}

async function getCamouflageServer(): Promise<string> {
  return resolveProtocolConfigString("shadowtls_v3_shadowsocks_singbox", "camouflageServer", process.env.SHADOWTLS_CAMOUFLAGE_SERVER, "www.apple.com:443");
}

async function getPublicPort(): Promise<number> {
  return resolveProtocolConfigPort(
    "shadowtls_v3_shadowsocks_singbox",
    "publicPort",
    process.env.SHADOWTLS_PUBLIC_PORT ?? process.env.SINGBOX_PUBLIC_PORT,
    443
  );
}

function randomToken(length: number): string {
  return randomBytes(length).toString("base64").replace(/[^a-zA-Z0-9]/g, "").slice(0, length);
}

async function readClients(): Promise<ShadowTlsClientRecord[]> {
  const rows = await listProtocolClientsFromDb("shadowtls_v3_shadowsocks_singbox");
  return rows
    .map((item) => {
      const [ssPassword, shadowTlsPassword] = String(item.authSecret ?? "").split(":", 2);
      return {
        id: item.id,
        email: item.email,
        enable: item.enable,
        ssPassword: ssPassword ?? "",
        shadowTlsPassword: shadowTlsPassword ?? "",
        totalGB: item.totalGB,
        expiryTime: item.expiryTime,
        speedLimitKbps: item.speedLimitKbps
      };
    })
    .filter((item) => item.id && item.email && item.ssPassword && item.shadowTlsPassword);
}

async function writeClients(clients: ShadowTlsClientRecord[]): Promise<void> {
  const existing = await listProtocolClientsFromDb("shadowtls_v3_shadowsocks_singbox");
  for (const row of existing) {
    await deleteProtocolClientFromDb(row.id);
  }
  for (const client of clients) {
    await upsertProtocolClientToDb("shadowtls_v3_shadowsocks_singbox", {
      id: client.id,
      email: client.email,
      enable: client.enable,
      totalGB: client.totalGB,
      expiryTime: client.expiryTime,
      speedLimitKbps: client.speedLimitKbps,
      authUsername: "",
      authSecret: `${client.ssPassword}:${client.shadowTlsPassword}`
    });
  }
}

function normalizeImportedClients(input: unknown[]): ShadowTlsClientRecord[] {
  return input.map((item) => {
    if (typeof item !== "object" || item === null) {
      throw new Error("ShadowTLS backup contains an invalid client record.");
    }

    const record = item as Record<string, unknown>;
    const id = String(record.id ?? "").trim();
    const email = String(record.email ?? "").trim();
    const ssPassword = String(record.ssPassword ?? "").trim();
    const shadowTlsPassword = String(record.shadowTlsPassword ?? "").trim();
    if (!id || !email || !ssPassword || !shadowTlsPassword) {
      throw new Error("ShadowTLS import requires id, email, ssPassword, and shadowTlsPassword for every client.");
    }

    return {
      ...record,
      id,
      email,
      enable: Boolean(record.enable ?? true),
      ssPassword,
      shadowTlsPassword,
      totalGB: normalizeTotalGB(record.totalGB),
      expiryTime: normalizeExpiryTime(record.expiryTime),
      speedLimitKbps: normalizeSpeedLimitKbps(record.speedLimitKbps)
    };
  });
}

export class ShadowTlsShadowsocksProvider implements GatewayProtocolProvider {
  public readonly protocolId = "shadowtls_v3_shadowsocks_singbox";

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await readClients();
    const runtimeStats = await readRuntimeStatsByClientIds(clients.map((item) => item.id));
    return {
      inbound: {
        id: 1,
        protocol: this.protocolId,
        port: await getPublicPort(),
        remark: "OmniRelay Managed ShadowTLS v3 + Shadowsocks",
        enable: true
      },
      clients: clients.map((item) => ({
        id: item.id,
        email: item.email,
        enable: item.enable,
        totalGB: item.totalGB,
        expiryTime: item.expiryTime,
        speedLimitKbps: item.speedLimitKbps,
        usedBytes: runtimeStats.get(item.id)?.usedBytes ?? 0,
        lastSeenAtUnixMs: runtimeStats.get(item.id)?.lastSeenAtUnixMs ?? 0,
        activeConnections: runtimeStats.get(item.id)?.activeConnections ?? 0,
        isOnline: runtimeStats.get(item.id)?.isOnline ?? false
      })),
      capabilities: SINGBOX_PER_CLIENT_CAPABILITIES
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
      expiryTime: normalized.expiryTime,
      speedLimitKbps: normalized.speedLimitKbps
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
      expiryTime: Number(client.expiryTime ?? clients[index].expiryTime) || 0,
      speedLimitKbps: Number(client.speedLimitKbps ?? clients[index].speedLimitKbps) || 0
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

    const server = await resolveProtocolConfigString(
      "shadowtls_v3_shadowsocks_singbox",
      "publicHost",
      process.env.PANEL_PUBLIC_HOST,
      resolveGatewayHost(request)
    );
    const publicPort = await getPublicPort();
    const camouflage = await getCamouflageServer();
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

  public async exportBackup(_session: OmniSession): Promise<ProtocolBackupPayload> {
    const clients = await readClients();
    return createJsonClientBackup(this.protocolId, clients);
  }

  public async importBackup(_session: OmniSession, input: ProtocolBackupInput): Promise<void> {
    const clients = normalizeImportedClients(readJsonClientBackup(input, this.protocolId));
    await writeClients(clients);
    await runGatewaySync();
  }
}
