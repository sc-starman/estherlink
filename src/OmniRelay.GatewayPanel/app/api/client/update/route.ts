import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getGatewayProvider } from "@/lib/protocol";
import { type GatewayClientRecord } from "@/lib/providers/types";

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

  try {
    const provider = getGatewayProvider();
    await provider.updateClient(session, client as GatewayClientRecord);

    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to update client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
