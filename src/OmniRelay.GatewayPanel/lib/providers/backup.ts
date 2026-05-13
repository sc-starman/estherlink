import { promises as fs } from "node:fs";
import { basename, dirname, isAbsolute, join, normalize, relative } from "node:path";
import { tmpdir } from "node:os";
import { execFile as execFileCallback } from "node:child_process";
import { promisify } from "node:util";
import {
  type ProtocolBackupInput,
  type ProtocolBackupPayload
} from "@/lib/providers/types";

const execFile = promisify(execFileCallback);
const BACKUP_ROOT = process.env.OMNIRELAY_PROTOCOL_BACKUP_DIR?.trim() || "/opt/omnirelay/omni-gateway/backups";

async function resolveWritableBackupRoot(): Promise<string> {
  try {
    await fs.mkdir(BACKUP_ROOT, { recursive: true, mode: 0o750 });
    await fs.access(BACKUP_ROOT, fs.constants.W_OK);
    return BACKUP_ROOT;
  } catch {
    throw new Error(`Backup directory is not writable: ${BACKUP_ROOT}`);
  }
}

interface ManifestEntry {
  path: string;
  kind: "file" | "directory";
}

interface ProtocolBundleManifest {
  version: 1;
  protocolId: string;
  createdAtUtc: string;
  entries: ManifestEntry[];
}

export interface BundleSource {
  sourcePath: string;
  archivePath: string;
  required?: boolean;
  dereference?: boolean;
}

export interface BundleDestination {
  archivePath: string;
  destinationPath: string;
  kind: "file" | "directory";
  mode?: number;
}

function timestampForFile(): string {
  return new Date().toISOString().replace(/[:.]/g, "-");
}

async function exists(path: string): Promise<boolean> {
  try {
    await fs.access(path);
    return true;
  } catch {
    return false;
  }
}

