import { NextResponse } from "next/server";
import QRCode from "qrcode";
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

function resolveGatewayHost(request: Request): string {
  const configuredHost = process.env.PANEL_PUBLIC_HOST?.trim();
  if (configuredHost) {
    return configuredHost;
  }

  const forwardedHost = request.headers.get("x-forwarded-host")?.split(",")[0]?.trim();
  if (forwardedHost) {
    return forwardedHost.split(":")[0];
  }

  const host = request.headers.get("host") ?? "localhost";
  return host.split(":")[0];
}

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
    const inboundId = getInboundId();
    const response = await xuiJson<Record<string, unknown>>(session, `/panel/api/inbounds/get/${encodeURIComponent(inboundId)}`);
    const inbound = response.obj ?? {};

    const settings = safeParseJson<{ clients?: Array<Record<string, unknown>> }>(inbound.settings, { clients: [] });
    const streamSettings = safeParseJson<Record<string, unknown>>(inbound.streamSettings, {});
    const realitySettings = (streamSettings.realitySettings ?? {}) as Record<string, unknown>;
    const realityInnerSettings = (realitySettings.settings ?? {}) as Record<string, unknown>;

    const client = (settings.clients ?? []).find((item) => item.id === uuid);
    if (!client) {
      return NextResponse.json({ message: "Client not found." }, { status: 404 });
    }

    const host = resolveGatewayHost(request);
    const port = Number(inbound.port ?? 443);
    const sni = Array.isArray(realitySettings.serverNames) ? String(realitySettings.serverNames[0] ?? "") : "";
    const shortId = Array.isArray(realitySettings.shortIds) ? String(realitySettings.shortIds[0] ?? "") : "";
    const publicKey = String(realityInnerSettings.publicKey ?? "");
    const email = String(client.email ?? "OmniClient");

    if (!sni || !shortId || !publicKey) {
      return NextResponse.json({ message: "Inbound REALITY settings are incomplete." }, { status: 400 });
    }

    const query = new URLSearchParams({
      type: "tcp",
      security: "reality",
      flow: "xtls-rprx-vision",
      fp: "chrome",
      sni,
      pbk: publicKey,
      sid: shortId,
      spx: "/",
      encryption: "none"
    });

    const uri = `vless://${uuid}@${host}:${port}?${query.toString()}#${encodeURIComponent(email)}`;
    const qrCodeDataUrl = await QRCode.toDataURL(uri, {
      width: 320,
      margin: 1
    });

    await session.save();
    return NextResponse.json({ uri, qrCodeDataUrl });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to generate client config.";
    return NextResponse.json({ message }, { status: 400 });
  }
}