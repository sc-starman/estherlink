import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getActiveProtocol, getGatewayProvider } from "@/lib/protocol";
import { getProtocolCapabilities } from "@/lib/protocol-capabilities";

async function readBackupInput(request: Request) {
  const contentType = request.headers.get("content-type") ?? "";
  if (contentType.toLowerCase().includes("multipart/form-data")) {
    const formData = await request.formData();
    const file = formData.get("file") as
      | { name?: string; type?: string; arrayBuffer?: () => Promise<ArrayBuffer> }
      | null;
    if (!file || typeof file.arrayBuffer !== "function") {
      throw new Error("Import file is required.");
    }
    return {
      fileName: (file.name ?? "").trim() || "clients-backup",
      contentType: (file.type ?? "").trim() || "application/octet-stream",
      body: new Uint8Array(await file.arrayBuffer())
    };
  }

  return {
    fileName: "clients.json",
    contentType: contentType || "application/json",
    body: new Uint8Array(await request.arrayBuffer())
  };
}

export async function POST(request: Request) {
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
    const input = await readBackupInput(request);
    await provider.importBackup(session, input);

    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to import clients.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
