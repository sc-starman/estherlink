import { randomUUID } from "node:crypto";
import { type OmniSession } from "@/lib/session";

interface XuiResponse<T = unknown> {
  success?: boolean;
  msg?: string;
  obj?: T;
}

function requireEnv(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) {
    throw new Error(`${name} is not configured.`);
  }

  return value;
}

export function getXuiBaseUrl(): string {
  return requireEnv("XUI_BASE_URL").replace(/\/+$/, "");
}

export function getInboundId(): string {
  return requireEnv("XUI_INBOUND_ID");
}

function extractXuiCookie(setCookieHeader: string | null): string | null {
  if (!setCookieHeader) {
    return null;
  }

  const match = setCookieHeader.match(/3x-ui=[^;]+/i);
  return match ? match[0] : null;
}

async function loginAndStoreCookie(session: OmniSession, username: string, password: string): Promise<boolean> {
  const body = new URLSearchParams();
  body.set("username", username);
  body.set("password", password);
  body.set("twoFactorCode", "");

  const response = await fetch(`${getXuiBaseUrl()}/login/`, {
    method: "POST",
    body,
    redirect: "manual",
    cache: "no-store"
  });

  if (!response.ok) {
    return false;
  }

  const cookie = extractXuiCookie(response.headers.get("set-cookie"));
  if (!cookie) {
    return false;
  }

  session.xuiCookie = cookie;
  session.username = username;
  session.password = password;
  session.isAuthenticated = true;
  return true;
}

export async function loginSessionToXui(session: OmniSession, username: string, password: string): Promise<boolean> {
  return loginAndStoreCookie(session, username, password);
}

async function ensureXuiCookie(session: OmniSession): Promise<void> {
  if (session.xuiCookie) {
    return;
  }

  if (!session.username || !session.password) {
    throw new Error("Session is missing credentials.");
  }

  const ok = await loginAndStoreCookie(session, session.username, session.password);
  if (!ok) {
    throw new Error("3x-ui login failed.");
  }
}

export async function xuiRequest(
  session: OmniSession,
  endpointPath: string,
  init?: RequestInit,
  retryOnAuthError = true
): Promise<Response> {
  await ensureXuiCookie(session);

  const headers = new Headers(init?.headers);
  if (session.xuiCookie) {
    headers.set("cookie", session.xuiCookie);
  }

  const response = await fetch(`${getXuiBaseUrl()}${endpointPath}`, {
    ...init,
    cache: "no-store",
    headers
  });

  if ((response.status === 401 || response.status === 403) && retryOnAuthError && session.username && session.password) {
    const relogin = await loginAndStoreCookie(session, session.username, session.password);
    if (relogin) {
      return xuiRequest(session, endpointPath, init, false);
    }
  }

  return response;
}

export async function xuiJson<T>(
  session: OmniSession,
  endpointPath: string,
  init?: RequestInit
): Promise<XuiResponse<T>> {
  const response = await xuiRequest(session, endpointPath, init);
  const text = await response.text();

  let payload: XuiResponse<T>;
  try {
    payload = text ? (JSON.parse(text) as XuiResponse<T>) : {};
  } catch {
    throw new Error(`Unexpected 3x-ui response (${response.status}).`);
  }

  if (!response.ok || payload.success === false) {
    throw new Error(payload.msg || `3x-ui request failed (${response.status}).`);
  }

  return payload;
}

export function generateClientPayload(email: string) {
  const nowId = randomUUID();
  return {
    id: nowId,
    flow: "xtls-rprx-vision",
    email,
    limitIp: 0,
    totalGB: 0,
    expiryTime: 0,
    enable: true,
    tgId: "",
    subId: randomUUID().replace(/-/g, "").slice(0, 16),
    comment: "",
    reset: 0
  };
}