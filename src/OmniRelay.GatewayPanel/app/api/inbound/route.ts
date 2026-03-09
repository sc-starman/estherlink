import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { getInboundId, xuiJson } from "@/lib/xui";

function safeParseJson<T>(raw: unknown, fallback: T): T {
  if (typeof raw !== "string" || !raw.trim()) {
    return fallback;
  }

  try {
    return JSON.parse(raw) as T;
  } catch {
    return fallback;
  }
}

export async function GET() {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  try {
    const inboundId = getInboundId();
    const response = await xuiJson<Record<string, unknown>>(session, `/panel/api/inbounds/get/${encodeURIComponent(inboundId)}`);
    const inbound = response.obj ?? {};

    const settings = safeParseJson<{ clients?: Array<Record<string, unknown>> }>(inbound.settings, { clients: [] });
    const streamSettings = safeParseJson<Record<string, unknown>>(inbound.streamSettings, {});

    await session.save();
    return NextResponse.json({
      inbound: {
        id: inbound.id,
        remark: inbound.remark,
        protocol: inbound.protocol,
        port: inbound.port,
        enable: inbound.enable
      },
      clients: settings.clients ?? [],
      streamSettings
    });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to query inbound.";
    return NextResponse.json({ message }, { status: 400 });
  }
}