import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getInboundId, xuiJson } from "@/lib/xui";

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
    const inboundId = getInboundId();
    await xuiJson(session, `/panel/api/inbounds/${encodeURIComponent(inboundId)}/delClient/${encodeURIComponent(uuid)}`, {
      method: "POST"
    });

    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to delete client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}