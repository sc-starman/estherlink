import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getActiveProtocol, getGatewayProvider } from "@/lib/protocol";
import { getProtocolCapabilities } from "@/lib/protocol-capabilities";

interface DeleteClientRequest {
  uuid?: string;
}

export async function POST(request: Request) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  const body = (await request.json()) as DeleteClientRequest;
  const uuid = (body.uuid ?? "").trim();
  if (!uuid) {
    return NextResponse.json({ message: "uuid is required." }, { status: 400 });
  }

  try {
    const activeProtocol = getActiveProtocol();
    const capabilities = getProtocolCapabilities(activeProtocol);
    if (!capabilities.supportsClientLifecycle) {
      return NextResponse.json({ message: "Per-client management is not supported for this protocol." }, { status: 400 });
    }

    const provider = getGatewayProvider();
    await provider.deleteClient(session, uuid);

    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to delete client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
