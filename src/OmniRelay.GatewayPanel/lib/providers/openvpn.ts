import { promises as fs } from "node:fs";
import { dirname, join } from "node:path";
import { exec as execCallback } from "node:child_process";
import { promisify } from "node:util";
import { randomBytes, randomUUID } from "node:crypto";
import { type OmniSession } from "@/lib/session";
import {
  type ClientConfigPayload,
  type GatewayClientCreateOptions,
  type GatewayClientRecord,
  type GatewayInboundSnapshot,
  type GatewayProtocolProvider
} from "@/lib/providers/types";

const OPENVPN_ACCOUNTING_CAPABILITIES = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true
} as const;

const exec = promisify(execCallback);
const DEFAULT_SYNC_COMMAND = "/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients";
const DEFAULT_ACCOUNTING_DB = "/etc/omnirelay/gateway/openvpn/accounting.db";

interface OpenVpnClientRecord extends GatewayClientRecord {
  username: string;
  password: string;
  totalGB: number;
  expiryTime: number;
}

interface OpenVpnAccountingSource {
  getCapabilities(): typeof OPENVPN_ACCOUNTING_CAPABILITIES;
  getUsageByClientId(clientIds: string[]): Promise<Map<string, number>>;
}

class LocalSqliteOpenVpnAccountingSource implements OpenVpnAccountingSource {
  public getCapabilities(): typeof OPENVPN_ACCOUNTING_CAPABILITIES {
    return OPENVPN_ACCOUNTING_CAPABILITIES;
  }

  public async getUsageByClientId(clientIds: string[]): Promise<Map<string, number>> {
    if (clientIds.length === 0) {
      return new Map();
    }

    const dbPath = getAccountingDbPath();
    try {
      await fs.access(dbPath);
    } catch {
      return new Map();
    }

    const quotedIds = clientIds
      .map((id) => `'${escapeSqlLiteral(id)}'`)
      .join(",");

    if (!quotedIds) {
      return new Map();
    }

    try {
      const { stdout } = await exec(
        `sqlite3 -csv -noheader "${dbPath}" "SELECT client_id, used_bytes FROM usage_totals WHERE client_id IN (${quotedIds});"`
      );
      const usageMap = new Map<string, number>();
      for (const line of stdout.split(/\r?\n/)) {
        const trimmed = line.trim();
        if (!trimmed) {
          continue;
        }

        const commaIndex = trimmed.indexOf(",");
        if (commaIndex <= 0) {
          continue;
        }

        const clientId = trimmed.slice(0, commaIndex);
        const usedRaw = trimmed.slice(commaIndex + 1);
        const usedBytes = Number.parseInt(usedRaw, 10);
        if (!Number.isFinite(usedBytes) || usedBytes < 0) {
          continue;
        }

        usageMap.set(clientId, usedBytes);
      }

      return usageMap;
    } catch {
      return new Map();
    }
  }
}

function parsePort(value: string | undefined, fallback: number): number {
  const parsed = Number.parseInt((value ?? "").trim(), 10);
  return Number.isFinite(parsed) && parsed > 0 && parsed <= 65535 ? parsed : fallback;
}

function getClientsFilePath(): string {
  return process.env.OPENVPN_CLIENTS_FILE?.trim() || "/opt/omnirelay/omni-gateway/openvpn_clients.json";
}

function getExportsDir(): string {
  return process.env.OPENVPN_EXPORT_DIR?.trim() || "/opt/omnirelay/omni-gateway/openvpn-exports";
}

function getPublicPort(): number {
  return parsePort(process.env.OPENVPN_PUBLIC_PORT, 443);
}

function getAccountingDbPath(): string {
  return process.env.OPENVPN_ACCOUNTING_DB?.trim() || DEFAULT_ACCOUNTING_DB;
}

function escapeSqlLiteral(value: string): string {
  return value.replace(/'/g, "''");
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

function randomAlphaNum(length: number): string {
  return randomBytes(length)
    .toString("base64")
    .replace(/[^a-zA-Z0-9]/g, "")
    .slice(0, length);
}

function toSafeFileStem(value: string): string {
  const safe = value
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 48);
  return safe || "openvpn-client";
}

function normalizeTotalGB(value: unknown): number {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric < 0) {
    return 0;
  }

  return numeric;
}

function normalizeExpiryTime(value: unknown): number {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric < 0) {
    return 0;
  }

  return Math.trunc(numeric);
}

function toSafeUsername(seed: string): string {
  const normalized = seed
    .toLowerCase()
    .replace(/[^a-z0-9]/g, "")
    .slice(0, 18);
  return normalized || "client";
}

function makeUsername(email: string, existing: Set<string>): string {
  const base = `ovpn_${toSafeUsername(email)}`;
  if (!existing.has(base)) {
    return base;
  }

  for (let i = 0; i < 100; i += 1) {
    const candidate = `${base}${randomAlphaNum(4).toLowerCase()}`;
    if (!existing.has(candidate)) {
      return candidate;
    }
  }

  return `ovpn_${randomAlphaNum(10).toLowerCase()}`;
}

