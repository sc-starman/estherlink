import { cookies } from "next/headers";
import { getIronSession, type SessionOptions } from "iron-session";

export interface OmniSession {
  isAuthenticated: boolean;
  username?: string;
  password?: string;
  xuiCookie?: string;
}

const fallbackPassword = "dev-only-change-me-session-password-32chars";
const sessionPassword = process.env.SESSION_SECRET ?? fallbackPassword;

if (sessionPassword.length < 32) {
  throw new Error("SESSION_SECRET must be at least 32 characters.");
}

export const sessionOptions: SessionOptions = {
  cookieName: "omni.gateway.session",
  password: sessionPassword,
  cookieOptions: {
    httpOnly: true,
    secure: process.env.NODE_ENV === "production",
    sameSite: "lax",
    path: "/"
  }
};

export async function getSession() {
  const cookieStore = await cookies();
  return getIronSession<OmniSession>(cookieStore, sessionOptions);
}
