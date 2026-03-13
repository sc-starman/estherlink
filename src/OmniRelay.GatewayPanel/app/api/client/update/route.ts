import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getGatewayProvider } from "@/lib/protocol";
import { type GatewayClientRecord } from "@/lib/providers/types";

interface UpdateClientRequest {
  client?: Record<string, unknown>;
}

function normalizeOptionalTotalGB(value: unknown): number | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (value === null || value === "") {
    return 0;
  }

  const totalGB = Number(value);
  if (!Number.isFinite(totalGB) || totalGB < 0) {
    throw new Error("totalGB must be a non-negative number.");
  }

  return totalGB;
}

function normalizeOptionalExpiryTime(value: unknown): number | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (value === null || value === "") {
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

  const body = (await request.json()) as UpdateClientRequest;
  const client = body.client;
  const clientId = typeof client?.id === "string" ? client.id : "";
  if (!client || !clientId) {
    return NextResponse.json({ message: "Client payload is required." }, { status: 400 });
  }

  try {
    const provider = getGatewayProvider();
    const sanitized = { ...(client as GatewayClientRecord) };

    const totalGB = normalizeOptionalTotalGB((client as Record<string, unknown>).totalGB);
    if (totalGB !== undefined) {
      sanitized.totalGB = totalGB;
    }

    const expiryTime = normalizeOptionalExpiryTime((client as Record<string, unknown>).expiryTime);
    if (expiryTime !== undefined) {
      sanitized.expiryTime = expiryTime;
    }

    await provider.updateClient(session, sanitized);

    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to update client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
