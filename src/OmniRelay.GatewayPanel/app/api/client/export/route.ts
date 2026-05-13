import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getActiveProtocol, getGatewayProvider } from "@/lib/protocol";
import { getProtocolCapabilities } from "@/lib/protocol-capabilities";

export async function GET() {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  try {
    const activeProtocol = getActiveProtocol();
    const capabilities = getProtocolCapabilities(activeProtocol);
    if (!capabilities.supportsClientLifecycle) {
      return NextResponse.json({ message: "Per-client management is not supported for this protocol." }, { status: 400 });
    }

    const provider = getGatewayProvider();
    const backup = await provider.exportBackup(session);
    await session.save();
    return new NextResponse(Buffer.from(backup.body) as unknown as BodyInit, {
      headers: {
        "Content-Type": backup.contentType,
        "Content-Disposition": `attachment; filename="${backup.fileName.replace(/"/g, "")}"`,
        "Cache-Control": "no-store"
      }
    });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to export clients.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
