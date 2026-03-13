import { promises as fs } from "node:fs";
import { dirname } from "node:path";
import { exec as execCallback } from "node:child_process";
import { promisify } from "node:util";
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

const exec = promisify(execCallback);
const DEFAULT_SYNC_COMMAND = "/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients";
const DEFAULT_CLIENTS_FILE = "/opt/omnirelay/omni-gateway/ipsec_l2tp_clients.json";
const DEFAULT_PSK_FILE = "/etc/omnirelay/gateway/ipsec/shared_psk";
const DEFAULT_ACCOUNTING_DB = "/etc/omnirelay/gateway/ipsec/accounting.db";
const IPSEC_ACCOUNTING_CAPABILITIES = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true
} as const;

interface IpsecL2tpClientRecord extends GatewayClientRecord {
  username: string;
  password: string;
  totalGB: number;
  expiryTime: number;
}

interface IpsecL2tpAccountingSource {
  getCapabilities(): typeof IPSEC_ACCOUNTING_CAPABILITIES;
  getUsageByClientId(clientIds: string[]): Promise<Map<string, number>>;
}

class LocalSqliteIpsecL2tpAccountingSource implements IpsecL2tpAccountingSource {
  public getCapabilities(): typeof IPSEC_ACCOUNTING_CAPABILITIES {
    return IPSEC_ACCOUNTING_CAPABILITIES;
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

function randomAlphaNum(length: number): string {
  return randomBytes(length)
    .toString("base64")
    .replace(/[^a-zA-Z0-9]/g, "")
    .slice(0, length);
}

function getClientsFilePath(): string {
  return process.env.IPSEC_L2TP_CLIENTS_FILE?.trim() || DEFAULT_CLIENTS_FILE;
}

function getPskFilePath(): string {
  return process.env.IPSEC_L2TP_PSK_FILE?.trim() || DEFAULT_PSK_FILE;
}

function getAccountingDbPath(): string {
  return process.env.IPSEC_L2TP_ACCOUNTING_DB?.trim() || DEFAULT_ACCOUNTING_DB;
}

function escapeSqlLiteral(value: string): string {
  return value.replace(/'/g, "''");
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

function toSafeUsername(seed: string): string {
  const normalized = seed
    .toLowerCase()
    .replace(/[^a-z0-9]/g, "")
    .slice(0, 18);
  return normalized || "client";
}

function makeUsername(email: string, existing: Set<string>): string {
  const base = `l2tp_${toSafeUsername(email)}`;
  if (!existing.has(base)) {
    return base;
  }

  for (let i = 0; i < 100; i += 1) {
    const candidate = `${base}${randomAlphaNum(4).toLowerCase()}`;
    if (!existing.has(candidate)) {
      return candidate;
    }
  }

  return `l2tp_${randomAlphaNum(10).toLowerCase()}`;
}

async function readClients(): Promise<IpsecL2tpClientRecord[]> {
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

async function writeClients(clients: IpsecL2tpClientRecord[]): Promise<void> {
  const filePath = getClientsFilePath();
  await fs.mkdir(dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.tmp`;
  const sorted = [...clients].sort((left, right) => left.email.localeCompare(right.email));
  await fs.writeFile(tempPath, `${JSON.stringify(sorted, null, 2)}\n`, { encoding: "utf8", mode: 0o640 });
  await fs.rename(tempPath, filePath);
}

async function readSharedPsk(): Promise<string> {
  const pskFile = getPskFilePath();
  try {
    return (await fs.readFile(pskFile, "utf8")).trim();
  } catch {
    return "";
  }
}

async function syncIpsecL2tp(): Promise<void> {
  const command = normalizeSudoCommand(process.env.IPSEC_L2TP_SYNC_COMMAND?.trim() || DEFAULT_SYNC_COMMAND);
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

export class IpsecL2tpProvider implements GatewayProtocolProvider {
  public readonly protocolId = "ipsec_l2tp_hwdsl2";
  private readonly accountingSource: IpsecL2tpAccountingSource;

  public constructor(accountingSource: IpsecL2tpAccountingSource = new LocalSqliteIpsecL2tpAccountingSource()) {
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
        port: 1701,
        remark: "OmniRelay Managed IPSec/L2TP",
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
    const client: IpsecL2tpClientRecord = {
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
    await syncIpsecL2tp();
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
    await syncIpsecL2tp();
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
    await syncIpsecL2tp();
  }

  public async buildClientConfig(_session: OmniSession, request: Request, clientId: string): Promise<ClientConfigPayload> {
    const trimmedId = String(clientId ?? "").trim();
    if (!trimmedId) {
      throw new Error("Client id is required.");
    }

    const clients = await readClients();
    const client = clients.find((item) => item.id === trimmedId);
    if (!client) {
      throw new Error("Client not found.");
    }

    let psk = await readSharedPsk();
    if (!psk) {
      await syncIpsecL2tp();
      psk = await readSharedPsk();
    }
    if (!psk) {
      throw new Error("IPSec shared PSK is not available on gateway.");
    }

    const server = resolveGatewayHost(request);
    const manualBundle = [
      "OmniRelay IPSec/L2TP Client Bundle",
      "=================================",
      `Server: ${server}`,
      "Ports: UDP 500, UDP 4500, UDP 1701",
      `IPSec PSK: ${psk}`,
      `Username: ${client.username}`,
      `Password: ${client.password}`,
      "",
      "Setup:",
      "1) Add a new L2TP/IPSec PSK VPN profile on your device.",
      "2) Set server/host to the Server value above.",
      "3) Set IPSec pre-shared key to IPSec PSK.",
      "4) Use Username/Password for PPP authentication.",
      "5) Save and connect."
    ].join("\n");

    const qrCodeDataUrl = await QRCode.toDataURL(manualBundle, { width: 320, margin: 1 });
    return { uri: manualBundle, qrCodeDataUrl };
  }
}
