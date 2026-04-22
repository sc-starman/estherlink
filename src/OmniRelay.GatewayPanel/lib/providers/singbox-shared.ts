import { promises as fs } from "node:fs";
import { dirname } from "node:path";
import { exec as execCallback } from "node:child_process";
import { promisify } from "node:util";
import { type GatewayClientCreateOptions } from "@/lib/providers/types";

const exec = promisify(execCallback);
const DEFAULT_SYNC_COMMAND = "/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients";
const DEFAULT_ACCOUNTING_DB = "/etc/omnirelay/gateway/connector/accounting.db";
const LEGACY_ACCOUNTING_DB = "/etc/omnirelay/gateway/singbox/accounting.db";

export const SINGBOX_ACCOUNTING_CAPABILITIES = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true
} as const;

export interface SingboxManagedClient {
  id: string;
  email: string;
  enable: boolean;
  totalGB: number;
  expiryTime: number;
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

export function normalizeClientOptions(options?: GatewayClientCreateOptions): Required<GatewayClientCreateOptions> {
  return {
    totalGB: normalizeTotalGB(options?.totalGB),
    expiryTime: normalizeExpiryTime(options?.expiryTime)
  };
}

export function getAccountingDbPath(): string {
  return process.env.SINGBOX_ACCOUNTING_DB?.trim() || DEFAULT_ACCOUNTING_DB;
}

async function resolveAccountingDbPath(): Promise<string> {
  const envPath = process.env.SINGBOX_ACCOUNTING_DB?.trim();
  const candidates = [envPath, DEFAULT_ACCOUNTING_DB, LEGACY_ACCOUNTING_DB].filter((value): value is string => Boolean(value));
  for (const candidate of candidates) {
    try {
      await fs.access(candidate);
      return candidate;
    } catch {
      // try next candidate
    }
  }
  return envPath || DEFAULT_ACCOUNTING_DB;
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
  const resolved = normalizeSudoCommand((command ?? process.env.SINGBOX_RELOAD_COMMAND ?? DEFAULT_SYNC_COMMAND).trim());
  if (!resolved) {
    return;
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
        enable: Boolean((item as Record<string, unknown>).enable ?? true),
        totalGB: normalizeTotalGB((item as Record<string, unknown>).totalGB),
        expiryTime: normalizeExpiryTime((item as Record<string, unknown>).expiryTime),
        ...(item as Record<string, unknown>)
      }))
      .filter((item) => item.id && item.email);
  } catch {
    return [];
  }
}

export async function writeClientFile(filePath: string, clients: SingboxManagedClient[]): Promise<void> {
  await fs.mkdir(dirname(filePath), { recursive: true });
  const tempPath = `${filePath}.tmp`;
  const sorted = [...clients].sort((left, right) => left.email.localeCompare(right.email));
  await fs.writeFile(tempPath, `${JSON.stringify(sorted, null, 2)}\n`, { encoding: "utf8", mode: 0o640 });
  await fs.rename(tempPath, filePath);
}

export async function readUsageByClientIds(clientIds: string[]): Promise<Map<string, number>> {
  if (clientIds.length === 0) {
    return new Map();
  }
  const dbPath = await resolveAccountingDbPath();
  const quotedIds = clientIds.map((id) => `'${id.replace(/'/g, "''")}'`).join(",");
  if (!quotedIds) {
    return new Map();
  }

  try {
    const { stdout } = await exec(
      `sqlite3 -csv -noheader -cmd ".timeout 5000" -cmd "PRAGMA query_only=ON;" "${dbPath}" "SELECT c.client_id, COALESCE(u.used_bytes, 0) FROM clients c LEFT JOIN usage_totals u ON u.client_id = c.client_id WHERE c.client_id IN (${quotedIds});"`
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
