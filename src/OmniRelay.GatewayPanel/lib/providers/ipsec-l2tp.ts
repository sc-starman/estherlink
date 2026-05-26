import { promises as fs } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { exec as execCallback } from "node:child_process";
import { promisify } from "node:util";
import { randomBytes, randomUUID } from "node:crypto";
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
  createTarGzProtocolBackup,
  importTarGzProtocolBackup,
  readTarGzBundleEntryUtf8,
  readJsonClientBackup,
  type BundleDestination
} from "@/lib/providers/backup";
import {
  normalizeUsedBytes,
  readRuntimeStatsByClientIds,
  listProtocolClientsFromDb,
  upsertProtocolClientToDb,
  deleteProtocolClientFromDb,
  upsertUsageTotalByClientId
} from "@/lib/providers/singbox-shared";
import { resolveProtocolConfigString } from "@/lib/protocol-config";

const exec = promisify(execCallback);
const DEFAULT_SYNC_COMMAND = "";
const DEFAULT_PSK_FILE = "/etc/omnirelay/gateway/ipsec/shared_psk";
const DEFAULT_ACCOUNTING_DB = "/etc/omnirelay/gateway/connector/accounting.db";
const LEGACY_ACCOUNTING_DB = "/etc/omnirelay/gateway/ipsec/accounting.db";
function sqlite3Command(): string {
  const configured =
    process.env.OMNIRELAY_SQLITE3_BIN?.trim() ||
    process.env.SQLITE3_BIN?.trim() ||
    "sqlite3";
  return /[\s\\/]/.test(configured) ? `"${configured.replace(/"/g, '\\"')}"` : configured;
}
const IPSEC_ACCOUNTING_CAPABILITIES = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true,
  supportsOnlineStatus: true
} as const;

interface IpsecL2tpClientRecord extends GatewayClientRecord {
  username: string;
  password: string;
  totalGB: number;
  expiryTime: number;
  usedBytes?: number;
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

    const dbCandidates = getAccountingDbCandidates();

    let dbPath = "";
    for (const candidate of dbCandidates) {
      try {
        await fs.access(candidate);
        dbPath = candidate;
        break;
      } catch {
        continue;
      }
    }

