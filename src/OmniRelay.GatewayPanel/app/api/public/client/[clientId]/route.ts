import { NextResponse } from "next/server";
import { getGatewayProvider } from "@/lib/protocol";
import { getActiveProtocol } from "@/lib/protocol";
import { getProtocolCapabilities } from "@/lib/protocol-capabilities";
import {
  DEFAULT_PUBLIC_CAPABILITIES,
  isClientPubliclyAccessible,
  toPublicClientViewModel
} from "@/lib/public-client";
import { type OmniSession } from "@/lib/session";

interface RouteParams {
  params: Promise<{
    clientId?: string;
  }>;
}

const PUBLIC_SESSION: OmniSession = {
  isAuthenticated: false,
  username: "public"
};

export async function GET(request: Request, context: RouteParams) {
  const { clientId: clientIdRaw } = await context.params;
  const clientId = String(clientIdRaw ?? "").trim();
  if (!clientId) {
    return NextResponse.json({ message: "Not found." }, { status: 404 });
  }

  try {
    const activeProtocol = getActiveProtocol();
    const capabilities = getProtocolCapabilities(activeProtocol);
    if (!capabilities.supportsClientLifecycle) {
      return NextResponse.json({ message: "Not found." }, { status: 404 });
    }

    const provider = getGatewayProvider();
    const snapshot = await provider.getInbound(PUBLIC_SESSION);
    const client = snapshot.clients.find((item) => item.id === clientId);
    if (!client || !isClientPubliclyAccessible(client, Date.now())) {
      return NextResponse.json({ message: "Not found." }, { status: 404 });
    }

    const config = await provider.buildClientConfig(PUBLIC_SESSION, request, clientId);

    return NextResponse.json({
      config,
      client: toPublicClientViewModel(client),
      capabilities: snapshot.capabilities ?? DEFAULT_PUBLIC_CAPABILITIES
    });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Config is not available.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
