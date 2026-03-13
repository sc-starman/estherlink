import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getGatewayProvider } from "@/lib/protocol";

export async function GET(request: Request) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  const url = new URL(request.url);
  const uuid = (url.searchParams.get("uuid") ?? "").trim();
  if (!uuid) {
    return NextResponse.json({ message: "uuid query parameter is required." }, { status: 400 });
  }

  try {
    const provider = getGatewayProvider();
    const payload = await provider.buildClientConfig(session, request, uuid);

    await session.save();
    return NextResponse.json(payload);
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to generate client config.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
