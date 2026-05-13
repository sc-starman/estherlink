"use client";

import Image from "next/image";
import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { copyToClipboard } from "@/lib/clipboard";

interface InboundClient {
  id: string;
  email: string;
  enable: boolean;
  flow?: string;
  totalGB?: number;
  expiryTime?: number;
  speedLimitKbps?: number;
  usedBytes?: number | null;
  lastSeenAtUnixMs?: number;
  activeConnections?: number;
  isOnline?: boolean;
  [key: string]: unknown;
}

interface ProtocolCapabilities {
  supportsTrafficLimit: boolean;
  supportsDurationLimit: boolean;
  supportsUsageAccounting: boolean;
  supportsSpeedLimit?: boolean;
  supportsOnlineStatus?: boolean;
  supportsClientLifecycle?: boolean;
}

interface InboundResponse {
  inbound: {
    id: number;
    protocol: string;
    port: number;
    remark: string;
    enable: boolean;
  };
  clients: InboundClient[];
  capabilities?: ProtocolCapabilities;
}

interface ShadowsocksDecodedConfig {
  method: string;
  password: string;
  server: string;
  port: number;
}

type ConfigPayload =
  | {
      mode: "qr";
      uri: string;
      qrCodeDataUrl: string;
      title?: string;
    }
  | {
      mode: "ipsec_manual";
      uri: string;
      title?: string;
      fields: {
        server: string;
        ports: string[];
        username: string;
        password: string;
        preSharedKey: string;
      };
      setupSteps: string[];
    }
  | {
      mode: "openvpn_bundle";
      uri: string;
      title?: string;
      username: string;
      password: string;
      privateKeyPassphrase: string;
      ovpnFileName: string;
      ovpnContent: string;
    };

interface ProtocolConfigField {
  key: string;
  label: string;
  type: "text" | "number" | "password";
  required: boolean;
  value: string;
  helpText?: string;
}

interface ProtocolConfigPayload {
  protocolId: string;
  fields: ProtocolConfigField[];
  message?: string;
}

const DEFAULT_CAPABILITIES: ProtocolCapabilities = {
  supportsTrafficLimit: false,
  supportsDurationLimit: false,
  supportsUsageAccounting: false,
  supportsSpeedLimit: false,
  supportsOnlineStatus: false,
  supportsClientLifecycle: false
};

function toDateTimeLocal(unixMs: number): string {
  if (!Number.isFinite(unixMs) || unixMs <= 0) {
    return "";
  }

  const value = new Date(unixMs);
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, "0");
  const day = String(value.getDate()).padStart(2, "0");
  const hours = String(value.getHours()).padStart(2, "0");
  const minutes = String(value.getMinutes()).padStart(2, "0");
  return `${year}-${month}-${day}T${hours}:${minutes}`;
}

function parseDateTimeLocal(value: string): number {
  if (!value.trim()) {
    return 0;
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return 0;
  }

  return parsed.getTime();
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return "N/A";
  }

  if (bytes < 1024) {
    return `${Math.round(bytes)} B`;
  }

  const units = ["KB", "MB", "GB", "TB", "PB"];
  let value = bytes / 1024;
  let idx = 0;
  while (value >= 1024 && idx < units.length - 1) {
    value /= 1024;
    idx += 1;
  }
  return `${value.toFixed(value >= 100 ? 0 : value >= 10 ? 1 : 2)} ${units[idx]}`;
}