    if (!dbPath) {
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
        `${sqlite3Command()} -csv -noheader -cmd ".timeout 5000" -cmd "PRAGMA query_only=ON;" "${dbPath}" "SELECT c.client_id, COALESCE(u.used_bytes, 0) AS used_bytes FROM clients c LEFT JOIN usage_totals u ON u.client_id = c.client_id WHERE c.client_id IN (${quotedIds});"`
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

function getRuntimeFilePath(): string {
  return process.env.IPSEC_L2TP_RUNTIME_FILE?.trim() || "/etc/omnirelay/gateway/ipsec_l2tp_runtime.json";
}

function getPskFilePath(): string {
  return process.env.IPSEC_L2TP_PSK_FILE?.trim() || DEFAULT_PSK_FILE;
}

function getAccountingDbCandidates(): string[] {
  return [
    process.env.IPSEC_L2TP_ACCOUNTING_DB?.trim() || "",
    DEFAULT_ACCOUNTING_DB,
    LEGACY_ACCOUNTING_DB
  ].filter((item, index, array) => item && array.indexOf(item) === index);
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
  const dbPath = getAccountingDbCandidates()[0] || DEFAULT_ACCOUNTING_DB;
  try {
    const { stdout } = await exec(
      `${sqlite3Command()} -csv -noheader -cmd ".timeout 5000" "${dbPath}" "SELECT client_id,email,enabled,auth_username,auth_secret,total_bytes_limit,expiry_unix_ms FROM clients WHERE protocol_id='ipsec_l2tp_singbox' ORDER BY email COLLATE NOCASE, client_id;"`
    );
    const clients: IpsecL2tpClientRecord[] = [];
    for (const line of stdout.split(/\r?\n/)) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      const parts = trimmed.split(",");
      if (parts.length < 7) continue;
      const totalBytes = Number.parseInt(parts[5] ?? "0", 10);
      clients.push({
        id: parts[0] ?? "",
        email: parts[1] ?? "",
        enable: (parts[2] ?? "0") !== "0",
        username: parts[3] ?? "",
        password: parts[4] ?? "",
        totalGB: Number.isFinite(totalBytes) && totalBytes > 0 ? totalBytes / (1024 * 1024 * 1024) : 0,
        expiryTime: normalizeExpiryTime(parts[6])
      });
    }
    return clients.filter((item) => item.id && item.email && item.username && item.password);
  } catch (error) {
    const err = error as NodeJS.ErrnoException;
    if (err?.code === "ENOENT") {
      return [];
    }

    if (err?.code === "EACCES" || err?.code === "EPERM") {
      throw new Error(`IPSec/L2TP clients DB is not readable by OmniPanel user (${dbPath}).`);
    }

    throw new Error(`IPSec/L2TP clients DB is invalid or unreadable (${dbPath}): ${err?.message ?? "unknown error"}`);
  }
}

async function writeClients(clients: IpsecL2tpClientRecord[]): Promise<void> {
  const protocolId = "ipsec_l2tp_singbox";
  const dbCandidates = getAccountingDbCandidates();
  const existing = await listProtocolClientsFromDb(protocolId, dbCandidates);
  for (const row of existing) {
    await deleteProtocolClientFromDb(row.id, dbCandidates);
  }

  const sorted = [...clients].sort((left, right) => left.email.localeCompare(right.email));
  for (const client of sorted) {
    await upsertProtocolClientToDb(
      protocolId,
      {
        id: client.id,
        email: client.email,
        enable: client.enable,
        totalGB: client.totalGB,
        expiryTime: client.expiryTime,
        speedLimitKbps: 0,
        authUsername: client.username,
        authSecret: client.password
      },
      dbCandidates
    );
  }
}

function normalizeImportedIpsecClients(input: unknown[]): IpsecL2tpClientRecord[] {
  return input.map((item) => {
    if (typeof item !== "object" || item === null) {
      throw new Error("IPSec/L2TP backup contains an invalid client record.");
    }

    const record = item as Record<string, unknown>;
    const id = String(record.id ?? "").trim();
    const email = String(record.email ?? "").trim();
    const username = String(record.username ?? "").trim();
    const password = String(record.password ?? "").trim();
    if (!id || !email || !username || !password) {
      throw new Error("IPSec/L2TP import requires id, email, username, and password for every client.");
    }

    return {
      ...record,
      id,
      email,
      enable: Boolean(record.enable ?? true),
      username,
      password,
      totalGB: normalizeTotalGB(record.totalGB),
      expiryTime: normalizeExpiryTime(record.expiryTime),
      usedBytes: normalizeUsedBytes(record.usedBytes)
    };
  });
}

async function readSharedPsk(): Promise<string> {
  const pskFile = getPskFilePath();
  try {
    return (await fs.readFile(pskFile, "utf8")).trim();
  } catch {
    return "";
  }
}

function ipsecBundleDestinations(): BundleDestination[] {
  return [
    {
      archivePath: "ipsec_l2tp_runtime.json",
      destinationPath: getRuntimeFilePath(),
      kind: "file",
      mode: 0o600
    },
    {
      archivePath: "shared_psk",
      destinationPath: getPskFilePath(),
      kind: "file",
      mode: 0o600
    }
  ];
}

async function syncIpsecL2tp(): Promise<void> {
  const command = normalizeSudoCommand(
    process.env.IPSEC_L2TP_SYNC_COMMAND?.trim() ||
      process.env.SINGBOX_RELOAD_COMMAND?.trim() ||
      DEFAULT_SYNC_COMMAND
  );
  if (!command) {
    throw new Error("IPSEC_L2TP_SYNC_COMMAND is required in relay-scoped mode.");
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
  public readonly protocolId = "ipsec_l2tp_singbox";
  private readonly accountingSource: IpsecL2tpAccountingSource;

  public constructor(accountingSource: IpsecL2tpAccountingSource = new LocalSqliteIpsecL2tpAccountingSource()) {
    this.accountingSource = accountingSource;
  }

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    const clients = await readClients();
    const clientIds = clients.map((item) => item.id);
    const runtimeStats = await readRuntimeStatsByClientIds(clientIds, getAccountingDbCandidates());
    const usageByClientId = await this.accountingSource.getUsageByClientId(clientIds);
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
        usedBytes: Math.max(runtimeStats.get(item.id)?.usedBytes ?? 0, usageByClientId.get(item.id) ?? 0),
        id: item.id,
        email: item.email,
        enable: item.enable,
        totalGB: item.totalGB,
        expiryTime: item.expiryTime,
        lastSeenAtUnixMs: runtimeStats.get(item.id)?.lastSeenAtUnixMs ?? 0,
        activeConnections: runtimeStats.get(item.id)?.activeConnections ?? 0,
        isOnline: runtimeStats.get(item.id)?.isOnline ?? false
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
      usedBytes: 0
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

    const server = await resolveProtocolConfigString("ipsec_l2tp_singbox", "publicHost", process.env.PANEL_PUBLIC_HOST, resolveGatewayHost(request));
    const setupSteps = [
      "Add a new L2TP/IPSec PSK VPN profile on your device.",
      "Set server/host to the Server value above.",
      "Set IPSec pre-shared key to the value shown.",
      "Use Username/Password for PPP authentication.",
      "Save and connect."
    ];
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
      ...setupSteps.map((step, index) => `${index + 1}) ${step}`)
    ].join("\n");

    return {
      mode: "ipsec_manual",
      title: "IPSec/L2TP Manual Bundle",
      uri: manualBundle,
      fields: {
        server,
        ports: ["UDP 500", "UDP 4500", "UDP 1701"],
        username: client.username,
        password: client.password,
        preSharedKey: psk
      },
      setupSteps
    };
  }

