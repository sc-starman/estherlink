import Link from "next/link";
import { redirect } from "next/navigation";
import { getSession } from "@/lib/session";

const docs = [
  {
    title: "Add Client",
    method: "POST",
    path: "/api/client/add",
    notes: "Creates a new client. Requires email. Optional limits: totalGB, expiryTime (unix-ms), speedLimitKbps.",
    requestExample: `{
  "email": "user@example.com",
  "totalGB": 50,
  "expiryTime": 1767225600000,
  "speedLimitKbps": 2048
}`,
    responseExample: `{
  "ok": true,
  "client": {
    "id": "c2ea3d9f-9d34-4f48-8b3b-d3f79f9f6021",
    "email": "user@example.com",
    "enable": true
  }
}`
  },
  {
    title: "Edit Client / Disable Client",
    method: "POST",
    path: "/api/client/update",
    notes: "Updates an existing client. Disable by sending client.enable=false.",
    requestExample: `{
  "client": {
    "id": "c2ea3d9f-9d34-4f48-8b3b-d3f79f9f6021",
    "email": "user@example.com",
    "enable": false,
    "totalGB": 20,
    "expiryTime": 0,
    "speedLimitKbps": 0
  }
}`,
    responseExample: `{
  "ok": true
}`
  },
  {
    title: "Delete Client",
    method: "POST",
    path: "/api/client/delete",
    notes: "Deletes a client by id.",
    requestExample: `{
  "uuid": "c2ea3d9f-9d34-4f48-8b3b-d3f79f9f6021"
}`,
    responseExample: `{
  "ok": true
}`
  },
  {
    title: "Get Clients",
    method: "GET",
    path: "/api/client",
    notes: "Returns all clients plus capability flags for the active protocol.",
    requestExample: `GET /api/client`,
    responseExample: `{
  "clients": [
    {
      "id": "c2ea3d9f-9d34-4f48-8b3b-d3f79f9f6021",
      "email": "user@example.com",
      "enable": true
    }
  ],
  "capabilities": {
    "supportsTrafficLimit": true,
    "supportsDurationLimit": true,
    "supportsUsageAccounting": true
  }
}`
  },
  {
    title: "Get Client By ID",
    method: "GET",
    path: "/api/client/{clientId}",
    notes: "Returns one client by id. Returns 404 when not found.",
    requestExample: `GET /api/client/c2ea3d9f-9d34-4f48-8b3b-d3f79f9f6021`,
    responseExample: `{
  "client": {
    "id": "c2ea3d9f-9d34-4f48-8b3b-d3f79f9f6021",
    "email": "user@example.com",
    "enable": true
  },
  "capabilities": {
    "supportsTrafficLimit": true,
    "supportsDurationLimit": true,
    "supportsUsageAccounting": true
  }
}`
  }
];

export default async function ApiDocsPage() {
  const session = await getSession();
  if (!session.isAuthenticated) {
    redirect("/login");
  }

  return (
    <main className="mx-auto min-h-screen w-full max-w-6xl px-6 py-10">
      <section className="card p-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <p className="text-xs uppercase tracking-[0.35em] text-cyan-700">Gateway Panel</p>
            <h1 className="text-3xl font-semibold text-slate-900">API Docs</h1>
            <p className="mt-1 text-sm text-slate-600">Authenticated OmniPanel client management APIs.</p>
          </div>
          <div className="flex gap-2">
            <Link href="/panel" className="rounded-xl border border-slate-300 px-4 py-2 text-sm">
              Back to Panel
            </Link>
          </div>
        </div>

        <div className="mt-6 rounded-xl border border-amber-200 bg-amber-50 p-4 text-sm text-amber-900">
          All endpoints require an authenticated OmniPanel session cookie. Typical errors: 401 Unauthorized, 400 Validation Error, 404 Not Found.
        </div>

        <div className="mt-6 space-y-6">
          {docs.map((doc) => (
            <article key={`${doc.method}-${doc.path}`} className="rounded-xl border border-slate-200 bg-white p-4">
              <div className="flex flex-wrap items-center gap-2">
                <span className="rounded-md bg-slate-900 px-2 py-1 font-[var(--font-mono)] text-xs text-white">{doc.method}</span>
                <code className="rounded-md bg-slate-100 px-2 py-1 text-sm text-slate-800">{doc.path}</code>
              </div>
              <h2 className="mt-3 text-xl font-semibold text-slate-900">{doc.title}</h2>
              <p className="mt-1 text-sm text-slate-700">{doc.notes}</p>

              <div className="mt-4 grid gap-4 lg:grid-cols-2">
                <div>
                  <p className="mb-2 text-xs uppercase tracking-[0.2em] text-slate-500">Request</p>
                  <pre className="overflow-x-auto rounded-xl border border-slate-200 bg-slate-950 p-3 text-xs text-slate-100">
                    <code>{doc.requestExample}</code>
                  </pre>
                </div>
                <div>
                  <p className="mb-2 text-xs uppercase tracking-[0.2em] text-slate-500">Response</p>
                  <pre className="overflow-x-auto rounded-xl border border-slate-200 bg-slate-950 p-3 text-xs text-slate-100">
                    <code>{doc.responseExample}</code>
                  </pre>
                </div>
              </div>
            </article>
          ))}
        </div>
      </section>
    </main>
  );
}
