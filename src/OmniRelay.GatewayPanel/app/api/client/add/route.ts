import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getGatewayProvider } from "@/lib/protocol";

interface AddClientRequest {
  email?: string;
  totalGB?: number;
  expiryTime?: number;
}

function normalizeTotalGB(value: unknown): number {
  if (value === undefined || value === null || value === "") {
    return 0;
  }

  const totalGB = Number(value);
  if (!Number.isFinite(totalGB) || totalGB < 0) {
    throw new Error("totalGB must be a non-negative number.");
  }

  return totalGB;
}

function normalizeExpiryTime(value: unknown): number {
  if (value === undefined || value === null || value === "") {
    return 0;
  }

  const expiryTime = Number(value);
  if (!Number.isFinite(expiryTime) || expiryTime < 0) {
    throw new Error("expiryTime must be zero or a non-negative unix-ms timestamp.");
  }

  return Math.trunc(expiryTime);
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
    const totalGB = normalizeTotalGB(body.totalGB);
    const expiryTime = normalizeExpiryTime(body.expiryTime);
    const provider = getGatewayProvider();
    const client = await provider.addClient(session, email, { totalGB, expiryTime });

    await session.save();
    return NextResponse.json({ ok: true, client });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to add client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
