import { type OmniSession } from "@/lib/session";
import { xuiRequest } from "@/lib/xui";
import { type GatewayClientRecord, type GatewayProtocolCapabilities } from "@/lib/providers/types";

const XUI_TRAFFIC_ENDPOINTS = [
  "/panel/api/inbounds/getClientTraffics/",
  "/panel/api/inbounds/clientTraffics/"
];

export const XUI_ACCOUNTING_CAPABILITIES: GatewayProtocolCapabilities = {
  supportsTrafficLimit: true,
  supportsDurationLimit: true,
  supportsUsageAccounting: true
};

function toFiniteNumber(value: unknown): number | null {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }

  if (typeof value === "string") {
    const parsed = Number(value.trim());
    if (Number.isFinite(parsed)) {
      return parsed;
    }
  }

  return null;
}

function toNonNegative(value: unknown, fallback = 0): number {
  const parsed = toFiniteNumber(value);
  if (parsed === null || parsed < 0) {
    return fallback;
  }

  return parsed;
}

function getRecordValue(record: Record<string, unknown>, keys: string[]): unknown {
  for (const key of keys) {
    if (key in record) {
      return record[key];
    }
  }

  return undefined;
}

function readUsageBytes(item: Record<string, unknown>): number | null {
  const direct = toFiniteNumber(
    getRecordValue(item, ["usedBytes", "used", "usage", "traffic", "total", "totalBytes"])
  );
  if (direct !== null && direct >= 0) {
    return direct;
  }

  const up = toFiniteNumber(getRecordValue(item, ["up", "uplink", "upload", "upstream"]));
  const down = toFiniteNumber(getRecordValue(item, ["down", "downlink", "download", "downstream"]));
  if (up === null && down === null) {
    return null;
  }

  return Math.max(0, (up ?? 0) + (down ?? 0));
}

function extractItems(payload: unknown): Record<string, unknown>[] {
  if (Array.isArray(payload)) {
    return payload.filter((item): item is Record<string, unknown> => typeof item === "object" && item !== null);
  }

  if (typeof payload === "string" && payload.trim()) {
    try {
      return extractItems(JSON.parse(payload));
    } catch {
      return [];
    }
  }

  if (typeof payload !== "object" || payload === null) {
    return [];
  }

  const record = payload as Record<string, unknown>;
  const nestedKeys = ["clientStats", "clients", "list", "items", "traffics", "stats", "data", "obj"];
  for (const key of nestedKeys) {
    if (key in record) {
      const nested = extractItems(record[key]);
      if (nested.length > 0) {
        return nested;
      }
    }
  }

  return [];
}

function parseUsageMaps(
  payload: unknown,
  byId: Map<string, number | null>,
  byEmail: Map<string, number | null>
): void {
  for (const item of extractItems(payload)) {
    const id = String(getRecordValue(item, ["id", "clientId", "uuid"]) ?? "").trim();
    const email = String(getRecordValue(item, ["email", "clientEmail", "remark"]) ?? "").trim().toLowerCase();
    const used = readUsageBytes(item);

    if (id) {
      byId.set(id, used);
    }

    if (email) {
      byEmail.set(email, used);
    }
  }
}

async function fetchEndpoint(
  session: OmniSession,
  endpoint: string,
  byId: Map<string, number | null>,
  byEmail: Map<string, number | null>
): Promise<void> {
  const response = await xuiRequest(session, endpoint);
  if (!response.ok) {
    return;
  }

  const text = await response.text();
  if (!text.trim()) {
    return;
  }

  let payload: unknown;
  try {
    payload = JSON.parse(text);
  } catch {
    return;
  }

  if (typeof payload === "object" && payload !== null) {
    const record = payload as Record<string, unknown>;
    if (record.success === false) {
      return;
    }
    parseUsageMaps(record.obj ?? record, byId, byEmail);
    return;
  }

  parseUsageMaps(payload, byId, byEmail);
}

export async function fetchXuiClientUsage(
  session: OmniSession,
  inboundId: string,
  inboundObject: Record<string, unknown>
): Promise<{ byId: Map<string, number | null>; byEmail: Map<string, number | null> }> {
  const byId = new Map<string, number | null>();
  const byEmail = new Map<string, number | null>();

  parseUsageMaps(inboundObject, byId, byEmail);

  for (const prefix of XUI_TRAFFIC_ENDPOINTS) {
    try {
      await fetchEndpoint(session, `${prefix}${encodeURIComponent(inboundId)}`, byId, byEmail);
      if (byId.size > 0 || byEmail.size > 0) {
        break;
      }
    } catch {
      // Best-effort accounting: missing endpoint/version mismatch should not break panel.
    }
  }

  return { byId, byEmail };
}

export function applyXuiClientAccounting(
  clients: GatewayClientRecord[],
  usageById: Map<string, number | null>,
  usageByEmail: Map<string, number | null>
): GatewayClientRecord[] {
  return clients.map((client) => {
    const id = String(client.id ?? "").trim();
    const email = String(client.email ?? "").trim().toLowerCase();

    let usedBytes: number | null = null;
    if (id && usageById.has(id)) {
      usedBytes = usageById.get(id) ?? null;
    } else if (email && usageByEmail.has(email)) {
      usedBytes = usageByEmail.get(email) ?? null;
    }

    return {
      ...client,
      totalGB: toNonNegative(client.totalGB, 0),
      expiryTime: toNonNegative(client.expiryTime, 0),
      usedBytes
    };
  });
}

