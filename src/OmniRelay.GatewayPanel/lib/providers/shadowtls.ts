import { promises as fs } from "node:fs";
import { dirname } from "node:path";
import { exec as execCallback } from "node:child_process";
import { promisify } from "node:util";
import { randomBytes, randomUUID } from "node:crypto";
import QRCode from "qrcode";
import { type OmniSession } from "@/lib/session";
import { type ClientConfigPayload, type GatewayClientRecord, type GatewayInboundSnapshot, type GatewayProtocolProvider, resolveGatewayHost } from "@/lib/providers/types";

const exec = promisify(execCallback);
const DEFAULT_RELOAD_COMMAND = "/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients";

interface ShadowTlsClientRecord extends GatewayClientRecord {
  ssPassword: string;
  shadowTlsPassword: string;
}

function randomToken(length: number): string {
  return randomBytes(length)
    .toString("base64")
    .replace(/[^a-zA-Z0-9]/g, "")
    .slice(0, length);
}

function randomBase64(length: number): string {
  return randomBytes(length).toString("base64");
}

function normalizeEmail(value: unknown): string {
  return String(value ?? "").trim();
}

function parsePort(value: string | undefined, fallback: number): number {
  const parsed = Number.parseInt((value ?? "").trim(), 10);
  return Number.isFinite(parsed) && parsed > 0 && parsed <= 65535 ? parsed : fallback;
}

function getClientsFilePath(): string {
  return process.env.SHADOWTLS_CLIENTS_FILE?.trim() || "/etc/omnirelay/gateway/shadowtls_clients.json";
}

function getCamouflageServer(): string {
  return process.env.SHADOWTLS_CAMOUFLAGE_SERVER?.trim() || "www.apple.com:443";
}

function getPublicPort(): number {
  return parsePort(process.env.SHADOWTLS_PUBLIC_PORT, 443);
}

function normalizeSudoCommand(command: string): string {
  const trimmed = command.trim();
  if (!trimmed) {
    return trimmed;
  }

  if (/(^|\s)-n(\s|$)/.test(trimmed)) {
    return trimmed;
  }

  return trimmed.replace(/^(\S*sudo)\s+/, "$1 -n ");
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
        shadowTlsPassword: String((item as Record<string, unknown>).shadowTlsPassword ?? "")
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

async function reloadSingBox(): Promise<void> {
  const command = normalizeSudoCommand(process.env.SINGBOX_RELOAD_COMMAND?.trim() || DEFAULT_RELOAD_COMMAND);
  if (!command) {
    return;
  }

  try {
    await exec(command);
  } catch (error) {
    const failure = error as { message?: string; stdout?: string; stderr?: string };
    const detail = [failure.message, failure.stderr, failure.stdout]
      .map((part) => String(part ?? "").trim())
      .filter((part) => part.length > 0)
      .join("\n");
    const lower = detail.toLowerCase();
    if (lower.includes("a password is required") || lower.includes("a terminal is required") || lower.includes("not allowed to run sudo")) {
      throw new Error(`Gateway sync-clients failed: sudo permission issue for omnipanel user.\n${detail}`);
    }

    throw new Error(detail ? `Gateway sync-clients failed:\n${detail}` : "Gateway sync-clients failed.");
  }
}

export class ShadowTlsShadowsocksProvider implements GatewayProtocolProvider {
  public readonly protocolId = "shadowtls_v3_shadowsocks_singbox";

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await readClients();
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
        enable: item.enable
      }))
    };
  }

  public async addClient(_session: OmniSession, email: string): Promise<GatewayClientRecord> {
    const clients = await readClients();
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedEmail) {
      throw new Error("Client email is required.");
    }

    const client: ShadowTlsClientRecord = {
      id: randomUUID(),
      email: normalizedEmail,
      enable: true,
      ssPassword: randomBase64(16),
      shadowTlsPassword: randomToken(32)
    };

    clients.push(client);
    await writeClients(clients);
    await reloadSingBox();
    return {
      id: client.id,
      email: client.email,
      enable: client.enable
    };
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

    const existing = clients[index];
    clients[index] = {
      ...existing,
      email: normalizeEmail(client.email) || existing.email,
      enable: Boolean(client.enable)
    };

    await writeClients(clients);
    await reloadSingBox();
  }

  public async deleteClient(_session: OmniSession, clientId: string): Promise<void> {
    const trimmed = clientId.trim();
    if (!trimmed) {
      throw new Error("Client id is required.");
    }

    const clients = await readClients();
    const filtered = clients.filter((item) => item.id !== trimmed);
    if (filtered.length == clients.length) {
      throw new Error("Client not found.");
    }

    await writeClients(filtered);
    await reloadSingBox();
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
      log: {
        level: "warn"
      },
      inbounds: [
        {
          type: "socks",
          tag: "socks-in",
          listen: "127.0.0.1",
          listen_port: 10808
        }
      ],
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
          tls: {
            enabled: true,
            server_name: camouflageHost
          }
        },
        {
          type: "direct",
          tag: "direct"
        }
      ],
      route: {
        final: "proxy"
      }
    };

    const uri = JSON.stringify(config, null, 2);
    const qrCodeDataUrl = await QRCode.toDataURL(uri, { width: 320, margin: 1 });
    return { uri, qrCodeDataUrl };
  }
}
