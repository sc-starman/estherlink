import { promises as fs } from "node:fs";
import { dirname } from "node:path";

export type ProtocolFieldType = "text" | "number" | "password";

export interface ProtocolEditableField {
  key: string;
  label: string;
  type: ProtocolFieldType;
  required: boolean;
  value: string;
  helpText?: string;
}

export interface ProtocolConfigApiPayload {
  protocolId: string;
  fields: ProtocolEditableField[];
}

interface ProtocolConfigStore {
  version: 1;
  protocols: Record<string, Record<string, string>>;
}

const DEFAULT_STORE_PATH = "/etc/omnirelay/gateway/protocol-config.json";

type FieldSpec = Omit<ProtocolEditableField, "value">;

function normalizeProtocolId(protocolId: string): string {
  const raw = normalize(protocolId).toLowerCase();
  if (raw === "openvpn_local_tcp") return "openvpn_tcp_singbox";
  if (raw === "shadowsocks_local") return "shadowsocks_singbox";
  if (
    raw === "vless_tls_singbox" ||
    raw === "vless_local_tcp_plain" ||
    raw === "mixed_singbox" ||
    raw === "socks_singbox" ||
    raw === "http_singbox" ||
    raw === "hysteria2_singbox" ||
    raw === "trojan_singbox" ||
    raw === "naive_singbox"
  ) {
    return "vless_tls_singbox";
  }
  return raw || "vless_tls_singbox";
}

function getStorePath(): string {
  return process.env.OMNIPANEL_PROTOCOL_CONFIG_FILE?.trim() || DEFAULT_STORE_PATH;
}

function normalize(raw: string | undefined): string {
  return String(raw ?? "").trim();
}

function getFieldSpecs(protocolId: string): FieldSpec[] {
  if (protocolId === "openvpn_tcp_singbox") {
    return [
      { key: "publicHost", label: "Public Host", type: "text", required: true, helpText: "Shown in generated client profile links." },
      { key: "publicPort", label: "Public Port", type: "number", required: true, helpText: "Port shown in generated client profiles." }
    ];
  }

  if (protocolId === "shadowsocks_singbox") {
    return [
      { key: "publicHost", label: "Public Host", type: "text", required: true, helpText: "Server hostname/IP for generated links." },
      { key: "publicPort", label: "Public Port", type: "number", required: true, helpText: "Server port for generated links." },
      { key: "serverPassword", label: "Server Password", type: "password", required: true, helpText: "Required for 2022 Shadowsocks links." }
    ];
  }

  if (protocolId === "shadowtls_v3_shadowsocks_singbox") {
    return [
      { key: "publicHost", label: "Public Host", type: "text", required: true, helpText: "Server hostname/IP for generated links." },
      { key: "publicPort", label: "Public Port", type: "number", required: true, helpText: "Server port for generated links." },
      { key: "camouflageServer", label: "Camouflage Server", type: "text", required: true, helpText: "Example: www.apple.com:443" }
    ];
  }

  if (protocolId === "ipsec_l2tp_singbox") {
    return [{ key: "publicHost", label: "Public Host", type: "text", required: true, helpText: "Server hostname/IP for IPSec manual config." }];
  }

  return [
    { key: "publicHost", label: "Public Host", type: "text", required: true, helpText: "Server hostname/IP for generated links." },
    { key: "publicPort", label: "Public Port", type: "number", required: true, helpText: "Server port for generated links." }
  ];
}

async function readStore(): Promise<ProtocolConfigStore> {
  try {
    const raw = await fs.readFile(getStorePath(), "utf8");
    const parsed = JSON.parse(raw) as Partial<ProtocolConfigStore>;
    const protocols = parsed.protocols && typeof parsed.protocols === "object" ? parsed.protocols : {};
    return { version: 1, protocols: protocols as Record<string, Record<string, string>> };
  } catch {
    return { version: 1, protocols: {} };
  }
}