function normalizeArchivePath(path: string): string {
  const cleaned = path.replace(/\\/g, "/").replace(/^\.\//, "");
  if (!cleaned || isAbsolute(cleaned) || cleaned.split("/").includes("..")) {
    throw new Error(`Unsafe archive path: ${path}`);
  }
  return cleaned;
}

function stagingPath(root: string, archivePath: string): string {
  const normalizedPath = normalizeArchivePath(archivePath);
  const target = normalize(join(root, normalizedPath));
  const rel = relative(root, target);
  if (rel.startsWith("..") || isAbsolute(rel)) {
    throw new Error(`Unsafe archive path: ${archivePath}`);
  }
  return target;
}

function parseJsonBackupClients(input: ProtocolBackupInput, expectedProtocolId: string): unknown[] {
  const raw = Buffer.from(input.body).toString("utf8");
  const payload = JSON.parse(raw) as unknown;
  if (Array.isArray(payload)) {
    return payload;
  }
  if (typeof payload !== "object" || payload === null) {
    throw new Error("Backup JSON must be a client array or backup envelope.");
  }

  const record = payload as Record<string, unknown>;
  const protocolId = String(record.protocolId ?? "").trim();
  if (protocolId && protocolId !== expectedProtocolId) {
    throw new Error(`Backup protocol ${protocolId} does not match active protocol ${expectedProtocolId}.`);
  }
  if (!Array.isArray(record.clients)) {
    throw new Error("Backup JSON clients array is required.");
  }
  return record.clients;
}

export function createJsonClientBackup(protocolId: string, clients: unknown[]): ProtocolBackupPayload {
  const body = Buffer.from(
    `${JSON.stringify({ version: 1, protocolId, createdAtUtc: new Date().toISOString(), clients }, null, 2)}\n`,
    "utf8"
  );
  return {
    fileName: `clients-${protocolId}-${timestampForFile()}.json`,
    contentType: "application/json",
    body
  };
}

export function readJsonClientBackup(input: ProtocolBackupInput, expectedProtocolId: string): unknown[] {
  return parseJsonBackupClients(input, expectedProtocolId);
}

export async function createTarGzProtocolBackup(
  protocolId: string,
  fileNamePrefix: string,
  sources: BundleSource[]
): Promise<ProtocolBackupPayload> {
  const tempRoot = await fs.mkdtemp(join(tmpdir(), "omnirelay-backup-"));
  const stagingRoot = join(tempRoot, "staging");
  const archivePath = join(tempRoot, "backup.tar.gz");
  const entries: ManifestEntry[] = [];
  await fs.mkdir(stagingRoot, { recursive: true });

  try {
    for (const source of sources) {
      const sourceExists = await exists(source.sourcePath);
      if (!sourceExists) {
        if (source.required) {
          throw new Error(`Required backup source is missing: ${source.sourcePath}`);
        }
        continue;
      }

      const stat = await fs.lstat(source.sourcePath);
      if (stat.isSymbolicLink()) {
        throw new Error(`Refusing to backup symbolic link: ${source.sourcePath}`);
      }
      const archivePathName = normalizeArchivePath(source.archivePath);
      const target = stagingPath(stagingRoot, archivePathName);
      await fs.mkdir(dirname(target), { recursive: true });

      if (stat.isDirectory()) {
        await fs.cp(source.sourcePath, target, { recursive: true, dereference: Boolean(source.dereference) });
        entries.push({ path: archivePathName, kind: "directory" });
      } else if (stat.isFile()) {
        await fs.copyFile(source.sourcePath, target);
        entries.push({ path: archivePathName, kind: "file" });
      }
    }

    const manifest: ProtocolBundleManifest = {
      version: 1,
      protocolId,
      createdAtUtc: new Date().toISOString(),
      entries
    };
    await fs.writeFile(join(stagingRoot, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
    await listExtractedEntries(stagingRoot);
    await execFile("tar", ["-czf", archivePath, "-C", stagingRoot, "."]);
    const body = await fs.readFile(archivePath);
    return {
      fileName: `${fileNamePrefix}-${timestampForFile()}.tar.gz`,
      contentType: "application/gzip",
      body
    };
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
}

async function writeArchiveToTemp(input: ProtocolBackupInput, tempRoot: string): Promise<string> {
  const archivePath = join(tempRoot, basename(input.fileName || "backup.tar.gz"));
  await fs.writeFile(archivePath, Buffer.from(input.body));
  return archivePath;
}

async function validateTarListing(archivePath: string): Promise<void> {
  const { stdout } = await execFile("tar", ["-tvzf", archivePath]);
  for (const line of stdout.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (!trimmed) {
      continue;
    }
    const type = trimmed[0];
    if (type === "l" || type === "h") {
      throw new Error("Backup archive contains links, which are not allowed.");
    }
  }

  const list = await execFile("tar", ["-tzf", archivePath]);
  for (const rawEntry of list.stdout.split(/\r?\n/)) {
    const entry = rawEntry.trim();
    if (!entry) {
      continue;
    }
    if (entry === "." || entry === "./") {
      continue;
    }
    normalizeArchivePath(entry);
  }
}

async function listExtractedEntries(root: string): Promise<string[]> {
  const entries: string[] = [];
  async function walk(current: string): Promise<void> {
    const children = await fs.readdir(current, { withFileTypes: true });
    for (const child of children) {
      const absolute = join(current, child.name);
      const rel = relative(root, absolute).replace(/\\/g, "/");
      const stat = await fs.lstat(absolute);
      if (stat.isSymbolicLink()) {
        throw new Error(`Backup contains symbolic link: ${rel}`);
      }
      entries.push(rel);
      if (child.isDirectory()) {
        await walk(absolute);
      }
    }
  }
  await walk(root);
  return entries;
}

function ensureExtractedEntryIsAllowed(path: string, manifest: ProtocolBundleManifest): void {
  const normalizedPath = normalizeArchivePath(path);
  if (normalizedPath === "manifest.json") {
    return;
  }
  for (const entry of manifest.entries) {
    const entryPath = normalizeArchivePath(entry.path);
    // Allow implicit parent directories emitted by tar extraction for nested entries,
    // e.g. "openvpn-state" when manifest contains "openvpn-state/pki".
    if (entryPath.startsWith(`${normalizedPath}/`)) {
      return;
    }
    if (entry.kind === "file" && normalizedPath === entryPath) {
      return;
    }
    if (entry.kind === "directory" && (normalizedPath === entryPath || normalizedPath.startsWith(`${entryPath}/`))) {
      return;
    }
  }
  throw new Error(`Backup contains unexpected entry: ${path}`);
}

function readManifest(payload: unknown, expectedProtocolId: string): ProtocolBundleManifest {
  if (typeof payload !== "object" || payload === null) {
    throw new Error("Backup manifest is invalid.");
  }
  const manifest = payload as Partial<ProtocolBundleManifest>;
  if (manifest.version !== 1) {
    throw new Error("Unsupported backup manifest version.");
  }
  if (manifest.protocolId !== expectedProtocolId) {
    throw new Error(`Backup protocol ${String(manifest.protocolId ?? "")} does not match active protocol ${expectedProtocolId}.`);
  }
  if (!Array.isArray(manifest.entries)) {
    throw new Error("Backup manifest entries are missing.");
  }
  for (const entry of manifest.entries) {
    normalizeArchivePath(entry.path);
    if (entry.kind !== "file" && entry.kind !== "directory") {
      throw new Error(`Backup manifest contains invalid entry kind for ${entry.path}.`);
    }
  }
  return manifest as ProtocolBundleManifest;
}

function ensureExpectedEntry(entry: ManifestEntry, destinations: BundleDestination[]): BundleDestination {
  const archivePath = normalizeArchivePath(entry.path);
  const destination = destinations.find((item) => item.archivePath === archivePath && item.kind === entry.kind);
  if (!destination) {
    throw new Error(`Backup contains unexpected entry: ${entry.path}`);
  }
  return destination;
}

export async function importTarGzProtocolBackup(
  expectedProtocolId: string,
  input: ProtocolBackupInput,
  destinations: BundleDestination[]
): Promise<void> {
  const tempRoot = await fs.mkdtemp(join(tmpdir(), "omnirelay-restore-"));
  const extractRoot = join(tempRoot, "extract");
  const backupRoot = await resolveWritableBackupRoot();
  const currentBackupRoot = join(backupRoot, `${expectedProtocolId}-${timestampForFile()}`);

  try {
    await fs.mkdir(extractRoot, { recursive: true });
    const archivePath = await writeArchiveToTemp(input, tempRoot);
    await validateTarListing(archivePath);
    await execFile("tar", ["-xzf", archivePath, "-C", extractRoot]);

    const manifestRaw = await fs.readFile(join(extractRoot, "manifest.json"), "utf8");
    const manifest = readManifest(JSON.parse(manifestRaw) as unknown, expectedProtocolId);
    const extractedEntries = await listExtractedEntries(extractRoot);
    for (const entry of extractedEntries) {
      ensureExtractedEntryIsAllowed(entry, manifest);
    }

    for (const entry of manifest.entries) {
      ensureExpectedEntry(entry, destinations);
      const source = stagingPath(extractRoot, entry.path);
      const stat = await fs.lstat(source);
      if (stat.isSymbolicLink()) {
        throw new Error(`Backup contains symbolic link: ${entry.path}`);
      }
      if (entry.kind === "file" && !stat.isFile()) {
        throw new Error(`Backup entry is not a file: ${entry.path}`);
      }
      if (entry.kind === "directory" && !stat.isDirectory()) {
        throw new Error(`Backup entry is not a directory: ${entry.path}`);
      }
    }

    for (const destination of destinations) {
      if (await exists(destination.destinationPath)) {
        const backupPath = join(currentBackupRoot, destination.archivePath);
        await fs.mkdir(dirname(backupPath), { recursive: true });
        await fs.cp(destination.destinationPath, backupPath, { recursive: true, dereference: false });
      }
    }

    for (const destination of destinations) {
      if (destination.kind === "directory") {
        await fs.rm(destination.destinationPath, { recursive: true, force: true });
      }
    }

    for (const entry of manifest.entries) {
      const destination = ensureExpectedEntry(entry, destinations);
      const source = stagingPath(extractRoot, entry.path);
      await fs.mkdir(dirname(destination.destinationPath), { recursive: true });
      if (entry.kind === "file") {
        // Avoid unlink/rename replacement paths (can fail on strict directory perms).
        // Truncate/write in place when target exists.
        const body = await fs.readFile(source);
        await fs.writeFile(destination.destinationPath, body);
      } else {
        await fs.cp(source, destination.destinationPath, {
          recursive: true,
          dereference: false,
          force: true
        });
      }
      if (destination.mode !== undefined) {
        try {
          await fs.chmod(destination.destinationPath, destination.mode);
        } catch (error) {
          const err = error as NodeJS.ErrnoException;
          if (err?.code !== "EPERM" && err?.code !== "EACCES") {
            throw error;
          }
        }
      }
    }
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
}

export async function readTarGzBundleEntryUtf8(
  expectedProtocolId: string,
  input: ProtocolBackupInput,
  archivePath: string
): Promise<string | null> {
  const tempRoot = await fs.mkdtemp(join(tmpdir(), "omnirelay-read-bundle-"));
  const extractRoot = join(tempRoot, "extract");
  const normalizedEntry = normalizeArchivePath(archivePath);
  try {
    await fs.mkdir(extractRoot, { recursive: true });
    const archiveFile = await writeArchiveToTemp(input, tempRoot);
    await validateTarListing(archiveFile);
    await execFile("tar", ["-xzf", archiveFile, "-C", extractRoot]);

    const manifestRaw = await fs.readFile(join(extractRoot, "manifest.json"), "utf8");
    const manifest = readManifest(JSON.parse(manifestRaw) as unknown, expectedProtocolId);
    const extractedEntries = await listExtractedEntries(extractRoot);
    for (const entry of extractedEntries) {
      ensureExtractedEntryIsAllowed(entry, manifest);
    }

    const sourcePath = stagingPath(extractRoot, normalizedEntry);
    if (!(await exists(sourcePath))) {
      return null;
    }

    const stat = await fs.lstat(sourcePath);
    if (!stat.isFile() || stat.isSymbolicLink()) {
      throw new Error(`Backup entry is not a regular file: ${normalizedEntry}`);
    }

    return await fs.readFile(sourcePath, "utf8");
  } finally {
    await fs.rm(tempRoot, { recursive: true, force: true });
  }
}
