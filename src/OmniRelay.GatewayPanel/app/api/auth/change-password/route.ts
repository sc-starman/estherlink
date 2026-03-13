import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { changePanelCredentials } from "@/lib/panel-auth";

interface ChangePasswordRequest {
  oldUsername?: string;
  oldPassword?: string;
  newUsername?: string;
  newPassword?: string;
}

export async function POST(request: Request) {
  const session = await getSession();
  if (!session.isAuthenticated) {
    return NextResponse.json({ message: "Unauthorized." }, { status: 401 });
  }

  const body = (await request.json()) as ChangePasswordRequest;
  const oldUsername = (body.oldUsername ?? session.username ?? "").trim();
  const oldPassword = (body.oldPassword ?? "").trim();
  const newUsername = (body.newUsername ?? "").trim();
  const newPassword = (body.newPassword ?? "").trim();

  if (!oldUsername || !oldPassword || !newUsername || !newPassword) {
    return NextResponse.json({ message: "All password fields are required." }, { status: 400 });
  }

  try {
    await changePanelCredentials(oldUsername, oldPassword, newUsername, newPassword);
    session.username = newUsername;
    session.xuiCookie = undefined;
    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to change password.";
    return NextResponse.json({ message }, { status: 400 });
  }
}
