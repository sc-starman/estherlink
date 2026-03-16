import { cookies } from "next/headers";
import { getIronSession, type SessionOptions } from "iron-session";

export interface OmniSession {
  isAuthenticated: boolean;
  username?: string;
  xuiCookie?: string;
}

const fallbackPassword = "dev-only-change-me-session-password-32chars";
const sessionPassword = process.env.SESSION_SECRET ?? fallbackPassword;

function parseBoolean(value: string | undefined): boolean | null {
  const normalized = (value ?? "").trim().toLowerCase();
  if (!normalized) {
    return null;
  }

  if (["1", "true", "yes", "y", "on"].includes(normalized)) {
    return true;
  }

  if (["0", "false", "no", "n", "off"].includes(normalized)) {
    return false;
  }

  return null;
}

const sessionCookieSecure =
  parseBoolean(process.env.OMNIPANEL_SESSION_SECURE) ?? (process.env.NODE_ENV === "production");

if (sessionPassword.length < 32) {
  throw new Error("SESSION_SECRET must be at least 32 characters.");
}

export const sessionOptions: SessionOptions = {
  cookieName: "omni.gateway.session",
  password: sessionPassword,
  cookieOptions: {
    httpOnly: true,
    secure: sessionCookieSecure,
    sameSite: "lax",
    path: "/"
  }
};

export async function getSession() {
  const cookieStore = await cookies();
  return getIronSession<OmniSession>(cookieStore, sessionOptions);
}
