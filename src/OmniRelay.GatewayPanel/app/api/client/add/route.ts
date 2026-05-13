import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getActiveProtocol, getGatewayProvider } from "@/lib/protocol";
import { getProtocolCapabilities } from "@/lib/protocol-capabilities";

interface AddClientRequest {
  email?: string;
  totalGB?: number;
  expiryTime?: number;
  speedLimitKbps?: number;
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

function normalizeSpeedLimitKbps(value: unknown): number {
  if (value === undefined || value === null || value === "") {
    return 0;
  }

  const speed = Number(value);
  if (!Number.isFinite(speed) || speed < 0) {
    throw new Error("speedLimitKbps must be a non-negative integer.");
  }

  return Math.trunc(speed);
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
    const activeProtocol = getActiveProtocol();
    const capabilities = getProtocolCapabilities(activeProtocol);
    if (!capabilities.supportsClientLifecycle) {
      return NextResponse.json({ message: "Per-client management is not supported for this protocol." }, { status: 400 });
    }

    const totalGB = normalizeTotalGB(body.totalGB);
    const expiryTime = normalizeExpiryTime(body.expiryTime);
    const speedLimitKbps = normalizeSpeedLimitKbps(body.speedLimitKbps);
    const provider = getGatewayProvider();
    const client = await provider.addClient(session, email, { totalGB, expiryTime, speedLimitKbps });

    await session.save();
    return NextResponse.json({ ok: true, client });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to add client.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
