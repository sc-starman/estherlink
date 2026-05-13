import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getGatewayProvider } from "@/lib/protocol";

export async function GET() {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  try {
    const provider = getGatewayProvider();
    const snapshot = await provider.getInbound(session);

    await session.save();
    return NextResponse.json({
      clients: snapshot.clients,
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
    const message = error instanceof Error ? error.message : "Failed to load clients.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
