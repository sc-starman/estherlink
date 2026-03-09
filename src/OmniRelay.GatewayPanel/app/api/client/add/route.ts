import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { generateClientPayload, getInboundId, xuiJson } from "@/lib/xui";

interface AddClientRequest {
  email?: string;
}

export async function POST(request: Request) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  const body = (await request.json()) as AddClientRequest;
  const email = (body.email ?? "").trim();
  if (!email) {
    return NextResponse.json({ message: "Email is required." }, { status: 400 });
  }

  try {
    const inboundId = getInboundId();
    const client = generateClientPayload(email);
    const form = new URLSearchParams();
    form.set("id", inboundId);
    form.set("settings", JSON.stringify({ clients: [client] }));

    await xuiJson(session, "/panel/api/inbounds/addClient", {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
      },
      body: form
    });

    await session.save();
    return NextResponse.json({ ok: true, client });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to add client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}