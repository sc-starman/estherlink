import { promises as fs } from "node:fs";
import { dirname } from "node:path";
import { exec as execCallback } from "node:child_process";
import { promisify } from "node:util";
import { randomBytes, randomUUID } from "node:crypto";
import { type GatewayClientCreateOptions } from "@/lib/providers/types";

const exec = promisify(execCallback);
const DEFAULT_SYNC_COMMAND = "";
const DEFAULT_ACCOUNTING_DB = "/etc/omnirelay/gateway/connector/accounting.db";
const LEGACY_ACCOUNTING_DB = "/etc/omnirelay/gateway/singbox/accounting.db";

function sqlite3Command(): string {
  const configured =
    process.env.OMNIRELAY_SQLITE3_BIN?.trim() ||
    process.env.SQLITE3_BIN?.trim() ||
    "sqlite3";
  return /[\s\\/]/.test(configured) ? `"${configured.replace(/"/g, '\\"')}"` : configured;
}

export const SINGBOX_PER_CLIENT_CAPABILITIES = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true,
  supportsSpeedLimit: true,
  supportsOnlineStatus: true,
  supportsClientLifecycle: true
} as const;

export interface SingboxManagedClient {
  id: string;
  email: string;
  enable: boolean;
  totalGB: number;
  expiryTime: number;
  speedLimitKbps: number;
  [key: string]: unknown;
}

export function normalizeTotalGB(value: unknown): number {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric < 0) {
    return 0;
  }
  return numeric;
}

export function normalizeExpiryTime(value: unknown): number {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric < 0) {
    return 0;
  }
  return Math.trunc(numeric);
}

export function normalizeSpeedLimitKbps(value: unknown): number {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric < 0) {
    return 0;
  }
  return Math.trunc(numeric);
}

export function normalizeUsedBytes(value: unknown): number {
  const numeric = Number(value);
  if (!Number.isFinite(numeric) || numeric < 0) {
    return 0;
  }
  return Math.trunc(numeric);
}

export function normalizeClientOptions(options?: GatewayClientCreateOptions): Required<GatewayClientCreateOptions> {
  return {
    totalGB: normalizeTotalGB(options?.totalGB),
    expiryTime: normalizeExpiryTime(options?.expiryTime),
    speedLimitKbps: normalizeSpeedLimitKbps(options?.speedLimitKbps)
  };
}

export function getAccountingDbPath(): string {
  return process.env.SINGBOX_ACCOUNTING_DB?.trim() || DEFAULT_ACCOUNTING_DB;
}

function defaultAccountingDbCandidates(): string[] {
  const envPath = process.env.SINGBOX_ACCOUNTING_DB?.trim();
  return [envPath, DEFAULT_ACCOUNTING_DB, LEGACY_ACCOUNTING_DB].filter((value): value is string => Boolean(value));
}

export function randomBase64Token(byteLength = 16): string {
  const safeLen = Number.isFinite(byteLength) && byteLength > 0 ? Math.trunc(byteLength) : 16;
  return randomBytes(Math.max(16, safeLen)).toString("base64url").slice(0, safeLen);
}

async function resolveAccountingDbPath(candidates: string[] = defaultAccountingDbCandidates()): Promise<string> {
  for (const candidate of candidates) {
    try {
      await fs.access(candidate);
      return candidate;
    } catch {
      // try next candidate
    }
  }
  return candidates[0] || DEFAULT_ACCOUNTING_DB;
}

export interface DbProtocolClientRecord {
  id: string;
  email: string;
  enable: boolean;
  totalGB: number;
  expiryTime: number;
  speedLimitKbps: number;
  authUsername: string;
  authSecret: string;
}

async function ensureClientColumns(dbPath: string): Promise<void> {
  const statements = [
    "ALTER TABLE clients ADD COLUMN email TEXT NOT NULL DEFAULT '';",
    "ALTER TABLE clients ADD COLUMN auth_username TEXT NOT NULL DEFAULT '';",
    "ALTER TABLE clients ADD COLUMN auth_secret TEXT NOT NULL DEFAULT '';",
    "CREATE INDEX IF NOT EXISTS idx_clients_protocol_email ON clients(protocol_id,email);",
    "CREATE INDEX IF NOT EXISTS idx_clients_protocol_auth_username ON clients(protocol_id,auth_username);"
  ];
  for (const statement of statements) {
    try {
      await exec(`${sqlite3Command()} "${dbPath}" "${statement.replace(/"/g, '\\"')}"`);
    } catch {
      // expected when column already exists
    }
  }
}

