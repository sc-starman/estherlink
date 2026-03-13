import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { verifyPanelCredentials } from "@/lib/panel-auth";

interface LoginRequest {
  username?: string;
  password?: string;
}

export async function POST(request: Request) {
  const body = (await request.json()) as LoginRequest;
  const username = (body.username ?? "").trim();
  const password = (body.password ?? "").trim();

  if (!username || !password) {
    return NextResponse.json({ message: "Username and password are required." }, { status: 400 });
  }

  const session = await getSession();
  const ok = await verifyPanelCredentials(username, password);
  if (!ok) {
    return NextResponse.json({ message: "Invalid credentials." }, { status: 401 });
  }

  session.isAuthenticated = true;
  session.username = username;
  session.xuiCookie = undefined;
  await session.save();
  return NextResponse.json({ ok: true });
}
