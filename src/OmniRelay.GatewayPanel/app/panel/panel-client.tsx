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
  [key: string]: unknown;
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
}

interface ConfigPayload {
  uri: string;
  qrCodeDataUrl: string;
}

export function PanelClient() {
  const router = useRouter();
  const [clients, setClients] = useState<InboundClient[]>([]);
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [email, setEmail] = useState("");
  const [selectedConfig, setSelectedConfig] = useState<ConfigPayload | null>(null);
  const [copied, setCopied] = useState(false);

  const activeClients = useMemo(() => clients.filter((client) => client.enable).length, [clients]);

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
    } catch {
      setError("Failed to load clients.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void loadInbound();
  }, []);

  async function addClient() {
    setError("");
    if (!email.trim()) {
      setError("Client email is required.");
      return;
    }

    const response = await fetch("/api/client/add", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email: email.trim() })
    });

    const payload = (await response.json()) as { message?: string };
    if (!response.ok) {
      setError(payload.message ?? "Failed to add client.");
      return;
    }

    setEmail("");
    await loadInbound();
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
    const payload = (await response.json()) as ConfigPayload & { message?: string };
    if (!response.ok) {
      setError(payload.message ?? "Failed to build config.");
      return;
    }

    setSelectedConfig(payload);
    setCopied(false);
  }

  async function copyConfigUri() {
    if (!selectedConfig) {
      return;
    }

    try {
      await navigator.clipboard.writeText(selectedConfig.uri);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      setError("Failed to copy config.");
    }
  }

  async function logout() {
    await fetch("/api/auth/logout", { method: "POST" });
    router.replace("/login");
    router.refresh();
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

        <div className="mt-6 flex flex-wrap items-end gap-3 rounded-xl border border-slate-200 bg-white p-4">
          <label className="min-w-64 flex-1 text-sm text-slate-700">
            New Client Memo
            <input
              className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2"
              value={email}
              onChange={(event) => setEmail(event.target.value)}
              placeholder="Client Memo (e.g. user email or note)"
            />
          </label>
          <button className="rounded-xl bg-cyan-600 px-4 py-2 text-sm font-medium text-white" onClick={() => void addClient()}>
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
                <th className="px-4 py-3">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 bg-white">
              {loading ? (
                <tr>
                  <td className="px-4 py-4 text-slate-500" colSpan={4}>
                    Loading clients...
                  </td>
                </tr>
              ) : clients.length === 0 ? (
                <tr>
                  <td className="px-4 py-4 text-slate-500" colSpan={4}>
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
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap gap-2">
                        <button
                          className="rounded-lg border border-slate-300 px-2 py-1 text-xs"
                          onClick={() => void updateClient({ ...client, enable: !client.enable, flow: "xtls-rprx-vision" })}
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

      {selectedConfig ? (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-slate-900/55 p-4" onClick={() => setSelectedConfig(null)}>
          <section className="card w-full max-w-xl p-6" onClick={(event) => event.stopPropagation()}>
            <div className="flex items-center justify-between gap-3">
              <h2 className="text-xl font-semibold text-slate-900">Client Config</h2>
              <button className="rounded-xl border border-slate-300 px-3 py-1 text-sm" onClick={() => setSelectedConfig(null)}>
                Close
              </button>
            </div>
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
              <Image src={selectedConfig.qrCodeDataUrl} width={240} height={240} alt="QR Code" className="rounded-xl border border-slate-200" />
            </div>
          </section>
        </div>
      ) : null}
    </main>
  );
}