function gbToBytes(value: number): number {
  const normalized = normalizeTotalGB(value);
  if (normalized <= 0) return 0;
  return Math.trunc(normalized * 1024 * 1024 * 1024);
}

function bytesToGb(value: number): number {
  if (!Number.isFinite(value) || value <= 0) return 0;
  return value / (1024 * 1024 * 1024);
}

export async function listProtocolClientsFromDb(protocolId: string, dbCandidates?: string[]): Promise<DbProtocolClientRecord[]> {
  const dbPath = await resolveAccountingDbPath(dbCandidates);
  await ensureClientColumns(dbPath);
  const escapedProtocol = protocolId.replace(/'/g, "''");
  const query = `SELECT client_id,email,enabled,total_bytes_limit,expiry_unix_ms,speed_limit_kbps,auth_username,auth_secret FROM clients WHERE protocol_id='${escapedProtocol}' ORDER BY email COLLATE NOCASE, client_id;`;
  const { stdout } = await exec(`${sqlite3Command()} -csv -noheader -cmd ".timeout 5000" -cmd "PRAGMA query_only=ON;" "${dbPath}" "${query.replace(/"/g, '\\"')}"`);
  const rows: DbProtocolClientRecord[] = [];
  for (const line of stdout.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    const parts = trimmed.split(",");
    if (parts.length < 8) continue;
    const totalBytes = Number.parseInt(parts[3] ?? "0", 10);
    const expiry = Number.parseInt(parts[4] ?? "0", 10);
    const speed = Number.parseInt(parts[5] ?? "0", 10);
    rows.push({
      id: parts[0] ?? "",
      email: parts[1] ?? "",
      enable: (parts[2] ?? "0") !== "0",
      totalGB: bytesToGb(Number.isFinite(totalBytes) ? totalBytes : 0),
      expiryTime: Number.isFinite(expiry) && expiry > 0 ? expiry : 0,
      speedLimitKbps: Number.isFinite(speed) && speed > 0 ? speed : 0,
      authUsername: parts[6] ?? "",
      authSecret: parts[7] ?? ""
    });
  }
  return rows.filter((x) => x.id.length > 0 && x.email.length > 0);
}

export async function upsertProtocolClientToDb(protocolId: string, client: DbProtocolClientRecord, dbCandidates?: string[]): Promise<void> {
  const dbPath = await resolveAccountingDbPath(dbCandidates);
  await ensureClientColumns(dbPath);
  const now = Math.trunc(Date.now() / 1000);
  const escapedId = client.id.replace(/'/g, "''");
  const escapedProtocol = protocolId.replace(/'/g, "''");
  const escapedEmail = client.email.replace(/'/g, "''");
  const escapedAuthUsername = (client.authUsername ?? "").replace(/'/g, "''");
  const escapedAuthSecret = (client.authSecret ?? "").replace(/'/g, "''");
  const enabled = client.enable ? 1 : 0;
  const totalBytes = gbToBytes(client.totalGB);
  const speed = normalizeSpeedLimitKbps(client.speedLimitKbps);
  const expiry = normalizeExpiryTime(client.expiryTime);
  const sql = `
INSERT OR REPLACE INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,total_bytes_limit,speed_limit_kbps,expiry_unix_ms,created_at,updated_at)
VALUES('${escapedId}','${escapedProtocol}','${escapedEmail}','${escapedEmail}','${escapedAuthUsername}','${escapedAuthSecret}',${enabled},${totalBytes},${speed},${expiry},COALESCE((SELECT created_at FROM clients WHERE client_id='${escapedId}'),${now}),${now});
INSERT OR IGNORE INTO usage_totals(client_id,used_bytes,updated_at) VALUES('${escapedId}',0,${now});
INSERT OR IGNORE INTO connection_counters(client_id,active_connections,last_seen_at) VALUES('${escapedId}',0,0);
INSERT OR IGNORE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at) VALUES('${escapedId}','',0,0);
`;
  await exec(`${sqlite3Command()} "${dbPath}" "${sql.replace(/"/g, '\\"').replace(/\n/g, " ")}"`);
}

export async function upsertUsageTotalByClientId(clientId: string, usedBytes: number, dbCandidates?: string[]): Promise<void> {
  const dbPath = await resolveAccountingDbPath(dbCandidates);
  const escapedId = clientId.replace(/'/g, "''");
  const normalizedUsedBytes = normalizeUsedBytes(usedBytes);
  const now = Math.trunc(Date.now() / 1000);
  const sql = `
INSERT OR REPLACE INTO usage_totals(client_id,used_bytes,updated_at)
VALUES('${escapedId}',${normalizedUsedBytes},${now});
`;
  await exec(`${sqlite3Command()} "${dbPath}" "${sql.replace(/"/g, '\\"').replace(/\n/g, " ")}"`);
}

export async function deleteProtocolClientFromDb(clientId: string, dbCandidates?: string[]): Promise<void> {
  const dbPath = await resolveAccountingDbPath(dbCandidates);
  const escapedId = clientId.replace(/'/g, "''");
  const sql = `
DELETE FROM clients WHERE client_id='${escapedId}';
DELETE FROM usage_totals WHERE client_id='${escapedId}';
DELETE FROM connection_counters WHERE client_id='${escapedId}';
DELETE FROM enforcement_state WHERE client_id='${escapedId}';
`;
  await exec(`${sqlite3Command()} "${dbPath}" "${sql.replace(/"/g, '\\"').replace(/\n/g, " ")}"`);
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

export async function runGatewaySync(command?: string): Promise<void> {
  if ((process.env.OMNIRELAY_LOCAL_RELAY_MODE ?? "").trim().toLowerCase() === "true") {
    return;
  }

  const resolved = normalizeSudoCommand((command ?? process.env.SINGBOX_RELOAD_COMMAND ?? DEFAULT_SYNC_COMMAND).trim());
  if (!resolved) {
    throw new Error("SINGBOX_RELOAD_COMMAND is required in relay-scoped mode.");
  }

  try {
    await exec(resolved);
  } catch (error) {
    const failure = error as { message?: string; stdout?: string; stderr?: string };
    const detail = [failure.message, failure.stderr, failure.stdout]
      .map((part) => String(part ?? "").trim())
      .filter((part) => part.length > 0)
      .join("\n");
    throw new Error(detail || "Gateway sync-clients failed.");
  }
}

export async function readClientFile(filePath: string): Promise<SingboxManagedClient[]> {
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
        enable: Boolean((item as Record<string, unknown>).enable ?? (item as Record<string, unknown>).enabled ?? true),
        totalGB: normalizeTotalGB((item as Record<string, unknown>).totalGB),
        expiryTime: normalizeExpiryTime((item as Record<string, unknown>).expiryTime),
        speedLimitKbps: normalizeSpeedLimitKbps((item as Record<string, unknown>).speedLimitKbps),
        ...(item as Record<string, unknown>)
      }))
      .filter((item) => item.id && item.email);
  } catch {
    return [];
  }
}

export function normalizeImportedClientFile(input: unknown[]): SingboxManagedClient[] {
  return input
    .map<SingboxManagedClient | null>((item) => {
      if (typeof item !== "object" || item === null) {
        return null;
      }

      const record = item as Record<string, unknown>;
      const id = String(record.id ?? "").trim();
      const email = String(record.email ?? "").trim();
      if (!id || !email) {
        return null;
      }

      return {
        ...record,
        id,
        email,
        enable: Boolean(record.enable ?? record.enabled ?? true),
        totalGB: normalizeTotalGB(record.totalGB),
        expiryTime: normalizeExpiryTime(record.expiryTime),
        speedLimitKbps: normalizeSpeedLimitKbps(record.speedLimitKbps),
        usedBytes: normalizeUsedBytes(record.usedBytes)
      } satisfies SingboxManagedClient;
    })
    .filter((item): item is SingboxManagedClient => item !== null);
}

export async function writeClientFile(filePath: string, clients: SingboxManagedClient[]): Promise<void> {
  await fs.mkdir(dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.tmp`;
  const isLocalRelayMode = (process.env.OMNIRELAY_LOCAL_RELAY_MODE ?? "").trim().toLowerCase() === "true";
  const localProtocol = (process.env.OMNIRELAY_LOCAL_PROTOCOL_ID ?? "").trim().toLowerCase();
  const sorted = [...clients].sort((left, right) => left.email.localeCompare(right.email));
  const payload = isLocalRelayMode
    ? sorted.map((client) => {
        const base = {
          ...client,
          enabled: Boolean(client.enable),
          protocol: localProtocol || String(client.protocol ?? "")
        } as Record<string, unknown>;

        if (localProtocol === "shadowsocks_local") {
          base.secret = String(base.secret ?? randomUUID());
        }

        if (localProtocol === "openvpn_local_tcp") {
          const normalizedEmail = String(client.email ?? "").trim().replace(/[^a-z0-9_]/gi, "_").toLowerCase();
          base.username = String(base.username ?? `ovpn_${normalizedEmail}`);
          base.secret = String(base.secret ?? randomUUID());
        }

        return base;
      })
    : sorted;
  await fs.writeFile(tempPath, `${JSON.stringify(payload, null, 2)}\n`, { encoding: "utf8", mode: 0o640 });
  await fs.rename(tempPath, filePath);
}

export async function readUsageByClientIds(clientIds: string[], dbCandidates?: string[]): Promise<Map<string, number>> {
  if (clientIds.length === 0) {
    return new Map();
  }
  const dbPath = await resolveAccountingDbPath(dbCandidates);
  const quotedIds = clientIds.map((id) => `'${id.replace(/'/g, "''")}'`).join(",");
  if (!quotedIds) {
    return new Map();
  }

  try {
    const { stdout } = await exec(
      `${sqlite3Command()} -csv -noheader -cmd ".timeout 5000" -cmd "PRAGMA query_only=ON;" "${dbPath}" "SELECT c.client_id, COALESCE(u.used_bytes, 0) FROM clients c LEFT JOIN usage_totals u ON u.client_id = c.client_id WHERE c.client_id IN (${quotedIds});"`
    );
    const map = new Map<string, number>();
    for (const line of stdout.split(/\r?\n/)) {
      const trimmed = line.trim();
      if (!trimmed) {
        continue;
      }
      const comma = trimmed.indexOf(",");
      if (comma <= 0) {
        continue;
      }
      const id = trimmed.slice(0, comma);
      const used = Number.parseInt(trimmed.slice(comma + 1), 10);
      if (!Number.isFinite(used) || used < 0) {
        continue;
      }
      map.set(id, used);
    }
    return map;
  } catch {
    return new Map();
  }
}

export interface SingboxClientRuntimeStat {
  usedBytes: number;
  lastSeenAtUnixMs: number;
  activeConnections: number;
  isOnline: boolean;
}

export function createEmptyRuntimeStats(clientIds: string[]): Map<string, SingboxClientRuntimeStat> {
  const map = new Map<string, SingboxClientRuntimeStat>();
  for (const clientId of clientIds) {
    map.set(clientId, {
      usedBytes: 0,
      lastSeenAtUnixMs: 0,
      activeConnections: 0,
      isOnline: false
    });
  }
  return map;
}

export async function readRuntimeStatsByClientIds(clientIds: string[], dbCandidates?: string[]): Promise<Map<string, SingboxClientRuntimeStat>> {
  if (clientIds.length === 0) {
    return new Map();
  }

  const map = createEmptyRuntimeStats(clientIds);
  const usage = await readUsageByClientIds(clientIds, dbCandidates);
  for (const [clientId, usedBytes] of usage.entries()) {
    const current = map.get(clientId);
    if (current) {
      current.usedBytes = usedBytes;
    }
  }

  const dbPath = await resolveAccountingDbPath(dbCandidates);
  const quotedIds = clientIds.map((id) => `'${id.replace(/'/g, "''")}'`).join(",");
  if (!quotedIds) {
    return map;
  }

  try {
    const { stdout } = await exec(
      `${sqlite3Command()} -csv -noheader -cmd ".timeout 5000" -cmd "PRAGMA query_only=ON;" "${dbPath}" "SELECT c.client_id, COALESCE(cc.active_connections, 0), COALESCE(cc.last_seen_at, 0) FROM clients c LEFT JOIN connection_counters cc ON cc.client_id = c.client_id WHERE c.client_id IN (${quotedIds});"`
    );
    for (const line of stdout.split(/\r?\n/)) {
      const trimmed = line.trim();
      if (!trimmed) {
        continue;
      }

      const parts = trimmed.split(",");
      if (parts.length < 3) {
        continue;
      }

      const clientId = parts[0]?.trim();
      if (!clientId) {
        continue;
      }

      const activeConnections = Number.parseInt(parts[1] ?? "0", 10);
      const lastSeenAtUnixSeconds = Number.parseInt(parts[2] ?? "0", 10);
      const normalizedActiveConnections = Number.isFinite(activeConnections) && activeConnections > 0 ? activeConnections : 0;
      const current = map.get(clientId) ?? { usedBytes: 0, lastSeenAtUnixMs: 0, activeConnections: 0, isOnline: false };
      map.set(clientId, {
        usedBytes: current.usedBytes,
        lastSeenAtUnixMs: Number.isFinite(lastSeenAtUnixSeconds) && lastSeenAtUnixSeconds > 0 ? (lastSeenAtUnixSeconds * 1000) : 0,
        activeConnections: normalizedActiveConnections,
        isOnline: normalizedActiveConnections > 0
      });
    }
  } catch {
    // Keep usage-only fallback.
  }

  return map;
}
