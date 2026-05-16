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
  type GatewayProtocolCapabilities,
  type GatewayProtocolProvider,
  type ProtocolBackupInput,
  type ProtocolBackupPayload
} from "@/lib/providers/types";
import {
  deleteProtocolClientFromDb,
  listProtocolClientsFromDb,
  readRuntimeStatsByClientIds,
  upsertProtocolClientToDb
} from "@/lib/providers/singbox-shared";
import { resolveProtocolConfigPort, resolveProtocolConfigString } from "@/lib/protocol-config";

const OPENVPN_REMOTE_CAPABILITIES = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true,
  supportsSpeedLimit: true,
  supportsOnlineStatus: true
} as const;

const OPENVPN_LOCAL_CAPABILITIES = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true,
  supportsSpeedLimit: false,
  supportsOnlineStatus: false
} as const;

const exec = promisify(execCallback);
const DEFAULT_SYNC_COMMAND = "";
const DEFAULT_ACCOUNTING_DB = "/etc/omnirelay/gateway/connector/accounting.db";
const LEGACY_ACCOUNTING_DB = "/etc/omnirelay/gateway/openvpn/accounting.db";
const DEFAULT_STATUS_FILE = "/var/log/openvpn/omnirelay-status.log";

function sqlite3Command(): string {
  const configured =
    process.env.OMNIRELAY_SQLITE3_BIN?.trim() ||
    process.env.SQLITE3_BIN?.trim() ||
    "sqlite3";
  return /[\s\\/]/.test(configured) ? `"${configured.replace(/"/g, '\\"')}"` : configured;
}

interface OpenVpnClientRecord extends GatewayClientRecord {
  username: string;
  password: string;
  totalGB: number;
  expiryTime: number;
  speedLimitKbps: number;
}

interface OpenVpnAccountingSource {
  getCapabilities(isLocal: boolean): GatewayProtocolCapabilities;
  getUsageByClientId(clientIds: string[]): Promise<Map<string, number>>;
}

class LocalSqliteOpenVpnAccountingSource implements OpenVpnAccountingSource {
  public getCapabilities(isLocal: boolean): GatewayProtocolCapabilities {
    return isLocal ? OPENVPN_LOCAL_CAPABILITIES : OPENVPN_REMOTE_CAPABILITIES;
  }

