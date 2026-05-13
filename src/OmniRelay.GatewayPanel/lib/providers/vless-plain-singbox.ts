import QRCode from "qrcode";
import { randomUUID } from "node:crypto";
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
  normalizeImportedClientFile,
  readRuntimeStatsByClientIds,
  normalizeSpeedLimitKbps,
  runGatewaySync,
  listProtocolClientsFromDb,
  upsertProtocolClientToDb,
  deleteProtocolClientFromDb
} from "@/lib/providers/singbox-shared";
import { createJsonClientBackup, readJsonClientBackup } from "@/lib/providers/backup";
import { resolveProtocolConfigPort, resolveProtocolConfigString } from "@/lib/protocol-config";

async function getPublicPort(): Promise<number> {
  return resolveProtocolConfigPort("vless_tls_singbox", "publicPort", process.env.SINGBOX_PUBLIC_PORT, 443);
}

export class VlessPlainSingboxProvider implements GatewayProtocolProvider {
  public readonly protocolId = "vless_tls_singbox";

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await listProtocolClientsFromDb(this.protocolId);
    const runtimeStats = await readRuntimeStatsByClientIds(clients.map((item) => item.id));
    return {
      inbound: {
        id: 1,
        protocol: "vless",
        port: await getPublicPort(),
        remark: "OmniRelay Managed VLESS Plain",
        enable: true
      },
      clients: clients.map((item) => ({
        id: item.id,
        email: item.email,
        enable: item.enable,
        totalGB: item.totalGB,
        expiryTime: item.expiryTime,
        speedLimitKbps: item.speedLimitKbps ?? 0,
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
      throw new Error("Email is required.");
    }
    const normalized = normalizeClientOptions(options);
    const client = {
      id: randomUUID(),
      email: normalizedEmail,
      enable: true,
      totalGB: normalized.totalGB,
      expiryTime: normalized.expiryTime,
      speedLimitKbps: normalized.speedLimitKbps
    };
    await upsertProtocolClientToDb(this.protocolId, {
      ...client,
      authUsername: "",
      authSecret: ""
    });
    await runGatewaySync();
    return { ...client, usedBytes: 0, lastSeenAtUnixMs: 0, activeConnections: 0, isOnline: false };
  }

  public async updateClient(_session: OmniSession, client: GatewayClientRecord): Promise<void> {
    const clientId = String(client.id ?? "").trim();
    if (!clientId) {
      throw new Error("Client id is required.");
    }
    const clients = await listProtocolClientsFromDb(this.protocolId);
    const existing = clients.find((item) => item.id === clientId);
    if (!existing) {
      throw new Error("Client not found.");
    }
    await upsertProtocolClientToDb(this.protocolId, {
      ...existing,
      email: String(client.email ?? existing.email).trim() || existing.email,
      enable: Boolean(client.enable),
      totalGB: Number(client.totalGB ?? existing.totalGB) || 0,
      expiryTime: Number(client.expiryTime ?? existing.expiryTime) || 0,
      speedLimitKbps: normalizeSpeedLimitKbps(client.speedLimitKbps ?? existing.speedLimitKbps ?? 0)
    });
    await runGatewaySync();
  }

  public async deleteClient(_session: OmniSession, clientId: string): Promise<void> {
    const trimmed = String(clientId ?? "").trim();
    if (!trimmed) {
      throw new Error("Client id is required.");
    }
    const clients = await listProtocolClientsFromDb(this.protocolId);
    if (!clients.some((item) => item.id === trimmed)) {
      throw new Error("Client not found.");
    }
    await deleteProtocolClientFromDb(trimmed);
    await runGatewaySync();
  }

  public async buildClientConfig(_session: OmniSession, request: Request, clientId: string): Promise<ClientConfigPayload> {
    const clients = await listProtocolClientsFromDb(this.protocolId);
    const client = clients.find((item) => item.id === clientId);
    if (!client) {
      throw new Error("Client not found.");
    }

    const host = await resolveProtocolConfigString("vless_tls_singbox", "publicHost", process.env.PANEL_PUBLIC_HOST, resolveGatewayHost(request));
    const port = await getPublicPort();
    const query = new URLSearchParams({ type: "tcp", security: "tls", encryption: "none" });
    const uri = `vless://${client.id}@${host}:${port}?${query.toString()}#${encodeURIComponent(client.email)}`;
    const qrCodeDataUrl = await QRCode.toDataURL(uri, { width: 320, margin: 1 });
    return { mode: "qr", uri, qrCodeDataUrl };
  }

  public async exportBackup(_session: OmniSession): Promise<ProtocolBackupPayload> {
    const clients = await listProtocolClientsFromDb(this.protocolId);
    return createJsonClientBackup(this.protocolId, clients);
  }

  public async importBackup(_session: OmniSession, input: ProtocolBackupInput): Promise<void> {
    const clients = normalizeImportedClientFile(readJsonClientBackup(input, this.protocolId));
    for (const client of clients) {
      await upsertProtocolClientToDb(this.protocolId, {
        id: client.id,
        email: client.email,
        enable: client.enable,
        totalGB: client.totalGB,
        expiryTime: client.expiryTime,
        speedLimitKbps: normalizeSpeedLimitKbps(client.speedLimitKbps),
        authUsername: "",
        authSecret: String(client.password ?? "")
      });
    }
    await runGatewaySync();
  }
}
