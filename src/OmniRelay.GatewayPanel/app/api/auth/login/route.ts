import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { loginSessionToXui } from "@/lib/xui";

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
  const ok = await loginSessionToXui(session, username, password);
  if (!ok) {
    return NextResponse.json({ message: "Invalid credentials." }, { status: 401 });
  }

  await session.save();
  return NextResponse.json({ ok: true });
}