function formatRemainingDuration(unixMs: number, nowMs: number): string {
  if (!Number.isFinite(unixMs) || unixMs <= 0) {
    return "Never";
  }

  const diff = unixMs - nowMs;
  if (diff <= 0) {
    return "Expired";
  }

  const totalMinutes = Math.floor(diff / 60000);
  const days = Math.floor(totalMinutes / (24 * 60));
  const hours = Math.floor((totalMinutes % (24 * 60)) / 60);
  const minutes = totalMinutes % 60;

  if (days > 0) {
    return `${days}d ${hours}h`;
  }
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  return `${Math.max(1, minutes)}m`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function decodeBase64Url(input: string): string | null {
  const normalized = input.trim().replace(/-/g, "+").replace(/_/g, "/").replace(/\s+/g, "");
  if (!normalized) {
    return null;
  }

  const padLength = (4 - (normalized.length % 4)) % 4;
  const padded = `${normalized}${"=".repeat(padLength)}`;
  try {
    return atob(padded);
  } catch {
    return null;
  }
}

function safeDecodeURIComponent(input: string): string {
  try {
    return decodeURIComponent(input);
  } catch {
    return input;
  }
}

function parseEndpoint(endpoint: string): { server: string; port: number } | null {
  const trimmed = endpoint.trim();
  if (!trimmed) {
    return null;
  }

  if (trimmed.startsWith("[")) {
    const end = trimmed.indexOf("]");
    if (end <= 1) {
      return null;
    }
    const server = trimmed.slice(0, end + 1);
    const portPart = trimmed.slice(end + 1);
    if (!portPart.startsWith(":")) {
      return null;
    }
    const port = Number.parseInt(portPart.slice(1), 10);
    if (!Number.isFinite(port) || port <= 0) {
      return null;
    }
    return { server, port };
  }

  const idx = trimmed.lastIndexOf(":");
  if (idx <= 0) {
    return null;
  }
  const server = trimmed.slice(0, idx);
  const port = Number.parseInt(trimmed.slice(idx + 1), 10);
  if (!server || !Number.isFinite(port) || port <= 0) {
    return null;
  }
  return { server, port };
}

function parseShadowsocksUri(uri: string): ShadowsocksDecodedConfig | null {
  if (!uri.startsWith("ss://")) {
    return null;
  }

  const withoutScheme = uri.slice(5);
  const withoutFragment = withoutScheme.split("#", 1)[0] ?? "";
  if (!withoutFragment) {
    return null;
  }

  // SIP002 form: ss://BASE64(method:password)@host:port
  if (withoutFragment.includes("@")) {
    const atIndex = withoutFragment.indexOf("@");
    const userInfoRaw = withoutFragment.slice(0, atIndex);
    const endpointRaw = withoutFragment.slice(atIndex + 1).split(/[/?]/, 1)[0] ?? "";
    const endpoint = parseEndpoint(endpointRaw);
    if (!endpoint) {
      return null;
    }
    const userInfoDecoded = safeDecodeURIComponent(userInfoRaw);
    const decoded = decodeBase64Url(userInfoDecoded) ?? userInfoDecoded;
    const colonIndex = decoded.indexOf(":");
    if (colonIndex <= 0) {
      return null;
    }
    const method = decoded.slice(0, colonIndex);
    const password = decoded.slice(colonIndex + 1);
    if (!method || !password) {
      return null;
    }
    return { method, password, server: endpoint.server, port: endpoint.port };
  }

  // Legacy form: ss://BASE64(method:password@host:port)
  const decodedLegacy = decodeBase64Url(safeDecodeURIComponent(withoutFragment));
  if (!decodedLegacy) {
    return null;
  }
  const atIndex = decodedLegacy.lastIndexOf("@");
  if (atIndex <= 0) {
    return null;
  }
  const credentials = decodedLegacy.slice(0, atIndex);
  const endpoint = parseEndpoint(decodedLegacy.slice(atIndex + 1));
  if (!endpoint) {
    return null;
  }
  const colonIndex = credentials.indexOf(":");
  if (colonIndex <= 0) {
    return null;
  }
  const method = credentials.slice(0, colonIndex);
  const password = credentials.slice(colonIndex + 1);
  if (!method || !password) {
    return null;
  }
  return { method, password, server: endpoint.server, port: endpoint.port };
}

function normalizeConfigPayload(raw: unknown): ConfigPayload {
  if (!isRecord(raw)) {
    throw new Error("Invalid client config payload.");
  }

  const mode = String(raw.mode ?? "").trim();
  if (mode === "qr") {
    const uri = String(raw.uri ?? "");
    const qrCodeDataUrl = String(raw.qrCodeDataUrl ?? "");
    if (!uri || !qrCodeDataUrl) {
      throw new Error("QR config payload is incomplete.");
    }
    return {
      mode: "qr",
      uri,
      qrCodeDataUrl,
      title: String(raw.title ?? "")
    };
  }

  if (mode === "ipsec_manual") {
    const fields = isRecord(raw.fields) ? raw.fields : {};
    const setupStepsRaw = Array.isArray(raw.setupSteps) ? raw.setupSteps : [];
    return {
      mode: "ipsec_manual",
      uri: String(raw.uri ?? ""),
      title: String(raw.title ?? ""),
      fields: {
        server: String(fields.server ?? ""),
        ports: Array.isArray(fields.ports) ? fields.ports.map((item) => String(item)) : [],
        username: String(fields.username ?? ""),
        password: String(fields.password ?? ""),
        preSharedKey: String(fields.preSharedKey ?? "")
      },
      setupSteps: setupStepsRaw.map((step) => String(step))
    };
  }

  if (mode === "openvpn_bundle") {
    return {
      mode: "openvpn_bundle",
      uri: String(raw.uri ?? ""),
      title: String(raw.title ?? ""),
      username: String(raw.username ?? ""),
      password: String(raw.password ?? ""),
      privateKeyPassphrase: String(raw.privateKeyPassphrase ?? "not set"),
      ovpnFileName: String(raw.ovpnFileName ?? "omnirelay-client.ovpn"),
      ovpnContent: String(raw.ovpnContent ?? "")
    };
  }

  // Backward compatibility for older panel responses.
  const legacyUri = String(raw.uri ?? "");
  const legacyQr = String(raw.qrCodeDataUrl ?? "");
  if (legacyUri && legacyQr) {
    return {
      mode: "qr",
      uri: legacyUri,
      qrCodeDataUrl: legacyQr,
      title: "Client Config"
    };
  }

  throw new Error("Unsupported config format for this protocol.");
}

type DialogMode = "add" | "edit";

export function PanelClient() {
  const router = useRouter();
  const [clients, setClients] = useState<InboundClient[]>([]);
  const [capabilities, setCapabilities] = useState<ProtocolCapabilities>(DEFAULT_CAPABILITIES);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [selectedConfig, setSelectedConfig] = useState<ConfigPayload | null>(null);
  const [toastMessage, setToastMessage] = useState("");
  const [toastTone, setToastTone] = useState<"success" | "error">("success");
  const [dialogMode, setDialogMode] = useState<DialogMode | null>(null);
  const [editingClientId, setEditingClientId] = useState<string>("");
  const [formEmail, setFormEmail] = useState("");
  const [formTotalGB, setFormTotalGB] = useState("0");
  const [formExpiry, setFormExpiry] = useState("");
  const [formSpeedLimitKbps, setFormSpeedLimitKbps] = useState("0");
  const [submitting, setSubmitting] = useState(false);
  const [pendingToggleClientId, setPendingToggleClientId] = useState<string>("");
  const [pendingDeleteClientId, setPendingDeleteClientId] = useState<string>("");
  const [importingClients, setImportingClients] = useState(false);
  const [protocolConfigOpen, setProtocolConfigOpen] = useState(false);
  const [protocolConfigProtocolId, setProtocolConfigProtocolId] = useState("");
  const [protocolConfigFields, setProtocolConfigFields] = useState<ProtocolConfigField[]>([]);
  const [protocolConfigLoading, setProtocolConfigLoading] = useState(false);
  const [protocolConfigSaving, setProtocolConfigSaving] = useState(false);
  const [nowMs, setNowMs] = useState(Date.now());
  const importFileInputRef = useRef<HTMLInputElement | null>(null);
  const toastTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const timer = setInterval(() => setNowMs(Date.now()), 60_000);
    return () => clearInterval(timer);
  }, []);

  useEffect(() => {
    return () => {
      if (toastTimerRef.current) {
        clearTimeout(toastTimerRef.current);
        toastTimerRef.current = null;
      }
    };
  }, []);

  const activeClients = useMemo(() => clients.filter((client) => client.enable).length, [clients]);
  const totalTrafficBytes = useMemo(
    () =>
      clients.reduce((sum, client) => {
        const used = Number(client.usedBytes ?? 0);
        return Number.isFinite(used) && used > 0 ? sum + used : sum;
      }, 0),
    [clients]
  );
  const decodedShadowsocksConfig = useMemo(() => {
    if (!selectedConfig || selectedConfig.mode !== "qr") {
      return null;
    }
    return parseShadowsocksUri(selectedConfig.uri);
  }, [selectedConfig]);
  const editingClient = useMemo(
    () => clients.find((item) => item.id === editingClientId) ?? null,
    [clients, editingClientId]
  );
  const supportsClientLifecycle = capabilities.supportsClientLifecycle !== false;

  function showToast(message: string, tone: "success" | "error" = "success") {
    if (toastTimerRef.current) {
      clearTimeout(toastTimerRef.current);
      toastTimerRef.current = null;
    }
    setToastTone(tone);
    setToastMessage(message);
    toastTimerRef.current = setTimeout(() => {
      setToastMessage("");
      toastTimerRef.current = null;
    }, 1800);
  }

  async function loadInbound() {
    setLoading(true);
    setError("");

    try {
      const response = await fetch("/api/inbound", { cache: "no-store" });
      if (response.status === 401) {
        router.replace("/login");
        return;
      }

      const payload = (await response.json()) as InboundResponse & { message?: string };
      if (!response.ok) {
        setError(payload.message ?? "Failed to load clients.");
        return;
      }

      setClients(payload.clients ?? []);
      setCapabilities(payload.capabilities ?? DEFAULT_CAPABILITIES);
    } catch {
      setError("Failed to load clients.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void loadInbound();
  }, []);

  function closeClientDialog() {
    if (submitting) {
      return;
    }

    setDialogMode(null);
    setEditingClientId("");
    setFormEmail("");
    setFormTotalGB("0");
    setFormExpiry("");
    setFormSpeedLimitKbps("0");
  }

  function openAddDialog() {
    if (!supportsClientLifecycle) {
      return;
    }
    setError("");
    setDialogMode("add");
    setEditingClientId("");
    setFormEmail("");
    setFormTotalGB("0");
    setFormExpiry("");
    setFormSpeedLimitKbps("0");
  }

  function openEditDialog(client: InboundClient) {
    if (!supportsClientLifecycle) {
      return;
    }
    setError("");
    setDialogMode("edit");
    setEditingClientId(client.id);
    setFormEmail(String(client.email ?? ""));
    setFormTotalGB(String(Number(client.totalGB ?? 0)));
    setFormExpiry(toDateTimeLocal(Number(client.expiryTime ?? 0)));
    setFormSpeedLimitKbps(String(Number(client.speedLimitKbps ?? 0)));
  }

  async function submitClientDialog() {
    if (!supportsClientLifecycle) {
      return;
    }
    setError("");
    const email = formEmail.trim();
    if (!email) {
      setError("Client memo/email is required.");
      return;
    }

    const totalGB = Number(formTotalGB || "0");
    if (capabilities.supportsTrafficLimit && (!Number.isFinite(totalGB) || totalGB < 0)) {
      setError("Traffic limit must be a non-negative number.");
      return;
    }

    const expiryTime = capabilities.supportsDurationLimit ? parseDateTimeLocal(formExpiry) : 0;
    const speedLimitKbps = Number(formSpeedLimitKbps || "0");
    if (capabilities.supportsSpeedLimit && (!Number.isFinite(speedLimitKbps) || speedLimitKbps < 0)) {
      setError("Speed limit must be a non-negative integer.");
      return;
    }

    setSubmitting(true);
    try {
      if (dialogMode === "add") {
        const response = await fetch("/api/client/add", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            email,
            totalGB: capabilities.supportsTrafficLimit ? totalGB : undefined,
            expiryTime: capabilities.supportsDurationLimit ? expiryTime : undefined,
            speedLimitKbps: capabilities.supportsSpeedLimit ? Math.trunc(speedLimitKbps) : undefined
          })
        });
        const payload = (await response.json()) as { message?: string };
        if (!response.ok) {
          setError(payload.message ?? "Failed to add client.");
          return;
        }
      } else if (dialogMode === "edit" && editingClient) {
        const response = await fetch("/api/client/update", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            client: {
              ...editingClient,
              email,
              totalGB: capabilities.supportsTrafficLimit ? totalGB : undefined,
              expiryTime: capabilities.supportsDurationLimit ? expiryTime : undefined,
              speedLimitKbps: capabilities.supportsSpeedLimit ? Math.trunc(speedLimitKbps) : undefined
            }
          })
        });
        const payload = (await response.json()) as { message?: string };
        if (!response.ok) {
          setError(payload.message ?? "Failed to update client.");
          return;
        }
      }

      closeClientDialog();
      await loadInbound();
    } finally {
      setSubmitting(false);
    }
  }

  async function exportClients() {
    if (!supportsClientLifecycle) {
      return;
    }
    setError("");
    try {
      const response = await fetch("/api/client/export", { cache: "no-store" });
      if (!response.ok) {
        const payload = (await response.json().catch(() => ({}))) as { message?: string };
        setError(payload.message ?? "Failed to export clients.");
        return;
      }

      const blob = await response.blob();
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      const disposition = response.headers.get("content-disposition") ?? "";
      const fileNameMatch = /filename="([^"]+)"/i.exec(disposition);
      anchor.href = url;
      anchor.download = fileNameMatch?.[1] ?? `clients-${new Date().toISOString().replace(/[:.]/g, "-")}.json`;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    } catch {
      setError("Failed to export clients.");
    }
  }

  async function importClients(file: File) {
    if (!supportsClientLifecycle) {
      return;
    }
    if (importingClients) {
      return;
    }
    setError("");
    setImportingClients(true);
    try {
      const formData = new FormData();
      formData.append("file", file);
      const response = await fetch("/api/client/import", {
        method: "POST",
        body: formData
      });
      const payload = (await response.json()) as { message?: string };
      if (!response.ok) {
        setError(payload.message ?? "Failed to import clients.");
        return;
      }
      await loadInbound();
    } catch {
      setError("Failed to import clients.");
    } finally {
      setImportingClients(false);
      if (importFileInputRef.current) {
        importFileInputRef.current.value = "";
      }
    }
  }

  async function updateClient(client: InboundClient) {
    if (!supportsClientLifecycle) {
      return;
    }
    if (!client.id || pendingToggleClientId || pendingDeleteClientId) {
      return;
    }
    setError("");
    setPendingToggleClientId(client.id);

    try {
      const response = await fetch("/api/client/update", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ client })
      });

      const payload = (await response.json()) as { message?: string };
      if (!response.ok) {
        setError(payload.message ?? "Failed to update client.");
        return;
      }

      await loadInbound();
    } finally {
      setPendingToggleClientId("");
    }
  }

  async function openProtocolConfigDialog() {
    if (protocolConfigLoading || protocolConfigSaving) {
      return;
    }
    setError("");
    setProtocolConfigLoading(true);
    setProtocolConfigOpen(true);
    try {
      const response = await fetch("/api/protocol-config", { cache: "no-store" });
      if (response.status === 401) {
        router.replace("/login");
        return;
      }
      const payload = (await response.json()) as ProtocolConfigPayload;
      if (!response.ok) {
        setError(payload.message ?? "Failed to load protocol config.");
        setProtocolConfigOpen(false);
        return;
      }
      setProtocolConfigProtocolId(String(payload.protocolId ?? ""));
      setProtocolConfigFields(Array.isArray(payload.fields) ? payload.fields : []);
    } catch {
      setError("Failed to load protocol config.");
      setProtocolConfigOpen(false);
    } finally {
      setProtocolConfigLoading(false);
    }
  }

  function updateProtocolConfigField(key: string, value: string) {
    setProtocolConfigFields((previous) =>
      previous.map((field) => (field.key === key ? { ...field, value } : field))
    );
  }

  function closeProtocolConfigDialog() {
    if (protocolConfigLoading || protocolConfigSaving) {
      return;
    }
    setProtocolConfigOpen(false);
    setProtocolConfigProtocolId("");
    setProtocolConfigFields([]);
  }

  async function saveProtocolConfig() {
    if (protocolConfigSaving || protocolConfigLoading) {
      return;
    }
    setError("");
    setProtocolConfigSaving(true);
    try {
      const values = Object.fromEntries(protocolConfigFields.map((field) => [field.key, field.value]));
      const response = await fetch("/api/protocol-config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ values })
      });
      const payload = (await response.json()) as ProtocolConfigPayload;
      if (!response.ok) {
        setError(payload.message ?? "Failed to save protocol config.");
        return;
      }
      setProtocolConfigProtocolId(String(payload.protocolId ?? protocolConfigProtocolId));
      setProtocolConfigFields(Array.isArray(payload.fields) ? payload.fields : []);
      showToast("Protocol config saved.", "success");
      setProtocolConfigOpen(false);
    } catch {
      setError("Failed to save protocol config.");
      showToast("Failed to save protocol config.", "error");
    } finally {
      setProtocolConfigSaving(false);
    }
  }

  async function deleteClient(uuid: string) {
    if (!supportsClientLifecycle) {
      return;
    }
    if (!uuid || pendingDeleteClientId || pendingToggleClientId) {
      return;
    }
    setError("");
    setPendingDeleteClientId(uuid);

    try {
      const response = await fetch("/api/client/delete", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ uuid })
      });

      const payload = (await response.json()) as { message?: string };
      if (!response.ok) {
        setError(payload.message ?? "Failed to remove client.");
        return;
      }

      await loadInbound();
    } finally {
      setPendingDeleteClientId("");
    }
  }

  async function showConfig(uuid: string) {
    if (!supportsClientLifecycle) {
      return;
    }
    setError("");

    const response = await fetch(`/api/client/config?uuid=${encodeURIComponent(uuid)}`, { cache: "no-store" });
    const payload = (await response.json()) as unknown;
    if (!response.ok) {
      const message = isRecord(payload) ? String(payload.message ?? "") : "";
      setError(message || "Failed to build config.");
      return;
    }

    try {
      const normalizedPayload = normalizeConfigPayload(payload);
      setSelectedConfig(normalizedPayload);
    } catch (error) {
      const message = error instanceof Error ? error.message : "Failed to parse client config payload.";
      setError(message);
      return;
    }

  }

  async function copyText(value: string, errorMessage: string, successMessage = "Copied.") {
    try {
      await copyToClipboard(value);
      showToast(successMessage, "success");
    } catch {
      setError(errorMessage);
      showToast(errorMessage, "error");
    }
  }

  async function copyConfigUri() {
    if (!selectedConfig) {
      return;
    }

    await copyText(selectedConfig.uri, "Failed to copy config.");
  }

  async function shareClientConfigPage(clientId: string) {
    if (!supportsClientLifecycle) {
      return;
    }
    const trimmed = String(clientId ?? "").trim();
    if (!trimmed) {
      setError("Invalid client id.");
      return;
    }

    const origin = typeof window !== "undefined" ? window.location.origin : "";
    let publicHost = "";
    try {
      const response = await fetch("/api/protocol-config", { cache: "no-store" });
      if (response.ok) {
        const payload = (await response.json()) as ProtocolConfigPayload;
        const hostField = Array.isArray(payload.fields) ? payload.fields.find((field) => field.key === "publicHost") : null;
        publicHost = String(hostField?.value ?? "").trim();
      }
    } catch {
      // Ignore and fall back to current origin.
    }

    let publicUrl = `${origin}/c/${encodeURIComponent(trimmed)}`;
    if (typeof window !== "undefined" && publicHost) {
      const protocol = window.location.protocol;
      const port = window.location.port;
      const includePort =
        (protocol === "https:" && port && port !== "443") ||
        (protocol === "http:" && port && port !== "80");
      const portPart = includePort ? `:${port}` : "";
      publicUrl = `${protocol}//${publicHost}${portPart}/c/${encodeURIComponent(trimmed)}`;
    }
    await copyText(publicUrl, "Failed to copy share link.", "Share link copied.");
  }

  function downloadOpenVpnConfig() {
    if (!selectedConfig || selectedConfig.mode !== "openvpn_bundle") {
      return;
    }

    const content = selectedConfig.ovpnContent || selectedConfig.uri;
    if (!content.trim()) {
      setError("OpenVPN profile content is empty.");
      return;
    }

    const blob = new Blob([content], { type: "application/x-openvpn-profile" });
    const objectUrl = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = objectUrl;
    anchor.download = selectedConfig.ovpnFileName || "omnirelay-client.ovpn";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(objectUrl);
  }

  async function logout() {
    await fetch("/api/auth/logout", { method: "POST" });
    router.replace("/login");
    router.refresh();
  }

  function renderTraffic(client: InboundClient): string {
    if (!capabilities.supportsTrafficLimit) {
      return "Not supported";
    }

    const totalGB = Number(client.totalGB ?? 0);
    const totalBytes = totalGB > 0 ? totalGB * 1024 * 1024 * 1024 : null;
    const totalText = totalBytes === null ? "∞" : formatBytes(totalBytes);

    if (!capabilities.supportsUsageAccounting) {
      return `Not supported / ${totalText}`;
    }

    const usedBytes = typeof client.usedBytes === "number" && Number.isFinite(client.usedBytes) ? client.usedBytes : null;
    const usedText = usedBytes === null ? "N/A" : formatBytes(usedBytes);
    return `${usedText} / ${totalText}`;
  }

  function renderDuration(client: InboundClient): string {
    if (!capabilities.supportsDurationLimit) {
      return "Not supported";
    }

    const expiryTime = Number(client.expiryTime ?? 0);
    return formatRemainingDuration(expiryTime, nowMs);
  }

  function renderOnlineState(client: InboundClient): string {
    if (!capabilities.supportsOnlineStatus) {
      return "Unknown";
    }

    if (client.isOnline === true) {
      return "Online";
    }
    return "Offline";
  }

  function renderLastOnline(client: InboundClient): string {
    if (!capabilities.supportsOnlineStatus) {
      return "Unknown";
    }

    const lastSeen = Number(client.lastSeenAtUnixMs ?? 0);
    if (!Number.isFinite(lastSeen) || lastSeen <= 0) {
      return "Never";
    }
    return new Date(lastSeen).toLocaleString();
  }

  return (
    <main className="mx-auto min-h-screen w-full max-w-6xl px-6 py-10">
      {toastMessage ? (
        <div className={`fixed right-5 top-5 z-[70] rounded-xl px-4 py-3 text-sm text-white shadow-lg ${toastTone === "error" ? "bg-rose-700" : "bg-emerald-700"}`}>
          {toastMessage}
        </div>
      ) : null}
      <section className="card p-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <p className="text-xs uppercase tracking-[0.35em] text-cyan-700">Gateway Panel</p>
            <div className="flex items-center gap-3">
              <Image src="/images/logo.png" width={40} height={40} alt="OmniPanel Logo" className="rounded-lg" />
              <h1 className="text-3xl font-semibold text-slate-900">OmniPanel</h1>
            </div>
            <p className="mt-1 text-sm text-slate-600">Manage gateway clients.</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Link href="/change-password" className="rounded-xl border border-slate-300 px-4 py-2 text-sm">
              Change Password
            </Link>
            <Link href="/api-docs" className="rounded-xl border border-slate-300 px-4 py-2 text-sm">
              API Docs
            </Link>
            <button className="rounded-xl bg-slate-900 px-4 py-2 text-sm text-white" onClick={() => void logout()}>
              Logout
            </button>
          </div>
        </div>

        <div className="mt-6 grid gap-4 sm:grid-cols-3">
          <div className="rounded-xl border border-slate-200 bg-slate-50 p-4">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-500">Total Clients</p>
            <p className="mt-2 text-2xl font-semibold text-slate-900">{clients.length}</p>
          </div>
          <div className="rounded-xl border border-slate-200 bg-slate-50 p-4">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-500">Enabled</p>
            <p className="mt-2 text-2xl font-semibold text-emerald-700">{activeClients}</p>
          </div>
          <div className="rounded-xl border border-slate-200 bg-slate-50 p-4">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-500">Total Traffic</p>
            <p className="mt-2 text-2xl font-semibold text-cyan-700">{formatBytes(totalTrafficBytes)}</p>
          </div>
        </div>

        <div className="mt-6 flex flex-wrap items-center gap-2">
          {supportsClientLifecycle ? (
            <button className="rounded-xl bg-cyan-600 px-4 py-2 text-sm font-medium text-white" onClick={openAddDialog}>
              Add Client
            </button>
          ) : null}
          <button className="rounded-xl border border-slate-300 px-4 py-2 text-sm" onClick={() => void loadInbound()}>
            Refresh
          </button>
          {supportsClientLifecycle ? (
            <>
              <button className="rounded-xl border border-slate-300 px-4 py-2 text-sm" onClick={() => void exportClients()}>
                Export Clients
              </button>
              <button
                className="rounded-xl border border-slate-300 px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
                onClick={() => importFileInputRef.current?.click()}
                disabled={importingClients}
              >
                {importingClients ? "Importing..." : "Import Clients"}
              </button>
            </>
          ) : null}
          <button
            className="rounded-xl border border-slate-300 px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            onClick={() => void openProtocolConfigDialog()}
            disabled={protocolConfigLoading || protocolConfigSaving}
          >
            {protocolConfigLoading ? "Loading..." : "Protocol Config"}
          </button>
          <input
            ref={importFileInputRef}
            type="file"
            accept="application/json,.json,application/gzip,.gz,.tgz,.tar.gz"
            className="hidden"
            onChange={(event) => {
              const file = event.target.files?.[0];
              if (file) {
                void importClients(file);
              }
            }}
          />
        </div>
        {!supportsClientLifecycle ? (
          <p className="mt-3 text-sm text-amber-700">
            This protocol uses a shared credential model. Per-client add/edit/delete/limit controls are disabled.
          </p>
        ) : null}

        {error ? <p className="mt-4 text-sm text-rose-600">{error}</p> : null}

        <div className="mt-6 overflow-x-auto rounded-xl border border-slate-200">
          <table className="min-w-full divide-y divide-slate-200 text-sm">
            <thead className="bg-slate-100 text-left text-slate-600">
              <tr>
                <th className="px-4 py-3">Email</th>
                <th className="px-4 py-3">State</th>
                {supportsClientLifecycle ? <th className="px-4 py-3">Traffic (Used / Total)</th> : null}
                {supportsClientLifecycle ? <th className="px-4 py-3">Speed</th> : null}
                {supportsClientLifecycle ? <th className="px-4 py-3">Duration</th> : null}
                <th className="px-4 py-3">Status</th>
                <th className="px-4 py-3">Last Online</th>
                {supportsClientLifecycle ? <th className="px-4 py-3">Actions</th> : null}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 bg-white">
              {loading ? (
                <tr>
                  <td className="px-4 py-4 text-slate-500" colSpan={supportsClientLifecycle ? 8 : 4}>
                    Loading clients...
                  </td>
                </tr>
              ) : clients.length === 0 ? (
                <tr>
                  <td className="px-4 py-4 text-slate-500" colSpan={supportsClientLifecycle ? 8 : 4}>
                    No clients found.
                  </td>
                </tr>
              ) : (
                clients.map((client) => (
                  <tr key={client.id}>
                    <td className="px-4 py-3">{client.email}</td>
                    <td className="px-4 py-3">
                      <span className={`rounded-full px-2 py-1 text-xs ${client.enable ? "bg-emerald-100 text-emerald-700" : "bg-amber-100 text-amber-700"}`}>
                        {client.enable ? "enabled" : "disabled"}
                      </span>
                    </td>
                    {supportsClientLifecycle ? <td className="px-4 py-3 text-slate-700">{renderTraffic(client)}</td> : null}
                    {supportsClientLifecycle ? (
                      <td className="px-4 py-3 text-slate-700">
                        {capabilities.supportsSpeedLimit ? `${Math.max(0, Math.trunc(Number(client.speedLimitKbps ?? 0)))} Kbps` : "Not supported"}
                      </td>
                    ) : null}
                    {supportsClientLifecycle ? <td className="px-4 py-3 text-slate-700">{renderDuration(client)}</td> : null}
                    <td className="px-4 py-3 text-slate-700">{renderOnlineState(client)}</td>
                    <td className="px-4 py-3 text-slate-700">{renderLastOnline(client)}</td>
                    {supportsClientLifecycle ? (
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap items-center gap-2">
                        {(() => {
                          const isToggling = pendingToggleClientId === client.id;
                          const isDeleting = pendingDeleteClientId === client.id;
                          const actionsLocked = Boolean(pendingToggleClientId || pendingDeleteClientId);
                          return (
                            <>
                        <button
                          title="Edit limits"
                          aria-label="Edit limits"
                          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-300 text-slate-700 hover:bg-slate-100"
                          onClick={() => openEditDialog(client)}
                          disabled={actionsLocked}
                        >
                          <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.8">
                            <path d="M12 20h9" />
                            <path d="M16.5 3.5a2.1 2.1 0 1 1 3 3L7 19l-4 1 1-4Z" />
                          </svg>
                        </button>
                        <button
                          title={client.enable ? "Disable client" : "Enable client"}
                          aria-label={client.enable ? "Disable client" : "Enable client"}
                          className={`inline-flex h-8 w-8 items-center justify-center rounded-lg border ${
                            client.enable ? "border-amber-400 text-amber-700 hover:bg-amber-50" : "border-emerald-400 text-emerald-700 hover:bg-emerald-50"
                          } disabled:cursor-not-allowed disabled:opacity-60`}
                          onClick={() => void updateClient({ ...client, enable: !client.enable })}
                          disabled={actionsLocked}
                        >
                          {isToggling ? (
                            <svg viewBox="0 0 24 24" className="h-4 w-4 animate-spin" fill="none" stroke="currentColor" strokeWidth="1.8">
                              <path d="M12 3a9 9 0 1 0 9 9" />
                            </svg>
                          ) : client.enable ? (
                            <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.8">
                              <path d="M6 12h12" />
                            </svg>
                          ) : (
                            <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.8">
                              <path d="M12 5v14" />
                              <path d="M5 12h14" />
                            </svg>
                          )}
                        </button>
                        <button
                          title="Show client config"
                          aria-label="Show client config"
                          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-cyan-600 text-cyan-700 hover:bg-cyan-50"
                          onClick={() => void showConfig(client.id)}
                          disabled={actionsLocked}
                        >
                          <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.8">
                            <path d="M2 12s3.5-6 10-6 10 6 10 6-3.5 6-10 6S2 12 2 12Z" />
                            <circle cx="12" cy="12" r="3" />
                          </svg>
                        </button>
                        <button
                          title="Copy share link"
                          aria-label="Copy share link"
                          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-indigo-500 text-indigo-700 hover:bg-indigo-50"
                          onClick={() => void shareClientConfigPage(client.id)}
                          disabled={actionsLocked}
                        >
                          <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.8">
                            <circle cx="18" cy="5" r="3" />
                            <circle cx="6" cy="12" r="3" />
                            <circle cx="18" cy="19" r="3" />
                            <path d="M8.6 10.8 15.4 6.9" />
                            <path d="m8.6 13.2 6.8 3.9" />
                          </svg>
                        </button>
                        <button
                          title="Delete client"
                          aria-label="Delete client"
                          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-rose-500 text-rose-700 hover:bg-rose-50 disabled:cursor-not-allowed disabled:opacity-60"
                          onClick={() => void deleteClient(client.id)}
                          disabled={actionsLocked}
                        >
                          {isDeleting ? (
                            <svg viewBox="0 0 24 24" className="h-4 w-4 animate-spin" fill="none" stroke="currentColor" strokeWidth="1.8">
                              <path d="M12 3a9 9 0 1 0 9 9" />
                            </svg>
                          ) : (
                            <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.8">
                              <path d="M3 6h18" />
                              <path d="M8 6V4h8v2" />
                              <path d="M19 6l-1 14H6L5 6" />
                              <path d="M10 11v6" />
                              <path d="M14 11v6" />
                            </svg>
                          )}
                        </button>
                            </>
                          );
                        })()}
                      </div>
                    </td>
                    ) : null}
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </section>

      {dialogMode ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/55 p-4" onClick={closeClientDialog}>
          <section className="card w-full max-w-xl p-6" onClick={(event) => event.stopPropagation()}>
            <div className="flex items-center justify-between gap-3">
              <h2 className="text-xl font-semibold text-slate-900">{dialogMode === "add" ? "Add Client" : "Edit Client"}</h2>
              <button className="rounded-xl border border-slate-300 px-3 py-1 text-sm" onClick={closeClientDialog} disabled={submitting}>
                Close
              </button>
            </div>

            <div className="mt-4 space-y-4">
              <label className="block text-sm text-slate-700">
                Client Memo / Email
                <input
                  className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2"
                  value={formEmail}
                  onChange={(event) => setFormEmail(event.target.value)}
                  placeholder="Client memo (e.g. user email or note)"
                  disabled={submitting}
                />
              </label>

              <label className="block text-sm text-slate-700">
                Traffic Limit (GB)
                <input
                  type="number"
                  min="0"
                  step="0.01"
                  className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2 disabled:bg-slate-100"
                  value={formTotalGB}
                  onChange={(event) => setFormTotalGB(event.target.value)}
                  disabled={submitting || !capabilities.supportsTrafficLimit}
                />
              </label>

              <label className="block text-sm text-slate-700">
                Expiry (leave blank = never)
                <input
                  type="datetime-local"
                  className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2 disabled:bg-slate-100"
                  value={formExpiry}
                  onChange={(event) => setFormExpiry(event.target.value)}
                  disabled={submitting || !capabilities.supportsDurationLimit}
                />
              </label>

              <label className="block text-sm text-slate-700">
                Speed Limit (Kbps, 0 = unlimited)
                <input
                  type="number"
                  min="0"
                  step="1"
                  className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2 disabled:bg-slate-100"
                  value={formSpeedLimitKbps}
                  onChange={(event) => setFormSpeedLimitKbps(event.target.value)}
                  disabled={submitting || !capabilities.supportsSpeedLimit}
                />
              </label>

              {(!capabilities.supportsTrafficLimit || !capabilities.supportsDurationLimit) ? (
                <p className="text-xs text-amber-700">Traffic/Duration controls are not supported for this protocol.</p>
              ) : null}
            </div>

            <div className="mt-6 flex justify-end gap-2">
              <button className="rounded-xl border border-slate-300 px-4 py-2 text-sm" onClick={closeClientDialog} disabled={submitting}>
                Cancel
              </button>
              <button
                className="rounded-xl bg-cyan-600 px-4 py-2 text-sm font-medium text-white disabled:opacity-60"
                onClick={() => void submitClientDialog()}
                disabled={submitting}
              >
                {submitting ? "Saving..." : "Save"}
              </button>
            </div>
          </section>
        </div>
      ) : null}

      {protocolConfigOpen ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/55 p-4" onClick={closeProtocolConfigDialog}>
          <section className="card w-full max-w-xl p-6" onClick={(event) => event.stopPropagation()}>
            <div className="flex items-center justify-between gap-3">
              <h2 className="text-xl font-semibold text-slate-900">Protocol Config</h2>
              <button
                className="rounded-xl border border-slate-300 px-3 py-1 text-sm"
                onClick={closeProtocolConfigDialog}
                disabled={protocolConfigLoading || protocolConfigSaving}
              >
                Close
              </button>
            </div>
            <p className="mt-2 text-xs text-slate-600">Active protocol: {protocolConfigProtocolId || "unknown"}</p>

            <div className="mt-4 space-y-4">
              {protocolConfigFields.map((field) => (
                <label key={field.key} className="block text-sm text-slate-700">
                  {field.label}
                  <input
                    type={field.type === "password" ? "password" : field.type === "number" ? "number" : "text"}
                    className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2"
                    value={field.value}
                    required={field.required}
                    onChange={(event) => updateProtocolConfigField(field.key, event.target.value)}
                    disabled={protocolConfigLoading || protocolConfigSaving}
                  />
                  {field.helpText ? <span className="mt-1 block text-xs text-slate-500">{field.helpText}</span> : null}
                </label>
              ))}
            </div>

            <div className="mt-6 flex justify-end gap-2">
              <button
                className="rounded-xl border border-slate-300 px-4 py-2 text-sm"
                onClick={closeProtocolConfigDialog}
                disabled={protocolConfigLoading || protocolConfigSaving}
              >
                Cancel
              </button>
              <button
                className="rounded-xl bg-cyan-600 px-4 py-2 text-sm font-medium text-white disabled:opacity-60"
                onClick={() => void saveProtocolConfig()}
                disabled={protocolConfigLoading || protocolConfigSaving}
              >
                {protocolConfigSaving ? "Saving..." : "Save"}
              </button>
            </div>
          </section>
        </div>
      ) : null}

      {selectedConfig ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/55 p-4" onClick={() => setSelectedConfig(null)}>
          <section className="card w-full max-w-xl p-6" onClick={(event) => event.stopPropagation()}>
            <div className="flex items-center justify-between gap-3">
              <h2 className="text-xl font-semibold text-slate-900">{selectedConfig.title?.trim() || "Client Config"}</h2>
              <button className="rounded-xl border border-slate-300 px-3 py-1 text-sm" onClick={() => setSelectedConfig(null)}>
                Close
              </button>
            </div>

            {selectedConfig.mode === "qr" ? (
              <>
                <div className="mt-4 flex items-start gap-2">
                  <textarea readOnly className="h-28 w-full rounded-xl border border-slate-300 p-3 font-[var(--font-mono)] text-xs" value={selectedConfig.uri} />
                  <button
                    title="Copy config"
                    aria-label="Copy config"
                    className="inline-flex h-10 w-10 items-center justify-center rounded-xl border border-slate-300 text-slate-700 hover:bg-slate-100"
                    onClick={() => void copyConfigUri()}
                  >
                    <svg viewBox="0 0 24 24" className="h-5 w-5" fill="none" stroke="currentColor" strokeWidth="1.8">
                      <rect x="9" y="9" width="10" height="10" rx="2" />
                      <path d="M6 15H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v1" />
                    </svg>
                  </button>
                </div>
                <div className="mt-4 flex justify-center">
                  {selectedConfig.qrCodeDataUrl ? (
                    <Image src={selectedConfig.qrCodeDataUrl} width={240} height={240} alt="QR Code" className="rounded-xl border border-slate-200" />
                  ) : (
                    <p className="text-sm text-slate-600">QR code is not available for this config.</p>
                  )}
                </div>
                {decodedShadowsocksConfig ? (
                  <div className="mt-4 space-y-2 rounded-xl border border-slate-200 bg-slate-50 p-3 text-sm">
                    <p className="text-xs uppercase tracking-[0.2em] text-slate-500">Decoded Shadowsocks Credentials</p>
                    <div className="grid gap-2 sm:grid-cols-[120px_1fr_auto] sm:items-center">
                      <p className="font-medium text-slate-600">Method</p>
                      <p className="font-[var(--font-mono)] break-all">{decodedShadowsocksConfig.method}</p>
                      <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(decodedShadowsocksConfig.method, "Failed to copy method.")}>Copy</button>
                    </div>
                    <div className="grid gap-2 sm:grid-cols-[120px_1fr_auto] sm:items-center">
                      <p className="font-medium text-slate-600">Password</p>
                      <p className="font-[var(--font-mono)] break-all">{decodedShadowsocksConfig.password}</p>
                      <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(decodedShadowsocksConfig.password, "Failed to copy password.")}>Copy</button>
                    </div>
                    <div className="grid gap-2 sm:grid-cols-[120px_1fr_auto] sm:items-center">
                      <p className="font-medium text-slate-600">Server</p>
                      <p className="font-[var(--font-mono)] break-all">{decodedShadowsocksConfig.server}</p>
                      <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(decodedShadowsocksConfig.server, "Failed to copy server.")}>Copy</button>
                    </div>
                    <div className="grid gap-2 sm:grid-cols-[120px_1fr_auto] sm:items-center">
                      <p className="font-medium text-slate-600">Port</p>
                      <p className="font-[var(--font-mono)]">{decodedShadowsocksConfig.port}</p>
                      <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(String(decodedShadowsocksConfig.port), "Failed to copy port.")}>Copy</button>
                    </div>
                  </div>
                ) : null}
              </>
            ) : null}

            {selectedConfig.mode === "ipsec_manual" ? (
              <div className="mt-4 space-y-3 text-sm text-slate-800">
                <div className="grid gap-2 sm:grid-cols-[170px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">Server</p>
                  <p className="font-[var(--font-mono)]">{selectedConfig.fields.server}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.fields.server, "Failed to copy server value.")}>Copy</button>
                </div>
                <div className="grid gap-2 sm:grid-cols-[170px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">Ports</p>
                  <p>{selectedConfig.fields.ports.join(", ")}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.fields.ports.join(", "), "Failed to copy ports.")}>Copy</button>
                </div>
                <div className="grid gap-2 sm:grid-cols-[170px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">Username</p>
                  <p className="font-[var(--font-mono)]">{selectedConfig.fields.username}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.fields.username, "Failed to copy username.")}>Copy</button>
                </div>
                <div className="grid gap-2 sm:grid-cols-[170px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">Password</p>
                  <p className="font-[var(--font-mono)]">{selectedConfig.fields.password}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.fields.password, "Failed to copy password.")}>Copy</button>
                </div>
                <div className="grid gap-2 sm:grid-cols-[170px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">L2TP Pre-shared Key</p>
                  <p className="font-[var(--font-mono)]">{selectedConfig.fields.preSharedKey}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.fields.preSharedKey, "Failed to copy pre-shared key.")}>Copy</button>
                </div>
                <div className="rounded-xl border border-slate-200 bg-slate-50 p-3">
                  <p className="mb-2 text-xs uppercase tracking-[0.2em] text-slate-500">Setup Steps</p>
                  <ol className="list-decimal space-y-1 pl-5 text-slate-700">
                    {selectedConfig.setupSteps.map((step, index) => (
                      <li key={`${step}-${index}`}>{step}</li>
                    ))}
                  </ol>
                </div>
              </div>
            ) : null}

            {selectedConfig.mode === "openvpn_bundle" ? (
              <div className="mt-4 space-y-3 text-sm text-slate-800">
                <div className="grid gap-2 sm:grid-cols-[190px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">OpenVPN Username</p>
                  <p className="font-[var(--font-mono)]">{selectedConfig.username}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.username, "Failed to copy OpenVPN username.")}>Copy</button>
                </div>
                <div className="grid gap-2 sm:grid-cols-[190px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">OpenVPN Password</p>
                  <p className="font-[var(--font-mono)]">{selectedConfig.password}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.password, "Failed to copy OpenVPN password.")}>Copy</button>
                </div>
                <div className="grid gap-2 sm:grid-cols-[190px_1fr_auto] sm:items-center">
                  <p className="font-medium text-slate-600">Private Key Passphrase</p>
                  <p className="font-[var(--font-mono)]">{selectedConfig.privateKeyPassphrase}</p>
                  <button className="rounded-lg border border-slate-300 px-2 py-1 text-xs" onClick={() => void copyText(selectedConfig.privateKeyPassphrase, "Failed to copy private key passphrase.")}>Copy</button>
                </div>
                <div className="mt-4 flex flex-wrap gap-2">
                  <button className="rounded-xl border border-cyan-600 px-4 py-2 text-sm font-medium text-cyan-700" onClick={downloadOpenVpnConfig}>
                    Download .ovpn
                  </button>
                  <button className="rounded-xl border border-slate-300 px-4 py-2 text-sm" onClick={() => void copyText(selectedConfig.ovpnContent || selectedConfig.uri, "Failed to copy OpenVPN profile.")}>
                    Copy .ovpn Text
                  </button>
                </div>
              </div>
            ) : null}

          </section>
        </div>
      ) : null}
    </main>
  );
}
