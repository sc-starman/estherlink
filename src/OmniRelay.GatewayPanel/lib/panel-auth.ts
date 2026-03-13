import { promises as fs } from "node:fs";
import { dirname } from "node:path";
import { timingSafeEqual } from "node:crypto";

export interface PanelCredentials {
  username: string;
  password: string;
}

interface AuthFilePayload {
  username?: string;
  password?: string;
}

function normalize(value: string | undefined): string {
  return (value ?? "").trim();
}

function getAuthFilePath(): string {
  return normalize(process.env.OMNIPANEL_AUTH_FILE) || "/etc/omnirelay/gateway/panel-auth.json";
}

function getEnvCredentials(): PanelCredentials | null {
  const username = normalize(process.env.OMNIPANEL_AUTH_USERNAME);
  const password = normalize(process.env.OMNIPANEL_AUTH_PASSWORD);
  if (!username || !password) {
    return null;
  }

  return { username, password };
}

async function readFileCredentials(filePath: string): Promise<PanelCredentials | null> {
  try {
    const raw = await fs.readFile(filePath, "utf8");
    const payload = JSON.parse(raw) as AuthFilePayload;
    const username = normalize(payload.username);
    const password = normalize(payload.password);
    if (!username || !password) {
      return null;
    }

    return { username, password };
  } catch {
    return null;
  }
}

async function persistCredentials(filePath: string, credentials: PanelCredentials): Promise<void> {
  const parent = dirname(filePath);
  await fs.mkdir(parent, { recursive: true });
  const tmp = `${filePath}.tmp`;
  const json = JSON.stringify(
    {
      username: credentials.username,
      password: credentials.password,
      updatedAtUtc: new Date().toISOString()
    },
    null,
    2
  );

  await fs.writeFile(tmp, `${json}\n`, { encoding: "utf8", mode: 0o600 });
  await fs.rename(tmp, filePath);
  try {
    await fs.chmod(filePath, 0o600);
  } catch {
    // best effort on non-posix filesystems
  }
}

function secureEqual(left: string, right: string): boolean {
  const leftBuffer = Buffer.from(left, "utf8");
  const rightBuffer = Buffer.from(right, "utf8");
  if (leftBuffer.length !== rightBuffer.length) {
    return false;
  }

  return timingSafeEqual(leftBuffer, rightBuffer);
}

export async function loadPanelCredentials(): Promise<PanelCredentials> {
  const filePath = getAuthFilePath();
  const fromFile = await readFileCredentials(filePath);
  if (fromFile) {
    return fromFile;
  }

  const fromEnv = getEnvCredentials();
  if (fromEnv) {
    return fromEnv;
  }

  throw new Error("OmniPanel credentials are not configured.");
}

export async function verifyPanelCredentials(username: string, password: string): Promise<boolean> {
  const submittedUser = normalize(username);
  const submittedPassword = normalize(password);
  if (!submittedUser || !submittedPassword) {
    return false;
  }

  let configured: PanelCredentials;
  try {
    configured = await loadPanelCredentials();
  } catch {
    return false;
  }

  return secureEqual(submittedUser, configured.username) && secureEqual(submittedPassword, configured.password);
}

export async function changePanelCredentials(
  oldUsername: string,
  oldPassword: string,
  newUsername: string,
  newPassword: string
): Promise<PanelCredentials> {
  const oldUser = normalize(oldUsername);
  const oldPass = normalize(oldPassword);
  const nextUser = normalize(newUsername);
  const nextPass = normalize(newPassword);

  if (!oldUser || !oldPass || !nextUser || !nextPass) {
    throw new Error("All password fields are required.");
  }

  const configured = await loadPanelCredentials();
  if (!secureEqual(oldUser, configured.username) || !secureEqual(oldPass, configured.password)) {
    throw new Error("Invalid current credentials.");
  }

  const updated: PanelCredentials = { username: nextUser, password: nextPass };
  await persistCredentials(getAuthFilePath(), updated);
  return updated;
}
