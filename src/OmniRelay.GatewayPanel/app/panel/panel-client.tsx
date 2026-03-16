"use client";

import Image from "next/image";
import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";

interface InboundClient {
  id: string;
  email: string;
  enable: boolean;
  flow?: string;
  totalGB?: number;
  expiryTime?: number;
  usedBytes?: number | null;
  [key: string]: unknown;
}

interface ProtocolCapabilities {
  supportsTrafficLimit: boolean;
  supportsDurationLimit: boolean;
  supportsUsageAccounting: boolean;
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

const DEFAULT_CAPABILITIES: ProtocolCapabilities = {
  supportsTrafficLimit: false,
  supportsDurationLimit: false,
  supportsUsageAccounting: false
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
  const [copied, setCopied] = useState(false);
  const [dialogMode, setDialogMode] = useState<DialogMode | null>(null);
  const [editingClientId, setEditingClientId] = useState<string>("");
  const [formEmail, setFormEmail] = useState("");
  const [formTotalGB, setFormTotalGB] = useState("0");
  const [formExpiry, setFormExpiry] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [nowMs, setNowMs] = useState(Date.now());

  useEffect(() => {
    const timer = setInterval(() => setNowMs(Date.now()), 60_000);
    return () => clearInterval(timer);
  }, []);

  const activeClients = useMemo(() => clients.filter((client) => client.enable).length, [clients]);
  const editingClient = useMemo(
    () => clients.find((item) => item.id === editingClientId) ?? null,
    [clients, editingClientId]
  );

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
  }

  function openAddDialog() {
    setError("");
    setDialogMode("add");
    setEditingClientId("");
    setFormEmail("");
    setFormTotalGB("0");
    setFormExpiry("");
  }

  function openEditDialog(client: InboundClient) {
    setError("");
    setDialogMode("edit");
    setEditingClientId(client.id);
    setFormEmail(String(client.email ?? ""));
    setFormTotalGB(String(Number(client.totalGB ?? 0)));
    setFormExpiry(toDateTimeLocal(Number(client.expiryTime ?? 0)));
  }