  public async exportBackup(_session: OmniSession): Promise<ProtocolBackupPayload> {
    const tempDir = await fs.mkdtemp(join(tmpdir(), "omnirelay-ipsec-export-"));
    const tempClientsPath = join(tempDir, "clients.db.json");
    const clientsForBackup = await readClients();
    const usageByClientId = await this.accountingSource.getUsageByClientId(clientsForBackup.map((client) => client.id));
    const clientsWithUsage = clientsForBackup.map((client) => ({
      ...client,
      usedBytes: usageByClientId.get(client.id) ?? 0
    }));
    await fs.writeFile(tempClientsPath, `${JSON.stringify(clientsWithUsage, null, 2)}\n`, "utf8");

    const sources = [
      { sourcePath: tempClientsPath, archivePath: "clients.db.json", required: true },
      { sourcePath: getRuntimeFilePath(), archivePath: "ipsec_l2tp_runtime.json" },
      { sourcePath: getPskFilePath(), archivePath: "shared_psk", required: true }
    ];
    try {
      return await createTarGzProtocolBackup(this.protocolId, "ipsec-l2tp-backup", sources);
    } finally {
      await fs.rm(tempDir, { recursive: true, force: true }).catch(() => undefined);
    }
  }

  public async importBackup(_session: OmniSession, input: ProtocolBackupInput): Promise<void> {
    if (input.contentType.includes("json") || input.fileName.toLowerCase().endsWith(".json")) {
      const clients = normalizeImportedIpsecClients(readJsonClientBackup(input, this.protocolId));
      await writeClients(clients);
      const dbCandidates = getAccountingDbCandidates();
      for (const client of clients) {
        await upsertUsageTotalByClientId(client.id, normalizeUsedBytes(client.usedBytes), dbCandidates);
      }
      await syncIpsecL2tp();
      return;
    }

    const clientsJson = await readTarGzBundleEntryUtf8(this.protocolId, input, "clients.db.json");
    if (!clientsJson) {
      throw new Error("Backup archive is missing clients.db.json.");
    }
    const clients = normalizeImportedIpsecClients(JSON.parse(clientsJson) as unknown[]);
    await writeClients(clients);
    const dbCandidates = getAccountingDbCandidates();
    for (const client of clients) {
      await upsertUsageTotalByClientId(client.id, normalizeUsedBytes(client.usedBytes), dbCandidates);
    }
    const tempDir = await fs.mkdtemp(join(tmpdir(), "omnirelay-ipsec-import-"));
    try {
      await importTarGzProtocolBackup(this.protocolId, input, [
        ...ipsecBundleDestinations(),
        {
          archivePath: "clients.db.json",
          destinationPath: join(tempDir, "clients.db.json"),
          kind: "file"
        }
      ]);
    } finally {
      await fs.rm(tempDir, { recursive: true, force: true }).catch(() => undefined);
    }
    await syncIpsecL2tp();
  }
}
