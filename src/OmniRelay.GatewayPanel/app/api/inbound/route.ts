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
      inbound: snapshot.inbound,
      clients: snapshot.clients,
      streamSettings: snapshot.streamSettings ?? {},
      capabilities: snapshot.capabilities ?? {
        supportsTrafficLimit: false,
        supportsDurationLimit: false,
        supportsUsageAccounting: false
      }
    });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to query inbound.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