  public async getUsageByClientId(clientIds: string[]): Promise<Map<string, number>> {
    if (clientIds.length === 0) {
      return new Map();
    }

    const dbCandidates = [
      process.env.OPENVPN_ACCOUNTING_DB?.trim() || "",
      DEFAULT_ACCOUNTING_DB,
      LEGACY_ACCOUNTING_DB
    ].filter((item, index, array) => item && array.indexOf(item) === index);

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

function parsePort(value: string | undefined, fallback: number): number {
  const parsed = Number.parseInt((value ?? "").trim(), 10);
  return Number.isFinite(parsed) && parsed > 0 && parsed <= 65535 ? parsed : fallback;
}

function getExportsDir(): string {
  return process.env.OPENVPN_EXPORT_DIR?.trim() || "/opt/omnirelay/omni-gateway/openvpn-exports";
}

function getOpenVpnRuntimeDir(): string {
  const configured = process.env.OPENVPN_STATE_DIR?.trim();
  if (configured) {
    return configured;
  }

  const exportsDir = process.env.OPENVPN_EXPORT_DIR?.trim();
  if (exportsDir) {
    return join(dirname(exportsDir), "openvpn");
  }

  return "/etc/omnirelay/gateway/openvpn";
}

async function getPublicPort(): Promise<number> {
  return resolveProtocolConfigPort("openvpn_tcp_singbox", "publicPort", process.env.OPENVPN_PUBLIC_PORT, 443);
}

function getStatusFilePath(): string {
  return process.env.OPENVPN_STATUS_FILE?.trim() || DEFAULT_STATUS_FILE;
}

function escapeSqlLiteral(value: string): string {
  return value.replace(/'/g, "''");
}

function getAccountingDbCandidates(): string[] {
  return [
    process.env.OPENVPN_ACCOUNTING_DB?.trim() || "",
    DEFAULT_ACCOUNTING_DB,
    LEGACY_ACCOUNTING_DB
  ].filter((item, index, array) => item && array.indexOf(item) === index);
}

async function readLiveUsageByUsername(): Promise<Map<string, number>> {
  const statusFile = getStatusFilePath();
  try {
    const raw = await fs.readFile(statusFile, "utf8");
    const usageByUsername = new Map<string, number>();
    let inClientTable = false;
    let tableHeaders: string[] = [];

    const parseCounter = (value: string | undefined): number => {
      const parsed = Number.parseInt((value ?? "").trim(), 10);
      return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
    };
    const isUnsigned = (value: string | undefined): boolean => /^\d+$/.test((value ?? "").trim());
    const splitStatusFields = (line: string): string[] => {
      if (line.includes(",")) {
        return line.split(",").map((part) => part.trim());
      }
      if (line.includes("\t")) {
        return line.split("\t").map((part) => part.trim());
      }
      return line.trim().split(/\s{2,}/).map((part) => part.trim());
    };

    const addUsage = (commonNameRaw: string, usernameRaw: string, rxRaw: string, txRaw: string): void => {
      const commonName = commonNameRaw.trim();
      const userField = usernameRaw.trim();
      const username = userField && userField !== "UNDEF" ? userField : commonName;
      if (!username) {
        return;
      }

      const total = parseCounter(rxRaw) + parseCounter(txRaw);
      if (total <= 0) {
        return;
      }

      usageByUsername.set(username, (usageByUsername.get(username) ?? 0) + total);
    };

    for (const rawLine of raw.split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!line) {
        continue;
      }
      const upperLine = line.toUpperCase();

      if (upperLine.startsWith("CLIENT_LIST")) {
        const fields = splitStatusFields(line);
        if (fields.length === 0 || fields[0].toUpperCase() !== "CLIENT_LIST") {
          continue;
        }
        const payload = fields.slice(1);
        if (payload.length === 0) {
          continue;
        }

        const commonName = payload[0] ?? "";
        const username = payload[8] ?? payload[9] ?? payload[7] ?? "";
        const pairs: Array<[number, number]> = [[4, 5], [2, 3], [5, 6], [3, 4]];
        let rxRaw = "0";
        let txRaw = "0";
        for (const [rxIndex, txIndex] of pairs) {
          if (payload.length > Math.max(rxIndex, txIndex)) {
            const rxCandidate = payload[rxIndex] ?? "0";
            const txCandidate = payload[txIndex] ?? "0";
            if (isUnsigned(rxCandidate) && isUnsigned(txCandidate)) {
              rxRaw = rxCandidate;
              txRaw = txCandidate;
              break;
            }
          }
        }

        addUsage(commonName, username, rxRaw, txRaw);
        continue;
      }

      if (upperLine.startsWith("OPENVPN CLIENT LIST")) {
        inClientTable = true;
        tableHeaders = [];
        continue;
      }

      if (
        upperLine.startsWith("ROUTING TABLE") ||
        upperLine.startsWith("ROUTING_TABLE") ||
        upperLine.startsWith("GLOBAL STATS") ||
        upperLine.startsWith("GLOBAL_STATS") ||
        upperLine === "END"
      ) {
        inClientTable = false;
        tableHeaders = [];
        continue;
      }

      const parts = splitStatusFields(line);
      if (parts.length < 2) {
        continue;
      }

      if (parts[0]?.toUpperCase() === "HEADER") {
        const headerKind = (parts[1] ?? "").toUpperCase();
        if (headerKind === "CLIENT_LIST") {
          inClientTable = true;
          tableHeaders = parts.slice(2).map((part) => part.toLowerCase());
          continue;
        }
        if (headerKind === "ROUTING_TABLE" || headerKind === "GLOBAL_STATS") {
          inClientTable = false;
          tableHeaders = [];
          continue;
        }
      }

      const normalized = parts.map((part) => part.toLowerCase());
      if (
        normalized.includes("common name") &&
        normalized.includes("bytes received") &&
        normalized.includes("bytes sent")
      ) {
        inClientTable = true;
        tableHeaders = normalized;
        continue;
      }

      if (!inClientTable) {
        continue;
      }

      const row = new Map<string, string>();
      if (tableHeaders.length > 0) {
        for (let i = 0; i < tableHeaders.length && i < parts.length; i += 1) {
          row.set(tableHeaders[i], parts[i]);
        }
      }

      const commonName = row.get("common name") ?? parts[0] ?? "";
      const username = row.get("username") ?? "";
      let rxRaw = row.get("bytes received") ?? "";
      let txRaw = row.get("bytes sent") ?? "";
      if (!rxRaw && !txRaw) {
        if (parts.length > 5) {
          rxRaw = parts[4] ?? "";
          txRaw = parts[5] ?? "";
        } else {
          rxRaw = parts[2] ?? "";
          txRaw = parts[3] ?? "";
        }
      }
      addUsage(commonName, username, rxRaw, txRaw);
    }

    return usageByUsername;
  } catch {
    return new Map();
  }
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

function isLocalRelayMode(): boolean {
  return (process.env.OMNIRELAY_LOCAL_RELAY_MODE ?? "").trim().toLowerCase() === "true";
}

function normalizePemBlock(value: string): string {
  return value.replace(/\r\n/g, "\n").trim();
}

async function buildLocalFallbackProfile(host: string, port: number): Promise<string> {
  const runtimeDir = getOpenVpnRuntimeDir();
  const readPem = async (fileName: string, label: string): Promise<string> => {
    const raw = await fs.readFile(join(runtimeDir, fileName), "utf8");
    const normalized = normalizePemBlock(raw);
    if (!normalized) {
      throw new Error(`${label} is empty`);
    }
    return normalized;
  };
  const readPemAny = async (fileNames: string[], label: string): Promise<string> => {
    let lastError = "";
    for (const fileName of fileNames) {
      try {
        return await readPem(fileName, label);
      } catch (error) {
        lastError = error instanceof Error ? error.message : String(error);
      }
    }
    throw new Error(`OpenVPN ${label} missing or unreadable (${join(runtimeDir, fileNames[0])}): ${lastError}`);
  };

  const ca = await readPemAny(["ca.crt"], "shared CA cert");
  const clientCert = await readPemAny(["client-shared.crt", "server.crt", "client.crt"], "shared client cert");
  const clientKey = await readPemAny(["client-shared.key", "server.key", "client.key"], "shared client key");
  const taKey = await readPemAny(["ta.key"], "tls-crypt key");

  return [
    "client",
    "dev tun",
    "proto tcp-client",
    `remote ${host} ${port}`,
    "nobind",
    "persist-key",
    "persist-tun",
    "auth-user-pass",
    "remote-cert-tls server",
    "cipher AES-256-GCM",
    "auth SHA256",
    "verb 3",
    "<ca>",
    ca,
    "</ca>",
    "<cert>",
    clientCert,
    "</cert>",
    "<key>",
    clientKey,
    "</key>",
    "<tls-crypt>",
    taKey,
    "</tls-crypt>",
    ""
  ].join("\n");
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

function openVpnCnFromClientId(clientId: string): string {
  const sanitized = String(clientId ?? "")
    .replace(/[^a-zA-Z0-9]/g, "")
    .slice(0, 40);
  return sanitized ? `ovpn-${sanitized}` : "";
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

function normalizeSpeedLimitKbps(value: unknown): number {
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
  const rows = await listProtocolClientsFromDb("openvpn_tcp_singbox", getAccountingDbCandidates());
  return rows
    .map((row) => ({
      id: row.id,
      email: row.email,
      enable: row.enable,
      username: String(row.authUsername ?? "").trim(),
      password: String(row.authSecret ?? "").trim(),
      totalGB: normalizeTotalGB(row.totalGB),
      expiryTime: normalizeExpiryTime(row.expiryTime),
      speedLimitKbps: normalizeSpeedLimitKbps(row.speedLimitKbps)
    }))
    .filter((item) => item.id && item.email && item.username && item.password);
}

async function upsertOpenVpnClient(client: OpenVpnClientRecord): Promise<void> {
  await upsertProtocolClientToDb(
    "openvpn_tcp_singbox",
    {
      id: client.id,
      email: client.email,
      enable: client.enable,
      totalGB: client.totalGB,
      expiryTime: client.expiryTime,
      speedLimitKbps: client.speedLimitKbps,
      authUsername: client.username,
      authSecret: client.password
    },
    getAccountingDbCandidates()
  );
}

async function syncOpenVpn(): Promise<void> {
  if (isLocalRelayMode()) {
    return;
  }

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
  public readonly protocolId = "openvpn_tcp_singbox";
  private readonly accountingSource: OpenVpnAccountingSource;

  public constructor(accountingSource: OpenVpnAccountingSource = new LocalSqliteOpenVpnAccountingSource()) {
    this.accountingSource = accountingSource;
  }

  public async getInbound(_session: OmniSession): Promise<GatewayInboundSnapshot> {
    let clients = await readClients();
    if (clients.length === 0 && isLocalRelayMode()) {
      const bootstrap: OpenVpnClientRecord = {
        id: randomUUID(),
        email: "ovpn_default@local",
        enable: true,
        username: "ovpn_default",
        password: randomAlphaNum(24),
        totalGB: 0,
        expiryTime: 0,
        speedLimitKbps: 0
      };
      clients = [bootstrap];
      await upsertOpenVpnClient(bootstrap);
    }
    const isLocal = isLocalRelayMode();
    const usageByClientId = await this.accountingSource.getUsageByClientId(clients.map((item) => item.id));
    const liveUsageByUsername = await readLiveUsageByUsername();
    const runtimeStats = isLocal ? new Map() : await readRuntimeStatsByClientIds(clients.map((item) => item.id));
    const capabilities = this.accountingSource.getCapabilities(isLocal);
    return {
      inbound: {
        id: 1,
        protocol: this.protocolId,
        port: await getPublicPort(),
        remark: "OmniRelay Managed OpenVPN (TCP)",
        enable: true
      },
      clients: clients.map((item) => ({
        // Persisted accounting is the source of truth; live status is a fallback for active sessions.
        usedBytes: Math.max(
          usageByClientId.get(item.id) ?? 0,
          liveUsageByUsername.get(item.username) ?? 0,
          liveUsageByUsername.get(openVpnCnFromClientId(item.id)) ?? 0,
          runtimeStats.get(item.id)?.usedBytes ?? 0
        ),
        id: item.id,
        email: item.email,
        enable: item.enable,
        totalGB: item.totalGB,
        expiryTime: item.expiryTime,
        speedLimitKbps: item.speedLimitKbps,
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
    const client: OpenVpnClientRecord = {
      id: randomUUID(),
      email: normalizedEmail,
      enable: true,
      username: makeUsername(normalizedEmail, usernames),
      password: randomAlphaNum(24),
      totalGB: normalizeTotalGB(options?.totalGB),
      expiryTime: normalizeExpiryTime(options?.expiryTime),
      speedLimitKbps: normalizeSpeedLimitKbps(options?.speedLimitKbps)
    };

    clients.push(client);
    await upsertOpenVpnClient(client);
    await syncOpenVpn();
    return {
      id: client.id,
      email: client.email,
      enable: client.enable,
      totalGB: client.totalGB,
      expiryTime: client.expiryTime,
      speedLimitKbps: client.speedLimitKbps,
      usedBytes: 0,
      lastSeenAtUnixMs: 0,
      activeConnections: 0,
      isOnline: false
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

    const updated: OpenVpnClientRecord = {
      ...clients[index],
      email: String(client.email ?? clients[index].email).trim() || clients[index].email,
      enable: Boolean(client.enable),
      totalGB: normalizeTotalGB(client.totalGB ?? clients[index].totalGB),
      expiryTime: normalizeExpiryTime(client.expiryTime ?? clients[index].expiryTime),
      speedLimitKbps: normalizeSpeedLimitKbps(client.speedLimitKbps ?? clients[index].speedLimitKbps)
    };
    await upsertOpenVpnClient(updated);
    await syncOpenVpn();
  }

  public async deleteClient(_session: OmniSession, clientId: string): Promise<void> {
    const trimmed = String(clientId ?? "").trim();
    if (!trimmed) {
      throw new Error("Client id is required.");
    }

    const clients = await readClients();
    if (!clients.some((item) => item.id === trimmed)) {
      throw new Error("Client not found.");
    }
    await deleteProtocolClientFromDb(trimmed, getAccountingDbCandidates());
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

    const host = await resolveProtocolConfigString(
      "openvpn_tcp_singbox",
      "publicHost",
      process.env.PANEL_PUBLIC_HOST?.trim() || process.env.OPENVPN_PUBLIC_HOST?.trim() || process.env.OPENVPN_HOST?.trim(),
      "127.0.0.1"
    );
    const port = await getPublicPort();
    const profilePath = join(getExportsDir(), `${trimmedId}.ovpn`);
    let profile = "";
    try {
      profile = await fs.readFile(profilePath, "utf8");
    } catch {
      if (isLocalRelayMode()) {
        profile = await buildLocalFallbackProfile(host, port);
      } else {
        await syncOpenVpn();
        profile = await fs.readFile(profilePath, "utf8");
      }
    }

    // Older gateway scripts generated minimal profiles without embedded CA/TLS material.
    // Retry a sync once, then fail with a clear operator-facing error.
    if (!profile.includes("<ca>")) {
      if (isLocalRelayMode()) {
        profile = await buildLocalFallbackProfile(host, port);
      } else {
        await syncOpenVpn();
        profile = await fs.readFile(profilePath, "utf8");
      }
    }
    if (!profile.includes("<ca>")) {
      throw new Error(
        "OpenVPN profile is missing embedded CA certificate. Upgrade gateway script and run sync-clients, then retry."
      );
    }
    if (isLocalRelayMode()) {
      try {
        await fs.mkdir(getExportsDir(), { recursive: true });
        await fs.writeFile(profilePath, profile, { encoding: "utf8", mode: 0o640 });
      } catch {
        // Best-effort cache only; the generated profile above is still returned.
      }
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

  public async exportBackup(_session: OmniSession): Promise<ProtocolBackupPayload> {
    const clients = await readClients();
    const payload = {
      protocolId: this.protocolId,
      exportedAt: new Date().toISOString(),
      clients
    };
    const body = new TextEncoder().encode(`${JSON.stringify(payload, null, 2)}\n`);
    return {
      fileName: `openvpn-clients-backup-${new Date().toISOString().replace(/[:.]/g, "-")}.json`,
      contentType: "application/json",
      body
    };
  }

  public async importBackup(_session: OmniSession, input: ProtocolBackupInput): Promise<void> {
    const raw = new TextDecoder().decode(input.body);
    let parsed: unknown;
    try {
      parsed = JSON.parse(raw);
    } catch {
      throw new Error("OpenVPN backup file is not valid JSON.");
    }

    const root = parsed as Record<string, unknown>;
    const clientsRaw = Array.isArray(root.clients) ? root.clients : null;
    if (!clientsRaw) {
      throw new Error("OpenVPN backup is missing 'clients' array.");
    }

    const imported: OpenVpnClientRecord[] = [];
    for (const item of clientsRaw) {
      if (typeof item !== "object" || item === null) {
        continue;
      }

      const row = item as Record<string, unknown>;
      const id = String(row.id ?? "").trim();
      const email = String(row.email ?? "").trim();
      const username = String(row.username ?? "").trim();
      const password = String(row.password ?? "").trim();
      if (!id || !email || !username || !password) {
        continue;
      }

      imported.push({
        id,
        email,
        enable: Boolean(row.enable ?? true),
        username,
        password,
        totalGB: normalizeTotalGB(row.totalGB),
        expiryTime: normalizeExpiryTime(row.expiryTime),
        speedLimitKbps: normalizeSpeedLimitKbps(row.speedLimitKbps)
      });
    }

    const dbCandidates = getAccountingDbCandidates();
    for (const client of imported) {
      await upsertProtocolClientToDb(
        "openvpn_tcp_singbox",
        {
          id: client.id,
          email: client.email,
          enable: client.enable,
          totalGB: client.totalGB,
          expiryTime: client.expiryTime,
          speedLimitKbps: client.speedLimitKbps,
          authUsername: client.username,
          authSecret: client.password
        },
        dbCandidates
      );
    }
    await syncOpenVpn();
  }
}