async function readClients(): Promise<OpenVpnClientRecord[]> {
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
        username: String((item as Record<string, unknown>).username ?? ""),
        password: String((item as Record<string, unknown>).password ?? ""),
        totalGB: normalizeTotalGB((item as Record<string, unknown>).totalGB),
        expiryTime: normalizeExpiryTime((item as Record<string, unknown>).expiryTime)
      }))
      .filter((item) => item.id && item.email && item.username && item.password);
  } catch {
    return [];
  }
}

async function writeClients(clients: OpenVpnClientRecord[]): Promise<void> {
  const filePath = getClientsFilePath();
  await fs.mkdir(dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.tmp`;
  const sorted = [...clients].sort((left, right) => left.email.localeCompare(right.email));
  await fs.writeFile(tempPath, `${JSON.stringify(sorted, null, 2)}\n`, { encoding: "utf8", mode: 0o640 });
  await fs.rename(tempPath, filePath);
}

async function syncOpenVpn(): Promise<void> {
  const command = normalizeSudoCommand(process.env.OPENVPN_SYNC_COMMAND?.trim() || DEFAULT_SYNC_COMMAND);
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
    if (
      lower.includes("a password is required") ||
      lower.includes("a terminal is required") ||
      lower.includes("not allowed to run sudo")
    ) {
      throw new Error(`Gateway sync-clients failed: sudo permission issue for omnipanel user.\n${detail}`);
    }

    throw new Error(detail ? `Gateway sync-clients failed:\n${detail}` : "Gateway sync-clients failed.");
  }
}

export class OpenVpnProvider implements GatewayProtocolProvider {
  public readonly protocolId = "openvpn_tcp_relay";
  private readonly accountingSource: OpenVpnAccountingSource;

  public constructor(accountingSource: OpenVpnAccountingSource = new LocalSqliteOpenVpnAccountingSource()) {
    this.accountingSource = accountingSource;
  }

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await readClients();
    const usageByClientId = await this.accountingSource.getUsageByClientId(clients.map((item) => item.id));
    const capabilities = this.accountingSource.getCapabilities();
    return {
      inbound: {
        id: 1,
        protocol: this.protocolId,
        port: getPublicPort(),
        remark: "OmniRelay Managed OpenVPN (TCP)",
        enable: true
      },
      clients: clients.map((item) => ({
        id: item.id,
        email: item.email,
        enable: item.enable,
        totalGB: item.totalGB,
        expiryTime: item.expiryTime,
        usedBytes: usageByClientId.get(item.id) ?? null
      })),
      capabilities
    };
  }

  public async addClient(_session: OmniSession, email: string, options?: GatewayClientCreateOptions): Promise<GatewayClientRecord> {
    const normalizedEmail = String(email ?? "").trim();
    if (!normalizedEmail) {
      throw new Error("Client email is required.");
    }

    const clients = await readClients();
    const usernames = new Set(clients.map((item) => item.username));
    const client: OpenVpnClientRecord = {
      id: randomUUID(),
      email: normalizedEmail,
      enable: true,
      username: makeUsername(normalizedEmail, usernames),
      password: randomAlphaNum(24),
      totalGB: normalizeTotalGB(options?.totalGB),
      expiryTime: normalizeExpiryTime(options?.expiryTime)
    };

    clients.push(client);
    await writeClients(clients);
    await syncOpenVpn();
    return {
      id: client.id,
      email: client.email,
      enable: client.enable,
      totalGB: client.totalGB,
      expiryTime: client.expiryTime,
      usedBytes: null
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

    clients[index] = {
      ...clients[index],
      email: String(client.email ?? clients[index].email).trim() || clients[index].email,
      enable: Boolean(client.enable),
      totalGB: normalizeTotalGB(client.totalGB ?? clients[index].totalGB),
      expiryTime: normalizeExpiryTime(client.expiryTime ?? clients[index].expiryTime)
    };

    await writeClients(clients);
    await syncOpenVpn();
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
    await syncOpenVpn();
  }

  public async buildClientConfig(_session: OmniSession, _request: Request, clientId: string): Promise<ClientConfigPayload> {
    const trimmedId = String(clientId ?? "").trim();
    if (!trimmedId) {
      throw new Error("Client id is required.");
    }

    const clients = await readClients();
    const client = clients.find((item) => item.id === trimmedId);
    if (!client) {
      throw new Error("Client not found.");
    }

    const profilePath = join(getExportsDir(), `${trimmedId}.ovpn`);
    let profile = "";
    try {
      profile = await fs.readFile(profilePath, "utf8");
    } catch {
      await syncOpenVpn();
      profile = await fs.readFile(profilePath, "utf8");
    }

    const fileStem = toSafeFileStem(client.email);
    return {
      mode: "openvpn_bundle",
      title: "OpenVPN Client Bundle",
      uri: profile.trimEnd(),
      username: client.username,
      password: client.password,
      privateKeyPassphrase: "not set",
      ovpnFileName: `${fileStem}-${client.id.slice(0, 8)}.ovpn`,
      ovpnContent: profile
    };
  }
}
