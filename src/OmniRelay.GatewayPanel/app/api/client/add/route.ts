import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getGatewayProvider } from "@/lib/protocol";

interface AddClientRequest {
  email?: string;
}

export async function POST(request: Request) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  const body = (await request.json()) as AddClientRequest;
  const email = (body.email ?? "").trim();
  if (!email) {
    return NextResponse.json({ message: "Email is required." }, { status: 400 });
  }

  try {
    const provider = getGatewayProvider();
    const client = await provider.addClient(session, email);

    await session.save();
    return NextResponse.json({ ok: true, client });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to add client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
