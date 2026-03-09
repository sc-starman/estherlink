import { NextResponse } from "next/server";
import { getSession } from "@/lib/session";
import { loginSessionToXui, xuiJson } from "@/lib/xui";

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
  const oldPassword = (body.oldPassword ?? session.password ?? "").trim();
  const newUsername = (body.newUsername ?? "").trim();
  const newPassword = (body.newPassword ?? "").trim();

  if (!oldUsername || !oldPassword || !newUsername || !newPassword) {
    return NextResponse.json({ message: "All password fields are required." }, { status: 400 });
  }

  const form = new URLSearchParams();
  form.set("oldUsername", oldUsername);
  form.set("oldPassword", oldPassword);
  form.set("newUsername", newUsername);
  form.set("newPassword", newPassword);

  try {
    await xuiJson(session, "/panel/setting/updateUser", {
      method: "POST",
      headers: {
        "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"
      },
      body: form
    });

    session.username = newUsername;
    session.password = newPassword;
    session.xuiCookie = undefined;

    const relogin = await loginSessionToXui(session, newUsername, newPassword);
    if (!relogin) {
      return NextResponse.json({ message: "Password changed, but session refresh failed. Please log in again." }, { status: 200 });
    }

    await session.save();
    return NextResponse.json({ ok: true });
  } catch (error) {
    const message = error instanceof Error ? error.message : "Failed to change password.";
    return NextResponse.json({ message }, { status: 400 });
  }
}