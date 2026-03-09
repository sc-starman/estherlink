"use client";

import Link from "next/link";
import { FormEvent, useState } from "react";

interface Props {
  username: string;
}

export function ChangePasswordClient({ username }: Props) {
  const [oldUsername, setOldUsername] = useState(username);
  const [oldPassword, setOldPassword] = useState("");
  const [newUsername, setNewUsername] = useState(username);
  const [newPassword, setNewPassword] = useState("");
  const [feedback, setFeedback] = useState("");
  const [saving, setSaving] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setFeedback("");
    setSaving(true);

    const response = await fetch("/api/auth/change-password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ oldUsername, oldPassword, newUsername, newPassword })
    });

    const payload = (await response.json()) as { message?: string };
    if (!response.ok) {
      setFeedback(payload.message ?? "Failed to change password.");
      setSaving(false);
      return;
    }

    setFeedback("Password changed successfully.");
    setOldPassword("");
    setNewPassword("");
    setSaving(false);
  }

  return (
    <main className="mx-auto flex min-h-screen w-full max-w-3xl items-center justify-center px-6 py-12">
      <section className="card w-full p-8">
        <div className="mb-6 flex items-center justify-between">
          <div>
            <p className="text-xs uppercase tracking-[0.35em] text-cyan-700">OmniPanel</p>
            <h1 className="mt-2 text-2xl font-semibold">Change Password</h1>
          </div>
          <Link href="/panel" className="rounded-xl border border-slate-300 px-4 py-2 text-sm">
            Back
          </Link>
        </div>

        <form className="space-y-4" onSubmit={handleSubmit}>
          <label className="block text-sm text-slate-700">
            Old Username
            <input className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2" value={oldUsername} onChange={(event) => setOldUsername(event.target.value)} required />
          </label>
          <label className="block text-sm text-slate-700">
            Old Password
            <input type="password" className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2" value={oldPassword} onChange={(event) => setOldPassword(event.target.value)} required />
          </label>
          <label className="block text-sm text-slate-700">
            New Username
            <input className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2" value={newUsername} onChange={(event) => setNewUsername(event.target.value)} required />
          </label>
          <label className="block text-sm text-slate-700">
            New Password
            <input type="password" className="mt-1 w-full rounded-xl border border-slate-300 px-3 py-2" value={newPassword} onChange={(event) => setNewPassword(event.target.value)} required />
          </label>

          {feedback ? <p className="text-sm text-slate-700">{feedback}</p> : null}

          <button type="submit" disabled={saving} className="rounded-xl bg-slate-900 px-5 py-2 text-sm font-medium text-white disabled:opacity-60">
            {saving ? "Saving..." : "Update Password"}
          </button>
        </form>
      </section>
    </main>
  );
}