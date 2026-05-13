import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getGatewayProvider } from "@/lib/protocol";

interface RouteParams {
  params: Promise<{
    clientId?: string;
  }>;
}

export async function GET(_request: Request, context: RouteParams) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  const { clientId: clientIdRaw } = await context.params;
  const clientId = String(clientIdRaw ?? "").trim();
  if (!clientId) {
    return NextResponse.json({ message: "clientId is required." }, { status: 400 });
  }

  try {
    const provider = getGatewayProvider();
    const snapshot = await provider.getInbound(session);
    const client = snapshot.clients.find((item) => item.id === clientId);
    if (!client) {
      return NextResponse.json({ message: "Client not found." }, { status: 404 });
    }

    await session.save();
    return NextResponse.json({
      client,
      capabilities: snapshot.capabilities ?? {
        supportsTrafficLimit: false,
        supportsDurationLimit: false,
        supportsUsageAccounting: false,
        supportsSpeedLimit: false,
        supportsOnlineStatus: false,
        supportsClientLifecycle: false
      }
    });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to load client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