async function writeStore(store: ProtocolConfigStore): Promise<void> {
  const path = getStorePath();
  await fs.mkdir(dirname(path), { recursive: true });
  const tmp = `${path}.tmp`;
  await fs.writeFile(tmp, `${JSON.stringify(store, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
  await fs.rename(tmp, path);
}

function parsePortOrThrow(raw: string, label: string): string {
  const parsed = Number.parseInt(raw, 10);
  if (!Number.isFinite(parsed) || parsed <= 0 || parsed > 65535) {
    throw new Error(`${label} must be a valid port between 1 and 65535.`);
  }
  return String(parsed);
}

function validateAndNormalize(protocolId: string, values: Record<string, unknown>): Record<string, string> {
  const specs = getFieldSpecs(protocolId);
  const output: Record<string, string> = {};
  for (const spec of specs) {
    const raw = normalize(typeof values[spec.key] === "string" ? (values[spec.key] as string) : String(values[spec.key] ?? ""));
    if (spec.required && !raw) {
      throw new Error(`${spec.label} is required.`);
    }
    if (!raw) {
      output[spec.key] = "";
      continue;
    }
    if (spec.type === "number") {
      output[spec.key] = parsePortOrThrow(raw, spec.label);
      continue;
    }
    output[spec.key] = raw;
  }
  return output;
}

function getEnvFallback(protocolId: string, key: string): string {
  if (key === "publicHost") {
    return normalize(process.env.PANEL_PUBLIC_HOST) || normalize(process.env.OPENVPN_PUBLIC_HOST) || normalize(process.env.OPENVPN_HOST);
  }
  if (key === "publicPort") {
    if (protocolId === "openvpn_tcp_singbox") {
      return normalize(process.env.OPENVPN_PUBLIC_PORT) || normalize(process.env.SINGBOX_PUBLIC_PORT) || normalize(process.env.PANEL_PUBLIC_PORT) || "443";
    }
    if (protocolId === "shadowtls_v3_shadowsocks_singbox") {
      return normalize(process.env.SHADOWTLS_PUBLIC_PORT) || normalize(process.env.SINGBOX_PUBLIC_PORT) || "443";
    }
    return normalize(process.env.SINGBOX_PUBLIC_PORT) || "443";
  }
  if (key === "serverPassword") {
    return normalize(process.env.SINGBOX_SHADOWSOCKS_SERVER_PASSWORD);
  }
  if (key === "camouflageServer") {
    return normalize(process.env.SHADOWTLS_CAMOUFLAGE_SERVER) || "www.apple.com:443";
  }
  return "";
}

export async function getProtocolConfigApiPayload(protocolId: string): Promise<ProtocolConfigApiPayload> {
  const normalizedProtocolId = normalizeProtocolId(protocolId);
  const store = await readStore();
  const stored = store.protocols[normalizedProtocolId] ?? {};
  const fields = getFieldSpecs(normalizedProtocolId).map((spec) => ({
    ...spec,
    value: normalize(stored[spec.key] ?? getEnvFallback(normalizedProtocolId, spec.key))
  }));
  return { protocolId: normalizedProtocolId, fields };
}

export async function updateProtocolConfig(protocolId: string, values: Record<string, unknown>): Promise<ProtocolConfigApiPayload> {
  const normalizedProtocolId = normalizeProtocolId(protocolId);
  const normalized = validateAndNormalize(normalizedProtocolId, values);
  const store = await readStore();
  store.protocols[normalizedProtocolId] = normalized;
  await writeStore(store);
  return getProtocolConfigApiPayload(normalizedProtocolId);
}

export async function resolveProtocolConfigString(
  protocolId: string,
  key: string,
  envValue: string | undefined,
  fallback: string
): Promise<string> {
  const normalizedProtocolId = normalizeProtocolId(protocolId);
  const store = await readStore();
  const stored = normalize(store.protocols[normalizedProtocolId]?.[key]);
  if (stored) {
    return stored;
  }
  const env = normalize(envValue);
  if (env) {
    return env;
  }
  return fallback;
}

export async function resolveProtocolConfigPort(
  protocolId: string,
  key: string,
  envValue: string | undefined,
  fallback: number
): Promise<number> {
  const resolved = await resolveProtocolConfigString(protocolId, key, envValue, String(fallback));
  const parsed = Number.parseInt(resolved, 10);
  if (!Number.isFinite(parsed) || parsed <= 0 || parsed > 65535) {
    return fallback;
  }
  return parsed;
}
