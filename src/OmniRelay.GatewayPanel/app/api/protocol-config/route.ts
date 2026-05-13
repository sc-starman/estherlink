import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getActiveProtocol } from "@/lib/protocol";
import { getProtocolConfigApiPayload, updateProtocolConfig } from "@/lib/protocol-config";

export async function GET() {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  try {
    const protocolId = getActiveProtocol();
    const payload = await getProtocolConfigApiPayload(protocolId);
    return NextResponse.json(payload);
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to load protocol config.";
    return NextResponse.json({ message }, { status: 400 });
  }
}

export async function PUT(request: Request) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  try {
    const raw = (await request.json()) as { values?: Record<string, unknown> };
    const protocolId = getActiveProtocol();
    const payload = await updateProtocolConfig(protocolId, raw.values ?? {});
    return NextResponse.json(payload);
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to update protocol config.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