  async function submitClientDialog() {
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

    setSubmitting(true);
    try {
      if (dialogMode === "add") {
        const response = await fetch("/api/client/add", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            email,
            totalGB: capabilities.supportsTrafficLimit ? totalGB : undefined,
            expiryTime: capabilities.supportsDurationLimit ? expiryTime : undefined
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
              expiryTime: capabilities.supportsDurationLimit ? expiryTime : undefined
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

  async function updateClient(client: InboundClient) {
    setError("");

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
  }

  async function deleteClient(uuid: string) {
    setError("");

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
  }

  async function showConfig(uuid: string) {
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

    setCopied(false);
  }

  async function copyText(value: string, errorMessage: string) {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      setError(errorMessage);
    }
  }

  async function copyConfigUri() {
    if (!selectedConfig) {
      return;
    }

    await copyText(selectedConfig.uri, "Failed to copy config.");
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

  return (
    <main className="mx-auto min-h-screen w-full max-w-6xl px-6 py-10">
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
            <button className="rounded-xl border border-slate-300 px-4 py-2 text-sm" onClick={() => void loadInbound()}>
              Refresh
            </button>
            <Link href="/change-password" className="rounded-xl border border-slate-300 px-4 py-2 text-sm">
              Change Password
            </Link>
            <button className="rounded-xl bg-slate-900 px-4 py-2 text-sm text-white" onClick={() => void logout()}>
              Logout
            </button>
          </div>
        </div>

        <div className="mt-6 grid gap-4 sm:grid-cols-2">
          <div className="rounded-xl border border-slate-200 bg-slate-50 p-4">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-500">Total Clients</p>
            <p className="mt-2 text-2xl font-semibold text-slate-900">{clients.length}</p>
          </div>
          <div className="rounded-xl border border-slate-200 bg-slate-50 p-4">
            <p className="text-xs uppercase tracking-[0.2em] text-slate-500">Enabled</p>
            <p className="mt-2 text-2xl font-semibold text-emerald-700">{activeClients}</p>
          </div>
        </div>

        <div className="mt-6">
          <button className="rounded-xl bg-cyan-600 px-4 py-2 text-sm font-medium text-white" onClick={openAddDialog}>
            Add Client
          </button>
        </div>

        {error ? <p className="mt-4 text-sm text-rose-600">{error}</p> : null}

        <div className="mt-6 overflow-x-auto rounded-xl border border-slate-200">
          <table className="min-w-full divide-y divide-slate-200 text-sm">
            <thead className="bg-slate-100 text-left text-slate-600">
              <tr>
                <th className="px-4 py-3">Email</th>
                <th className="px-4 py-3">UUID</th>
                <th className="px-4 py-3">State</th>
                <th className="px-4 py-3">Traffic (Used / Total)</th>
                <th className="px-4 py-3">Duration</th>
                <th className="px-4 py-3">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 bg-white">
              {loading ? (
                <tr>
                  <td className="px-4 py-4 text-slate-500" colSpan={6}>
                    Loading clients...
                  </td>
                </tr>
              ) : clients.length === 0 ? (
                <tr>
                  <td className="px-4 py-4 text-slate-500" colSpan={6}>
                    No clients found.
                  </td>
                </tr>
              ) : (
                clients.map((client) => (
                  <tr key={client.id}>
                    <td className="px-4 py-3">{client.email}</td>
                    <td className="px-4 py-3 font-[var(--font-mono)] text-xs text-slate-600">{client.id}</td>
                    <td className="px-4 py-3">
                      <span className={`rounded-full px-2 py-1 text-xs ${client.enable ? "bg-emerald-100 text-emerald-700" : "bg-amber-100 text-amber-700"}`}>
                        {client.enable ? "enabled" : "disabled"}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-slate-700">{renderTraffic(client)}</td>
                    <td className="px-4 py-3 text-slate-700">{renderDuration(client)}</td>
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap items-center gap-2">
                        <button
                          title="Edit limits"
                          aria-label="Edit limits"
                          className="inline-flex h-8 w-8 items-center justify-center rounded-lg border border-slate-300 text-slate-700 hover:bg-slate-100"
                          onClick={() => openEditDialog(client)}
                        >
                          <svg viewBox="0 0 24 24" className="h-4 w-4" fill="none" stroke="currentColor" strokeWidth="1.8">
                            <path d="M12 20h9" />
                            <path d="M16.5 3.5a2.1 2.1 0 1 1 3 3L7 19l-4 1 1-4Z" />
                          </svg>
                        </button>
                        <button
                          className="rounded-lg border border-slate-300 px-2 py-1 text-xs"
                          onClick={() => void updateClient({ ...client, enable: !client.enable })}
                        >
                          {client.enable ? "Disable" : "Enable"}
                        </button>
                        <button className="rounded-lg border border-cyan-600 px-2 py-1 text-xs text-cyan-700" onClick={() => void showConfig(client.id)}>
                          Show Config
                        </button>
                        <button className="rounded-lg border border-rose-500 px-2 py-1 text-xs text-rose-700" onClick={() => void deleteClient(client.id)}>
                          Delete
                        </button>
                      </div>
                    </td>
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
                {copied ? <p className="mt-2 text-xs text-emerald-700">Copied.</p> : null}
                <div className="mt-4 flex justify-center">
                  {selectedConfig.qrCodeDataUrl ? (
                    <Image src={selectedConfig.qrCodeDataUrl} width={240} height={240} alt="QR Code" className="rounded-xl border border-slate-200" />
                  ) : (
                    <p className="text-sm text-slate-600">QR code is not available for this config.</p>
                  )}
                </div>
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

            {copied ? <p className="mt-3 text-xs text-emerald-700">Copied.</p> : null}
          </section>
        </div>
      ) : null}
    </main>
  );
}
