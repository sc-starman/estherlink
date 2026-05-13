"use client";

import Image from "next/image";
import { useEffect, useMemo, useRef, useState } from "react";
import { copyToClipboard } from "@/lib/clipboard";

interface PublicClientViewModel {
  id: string;
  email: string;
  enable: boolean;
  totalGB?: number;
  expiryTime?: number;
  usedBytes?: number | null;
  speedLimitKbps?: number;
  isOnline?: boolean;
  lastSeenAtUnixMs?: number;
}

interface ProtocolCapabilities {
  supportsTrafficLimit: boolean;
  supportsDurationLimit: boolean;
  supportsUsageAccounting: boolean;
  supportsSpeedLimit?: boolean;
  supportsOnlineStatus?: boolean;
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

interface PublicClientPayload {
  config: ConfigPayload;
  client: PublicClientViewModel;
  capabilities: ProtocolCapabilities;
}

function formatBytes(bytes: number | null | undefined): string {
  if (typeof bytes !== "number" || !Number.isFinite(bytes) || bytes < 0) {
    return "N/A";
  }
  if (bytes < 1024) {
    return `${Math.round(bytes)} B`;
  }
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let idx = 0;
  while (value >= 1024 && idx < units.length - 1) {
    value /= 1024;
    idx += 1;
  }
  return `${value.toFixed(value >= 100 ? 0 : value >= 10 ? 1 : 2)} ${units[idx]}`;
}

function formatDate(unixMs: number | undefined): string {
  if (typeof unixMs !== "number" || !Number.isFinite(unixMs) || unixMs <= 0) {
    return "Never";
  }
  return new Date(unixMs).toLocaleString();
}

export function PublicClientConfig({ clientId }: { clientId: string }) {
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [payload, setPayload] = useState<PublicClientPayload | null>(null);
  const [toastMessage, setToastMessage] = useState("");
  const [toastTone, setToastTone] = useState<"success" | "error">("success");
  const toastTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    async function load() {
      if (!clientId) {
        setError("Config is not available.");
        setLoading(false);
        return;
      }

      try {
        const response = await fetch(`/api/public/client/${encodeURIComponent(clientId)}`, { cache: "no-store" });
        const body = (await response.json().catch(() => ({}))) as { message?: string } | PublicClientPayload;
        if (!response.ok) {
          setError((body as { message?: string }).message ?? "Config is not available.");
          setPayload(null);
          return;
        }

        setPayload(body as PublicClientPayload);
        setError("");
      } catch {
        setError("Config is not available.");
        setPayload(null);
      } finally {
        setLoading(false);
      }
    }

    void load();
  }, [clientId]);

  const config = payload?.config ?? null;
  const client = payload?.client ?? null;
  const decodedShadowsocks = useMemo(() => {
    if (!config || config.mode !== "qr" || !config.uri.startsWith("ss://")) {
      return null;
    }
    return config.uri;
  }, [config]);

  useEffect(() => {
    return () => {
      if (toastTimerRef.current) {
        clearTimeout(toastTimerRef.current);
        toastTimerRef.current = null;
      }
    };
  }, []);

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

  async function copyText(value: string, failMessage: string) {
    try {
      await copyToClipboard(value);
      showToast("Copied.", "success");
    } catch {
      setError(failMessage);
      showToast(failMessage, "error");
    }
  }

  function downloadOpenVpnConfig() {
    if (!config || config.mode !== "openvpn_bundle") {
      return;
    }
    const content = config.ovpnContent || config.uri;
    if (!content.trim()) {
      setError("OpenVPN profile content is empty.");
      return;
    }
    const blob = new Blob([content], { type: "application/x-openvpn-profile" });
    const objectUrl = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = objectUrl;
    anchor.download = config.ovpnFileName || "omnirelay-client.ovpn";
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(objectUrl);
  }

  return (
    <main className="mx-auto w-full max-w-3xl px-4 py-8 sm:px-6">
      {toastMessage ? (
        <div className={`fixed right-5 top-5 z-[70] rounded-xl px-4 py-3 text-sm text-white shadow-lg ${toastTone === "error" ? "bg-rose-700" : "bg-emerald-700"}`}>
          {toastMessage}
        </div>
      ) : null}
      <section className="card p-6 sm:p-8">
        <p className="text-xs uppercase tracking-[0.3em] text-cyan-700">OmniPanel Public Config</p>
        <h1 className="mt-2 text-2xl font-semibold text-slate-900">Client Connection Bundle</h1>

        {loading ? <p className="mt-6 text-slate-600">Loading client config...</p> : null}
        {!loading && error ? <p className="mt-6 text-rose-700">{error}</p> : null}

        {!loading && !error && payload && client && config ? (
          <div className="mt-6 space-y-6">
            <div className="rounded-xl border border-slate-200 bg-slate-50 p-4 text-sm text-slate-800">
              <p><span className="font-medium text-slate-600">Client ID:</span> <span className="font-[var(--font-mono)] break-all">{client.id}</span></p>
              <p className="mt-1"><span className="font-medium text-slate-600">Memo/Email:</span> {client.email}</p>
              <p className="mt-1"><span className="font-medium text-slate-600">Traffic Used:</span> {formatBytes(client.usedBytes)}</p>
              <p className="mt-1"><span className="font-medium text-slate-600">Traffic Limit:</span> {typeof client.totalGB === "number" ? `${client.totalGB} GB` : "Unlimited"}</p>
              <p className="mt-1"><span className="font-medium text-slate-600">Speed Limit:</span> {typeof client.speedLimitKbps === "number" ? `${client.speedLimitKbps} Kbps` : "Unlimited"}</p>
              <p className="mt-1"><span className="font-medium text-slate-600">Expiry:</span> {formatDate(client.expiryTime)}</p>
              <p className="mt-1"><span className="font-medium text-slate-600">Status:</span> {client.isOnline ? "Online" : "Offline"}</p>
              <p className="mt-1"><span className="font-medium text-slate-600">Last Seen:</span> {formatDate(client.lastSeenAtUnixMs)}</p>
            </div>

            {config.mode === "qr" ? (
              <div>
                <h2 className="text-lg font-semibold text-slate-900">{config.title?.trim() || "Client Config"}</h2>
                <div className="mt-3 flex items-start gap-2">
                  <textarea readOnly className="h-28 w-full rounded-xl border border-slate-300 p-3 font-[var(--font-mono)] text-xs" value={config.uri} />
                  <button
                    className="rounded-xl border border-slate-300 px-3 py-2 text-sm"
                    onClick={() => void copyText(config.uri, "Failed to copy config.")}
                  >
                    Copy
                  </button>
                </div>
                <div className="mt-4 flex justify-center">
                  <Image src={config.qrCodeDataUrl} width={240} height={240} alt="QR Code" className="rounded-xl border border-slate-200" />
                </div>
                {decodedShadowsocks ? <p className="mt-3 text-xs text-slate-600">Shadowsocks URI detected. Copy the full URI above for import.</p> : null}
              </div>
            ) : null}

            {config.mode === "ipsec_manual" ? (
              <div className="space-y-3 text-sm text-slate-800">
                <h2 className="text-lg font-semibold text-slate-900">{config.title?.trim() || "IPSec/L2TP Config"}</h2>
                <p><span className="font-medium text-slate-600">Server:</span> <span className="font-[var(--font-mono)]">{config.fields.server}</span></p>
                <p><span className="font-medium text-slate-600">Ports:</span> {config.fields.ports.join(", ")}</p>
                <p><span className="font-medium text-slate-600">Username:</span> <span className="font-[var(--font-mono)]">{config.fields.username}</span></p>
                <p><span className="font-medium text-slate-600">Password:</span> <span className="font-[var(--font-mono)]">{config.fields.password}</span></p>
                <p><span className="font-medium text-slate-600">L2TP PSK:</span> <span className="font-[var(--font-mono)]">{config.fields.preSharedKey}</span></p>
                <button className="rounded-xl border border-slate-300 px-4 py-2 text-sm" onClick={() => void copyText(config.uri, "Failed to copy manual config.")}>
                  Copy Full Manual Bundle
                </button>
              </div>
            ) : null}

            {config.mode === "openvpn_bundle" ? (
              <div className="space-y-3 text-sm text-slate-800">
                <h2 className="text-lg font-semibold text-slate-900">{config.title?.trim() || "OpenVPN Bundle"}</h2>
                <p><span className="font-medium text-slate-600">Username:</span> <span className="font-[var(--font-mono)]">{config.username}</span></p>
                <p><span className="font-medium text-slate-600">Password:</span> <span className="font-[var(--font-mono)]">{config.password}</span></p>
                <p><span className="font-medium text-slate-600">Private Key Passphrase:</span> <span className="font-[var(--font-mono)]">{config.privateKeyPassphrase}</span></p>
                <div className="flex flex-wrap gap-2">
                  <button className="rounded-xl border border-cyan-600 px-4 py-2 text-sm font-medium text-cyan-700" onClick={downloadOpenVpnConfig}>
                    Download .ovpn
                  </button>
                  <button className="rounded-xl border border-slate-300 px-4 py-2 text-sm" onClick={() => void copyText(config.ovpnContent || config.uri, "Failed to copy .ovpn text.")}>
                    Copy .ovpn Text
                  </button>
                </div>
              </div>
            ) : null}

          </div>
        ) : null}
      </section>
    </main>
  );
}
