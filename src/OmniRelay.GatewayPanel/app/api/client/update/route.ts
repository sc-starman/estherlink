import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getInboundId, xuiJson } from "@/lib/xui";

interface UpdateClientRequest {
  client?: Record<string, unknown>;
}

export async function POST(request: Request) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  const body = (await request.json()) as UpdateClientRequest;
  const client = body.client;
  const clientId = typeof client?.id === "string" ? client.id : "";
  if (!client || !clientId) {
    return NextResponse.json({ message: "Client payload is required." }, { status: 400 });
  }

  client.flow = "xtls-rprx-vision";

  try {
    const inboundId = getInboundId();
    const form = new URLSearchParams();
    form.set("id", inboundId);
    form.set("settings", JSON.stringify({ clients: [client] }));

    await xuiJson(session, `/panel/api/inbounds/updateClient/${encodeURIComponent(clientId)}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
      },
      body: form
    });

    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to update client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}