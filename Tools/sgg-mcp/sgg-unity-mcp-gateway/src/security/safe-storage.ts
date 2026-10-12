import { randomUUID, createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import fs, { type BigIntStats } from "node:fs";
import path from "node:path";

export type ProcessOwner = {
  schema_version: 1;
  nonce: string;
  pid: number;
  process_start_id: string | null;
  acquired_at: string;
};

export type ProcessOwnerGenerationState = "live" | "dead" | "replaced" | "uncertain";

export type StorageLockRecovery = {
  schema_version: 1;
  owner_generation_state: "none" | ProcessOwnerGenerationState;
  automatic_recovery: "not_needed" | "reclaimed_dead_owner" | "reclaimed_replaced_owner" | "resumed_interrupted_transition";
};

export type StorageLockFailure = {
  schema_version: 1;
  stage: "lock_acquisition";
  category: "lock_owner_live" | "lock_owner_generation_uncertain" | "transition_gate_live" | "transition_gate_generation_uncertain" | "lock_contention";
  owner_generation_state: "none" | ProcessOwnerGenerationState | "invalid";
  automatic_recovery: "not_attempted";
  retryable: boolean;
  recovery_hint: "retry_after_owner_exit" | "inspect_storage_owner_generation" | "retry_after_transient_contention";
};

export type StorageLock = { diagnostics: StorageLockRecovery; release(): void };

export class StorageLockUnavailableError extends Error {
  constructor(readonly diagnostics: StorageLockFailure, message: string) {
    super(message);
    this.name = "StorageLockUnavailableError";
  }
}

export type StorageLockOptions = {
  timeoutMs?: number;
  staleAfterMs?: number;
  beforeGenerationRename?: () => void;
  afterTransitionGate?: () => void;
  afterBlockedTransition?: () => void;
  afterTransitionMarker?: () => void;
  beforeTransitionRename?: () => void;
  afterTransitionCleanup?: () => void;
  beforeDeadTransitionGateRetirement?: () => void;
  beforeLockOwnerRevalidation?: () => void;
  afterDeadTransitionGateRetirement?: () => void;
};

export type FileObjectIdentity = { dev: bigint; ino: bigint };
export type SecureFileStat = FileObjectIdentity & {
  size: number;
  nlink: number;
  mtime: Date;
  mtimeMs: number;
};

type FileObjectStat = SecureFileStat & {
  isFile: boolean;
  isDirectory: boolean;
  isSymbolicLink: boolean;
};

const NO_FOLLOW = fs.constants.O_NOFOLLOW ?? 0;
const DIRECTORY = fs.constants.O_DIRECTORY ?? 0;
const SLEEP = new Int32Array(new SharedArrayBuffer(4));
const CURRENT_PROCESS_START_FALLBACK = `uptime:${Math.floor(Date.now() - process.uptime() * 1000)}`;
const WINDOWS_PROCESS_QUERY_TIMEOUT_MS = 2_000;
const PROCESS_START_TOLERANCE_MS = 2_000;
let currentWindowsProcessStartIdentity: string | null | undefined;
let currentWindowsProcessStartRetryAt = 0;
const windowsProcessObservationCache = new Map<number, { capturedAt: number; observation: WindowsProcessObservation | null }>();
const DELETE_OPERATION_GRACE_MS = 30_000;
const MAX_PUBLISH_TEMP_BYTES = 1024 * 1024;
const MAX_PUBLISH_TEMPS = 256;
const LOCK_TRANSITION_RETRY_MS = 250;
const LOCK_GATE_ACQUIRE_TIMEOUT_MS = 5_000;
const LOCK_ACTIVE_INSPECTION_INTERVAL_MS = 25;
const UUID_PATTERN = "[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}";
const UUID_REGEX = new RegExp(`^${UUID_PATTERN}$`);
const LEGACY_NONCE_REGEX = /^legacy-([1-9]\d*)$/;

export type DurablePublishOptions = {
  afterTempFsync?: () => void;
  afterPublish?: () => void;
  beforeTempCleanupStat?: (temp: string, final: string) => void;
  beforeTempCleanupUnlink?: (temp: string, final: string) => void;
};
export type DurableDeleteOptions = { afterRename?: () => void; metadataPublish?: DurablePublishOptions; expectedIdentity?: FileObjectIdentity };
export type SecureRegularFilesOptions = {
  afterEntryEnumerated?: (file: string) => void;
  afterEntryLstat?: (file: string, stat: SecureFileStat) => void;
  afterEntryRealpath?: (file: string, realpath: string) => void;
};
export type MaintainPublisherTempsOptions = {
  orphanedContainer?: boolean;
  deadlineUnixMs?: number;
  beforeRedundantTempStat?: (temp: string, final: string) => void;
  beforeRedundantTempUnlink?: (temp: string, final: string) => void;
};

export function absoluteStoragePath(value: string): string {
  return absolutePath(value);
}

export function ensureSecureRoot(root: string): string {
  const resolved = absolutePath(root);
  ensureDirectoryChain(resolved);
  const stat = fs.lstatSync(resolved);
  if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Unsafe storage root");
  assertPrivateDirectory(stat);
  const real = fs.realpathSync.native(resolved);
  if (!samePath(real, resolved)) throw new Error("Unsafe storage root containment");
  return real;
}

export function ensureSecureDirectory(root: string, relative: string): string {
  const secureRoot = ensureSecureRoot(root);
  const target = containedPath(secureRoot, relative);
  let current = secureRoot;
  for (const segment of path.relative(secureRoot, target).split(path.sep).filter(Boolean)) {
    current = path.join(current, segment);
    try { fs.mkdirSync(current, { mode: 0o700 }); fsyncDirectory(path.dirname(current)); } catch (error) { if (!isCode(error, "EEXIST")) throw error; }
    const stat = fs.lstatSync(current);
    if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Unsafe storage directory component");
    assertPrivateDirectory(stat);
    assertContained(secureRoot, fs.realpathSync.native(current));
  }
  return target;
}

export function secureRegularFiles(root: string, dir: string, predicate: (name: string) => boolean, options: SecureRegularFilesOptions = {}): string[] {
  const secureRoot = ensureSecureRoot(root);
  assertSecureDirectory(secureRoot, dir);
  const files: string[] = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (!predicate(entry.name)) continue;
    const disappearingPublicationTemp = entry.isFile() && isExpectedPublicationTempName(entry.name);
    if (!entry.isFile()) throw new Error("Unsafe storage file");
    const file = containedPath(secureRoot, path.relative(secureRoot, path.join(dir, entry.name)));
    options.afterEntryEnumerated?.(file);
    const stat = lstatEnumeratedFile(file, disappearingPublicationTemp);
    if (!stat) continue;
    if (!stat.isFile || stat.isSymbolicLink) throw new Error("Unsafe storage file");
    options.afterEntryLstat?.(file, stat);
    const real = realpathEnumeratedFile(secureRoot, file, stat, disappearingPublicationTemp);
    if (!real) continue;
    options.afterEntryRealpath?.(file, real);
    const after = lstatEnumeratedFile(file, disappearingPublicationTemp, stat);
    if (!after) continue;
    if (!after.isFile || after.isSymbolicLink || !sameIdentity(stat, after)) throw new Error("Storage file identity changed during enumeration");
    files.push(file);
  }
  return files;
}

export function secureFileStat(root: string, file: string): SecureFileStat | undefined {
  const secureRoot = ensureSecureRoot(root);
  assertContained(secureRoot, file);
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    let stat: FileObjectStat;
    try { stat = lstatFileObject(file); }
    catch (error) {
      if (isCode(error, "ENOENT")) return undefined;
      if (isRetryableWindowsPathContention(error) && Date.now() < deadline) { Atomics.wait(SLEEP, 0, 0, 5); continue; }
      throw error;
    }
    if (!stat.isFile || stat.isSymbolicLink) throw new Error("Unsafe storage file");
    try { assertContained(secureRoot, fs.realpathSync.native(file)); return stat; }
    catch (error) {
      let current: FileObjectStat;
      try { current = lstatFileObject(file); }
      catch (afterError) { if (isCode(afterError, "ENOENT")) return undefined; throw error; }
      if (!current.isFile || current.isSymbolicLink || !sameIdentity(stat, current)) throw error;
      const transientContainment = process.platform === "win32" && error instanceof Error && error.message === "Storage path escapes root";
      if ((!isRetryableWindowsPathContention(error) && !transientContainment) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function lstatEnumeratedFile(file: string, disappearingPublicationTemp: boolean, expected?: FileObjectIdentity): FileObjectStat | undefined {
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    try {
      const stat = lstatFileObject(file);
      if (expected && !sameIdentity(expected, stat)) throw new Error("Storage file identity changed during enumeration");
      return stat;
    } catch (error) {
      if (disappearingPublicationTemp && isCode(error, "ENOENT")) return undefined;
      if (!disappearingPublicationTemp || !isRetryableWindowsPathContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function realpathEnumeratedFile(root: string, file: string, identity: FileObjectIdentity, disappearingPublicationTemp: boolean): string | undefined {
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    try {
      const real = fs.realpathSync.native(file);
      assertContained(root, real);
      return real;
    }
    catch (error) {
      if (disappearingPublicationTemp && isCode(error, "ENOENT")) return undefined;
      const transientContainment = process.platform === "win32" && error instanceof Error && error.message === "Storage path escapes root";
      if (!disappearingPublicationTemp || !isRetryableWindowsPathContention(error) && !transientContainment || Date.now() >= deadline) throw error;
      let current: FileObjectStat;
      try { current = lstatFileObject(file); }
      catch (afterError) { if (isCode(afterError, "ENOENT")) return undefined; throw error; }
      if (!sameIdentity(identity, current)) throw new Error("Storage file identity changed during enumeration");
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

export function openSecureRegularFile(root: string, file: string, flags: number, mode = 0o600): number {
  const secureRoot = ensureSecureRoot(root);
  assertContained(secureRoot, file);
  const before = secureFileStat(secureRoot, file);
  const handle = fs.openSync(file, flags | NO_FOLLOW, mode);
  try {
    const opened = fstatFileObject(handle);
    if (!opened.isFile || (before && !sameIdentity(before, opened))) throw new Error("Storage file identity changed");
    const after = lstatFileObject(file);
    if (!after.isFile || after.isSymbolicLink || !sameIdentity(after, opened)) throw new Error("Storage file identity changed");
    assertContained(secureRoot, fs.realpathSync.native(file));
    return handle;
  } catch (error) { fs.closeSync(handle); throw error; }
}

export function readSecureFile(root: string, file: string, maxBytes = Number.MAX_SAFE_INTEGER): Buffer {
  const stat = secureFileStat(root, file);
  if (!stat) throw Object.assign(new Error("Storage file disappeared"), { code: "ENOENT" });
  if (stat.size > maxBytes) throw new Error("Storage file exceeds read limit");
  const handle = openSecureRegularFile(root, file, fs.constants.O_RDONLY);
  try { return fs.readFileSync(handle); } finally { fs.closeSync(handle); }
}

export function writeNewFileDurable(root: string, file: string, data: string | Buffer, options: DurablePublishOptions = {}): void {
  const secureRoot = ensureSecureRoot(root);
  const parent = path.dirname(file);
  assertSecureDirectory(secureRoot, parent);
  maintainPublisherTemps(secureRoot, parent);
  if (Buffer.byteLength(data) > MAX_PUBLISH_TEMP_BYTES) throw new Error("Durable publication exceeds temporary file limit");
  const temp = containedPath(secureRoot, path.relative(secureRoot, path.join(parent, `.publish-v1-${Date.now()}-${process.pid}-${currentProcessPublicationIdentityTag()}-${randomUUID()}.tmp`)));
  const handle = fs.openSync(temp, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL | NO_FOLLOW, 0o600);
  let tempIdentity: FileObjectIdentity | undefined;
  let finalIdentityVerified = false;
  try {
    const tempStat = fstatFileObject(handle);
    tempIdentity = tempStat;
    if (!tempStat.isFile) throw new Error("Unsafe temporary file");
    fs.writeFileSync(handle, data);
    fs.fsyncSync(handle);
    options.afterTempFsync?.();
    ensureSecureRoot(secureRoot);
    assertSecureDirectory(secureRoot, parent);
    try { fs.linkSync(temp, file); }
    catch (error) {
      const existing = isCode(error, "EEXIST") ? secureFileStat(secureRoot, file) : undefined;
      if (!existing || !sameIdentity(tempIdentity, existing)) throw error;
    }
    const published = secureFileStat(secureRoot, file);
    if (!published || !sameIdentity(tempIdentity, published)) throw new Error("Storage publication identity changed");
    finalIdentityVerified = true;
    fsyncDirectory(parent);
    options.afterPublish?.();
    ensureSecureRoot(secureRoot);
    assertSecureDirectory(secureRoot, parent);
  } finally {
    fs.closeSync(handle);
    if (tempIdentity) {
      if (finalIdentityVerified) {
        unlinkRedundantPublisherTemp(secureRoot, temp, tempIdentity, file, {
          ...(options.beforeTempCleanupStat ? { beforeRedundantTempStat: options.beforeTempCleanupStat } : {}),
          ...(options.beforeTempCleanupUnlink ? { beforeRedundantTempUnlink: options.beforeTempCleanupUnlink } : {}),
        });
      } else {
        unlinkUnpublishedPublisherTemp(secureRoot, temp, tempIdentity, file, options);
      }
    }
  }
}

export function durableRenameFile(root: string, source: string, destination: string): void {
  const secureRoot = ensureSecureRoot(root);
  const sourceStat = secureFileStatRequired(secureRoot, source);
  assertSecureDirectory(secureRoot, path.dirname(destination));
  if (secureFileStat(secureRoot, destination)) throw new Error("Storage destination exists");
  fs.renameSync(source, destination);
  const destinationStat = secureFileStatRequired(secureRoot, destination);
  if (!sameIdentity(sourceStat, destinationStat)) throw new Error("Storage file identity changed during rename");
  fsyncDirectory(path.dirname(source));
  if (path.dirname(destination) !== path.dirname(source)) fsyncDirectory(path.dirname(destination));
  ensureSecureRoot(secureRoot);
  assertSecureDirectory(secureRoot, path.dirname(destination));
}

export function durableUnlinkFile(root: string, file: string, options: DurableDeleteOptions = {}): void {
  const secureRoot = ensureSecureRoot(root);
  const sourceStat = secureFileStatRequired(secureRoot, file);
  if (options.expectedIdentity && !sameIdentity(options.expectedIdentity, sourceStat)) throw new Error("Storage file identity does not match the deletion precondition");
  const parent = path.dirname(file);
  assertSecureDirectory(secureRoot, parent);
  const nonce = randomUUID();
  const tombName = `.delete-v1-${nonce}.tomb`;
  const tombstone = path.join(parent, tombName);
  const metadataPath = path.join(parent, `.delete-v1-${nonce}.json`);
  const metadata: DeleteTombstoneMetadata = { schema_version: 1, operation: "delete", nonce, basename_hash: createHash("sha256").update(path.basename(file), "utf8").digest("hex"), dev: sourceStat.dev.toString(), ino: sourceStat.ino.toString(), created_at: new Date().toISOString(), tomb_name: tombName };
  writeNewFileDurable(secureRoot, metadataPath, `${JSON.stringify(metadata)}\n`, options.metadataPublish);
  if (!sameIdentity(sourceStat, secureFileStatRequired(secureRoot, file))) throw new Error("Storage file identity changed before deletion");
  try { fs.renameSync(file, tombstone); } catch (error) { unlinkVerifiedNow(secureRoot, metadataPath); throw error; }
  const moved = secureFileStatRequired(secureRoot, tombstone);
  if (!sameIdentity(sourceStat, moved)) throw new Error("Storage file identity changed during deletion");
  fsyncDirectory(parent);
  ensureSecureRoot(secureRoot);
  assertSecureDirectory(secureRoot, parent);
  options.afterRename?.();
  finalizeDeletePair(secureRoot, parent, nonce);
  ensureSecureRoot(secureRoot);
}

export function maintainPublisherTemps(root: string, dir: string, options: MaintainPublisherTempsOptions = {}): void {
  const secureRoot = ensureSecureRoot(root);
  assertSecureDirectory(secureRoot, dir);
  const temps = secureRegularFiles(secureRoot, dir, isExpectedPublicationTempName);
  if (temps.length > MAX_PUBLISH_TEMPS) throw new Error("Publisher temporary file limit exceeded");
  for (const temp of temps) {
    if (options.deadlineUnixMs !== undefined && Date.now() >= options.deadlineUnixMs) throw new Error("Publisher temporary maintenance deadline reached");
    const match = new RegExp(`^\\.publish-v1-(\\d+)-(\\d+)-(x[0-9a-f]{16}|[0-9a-f]{16}|unknown)-(${UUID_PATTERN})\\.tmp$`).exec(path.basename(temp));
    if (!match) throw new Error("Publisher temporary name is invalid");
    const createdAt = Number(match[1]);
    const ownerPid = Number(match[2]);
    const ownerTag = match[3]!;
    const stat = secureFileStat(secureRoot, temp);
    if (!stat) continue;
    if (!Number.isSafeInteger(createdAt) || createdAt <= 0 || createdAt > Date.now() + 60_000 || !Number.isSafeInteger(ownerPid) || ownerPid <= 0 || stat.size > MAX_PUBLISH_TEMP_BYTES) throw new Error("Publisher temporary metadata is invalid");
    const linkedFinals = sameIdentityFiles(secureRoot, dir, temp, stat);
    const ownerState = options.orphanedContainer ? "dead" : processIdentityTagOwnerState(ownerPid, ownerTag, options.deadlineUnixMs);
    if (linkedFinals.length > 1 || (stat.nlink > 1 && linkedFinals.length === 0)) throw new Error("Publisher temporary link identity is ambiguous");
    if (linkedFinals.length === 1) {
      const content = ownerState === "dead" ? readSecureFile(secureRoot, temp, MAX_PUBLISH_TEMP_BYTES).toString("utf8") : undefined;
      const deleteMetadata = content ? parseDeleteTombstoneMetadataForRecovery(content) : undefined;
      unlinkRedundantPublisherTemp(secureRoot, temp, stat, linkedFinals[0]!, options);
      if (deleteMetadata && path.basename(linkedFinals[0]!) === `.delete-v1-${deleteMetadata.nonce}.json` && !secureFileStat(secureRoot, path.join(dir, deleteMetadata.tomb_name))) unlinkVerifiedNow(secureRoot, linkedFinals[0]!);
      continue;
    }
    if (ownerState === "live" || (ownerState === "uncertain" && Date.now() - createdAt < DELETE_OPERATION_GRACE_MS)) continue;
    if (ownerState === "uncertain") throw new Error("Publisher temporary owner is uncertain");
    const content = readSecureFile(secureRoot, temp, MAX_PUBLISH_TEMP_BYTES);
    const deleteMetadata = parseDeleteTombstoneMetadataForRecovery(content.toString("utf8"));
    let recoveredDeleteMetadataPath: string | undefined;
    if (deleteMetadata) {
      const finalPath = path.join(dir, `.delete-v1-${deleteMetadata.nonce}.json`);
      const existing = secureFileStat(secureRoot, finalPath);
      if (existing) {
        if (!sameIdentity(stat, existing)) throw new Error("Delete metadata publication identity is ambiguous");
      } else {
        try { fs.linkSync(temp, finalPath); }
        catch (error) {
          const raced = isCode(error, "EEXIST") ? secureFileStat(secureRoot, finalPath) : undefined;
          if (!raced || !sameIdentity(stat, raced)) throw error;
        }
      }
      const finalStat = secureFileStatRequired(secureRoot, finalPath);
      if (!sameIdentity(stat, finalStat)) throw new Error("Recovered publication identity changed");
      fsyncDirectory(dir);
      recoveredDeleteMetadataPath = finalPath;
    }
    if (recoveredDeleteMetadataPath) {
      unlinkRedundantPublisherTemp(secureRoot, temp, stat, recoveredDeleteMetadataPath, options);
    } else {
      unlinkVerifiedNow(secureRoot, temp);
    }
    if (deleteMetadata && recoveredDeleteMetadataPath && !secureFileStat(secureRoot, path.join(dir, deleteMetadata.tomb_name))) unlinkVerifiedNow(secureRoot, recoveredDeleteMetadataPath);
  }
}

export function publisherTempFiles(root: string, dir: string): string[] {
  return secureRegularFiles(root, dir, isExpectedPublicationTempName);
}

export function maintainDeleteTombstones(root: string, dir: string, options: MaintainPublisherTempsOptions = {}): void {
  const secureRoot = ensureSecureRoot(root);
  assertSecureDirectory(secureRoot, dir);
  maintainPublisherTemps(secureRoot, dir, options);
  const artifacts = fs.readdirSync(dir, { withFileTypes: true }).filter((entry) => new RegExp(`^\\.delete-v1-${UUID_PATTERN}\\.(?:tomb|json)$`).test(entry.name));
  const nonces = new Set(artifacts.map((entry) => new RegExp(`^\\.delete-v1-(${UUID_PATTERN})\\.`).exec(entry.name)?.[1]).filter((value): value is string => Boolean(value)));
  for (const nonce of nonces) finalizeDeletePair(secureRoot, dir, nonce, true);
}

export function fsyncDirectory(dir: string): void {
  let handle: number | undefined;
  try {
    handle = fs.openSync(dir, fs.constants.O_RDONLY | DIRECTORY);
    fs.fsyncSync(handle);
  } catch (error) {
    if (process.platform === "win32" && (isCode(error, "EPERM") || isCode(error, "EACCES") || isCode(error, "EINVAL") || isCode(error, "EISDIR"))) return;
    throw error;
  } finally { if (handle !== undefined) fs.closeSync(handle); }
}

export function acquireStorageLock(root: string, name: string, options: StorageLockOptions = {}): StorageLock {
  const secureRoot = ensureSecureRoot(root);
  const lock = containedPath(secureRoot, name);
  const deadline = Date.now() + (options.timeoutMs ?? 5_000);
  maintainDeleteTombstones(secureRoot, secureRoot, { deadlineUnixMs: deadline });
  const staleAfterMs = options.staleAfterMs ?? 30_000;
  cleanupAbandonedGenerations(secureRoot, lock, staleAfterMs, deadline);
  cleanupAbandonedGenerations(secureRoot, transitionGate(lock), staleAfterMs, deadline);
  let nextActiveInspection = 0;
  let recovery: StorageLockRecovery = { schema_version: 1, owner_generation_state: "none", automatic_recovery: "not_needed" };
  while (true) {
    if (secureLockDirectoryExists(secureRoot, lock) && Date.now() < nextActiveInspection) {
      Atomics.wait(SLEEP, 0, 0, 5);
      continue;
    }
    if (secureLockDirectoryExists(secureRoot, lock) && nextActiveInspection === 0
      && deadline - Date.now() > LOCK_ACTIVE_INSPECTION_INTERVAL_MS * 2) {
      nextActiveInspection = Date.now() + LOCK_ACTIVE_INSPECTION_INTERVAL_MS;
      Atomics.wait(SLEEP, 0, 0, 5);
      continue;
    }
    let gate: TransitionGateLease | undefined;
    let publishedOwner: ProcessOwner | undefined;
    try {
      gate = tryAcquireTransitionGate(secureRoot, lock, boundedTransitionDeadline(deadline), options);
      if (gate) {
        options.afterTransitionGate?.();
        const activeAtGateEntry = secureLockDirectoryExists(secureRoot, lock);
        let authorizedTransition = false;
        let transitionBlocked = cleanupAbandonedTombstones(secureRoot, lock, staleAfterMs, deadline);
        if (!transitionBlocked && secureLockDirectoryExists(secureRoot, lock)) {
          const resume = resumeActiveLockTransition(secureRoot, lock, boundedTransitionDeadline(deadline), options);
          transitionBlocked = resume.status === "blocked";
          authorizedTransition = resume.status === "resumed";
          if (resume.status === "resumed") recovery = { schema_version: 1, owner_generation_state: resume.owner_generation_state, automatic_recovery: "resumed_interrupted_transition" };
          if (resume.status === "none") {
            const reclaim = tryReclaimStorageLock(secureRoot, lock, staleAfterMs, deadline, options);
            authorizedTransition = reclaim !== "not_reclaimed";
            if (reclaim === "reclaimed_dead_owner") recovery = { schema_version: 1, owner_generation_state: "dead", automatic_recovery: reclaim };
            if (reclaim === "reclaimed_replaced_owner") recovery = { schema_version: 1, owner_generation_state: "replaced", automatic_recovery: reclaim };
          }
        }
        if (activeAtGateEntry && !authorizedTransition && !secureLockDirectoryExists(secureRoot, lock)) transitionBlocked = true;
        if (!transitionBlocked && !secureLockDirectoryExists(secureRoot, lock)) {
          const owner = currentProcessOwner();
          const generation = `${lock}.gen-${owner.nonce}`;
          try {
            fs.mkdirSync(generation, { mode: 0o700 });
            fsyncDirectory(path.dirname(generation));
            writeNewFileDurable(secureRoot, path.join(generation, "owner.json"), `${JSON.stringify(owner)}\n`);
            if (publishLockGeneration(secureRoot, generation, lock, owner, gate, deadline, options)) publishedOwner = owner;
            else cleanupInactiveGeneration(secureRoot, generation, owner.nonce);
          } catch (error) {
            cleanupInactiveGeneration(secureRoot, generation, owner.nonce);
            if (!isExpectedLockRace(error)) throw error;
          }
        }
      }
    } finally {
      if (gate) releaseTransitionGate(secureRoot, gate);
    }
    if (publishedOwner) return { diagnostics: recovery, release: () => releaseStorageLock(secureRoot, lock, publishedOwner.nonce, options) };
    nextActiveInspection = secureLockDirectoryExists(secureRoot, lock) ? Date.now() + LOCK_ACTIVE_INSPECTION_INTERVAL_MS : 0;
    if (Date.now() >= deadline) throw storageLockUnavailableError(secureRoot, lock, deadline);
    Atomics.wait(SLEEP, 0, 0, 5);
  }
}

export class StorageDirectoryCustodyUnavailableError extends Error {
  constructor() { super("Handle-backed storage directory mutation custody is unavailable"); }
}
export type StorageDirectoryCustody = {
  readonly root: string;
  readonly identity: FileObjectIdentity;
  readonly anchored: boolean;
  assertCurrent(): void;
  requireMutationCustody(): void;
  pathFor(basename: string): string;
  close(): void;
};

/** Linux operations use the held directory descriptor, not a re-resolved root name.
 * Other hosts retain identity checks; mutation callers need separate native custody. */
export function openStorageDirectoryCustody(root: string): StorageDirectoryCustody {
  const secureRoot = ensureSecureRoot(root);
  const original = fs.lstatSync(secureRoot, { bigint: true });
  let handle: number | undefined;
  try {
    if (process.platform === "linux") {
      handle = fs.openSync(secureRoot, fs.constants.O_RDONLY | DIRECTORY | NO_FOLLOW);
      const opened = fs.fstatSync(handle, { bigint: true });
      if (!opened.isDirectory() || !sameIdentity(original, opened)) throw new Error("Storage directory changed while opening custody");
      const anchor = fs.statSync(`/proc/self/fd/${handle}`, { bigint: true });
      if (!anchor.isDirectory() || !sameIdentity(opened, anchor)) throw new StorageDirectoryCustodyUnavailableError();
    }
    let closed = false;
    const assertCurrent = () => {
      if (closed) throw new Error("Storage directory custody is closed");
      const current = fs.lstatSync(secureRoot, { bigint: true });
      if (!current.isDirectory() || current.isSymbolicLink() || !sameIdentity(original, current)) throw new Error("Storage directory identity changed");
      ensureSecureRoot(secureRoot);
      if (handle !== undefined && !sameIdentity(original, fs.fstatSync(handle, { bigint: true }))) throw new Error("Storage directory handle identity changed");
    };
    const result: StorageDirectoryCustody = {
      root: secureRoot, identity: { dev: original.dev, ino: original.ino }, anchored: handle !== undefined,
      assertCurrent,
      requireMutationCustody: () => { assertCurrent(); if (handle === undefined) throw new StorageDirectoryCustodyUnavailableError(); },
      pathFor: (name) => {
        assertCurrent();
        if (!name || name === "." || name === ".." || path.basename(name) !== name || name.includes("/") || name.includes("\\") || name.includes("\0")) throw new Error("Invalid custody basename");
        return path.join(handle === undefined ? secureRoot : `/proc/self/fd/${handle}`, name);
      },
      close: () => { if (!closed) { closed = true; if (handle !== undefined) fs.closeSync(handle); } },
    };
    assertCurrent();
    return result;
  } catch (error) { if (handle !== undefined) fs.closeSync(handle); throw error; }
}

/** Shares the publisher's exact active lock name, but never recovers, sweeps or
 * maintains unrelated files. Existing/uncertain owners are left untouched. */
export function acquireStorageAdminLock(custody: StorageDirectoryCustody, name: string, deadlineUnixMs: number): StorageLock {
  custody.assertCurrent();
  if (Date.now() >= deadlineUnixMs) throw new Error("Storage admin lock deadline reached");
  const file = custody.pathFor(name);
  try { fs.mkdirSync(file, { mode: 0o700 }); }
  catch (error) {
    if (isCode(error, "EEXIST")) throw new StorageLockUnavailableError({ schema_version: 1, stage: "lock_acquisition", category: "lock_contention", owner_generation_state: "uncertain", automatic_recovery: "not_attempted", retryable: true, recovery_hint: "retry_after_transient_contention" }, "Storage admin lock is held");
    throw error;
  }
  const directory = fs.lstatSync(file, { bigint: true });
  let directoryHandle: number | undefined;
  let ownerHandle: number | undefined;
  let ownerStat: FileObjectIdentity | undefined;
  let ownerFile = path.join(file, "owner.json");
  try {
    if (custody.anchored) {
      directoryHandle = fs.openSync(file, fs.constants.O_RDONLY | DIRECTORY | NO_FOLLOW);
      if (!sameIdentity(directory, fs.fstatSync(directoryHandle, { bigint: true }))) throw new Error("Admin lock directory changed");
      ownerFile = `/proc/self/fd/${directoryHandle}/owner.json`;
    }
    custody.assertCurrent();
    ownerHandle = fs.openSync(ownerFile, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL | NO_FOLLOW, 0o600);
    ownerStat = fs.fstatSync(ownerHandle, { bigint: true });
    fs.writeFileSync(ownerHandle, `${JSON.stringify(currentProcessOwner())}\n`);
    fs.fsyncSync(ownerHandle);
    fs.closeSync(ownerHandle);
    ownerHandle = fs.openSync(ownerFile, fs.constants.O_RDONLY | NO_FOLLOW);
    if (!ownerStat || !sameIdentity(ownerStat, fs.fstatSync(ownerHandle, { bigint: true }))) throw new Error("Admin lock owner changed after publication");
    custody.assertCurrent();
    if (!sameIdentity(directory, fs.lstatSync(file, { bigint: true }))) throw new Error("Admin lock publication changed");
    let released = false;
    return { diagnostics: { schema_version: 1, owner_generation_state: "none", automatic_recovery: "not_needed" }, release: () => {
      if (released) return;
      released = true;
      try {
        // The captured /proc paths still name only our original objects after a root rename.
        if (!custody.anchored) custody.assertCurrent();
        const currentDirectory = fs.lstatSync(file, { bigint: true });
        const currentOwner = fs.lstatSync(ownerFile, { bigint: true });
        if (!sameIdentity(directory, currentDirectory) || !ownerStat || !sameIdentity(ownerStat, currentOwner) || currentOwner.nlink !== 1n) throw new Error("Admin lock release identity changed");
        fs.closeSync(ownerHandle!); ownerHandle = undefined;
        fs.unlinkSync(ownerFile);
        fs.rmdirSync(file);
      } finally { if (ownerHandle !== undefined) fs.closeSync(ownerHandle); if (directoryHandle !== undefined) fs.closeSync(directoryHandle); }
    } };
  } catch (error) {
    if (ownerHandle !== undefined) fs.closeSync(ownerHandle);
    if (directoryHandle !== undefined) fs.closeSync(directoryHandle);
    // Uncertain publication is deliberately not cleaned by a read-only route.
    throw error;
  }
}

/** Exact confirmed admin deletion, anchored to a pinned directory. No publisher-temp
 * or old-tombstone maintenance runs as a side effect of this operation. */
export type CustodiedFileSnapshot = Readonly<FileObjectIdentity & {
  size: number;
  nlink: number;
  mtimeNs: bigint;
  ctimeNs: bigint;
  contentHash: string;
}>;
export function durableUnlinkCustodiedFile(custody: StorageDirectoryCustody, basename: string, expectedIdentity: FileObjectIdentity, expectedSnapshot?: CustodiedFileSnapshot): void {
  expectedSnapshot = expectedSnapshot ? Object.freeze({ ...expectedSnapshot }) : undefined;
  custody.requireMutationCustody();
  const file = custody.pathFor(basename);
  const source = fs.lstatSync(file, { bigint: true });
  if (!source.isFile() || source.isSymbolicLink() || source.nlink !== 1n || !sameIdentity(source, expectedIdentity)) throw new Error("Custodied deletion precondition changed");
  if (expectedSnapshot && (!sameIdentity(expectedIdentity, expectedSnapshot) || !Number.isSafeInteger(expectedSnapshot.size)
    || expectedSnapshot.size < 0 || expectedSnapshot.size > MAX_PUBLISH_TEMP_BYTES || expectedSnapshot.nlink !== 1
    || typeof expectedSnapshot.mtimeNs !== "bigint" || typeof expectedSnapshot.ctimeNs !== "bigint"
    || !/^sha256:[0-9a-f]{64}$/.test(expectedSnapshot.contentHash) || !matchesCustodiedSnapshot(source, expectedSnapshot))) throw new Error("Custodied deletion snapshot changed");
  const nonce = randomUUID();
  const tombName = `.delete-v1-${nonce}.tomb`;
  const tombstone = custody.pathFor(tombName);
  const metadataPath = custody.pathFor(`.delete-v1-${nonce}.json`);
  const metadata: DeleteTombstoneMetadata = { schema_version: 1, operation: "delete", nonce, basename_hash: createHash("sha256").update(basename, "utf8").digest("hex"), dev: source.dev.toString(), ino: source.ino.toString(), created_at: new Date().toISOString(), tomb_name: tombName };
  const handle = fs.openSync(metadataPath, fs.constants.O_WRONLY | fs.constants.O_CREAT | fs.constants.O_EXCL | NO_FOLLOW, 0o600);
  let metadataIdentity: FileObjectIdentity;
  try { metadataIdentity = fs.fstatSync(handle, { bigint: true }); fs.writeFileSync(handle, `${JSON.stringify(metadata)}\n`); fs.fsyncSync(handle); }
  finally { fs.closeSync(handle); }
  custody.assertCurrent();
  // Receipt publication/fsync may run user-visible filesystem work. The full
  // confirmed snapshot must still match AFTER it, at the actual rename commit.
  let comparedHandle: number | undefined;
  try {
    if (expectedSnapshot) comparedHandle = openMatchingCustodiedSnapshot(custody, file, expectedSnapshot);
    custody.assertCurrent();
    const current = fs.lstatSync(file, { bigint: true });
    if (!sameIdentity(source, current) || expectedSnapshot && (!matchesCustodiedSnapshot(current, expectedSnapshot)
      || !matchesCustodiedSnapshot(fs.fstatSync(comparedHandle!, { bigint: true }), expectedSnapshot))) throw new Error("Custodied deletion changed before rename");
    fs.renameSync(file, tombstone);
  } finally { if (comparedHandle !== undefined) fs.closeSync(comparedHandle); }
  custody.assertCurrent();
  if (!sameIdentity(source, fs.lstatSync(tombstone, { bigint: true }))) throw new Error("Custodied deletion changed during rename");
  fsyncDirectory(path.dirname(tombstone));
  custody.assertCurrent();
  fs.unlinkSync(tombstone);
  custody.assertCurrent();
  if (!sameIdentity(metadataIdentity, fs.lstatSync(metadataPath, { bigint: true }))) throw new Error("Custodied delete receipt changed");
  fs.unlinkSync(metadataPath);
  fsyncDirectory(path.dirname(metadataPath));
}

function matchesCustodiedSnapshot(stat: BigIntStats, expected: CustodiedFileSnapshot): boolean {
  return stat.isFile() && !stat.isSymbolicLink() && sameIdentity(stat, expected) && stat.nlink === BigInt(expected.nlink)
    && stat.size === BigInt(expected.size) && stat.mtimeNs === expected.mtimeNs && stat.ctimeNs === expected.ctimeNs;
}

function openMatchingCustodiedSnapshot(custody: StorageDirectoryCustody, file: string, expected: CustodiedFileSnapshot): number {
  const handle = fs.openSync(file, fs.constants.O_RDONLY | NO_FOLLOW | (fs.constants.O_NONBLOCK ?? 0));
  try {
    if (!matchesCustodiedSnapshot(fs.fstatSync(handle, { bigint: true }), expected)) throw new Error("Custodied deletion snapshot changed");
    const bytes = Buffer.alloc(expected.size + 1);
    let length = 0;
    while (length < bytes.length) {
      const read = fs.readSync(handle, bytes, length, bytes.length - length, null);
      if (read === 0) break;
      length += read;
    }
    custody.assertCurrent();
    if (length !== expected.size || `sha256:${createHash("sha256").update(bytes.subarray(0, length)).digest("hex")}` !== expected.contentHash
      || !matchesCustodiedSnapshot(fs.fstatSync(handle, { bigint: true }), expected)
      || !matchesCustodiedSnapshot(fs.lstatSync(file, { bigint: true }), expected)) throw new Error("Custodied deletion snapshot changed");
    return handle;
  } catch (error) { fs.closeSync(handle); throw error; }
}

function publishLockGeneration(root: string, generation: string, lock: string, owner: ProcessOwner, gate: TransitionGateLease, deadline: number, options: StorageLockOptions): boolean {
  while (true) {
    assertTransitionGateLease(root, gate);
    const generationOwner = readLockOwner(root, generation);
    if (!generationOwner || generationOwner.nonce !== owner.nonce) throw new Error("Lock generation ownership changed before publication");
    if (secureLockDirectoryExists(root, lock)) return false;
    try {
      options.beforeGenerationRename?.();
      assertTransitionGateLease(root, gate);
      fs.renameSync(generation, lock);
    } catch (error) {
      const state = revalidateGenerationPublication(root, generation, lock, owner.nonce);
      if (state === "published") return true;
      if (state === "contended") return false;
      if (!isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
      continue;
    }
    fsyncDirectory(path.dirname(lock));
    const publishedOwner = readLockOwner(root, lock);
    if (!publishedOwner || publishedOwner.nonce !== owner.nonce) throw new Error("Lock generation ownership changed during publication");
    return true;
  }
}

function revalidateGenerationPublication(root: string, generation: string, lock: string, nonce: string): "source" | "published" | "contended" {
  if (secureLockDirectoryExists(root, lock)) {
    const activeOwner = readLockOwner(root, lock);
    if (activeOwner?.nonce === nonce && !secureLockDirectoryExists(root, generation)) return "published";
    return "contended";
  }
  if (!secureLockDirectoryExists(root, generation)) throw new Error("Lock generation disappeared without publication");
  const owner = readLockOwner(root, generation);
  if (!owner || owner.nonce !== nonce) throw new Error("Lock generation ownership changed during publication");
  return "source";
}

type TransitionGateLease = {
  path: string;
  owner: ProcessOwner;
  identity: FileObjectIdentity;
};

function transitionGate(lock: string): string { return `${lock}.transition-gate`; }

function tryAcquireTransitionGate(root: string, lock: string, deadline: number, options: StorageLockOptions): TransitionGateLease | undefined {
  const gate = transitionGate(lock);
  if (secureLockDirectoryExists(root, gate)) {
    if (!retireConfirmedDeadTransitionGate(root, gate, deadline, options)) return undefined;
  }
  const owner = currentProcessOwner();
  const generation = `${gate}.gen-${owner.nonce}`;
  try {
    fs.mkdirSync(generation, { mode: 0o700 });
    fsyncDirectory(path.dirname(generation));
    writeNewFileDurable(root, path.join(generation, "owner.json"), `${JSON.stringify(owner)}\n`);
    while (true) {
      if (Date.now() >= deadline) {
        cleanupInactiveGeneration(root, generation, owner.nonce);
        return undefined;
      }
      if (secureLockDirectoryExists(root, gate)) {
        if (!retireConfirmedDeadTransitionGate(root, gate, deadline, options)) {
          cleanupInactiveGeneration(root, generation, owner.nonce);
          return undefined;
        }
        continue;
      }
      try { fs.renameSync(generation, gate); }
      catch (error) {
        const state = revalidateGenerationPublication(root, generation, gate, owner.nonce);
        if (state === "published") break;
        if (state === "contended") {
          if (!retireConfirmedDeadTransitionGate(root, gate, deadline, options)) {
            cleanupInactiveGeneration(root, generation, owner.nonce);
            return undefined;
          }
          continue;
        }
        if (!isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
        Atomics.wait(SLEEP, 0, 0, 5);
        continue;
      }
      fsyncDirectory(path.dirname(gate));
      break;
    }
    const publishedOwner = readLockOwner(root, gate);
    const identity = lockDirectoryIdentity(root, gate);
    if (!publishedOwner || publishedOwner.nonce !== owner.nonce || !identity) throw new Error("Transition gate ownership changed during publication");
    return { path: gate, owner, identity };
  } catch (error) {
    cleanupInactiveGeneration(root, generation, owner.nonce);
    if (isExpectedLockRace(error)) return undefined;
    throw error;
  }
}

function retireConfirmedDeadTransitionGate(root: string, gate: string, deadline: number, options: StorageLockOptions): boolean {
  const identity = lockDirectoryIdentity(root, gate);
  if (!identity) return true;
  const owner = readLockOwner(root, gate);
  // Directory and owner reads can straddle a normal release/publication. Never
  // combine one generation's directory identity with another generation's owner.
  const afterOwnerRead = lockDirectoryIdentity(root, gate);
  if (!afterOwnerRead) return true;
  if (!sameIdentity(identity, afterOwnerRead)) {
    if (!owner || transitionGateObservationBecameStale(root, gate, identity, owner)) return false;
    throw new Error("Transition gate identity changed while reading owner");
  }
  if (!owner) {
    if (!secureLockDirectoryExists(root, gate)) return true;
    throw transitionGateUnavailableError(gate, "invalid");
  }
  const state = processOwnerIsLive(owner, deadline);
  if (state !== "dead") return false;

  options.beforeDeadTransitionGateRetirement?.();
  const currentState = processOwnerIsLive(owner, deadline);
  if (currentState !== "dead") return false;
  const currentIdentity = lockDirectoryIdentity(root, gate);
  if (!currentIdentity) return true;
  const currentOwner = readLockOwner(root, gate);
  if (!currentOwner || !sameIdentity(identity, currentIdentity) || !sameProcessOwner(owner, currentOwner)) {
    if (transitionGateObservationBecameStale(root, gate, identity, owner)) return false;
    throw new Error("Transition gate identity changed before dead-owner retirement");
  }
  try { assertCompleteTransitionGate(root, gate, currentOwner); }
  catch (error) {
    if (transitionGateObservationBecameStale(root, gate, identity, currentOwner)) return false;
    throw error;
  }

  const retired = `${gate}.gen-${currentOwner.nonce}`;
  while (true) {
    // Windows process observation may take hundreds of milliseconds. Perform it
    // before the final namespace/owner comparisons, never between them and rename.
    if (processOwnerIsLive(currentOwner, deadline) !== "dead") return false;
    const revalidatedIdentity = lockDirectoryIdentity(root, gate);
    const revalidatedOwner = revalidatedIdentity ? readLockOwner(root, gate) : undefined;
    if (!revalidatedIdentity) return true;
    if (!revalidatedOwner || !sameIdentity(identity, revalidatedIdentity) || !sameProcessOwner(currentOwner, revalidatedOwner)) {
      if (transitionGateObservationBecameStale(root, gate, identity, currentOwner)) return false;
      throw new Error("Transition gate identity changed during dead-owner retirement");
    }
    try {
      fs.renameSync(gate, retired);
    } catch (error) {
      const retiredIdentity = lockDirectoryIdentity(root, retired);
      const retiredOwner = retiredIdentity ? readLockOwner(root, retired) : undefined;
      if (retiredIdentity && sameIdentity(identity, retiredIdentity) && retiredOwner && sameProcessOwner(currentOwner, retiredOwner)
        && transitionGateObservationBecameStale(root, gate, identity, currentOwner)) break;
      if (!secureLockDirectoryExists(root, gate)) return true;
      if (!isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
      continue;
    }
    break;
  }

  fsyncDirectory(path.dirname(gate));
  const retiredIdentity = lockDirectoryIdentity(root, retired);
  const retiredOwner = retiredIdentity ? readLockOwner(root, retired) : undefined;
  if (!retiredIdentity || !sameIdentity(identity, retiredIdentity) || !retiredOwner || !sameProcessOwner(currentOwner, retiredOwner)
    || !transitionGateObservationBecameStale(root, gate, identity, currentOwner)) throw new Error("Transition gate identity changed during dead-owner retirement");
  options.afterDeadTransitionGateRetirement?.();
  cleanupInactiveGeneration(root, retired, currentOwner.nonce);
  return true;
}

/** Absence, a coherent complete successor or reads crossing another namespace
 * publication invalidate the old observation; none authorize retiring any successor.
 * Same-owner clones, in-place owner changes and malformed gates still fail closed. */
function transitionGateObservationBecameStale(root: string, gate: string, priorIdentity: FileObjectIdentity, priorOwner: ProcessOwner): boolean {
  const identity = lockDirectoryIdentity(root, gate);
  if (!identity) return true;
  const owner = readLockOwner(root, gate);
  const afterOwnerRead = lockDirectoryIdentity(root, gate);
  if (!afterOwnerRead || !sameIdentity(identity, afterOwnerRead)) return true;
  if (!owner || sameIdentity(identity, priorIdentity) || owner.nonce === priorOwner.nonce) return false;
  try { assertCompleteTransitionGate(root, gate, owner); }
  catch (error) {
    const afterFailure = lockDirectoryIdentity(root, gate);
    if (!afterFailure || !sameIdentity(identity, afterFailure)) return true;
    throw error;
  }
  const after = lockDirectoryIdentity(root, gate);
  if (!after) return true;
  // A second publication during these reads still invalidates the prior dead
  // candidate. The caller retries observation; it never retires this new path.
  if (!sameIdentity(identity, after)) return true;
  const afterOwner = readLockOwner(root, gate);
  const finalIdentity = lockDirectoryIdentity(root, gate);
  if (!finalIdentity || !sameIdentity(identity, finalIdentity)) return true;
  return Boolean(afterOwner && sameProcessOwner(owner, afterOwner));
}

function assertCompleteTransitionGate(root: string, gate: string, owner: ProcessOwner): void {
  if (!UUID_REGEX.test(owner.nonce)) throw transitionGateUnavailableError(gate, "invalid");
  let entries: fs.Dirent[];
  try { entries = fs.readdirSync(gate, { withFileTypes: true }); }
  catch (error) {
    if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, gate)) return;
    throw error;
  }
  if (entries.length !== 1 || entries[0]?.name !== "owner.json" || !entries[0].isFile()
    || !secureFileStat(root, path.join(gate, "owner.json"))) throw transitionGateUnavailableError(gate, "invalid");
}

function sameProcessOwner(left: ProcessOwner, right: ProcessOwner): boolean {
  return left.schema_version === right.schema_version && left.nonce === right.nonce && left.pid === right.pid
    && left.process_start_id === right.process_start_id && left.acquired_at === right.acquired_at;
}

function assertTransitionGateLease(root: string, lease: TransitionGateLease): void {
  const identity = lockDirectoryIdentity(root, lease.path);
  const owner = readLockOwner(root, lease.path);
  if (!identity || !sameIdentity(identity, lease.identity) || !owner || owner.nonce !== lease.owner.nonce
    || owner.pid !== lease.owner.pid || owner.process_start_id !== lease.owner.process_start_id) {
    throw new Error("Transition gate ownership changed while held");
  }
}

function releaseTransitionGate(root: string, lease: TransitionGateLease): void {
  const retired = `${lease.path}.released-${lease.owner.nonce}`;
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    assertTransitionGateLease(root, lease);
    try { fs.renameSync(lease.path, retired); }
    catch (error) {
      const retiredIdentity = lockDirectoryIdentity(root, retired);
      const retiredOwner = readLockOwner(root, retired);
      if (retiredIdentity && sameIdentity(retiredIdentity, lease.identity) && retiredOwner?.nonce === lease.owner.nonce
        && !secureLockDirectoryExists(root, lease.path)) break;
      assertTransitionGateLease(root, lease);
      if (!isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
      continue;
    }
    fsyncDirectory(path.dirname(lease.path));
    break;
  }
  cleanupInactiveGeneration(root, retired, lease.owner.nonce);
}

function transitionGateRecoveryError(gate: string, state: string): Error {
  return new Error(`Storage transition gate is ${state} at ${gate}. Stop all gateway processes, verify no owner is live, then rename the complete gate directory aside for operator recovery; never delete or replace a live or uncertain gate.`);
}

function transitionGateUnavailableError(gate: string, state: "live" | "uncertain" | "invalid"): StorageLockUnavailableError {
  return new StorageLockUnavailableError({
    schema_version: 1,
    stage: "lock_acquisition",
    category: state === "live" ? "transition_gate_live" : "transition_gate_generation_uncertain",
    owner_generation_state: state,
    automatic_recovery: "not_attempted",
    retryable: state === "live",
    recovery_hint: state === "live" ? "retry_after_owner_exit" : "inspect_storage_owner_generation",
  }, transitionGateRecoveryError(gate, state === "invalid" ? "incomplete or invalid" : state).message);
}

function storageLockUnavailableError(root: string, lock: string, deadlineUnixMs?: number): Error {
  const gate = transitionGate(lock);
  if (secureLockDirectoryExists(root, gate)) {
    const owner = readLockOwner(root, gate);
    const state = owner ? processOwnerGenerationState(owner, deadlineUnixMs) : "invalid";
    const messageState = state === "invalid" ? "incomplete or invalid" : state;
    return new StorageLockUnavailableError({
      schema_version: 1,
      stage: "lock_acquisition",
      category: state === "live" ? "transition_gate_live" : "transition_gate_generation_uncertain",
      owner_generation_state: state,
      automatic_recovery: "not_attempted",
      retryable: state === "live",
      recovery_hint: state === "live" ? "retry_after_owner_exit" : "inspect_storage_owner_generation",
    }, transitionGateRecoveryError(gate, messageState).message);
  }
  const lockExists = secureLockDirectoryExists(root, lock);
  const owner = lockExists ? readLockOwner(root, lock) : undefined;
  const state = owner ? processOwnerGenerationState(owner, deadlineUnixMs) : lockExists ? "invalid" : "none";
  return new StorageLockUnavailableError({
    schema_version: 1,
    stage: "lock_acquisition",
    category: state === "live" ? "lock_owner_live" : state === "uncertain" || state === "invalid" ? "lock_owner_generation_uncertain" : "lock_contention",
    owner_generation_state: state,
    automatic_recovery: "not_attempted",
    retryable: state === "live" || state === "none",
    recovery_hint: state === "live" ? "retry_after_owner_exit" : state === "none" ? "retry_after_transient_contention" : "inspect_storage_owner_generation",
  }, "Storage lock unavailable. Stop all gateway processes, inspect owner.json, then rename the complete lock directory aside before restarting; never delete a live or uncertain owner lock.");
}

function secureLockDirectoryExists(root: string, lock: string): boolean {
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    let stat: FileObjectStat;
    try { stat = lstatFileObject(lock); }
    catch (error) {
      if (isCode(error, "ENOENT")) return false;
      if (isRetryableWindowsPathContention(error) && Date.now() < deadline) { Atomics.wait(SLEEP, 0, 0, 5); continue; }
      throw error;
    }
    if (!stat.isDirectory || stat.isSymbolicLink) throw new Error("Unsafe lock path");
    try { assertContained(root, fs.realpathSync.native(lock)); return true; }
    catch (error) {
      let current: FileObjectStat;
      try { current = lstatFileObject(lock); }
      catch (afterError) { if (isCode(afterError, "ENOENT")) return false; throw error; }
      if (!current.isDirectory || current.isSymbolicLink) throw new Error("Unsafe lock path");
      if (!sameIdentity(stat, current) || !isRetryableWindowsPathContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

export function currentProcessOwner(now = new Date()): ProcessOwner {
  return { schema_version: 1, nonce: randomUUID(), pid: process.pid, process_start_id: processStartIdentity(process.pid), acquired_at: now.toISOString() };
}

export function processStartIdentity(pid: number): string | null {
  if (!Number.isSafeInteger(pid) || pid <= 0) return null;
  if (process.platform === "linux") {
    try {
      const stat = fs.readFileSync(`/proc/${pid}/stat`, "utf8").trim();
      const commandEnd = stat.lastIndexOf(")");
      const fieldsAfterCommand = commandEnd >= 0 ? stat.slice(commandEnd + 1).trim().split(/\s+/) : [];
      return fieldsAfterCommand[19] ? `linux:${fieldsAfterCommand[19]}` : null;
    } catch { return null; }
  }
  if (process.platform === "win32") {
    if (pid !== process.pid) return windowsProcessStartIdentity(pid);
    if (currentWindowsProcessStartIdentity === undefined || currentWindowsProcessStartIdentity === null && Date.now() >= currentWindowsProcessStartRetryAt) {
      currentWindowsProcessStartIdentity = windowsProcessStartIdentity(pid);
      if (currentWindowsProcessStartIdentity === null) currentWindowsProcessStartRetryAt = Date.now() + 1_000;
    }
    return currentWindowsProcessStartIdentity;
  }
  return pid === process.pid ? CURRENT_PROCESS_START_FALLBACK : null;
}

export function processOwnerIsLive(owner: Pick<ProcessOwner, "pid" | "process_start_id">, deadlineUnixMs?: number): "live" | "dead" | "uncertain" {
  const state = processOwnerGenerationState(owner, deadlineUnixMs);
  return state === "replaced" ? "dead" : state;
}

export function processOwnerGenerationState(owner: Pick<ProcessOwner, "pid" | "process_start_id"> & Partial<Pick<ProcessOwner, "acquired_at">>, deadlineUnixMs?: number): ProcessOwnerGenerationState {
  if (owner.process_start_id !== null && !isProcessStartIdentity(owner.process_start_id)) return "uncertain";
  try { process.kill(owner.pid, 0); } catch (error) { return isCode(error, "ESRCH") ? "dead" : "uncertain"; }
  if (owner.process_start_id === null) return "uncertain";
  let current = process.platform === "win32" && owner.pid !== process.pid
    ? windowsProcessObservation(owner.pid, windowsQueryTimeout(deadlineUnixMs))?.process_start_id ?? null
    : processStartIdentity(owner.pid);
  if (current === null) {
    try { process.kill(owner.pid, 0); } catch (error) { return isCode(error, "ESRCH") ? "dead" : "uncertain"; }
    return "uncertain";
  }
  if (processStartIdentitiesMatch(current, owner.process_start_id)) return "live";
  if (process.platform === "win32" && owner.pid !== process.pid) {
    invalidateWindowsProcessObservation(owner.pid);
    current = windowsProcessObservation(owner.pid, windowsQueryTimeout(deadlineUnixMs))?.process_start_id ?? null;
    if (current === null) return "uncertain";
    if (processStartIdentitiesMatch(current, owner.process_start_id)) return "live";
  }
  if (process.platform === "win32" && owner.process_start_id.startsWith("uptime:") && current.startsWith("windows:")) {
    return windowsProcessExecutableIsCategoricallyForeign(owner.pid, deadlineUnixMs) === true ? "replaced" : "uncertain";
  }
  const sameExactIdentityFamily = owner.process_start_id.startsWith("windows:") && current.startsWith("windows:")
    || owner.process_start_id.startsWith("linux:") && current.startsWith("linux:");
  return sameExactIdentityFamily ? "replaced" : "uncertain";
}

export function processStartIdentitiesMatch(left: string, right: string): boolean {
  if (!isProcessStartIdentity(left) || !isProcessStartIdentity(right)) return false;
  if (left === right) return true;
  const leftWindows = /^windows:(\d+)$/.exec(left);
  const rightUptimeForWindows = /^uptime:(\d+)$/.exec(right);
  if (leftWindows && rightUptimeForWindows) return epochIdentitiesWithinTolerance(leftWindows[1]!, rightUptimeForWindows[1]!);
  const rightWindows = /^windows:(\d+)$/.exec(right);
  const leftUptimeForWindows = /^uptime:(\d+)$/.exec(left);
  if (rightWindows && leftUptimeForWindows) return epochIdentitiesWithinTolerance(rightWindows[1]!, leftUptimeForWindows[1]!);
  const leftUptime = /^uptime:(\d+)$/.exec(left);
  const rightUptime = /^uptime:(\d+)$/.exec(right);
  if (!leftUptime || !rightUptime) return false;
  return epochIdentitiesWithinTolerance(leftUptime[1]!, rightUptime[1]!);
}

type WindowsProcessObservation = { process_start_id: string; executable_path: string | null };

function windowsProcessStartIdentity(pid: number): string | null {
  return windowsProcessObservation(pid)?.process_start_id ?? null;
}

function windowsProcessObservation(pid: number, timeoutMs = WINDOWS_PROCESS_QUERY_TIMEOUT_MS): WindowsProcessObservation | null {
  if (!Number.isSafeInteger(pid) || pid <= 0) return null;
  const cached = windowsProcessObservationCache.get(pid);
  if (cached && Date.now() - cached.capturedAt <= 250) return cached.observation;
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0) return null;
  const powershell = trustedWindowsPowerShellPath();
  if (!powershell) return null;
  try {
    const script = "$ErrorActionPreference='Stop'; $targetPid=0; if (-not [int]::TryParse($env:SGG_MCP_PROCESS_ID, [ref]$targetPid) -or $targetPid -le 0) { throw 'Invalid process id' }; $p=[System.Diagnostics.Process]::GetProcessById($targetPid); try { $image=''; try { $image=$p.MainModule.FileName } catch {}; @{schema_version=1;start_epoch_ms=([DateTimeOffset]$p.StartTime).ToUnixTimeMilliseconds();executable_path=$image} | ConvertTo-Json -Compress } finally { $p.Dispose() }";
    const output = execFileSync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], {
      encoding: "utf8",
      timeout: Math.max(1, Math.min(WINDOWS_PROCESS_QUERY_TIMEOUT_MS, Math.floor(timeoutMs))),
      windowsHide: true,
      stdio: ["ignore", "pipe", "ignore"],
      env: { ...process.env, SGG_MCP_PROCESS_ID: String(pid) },
    }).trim();
    const parsed = JSON.parse(output) as Record<string, unknown>;
    const start = parsed.start_epoch_ms;
    const executable = parsed.executable_path;
    const observation = parsed.schema_version === 1 && Number.isSafeInteger(start) && Number(start) >= 0 && Number(start) <= 9_999_999_999_999_999
      && typeof executable === "string" && executable.length <= 32_768
      ? { process_start_id: `windows:${start}`, executable_path: executable.length > 0 ? executable : null }
      : null;
    windowsProcessObservationCache.set(pid, { capturedAt: Date.now(), observation });
    return observation;
  } catch {
    windowsProcessObservationCache.set(pid, { capturedAt: Date.now(), observation: null });
    return null;
  }
}

function windowsProcessExecutableIsCategoricallyForeign(pid: number, deadlineUnixMs?: number): boolean | undefined {
  const observedPath = windowsProcessObservation(pid, windowsQueryTimeout(deadlineUnixMs))?.executable_path;
  if (!observedPath) return undefined;
  const observedName = path.win32.basename(observedPath).toLowerCase();
  const currentName = path.win32.basename(process.execPath).toLowerCase();
  if (observedName === "node.exe" || observedName === currentName) return false;
  return true;
}

function invalidateWindowsProcessObservation(pid: number): void {
  if (process.platform === "win32" && pid !== process.pid) windowsProcessObservationCache.delete(pid);
}

function windowsQueryTimeout(deadlineUnixMs?: number): number {
  return deadlineUnixMs === undefined ? WINDOWS_PROCESS_QUERY_TIMEOUT_MS : Math.min(WINDOWS_PROCESS_QUERY_TIMEOUT_MS, Math.max(0, deadlineUnixMs - Date.now()));
}

function trustedWindowsPowerShellPath(): string | null {
  const systemRoot = process.env.SystemRoot;
  const windowsDirectory = process.env.windir;
  if (!systemRoot || !windowsDirectory || !path.win32.isAbsolute(systemRoot) || !path.win32.isAbsolute(windowsDirectory)) return null;
  try {
    const realRoot = fs.realpathSync.native(systemRoot);
    const realWindowsDirectory = fs.realpathSync.native(windowsDirectory);
    if (realRoot.toLowerCase() !== realWindowsDirectory.toLowerCase() || path.win32.basename(realRoot).toLowerCase() !== "windows") return null;
    const executable = fs.realpathSync.native(path.win32.join(realRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
    const relative = path.win32.relative(realRoot, executable);
    return relative !== "" && relative !== ".." && !relative.startsWith(`..${path.win32.sep}`) && !path.win32.isAbsolute(relative) ? executable : null;
  } catch { return null; }
}

function transitionOwnerMayResume(transition: Pick<TransitionRecord, "pid" | "process_start_id">, deadlineUnixMs?: number, observedState?: ProcessOwnerGenerationState): boolean {
  const currentStart = processStartIdentity(process.pid);
  if (transition.pid === process.pid && currentStart !== null && transition.process_start_id === currentStart) return true;
  const state = observedState ?? transitionOwnerGenerationState(transition, deadlineUnixMs);
  return state === "dead" || state === "replaced";
}

function transitionOwnerGenerationState(transition: Pick<TransitionRecord, "pid" | "process_start_id">, deadlineUnixMs?: number): ProcessOwnerGenerationState {
  const currentStart = processStartIdentity(process.pid);
  if (transition.pid === process.pid && currentStart !== null && transition.process_start_id === currentStart) return "live";
  return processOwnerGenerationState(transition, deadlineUnixMs);
}

export function ownerIdentityTag(owner: Pick<ProcessOwner, "process_start_id">): string {
  return owner.process_start_id ? createHash("sha256").update(owner.process_start_id).digest("hex").slice(0, 16) : "unknown";
}

export function currentProcessIdentityTag(): string { return ownerIdentityTag(currentProcessOwner()); }

function currentProcessPublicationIdentityTag(): string {
  const owner = currentProcessOwner();
  const tag = ownerIdentityTag(owner);
  return owner.process_start_id?.startsWith("windows:") ? `x${tag}` : tag;
}

export function processIdentityTagOwnerState(pid: number, tag: string, deadlineUnixMs?: number): "live" | "dead" | "uncertain" {
  try { process.kill(pid, 0); } catch (error) { return isCode(error, "ESRCH") ? "dead" : "uncertain"; }
  if (tag === "unknown") return "uncertain";
  let identity = process.platform === "win32" && pid !== process.pid
    ? windowsProcessObservation(pid, windowsQueryTimeout(deadlineUnixMs))?.process_start_id ?? null
    : processStartIdentity(pid);
  if (identity === null) {
    try { process.kill(pid, 0); } catch (error) { return isCode(error, "ESRCH") ? "dead" : "uncertain"; }
    return "uncertain";
  }
  if (tag.startsWith("x") && ownerIdentityTag({ process_start_id: identity }) === tag.slice(1)) return "live";
  if (processIdentityTagMatches(identity, tag)) return "live";
  if (process.platform === "win32" && pid !== process.pid) {
    invalidateWindowsProcessObservation(pid);
    identity = windowsProcessObservation(pid, windowsQueryTimeout(deadlineUnixMs))?.process_start_id ?? null;
    if (identity === null) return "uncertain";
    if (tag.startsWith("x")) return ownerIdentityTag({ process_start_id: identity }) === tag.slice(1) ? "live" : "dead";
    if (processIdentityTagMatches(identity, tag)) return "live";
  } else if (tag.startsWith("x")) {
    return "dead";
  }
  if (process.platform === "win32" && identity.startsWith("windows:")) {
    return windowsProcessExecutableIsCategoricallyForeign(pid, deadlineUnixMs) === true ? "dead" : "uncertain";
  }
  return "dead";
}

function processIdentityTagMatches(identity: string, tag: string): boolean {
  if (ownerIdentityTag({ process_start_id: identity }) === tag) return true;
  const epoch = /^(?:uptime|windows):(\d+)$/.exec(identity);
  if (!epoch) return false;
  const epochMs = Number(epoch[1]);
  if (!Number.isSafeInteger(epochMs)) return false;
  for (let offset = -PROCESS_START_TOLERANCE_MS; offset <= PROCESS_START_TOLERANCE_MS; offset += 1) {
    if (ownerIdentityTag({ process_start_id: `uptime:${epochMs + offset}` }) === tag) return true;
  }
  return false;
}

function epochIdentitiesWithinTolerance(left: string, right: string): boolean {
  const leftEpochMs = Number(left);
  const rightEpochMs = Number(right);
  return Number.isSafeInteger(leftEpochMs) && Number.isSafeInteger(rightEpochMs)
    && Math.abs(leftEpochMs - rightEpochMs) <= PROCESS_START_TOLERANCE_MS;
}

function isProcessStartIdentity(value: string): boolean {
  const match = /^(?:linux|windows|uptime):(0|[1-9]\d*)$/.exec(value);
  if (!match) return false;
  const numeric = Number(match[1]);
  return Number.isSafeInteger(numeric) && numeric >= 0;
}

function tryReclaimStorageLock(root: string, lock: string, staleAfterMs: number, deadline: number, options: StorageLockOptions): "not_reclaimed" | "reclaimed_dead_owner" | "reclaimed_replaced_owner" {
  try {
    const observed = readLockOwner(root, lock);
    if (!observed || Date.now() - Date.parse(observed.acquired_at) < staleAfterMs) return "not_reclaimed";
    const state = processOwnerGenerationState(observed, deadline);
    if (state !== "dead" && state !== "replaced") return "not_reclaimed";
    options.beforeLockOwnerRevalidation?.();
    const current = readLockOwner(root, lock);
    if (!current || !sameProcessOwner(observed, current) || processOwnerGenerationState(current, deadline) !== state) return "not_reclaimed";
    transitionOwnedLock(root, lock, observed.nonce, "reclaim", deadline, options);
    return state === "replaced" ? "reclaimed_replaced_owner" : "reclaimed_dead_owner";
  } catch (error) {
    if (!isExpectedLockRace(error)) throw error;
    return "not_reclaimed";
  }
}

function releaseStorageLock(root: string, lock: string, nonce: string, options: StorageLockOptions): void {
  const gateDeadline = Date.now() + LOCK_GATE_ACQUIRE_TIMEOUT_MS;
  let gate: TransitionGateLease | undefined;
  try {
    while (!gate && Date.now() < gateDeadline) {
      gate = tryAcquireTransitionGate(root, lock, gateDeadline, options);
      if (!gate) Atomics.wait(SLEEP, 0, 0, 5);
    }
    if (!gate) throw storageLockUnavailableError(root, lock, gateDeadline);
    options.afterTransitionGate?.();
    const observed = readLockOwner(root, lock);
    if (!observed || observed.nonce !== nonce) return;
    transitionOwnedLock(root, lock, nonce, "release", Date.now() + LOCK_TRANSITION_RETRY_MS, options);
  } finally {
    if (gate) releaseTransitionGate(root, gate);
  }
}

function transitionOwnedLock(root: string, lock: string, nonce: string, operation: "release" | "reclaim", deadline: number, options: StorageLockOptions): void {
  const before = readLockOwner(root, lock);
  if (!before || before.nonce !== nonce) return;
  const existing = readActiveTransition(root, lock, before, deadline);
  if (existing) {
    if (!transitionOwnerMayResume(existing, deadline)) { options.afterBlockedTransition?.(); return; }
    resumeTransition(root, lock, before, existing, deadline, options);
    return;
  }
  const marker = path.join(lock, `transition-${nonce}.json`);
  const transitionOwner = currentProcessOwner();
  const transition: TransitionRecord = { schema_version: 1, nonce, operation, transition_nonce: randomUUID(), pid: transitionOwner.pid, process_start_id: transitionOwner.process_start_id, transitioned_at: transitionOwner.acquired_at };
  try {
    writeNewFileDurable(root, marker, `${JSON.stringify(transition)}\n`);
  } catch (error) {
    const currentOwner = readLockOwner(root, lock);
    if (!currentOwner || currentOwner.nonce !== nonce) return;
    const raced = readActiveTransition(root, lock, currentOwner, deadline);
    if (!raced) throw error;
    if (!transitionOwnerMayResume(raced, deadline)) { options.afterBlockedTransition?.(); return; }
    resumeTransition(root, lock, currentOwner, raced, deadline, options);
    return;
  }
  options.afterTransitionMarker?.();
  resumeTransition(root, lock, before, transition, deadline, options);
}

type ActiveTransitionResume = { status: "none" | "blocked" } | { status: "resumed"; owner_generation_state: ProcessOwnerGenerationState };

function resumeActiveLockTransition(root: string, lock: string, deadline: number, options: StorageLockOptions): ActiveTransitionResume {
  const owner = readLockOwner(root, lock);
  if (!owner) return { status: "none" };
  const transition = readActiveTransition(root, lock, owner, deadline);
  if (!transition) return { status: "none" };
  const ownerGenerationState = transitionOwnerGenerationState(transition, deadline);
  if (!transitionOwnerMayResume(transition, deadline, ownerGenerationState)) { options.afterBlockedTransition?.(); return { status: "blocked" }; }
  resumeTransition(root, lock, owner, transition, deadline, options);
  return { status: "resumed", owner_generation_state: ownerGenerationState };
}

function readActiveTransition(root: string, lock: string, owner: ProcessOwner, deadlineUnixMs?: number): TransitionRecord | undefined {
  const maintenanceOptions: MaintainPublisherTempsOptions = deadlineUnixMs === undefined ? {} : { deadlineUnixMs };
  try { maintainPublisherTemps(root, lock, maintenanceOptions); }
  catch (error) {
    const current = readLockOwner(root, lock);
    if (!current || current.nonce !== owner.nonce) return undefined;
    throw error;
  }
  let entries: fs.Dirent[];
  try { entries = fs.readdirSync(lock, { withFileTypes: true }); }
  catch (error) {
    if (isCode(error, "ENOENT")) {
      const current = readLockOwner(root, lock);
      if (!current || current.nonce !== owner.nonce) return undefined;
    }
    throw error;
  }
  const markers = entries.filter((entry) => /^transition-.*\.json$/.test(entry.name));
  if (markers.length === 0) return undefined;
  if (markers.length !== 1 || markers[0]!.name !== `transition-${owner.nonce}.json` || !markers[0]!.isFile()) throw new Error("Lock transition marker is ambiguous");
  const markerPath = path.join(lock, markers[0]!.name);
  const transition = readTransitionRecord(root, markerPath, owner);
  if (!transition) {
    const current = readLockOwner(root, lock);
    if (!current || current.nonce !== owner.nonce || !secureFileStat(root, markerPath)) return undefined;
    throw new Error("Lock transition marker is invalid");
  }
  return transition;
}

function resumeTransition(root: string, lock: string, owner: ProcessOwner, transition: TransitionRecord, deadline: number, options: StorageLockOptions): void {
  if (!transitionOwnerMayResume(transition, deadline)) { options.afterBlockedTransition?.(); return; }
  const tombstone = transitionTombstone(lock, transition.transition_nonce);
  while (true) {
    if (secureLockDirectoryExists(root, tombstone)) {
      removeOwnedLockDirectory(root, tombstone, owner.nonce, transition.transition_nonce);
      options.afterTransitionCleanup?.();
    }
    if (!secureLockDirectoryExists(root, lock)) return;
    const activeOwner = readLockOwner(root, lock);
    if (!activeOwner || activeOwner.nonce !== owner.nonce) return;
    const activeTransition = readActiveTransition(root, lock, activeOwner, deadline);
    if (!activeTransition) {
      if (secureLockDirectoryExists(root, tombstone)) { removeOwnedLockDirectory(root, tombstone, owner.nonce, transition.transition_nonce); options.afterTransitionCleanup?.(); return; }
      if (!secureLockDirectoryExists(root, lock)) return;
      const currentOwner = readLockOwner(root, lock);
      if (!currentOwner || currentOwner.nonce !== owner.nonce) return;
      throw new Error("Lock transition marker disappeared during resume");
    }
    if (activeTransition.transition_nonce !== transition.transition_nonce || activeTransition.operation !== transition.operation) throw new Error("Lock transition marker changed during resume");
    if (!transitionOwnerMayResume(activeTransition, deadline)) { options.afterBlockedTransition?.(); return; }
    try {
      options.beforeTransitionRename?.();
      fs.renameSync(lock, tombstone);
    } catch (error) {
      if (secureLockDirectoryExists(root, tombstone)) {
        removeOwnedLockDirectory(root, tombstone, owner.nonce, transition.transition_nonce);
        options.afterTransitionCleanup?.();
        return;
      }
      if (!secureLockDirectoryExists(root, lock)) return;
      const revalidatedOwner = readLockOwner(root, lock);
      if (!revalidatedOwner || revalidatedOwner.nonce !== owner.nonce) return;
      const revalidatedTransition = readActiveTransition(root, lock, revalidatedOwner, deadline);
      if (!revalidatedTransition) {
        if (secureLockDirectoryExists(root, tombstone)) { removeOwnedLockDirectory(root, tombstone, owner.nonce, transition.transition_nonce); options.afterTransitionCleanup?.(); return; }
        if (!secureLockDirectoryExists(root, lock)) return;
        const currentOwner = readLockOwner(root, lock);
        if (!currentOwner || currentOwner.nonce !== owner.nonce) return;
        throw new Error("Lock transition marker disappeared after rename contention");
      }
      if (revalidatedTransition.transition_nonce !== transition.transition_nonce) throw new Error("Lock transition ownership changed after rename contention");
      if (!transitionOwnerMayResume(revalidatedTransition, deadline)) { options.afterBlockedTransition?.(); return; }
      if (!isRetryableWindowsFsContention(error)) {
        if (isExpectedLockRace(error)) return;
        throw error;
      }
      if (Date.now() >= deadline) return;
      Atomics.wait(SLEEP, 0, 0, 5);
      continue;
    }
    fsyncDirectory(path.dirname(lock));
    removeOwnedLockDirectory(root, tombstone, owner.nonce, transition.transition_nonce);
    options.afterTransitionCleanup?.();
    return;
  }
}

function removeOwnedLockDirectory(root: string, tombstone: string, nonce: string, transitionNonce: string): void {
  if (!secureLockDirectoryExists(root, tombstone)) return;
  const tombIdentity = lockDirectoryIdentity(root, tombstone);
  if (!tombIdentity) return;
  const owner = readLockOwner(root, tombstone);
  if (owner && owner.nonce !== nonce) throw new Error("Refusing to remove unowned lock");
  const transitionFile = path.join(tombstone, `transition-${nonce}.json`);
  const transition = owner ? readTransitionRecord(root, transitionFile, owner) : readTransitionRecordForUnknownOwner(root, transitionFile);
  if (!transition) {
    if (!secureLockDirectoryExists(root, tombstone)) return;
    const currentOwner = readLockOwner(root, tombstone);
    if (!currentOwner && !secureFileStat(root, transitionFile)) {
      let remaining: string[];
      try { remaining = fs.readdirSync(tombstone); }
      catch (error) { if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, tombstone)) return; throw error; }
      if (remaining.length === 0) {
        removeLockDirectoryWithRetry(root, tombstone, Date.now() + LOCK_TRANSITION_RETRY_MS);
        fsyncDirectory(path.dirname(tombstone));
      }
      return;
    }
    throw new Error("Refusing to remove lock without its transition");
  }
  if (transition.transition_nonce !== transitionNonce || (!owner && tombstone !== transitionTombstone(tombstone.slice(0, tombstone.lastIndexOf(".tomb-")), transitionNonce))) throw new Error("Refusing to remove lock with another transition");
  if (!maintainLockTombstone(root, tombstone, tombIdentity, Date.now() + LOCK_TRANSITION_RETRY_MS)) return;
  const isTransitionArtifact = (name: string) => {
    const marker = /^transition-(.+)\.json$/.exec(name);
    if (marker) return isOwnerNonce(marker[1]!);
    const temp = /^\.transition-(.+)\.json\.\d+\.([^.]+)\.tmp$/.exec(name);
    return Boolean(temp && isOwnerNonce(temp[1]!) && UUID_REGEX.test(temp[2]!));
  };
  let entries: fs.Dirent[];
  try { entries = fs.readdirSync(tombstone, { withFileTypes: true }); }
  catch (error) { if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, tombstone)) return; throw error; }
  for (const entry of entries) {
    if (entry.name !== "owner.json" && entry.name !== "owner" && !isTransitionArtifact(entry.name)) throw new Error("Unexpected lock tombstone content");
    if (!entry.isFile()) throw new Error("Unsafe lock tombstone content");
    secureFileStat(root, path.join(tombstone, entry.name));
  }
  if (owner) {
    let ownerFile: string;
    try { ownerFile = lockOwnerFile(root, tombstone); }
    catch (error) { if (isCode(error, "ENOENT") && !secureFileStat(root, path.join(tombstone, "owner.json")) && !secureFileStat(root, path.join(tombstone, "owner"))) ownerFile = ""; else throw error; }
    if (ownerFile) unlinkLockArtifactWithRetry(root, tombstone, ownerFile, Date.now() + LOCK_TRANSITION_RETRY_MS);
  }
  unlinkLockArtifactWithRetry(root, tombstone, transitionFile, Date.now() + LOCK_TRANSITION_RETRY_MS);
  removeLockDirectoryWithRetry(root, tombstone, Date.now() + LOCK_TRANSITION_RETRY_MS);
  fsyncDirectory(path.dirname(tombstone));
}

function unlinkLockArtifactWithRetry(root: string, tombstone: string, file: string, deadline: number): void {
  const identity = lockArtifactStat(root, file);
  if (!identity) return;
  while (true) {
    try { fs.unlinkSync(file); fsyncLockArtifactParent(root, tombstone); return; }
    catch (error) {
      const current = lockArtifactStat(root, file);
      if (!current) { fsyncLockArtifactParent(root, tombstone); return; }
      if (!sameIdentity(identity, current) || !secureLockDirectoryExists(root, tombstone)) throw error;
      if (!isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function lockArtifactStat(root: string, file: string): FileObjectStat | undefined {
  assertContained(root, file);
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    try {
      const stat = lstatFileObject(file);
      if (!stat.isFile || stat.isSymbolicLink) throw new Error("Unsafe lock artifact");
      return stat;
    } catch (error) {
      if (isCode(error, "ENOENT")) return undefined;
      if (!isRetryableWindowsPathContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function fsyncLockArtifactParent(root: string, tombstone: string): void {
  try { fsyncDirectory(tombstone); }
  catch (error) {
    if (!isCode(error, "ENOENT") || secureLockDirectoryExists(root, tombstone)) throw error;
    fsyncDirectory(path.dirname(tombstone));
  }
}

function removeLockDirectoryWithRetry(root: string, tombstone: string, deadline: number): void {
  while (true) {
    try { fs.rmdirSync(tombstone); return; }
    catch (error) {
      if (!secureLockDirectoryExists(root, tombstone)) return;
      if (!isRetryableWindowsFsContention(error) || Date.now() >= deadline || fs.readdirSync(tombstone).length !== 0) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function maintainLockTombstone(root: string, tombstone: string, identity: FileObjectIdentity, deadline: number): boolean {
  while (true) {
    try { maintainPublisherTemps(root, tombstone, { orphanedContainer: true }); maintainDeleteTombstones(root, tombstone); return true; }
    catch (error) {
      const current = lockDirectoryIdentity(root, tombstone);
      if (!current) return false;
      if (!sameIdentity(identity, current) || !isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function lockDirectoryIdentity(root: string, directory: string): FileObjectIdentity | undefined {
  assertContained(root, directory);
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    try {
      const stat = lstatFileObject(directory);
      if (!stat.isDirectory || stat.isSymbolicLink) throw new Error("Unsafe lock directory");
      return stat;
    } catch (error) {
      if (isCode(error, "ENOENT")) return undefined;
      if (!isRetryableWindowsPathContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function transitionTombstone(lock: string, transitionNonce: string): string { return `${lock}.tomb-${transitionNonce}`; }
function boundedTransitionDeadline(deadline: number): number { return Math.min(deadline, Date.now() + LOCK_TRANSITION_RETRY_MS); }
function isRetryableWindowsFsContention(error: unknown): boolean { return process.platform === "win32" && (isCode(error, "EPERM") || isCode(error, "EACCES")); }
function isRetryableWindowsPathContention(error: unknown): boolean { return isRetryableWindowsFsContention(error) || process.platform === "win32" && isCode(error, "EBADF"); }

function readLockOwner(root: string, lock: string): ProcessOwner | undefined {
  try {
    if (!secureLockDirectoryExists(root, lock)) return undefined;
    const text = readSecureFile(root, lockOwnerFile(root, lock), 4096).toString("utf8").trim();
    if (/^[1-9]\d*$/.test(text)) {
      const pid = Number(text);
      if (!Number.isSafeInteger(pid)) return undefined;
      return { schema_version: 1, nonce: `legacy-${pid}`, pid, process_start_id: null, acquired_at: fs.lstatSync(lock).mtime.toISOString() };
    }
    const parsed = JSON.parse(text) as Partial<ProcessOwner>;
    const keys = Object.keys(parsed);
    const acquired = typeof parsed.acquired_at === "string" ? Date.parse(parsed.acquired_at) : NaN;
    if (keys.length !== 5 || !["schema_version", "nonce", "pid", "process_start_id", "acquired_at"].every((key) => keys.includes(key))
      || parsed.schema_version !== 1 || typeof parsed.nonce !== "string" || !UUID_REGEX.test(parsed.nonce)
      || !Number.isSafeInteger(parsed.pid) || (parsed.pid as number) <= 0 || !Number.isFinite(acquired) || new Date(acquired).toISOString() !== parsed.acquired_at
      || !(parsed.process_start_id === null || (typeof parsed.process_start_id === "string" && isProcessStartIdentity(parsed.process_start_id)))) return undefined;
    return parsed as ProcessOwner;
  } catch (error) { if (isCode(error, "ENOENT")) return undefined; throw error; }
}

function lockOwnerFile(root: string, lock: string): string {
  const current = path.join(lock, "owner.json");
  if (secureFileStat(root, current)) return current;
  const legacy = path.join(lock, "owner");
  if (secureFileStat(root, legacy)) return legacy;
  throw Object.assign(new Error("Lock owner is missing"), { code: "ENOENT" });
}

function ensureDirectoryChain(target: string): void {
  const parsed = path.parse(target);
  let current = parsed.root;
  for (const segment of target.slice(parsed.root.length).split(path.sep).filter(Boolean)) {
    current = path.join(current, segment);
    try { fs.mkdirSync(current, { mode: 0o700 }); fsyncDirectory(path.dirname(current)); } catch (error) { if (!isCode(error, "EEXIST")) throw error; }
    const stat = fs.lstatSync(current);
    if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Unsafe storage path component");
    if (samePath(current, target)) assertPrivateDirectory(stat);
  }
}

function assertSecureDirectory(root: string, dir: string): void {
  assertContained(root, dir);
  const stat = fs.lstatSync(dir);
  if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Unsafe storage directory");
  assertPrivateDirectory(stat);
  assertContained(root, fs.realpathSync.native(dir));
}

function containedPath(root: string, relative: string): string {
  const target = absolutePath(path.join(root, relative));
  assertContained(root, target);
  return target;
}

function assertContained(root: string, target: string): void {
  const relative = path.relative(root, absolutePath(target));
  if (relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative)) throw new Error("Storage path escapes root");
}

function secureFileStatRequired(root: string, file: string): SecureFileStat {
  const stat = secureFileStat(root, file);
  if (!stat) throw Object.assign(new Error("Storage file does not exist"), { code: "ENOENT" });
  return stat;
}

function samePath(left: string, right: string): boolean { return process.platform === "win32" ? left.toLowerCase() === right.toLowerCase() : left === right; }
function absolutePath(value: string): string { return path.isAbsolute(value) ? path.join(value) : path.join(process.cwd(), value); }
function sameIdentity(left: FileObjectIdentity, right: FileObjectIdentity): boolean { return sameFileObjectIdentity(left, right, process.platform); }
export function sameFileObjectIdentity(left: FileObjectIdentity, right: FileObjectIdentity, platform: NodeJS.Platform): boolean {
  if (!identityCertain(left, platform) || !identityCertain(right, platform) || left.ino !== right.ino) return false;
  return left.dev === right.dev || (platform === "win32" && ((left.dev === 0n) !== (right.dev === 0n)));
}
function identityCertain(identity: FileObjectIdentity, platform: NodeJS.Platform): boolean {
  return identity.ino !== 0n && (identity.dev !== 0n || platform === "win32");
}
function isCode(error: unknown, code: string): boolean { return typeof error === "object" && error !== null && "code" in error && error.code === code; }
function isExpectedLockRace(error: unknown): boolean { return isCode(error, "ENOENT") || isCode(error, "EEXIST") || isCode(error, "ENOTEMPTY"); }

function assertPrivateDirectory(stat: fs.Stats): void {
  if (process.platform !== "win32" && (stat.mode & 0o077) !== 0) throw new Error("Storage directory must be owner-only");
}

function cleanupInactiveGeneration(root: string, generation: string, nonce: string): void {
  let stat: fs.Stats;
  try {
    stat = fs.lstatSync(generation);
  } catch (error) { if (isCode(error, "ENOENT")) return; throw error; }
  if (!stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Unsafe lock generation");
  try { maintainPublisherTemps(root, generation, { orphanedContainer: true }); maintainDeleteTombstones(root, generation); }
  catch (error) { if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, generation)) return; throw error; }
  const owner = readLockOwner(root, generation);
  if (owner && owner.nonce !== nonce) throw new Error("Refusing to clean another lock generation");
  if (owner) {
    try { durableUnlinkFile(root, path.join(generation, "owner.json")); }
    catch (error) { if (!isCode(error, "ENOENT") || secureFileStat(root, path.join(generation, "owner.json"))) throw error; }
    try { maintainDeleteTombstones(root, generation); }
    catch (error) { if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, generation)) return; throw error; }
  }
  try { fs.rmdirSync(generation); }
  catch (error) { if (!isCode(error, "ENOENT") || secureLockDirectoryExists(root, generation)) throw error; }
  fsyncDirectory(path.dirname(generation));
}

function cleanupAbandonedGenerations(root: string, lock: string, staleAfterMs: number, deadlineUnixMs?: number): void {
  const parent = path.dirname(lock);
  const prefix = `${path.basename(lock)}.gen-`;
  const maintenanceOptions: MaintainPublisherTempsOptions = deadlineUnixMs === undefined
    ? { orphanedContainer: true }
    : { orphanedContainer: true, deadlineUnixMs };
  for (const entry of fs.readdirSync(parent, { withFileTypes: true })) {
    if (!entry.name.startsWith(prefix)) continue;
    if (!UUID_REGEX.test(entry.name.slice(prefix.length))) throw new Error("Lock generation name is invalid");
    const generation = path.join(parent, entry.name);
    let stat: fs.Stats;
    try { stat = fs.lstatSync(generation); }
    catch (error) { if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, generation)) continue; throw error; }
    if (!entry.isDirectory() || !stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Unsafe lock generation");
    // A freshly complete generation with an exact dead owner is recoverable. An incomplete or live initializer is not.
    const fresh = Date.now() - stat.mtimeMs < Math.max(staleAfterMs, LOCK_GATE_ACQUIRE_TIMEOUT_MS);
    const initialOwner = readLockOwner(root, generation);
    if (fresh && (!initialOwner || processOwnerIsLive(initialOwner, deadlineUnixMs) !== "dead")) continue;
    try { maintainDeleteTombstones(root, generation, maintenanceOptions); }
    catch (error) { if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, generation)) continue; throw error; }
    const owner = readLockOwner(root, generation);
    if (owner && processOwnerIsLive(owner, deadlineUnixMs) !== "dead") continue;
    const files = fs.readdirSync(generation, { withFileTypes: true });
    for (const file of files) {
      if (!file.isFile() || (file.name !== "owner.json" && !new RegExp(`^\\.owner\\.json\\.\\d+\\.${UUID_PATTERN}\\.tmp$`).test(file.name))) throw new Error("Unexpected lock generation content");
      const filePath = path.join(generation, file.name);
      try { durableUnlinkFile(root, filePath); } catch (error) { if (!isCode(error, "ENOENT") || secureFileStat(root, filePath)) throw error; }
    }
    try { maintainDeleteTombstones(root, generation, maintenanceOptions); }
    catch (error) { if (isCode(error, "ENOENT") && !secureLockDirectoryExists(root, generation)) continue; throw error; }
    try { fs.rmdirSync(generation); } catch (error) { if (!isCode(error, "ENOENT") || secureLockDirectoryExists(root, generation)) throw error; }
    fsyncDirectory(parent);
  }
}

function cleanupAbandonedTombstones(root: string, lock: string, staleAfterMs: number, deadlineUnixMs?: number): boolean {
  const parent = path.dirname(lock);
  const prefix = `${path.basename(lock)}.tomb-`;
  let blocked = false;
  for (const entry of fs.readdirSync(parent, { withFileTypes: true })) {
    if (!entry.name.startsWith(prefix)) continue;
    if (!UUID_REGEX.test(entry.name.slice(prefix.length))) throw new Error("Lock tombstone name is invalid");
    const tombstone = path.join(parent, entry.name);
    try {
      const stat = fs.lstatSync(tombstone);
      if (!entry.isDirectory() || !stat.isDirectory() || stat.isSymbolicLink()) throw new Error("Unsafe lock tombstone");
      const tombIdentity = lockDirectoryIdentity(root, tombstone);
      if (!tombIdentity || !maintainLockTombstone(root, tombstone, tombIdentity, Date.now() + LOCK_TRANSITION_RETRY_MS)) continue;
      const owner = readLockOwner(root, tombstone);
      if (!owner) {
        const markerNames = fs.readdirSync(tombstone).filter((name) => new RegExp(`^transition-${UUID_PATTERN}\\.json$`).test(name));
        if (markerNames.length === 1) {
          const markerPath = path.join(tombstone, markerNames[0]!);
          const transition = readTransitionRecordForUnknownOwner(root, markerPath);
          if (!transition) {
            if (!secureLockDirectoryExists(root, tombstone)) continue;
            if (!secureFileStat(root, markerPath)) {
              if (fs.readdirSync(tombstone).length === 0) {
                try { fs.rmdirSync(tombstone); } catch (error) { if (!isCode(error, "ENOENT") || secureLockDirectoryExists(root, tombstone)) throw error; }
                fsyncDirectory(parent);
              }
              continue;
            }
            throw new Error("Lock tombstone transition is invalid");
          }
          const baseLock = tombstone.slice(0, tombstone.lastIndexOf(".tomb-"));
          if (tombstone !== transitionTombstone(baseLock, transition.transition_nonce)) throw new Error("Lock tombstone transition is invalid");
          if (!transitionOwnerMayResume(transition, deadlineUnixMs)) { blocked = true; continue; }
          removeOwnedLockDirectory(root, tombstone, transition.nonce, transition.transition_nonce);
          continue;
        }
        if (markerNames.length > 1) throw new Error("Lock tombstone transition is ambiguous");
        if (fs.readdirSync(tombstone).length === 0) { fs.rmdirSync(tombstone); fsyncDirectory(parent); continue; }
        if (Date.now() - stat.mtimeMs < staleAfterMs) continue;
        throw new Error("Lock tombstone owner is incomplete");
      }
      const transitionFile = path.join(tombstone, `transition-${owner.nonce}.json`);
      const transition = readTransitionRecord(root, transitionFile, owner);
      if (!transition) {
        if (Date.now() - stat.mtimeMs < staleAfterMs) continue;
        throw new Error("Lock tombstone transition is incomplete");
      }
      if (!transitionOwnerMayResume(transition, deadlineUnixMs)) { blocked = true; continue; }
      removeOwnedLockDirectory(root, tombstone, owner.nonce, transition.transition_nonce);
    } catch (error) { if (!isCode(error, "ENOENT")) throw error; }
  }
  return blocked;
}

type TransitionRecord = {
  schema_version: 1;
  nonce: string;
  operation: "release" | "reclaim";
  transition_nonce: string;
  pid: number;
  process_start_id: string | null;
  transitioned_at: string;
};

function legacyOwnerPid(nonce: string): number | undefined {
  const match = LEGACY_NONCE_REGEX.exec(nonce);
  if (!match) return undefined;
  const pid = Number(match[1]);
  return Number.isSafeInteger(pid) && pid > 0 ? pid : undefined;
}

function isOwnerNonce(nonce: string): boolean { return UUID_REGEX.test(nonce) || legacyOwnerPid(nonce) !== undefined; }

function isExactDeadLegacyOwner(owner: ProcessOwner, nonce: string): boolean {
  const pid = legacyOwnerPid(nonce);
  return pid !== undefined && owner.nonce === nonce && owner.pid === pid && owner.process_start_id === null && processOwnerIsLive(owner) === "dead";
}

function readTransitionRecord(root: string, file: string, owner: ProcessOwner): TransitionRecord | undefined {
  try {
    const parsed = parseTransitionRecord(readSecureFile(root, file, 4096).toString("utf8"), owner);
    return parsed?.nonce === owner.nonce ? parsed : undefined;
  } catch (error) { if (isCode(error, "ENOENT")) return undefined; throw error; }
}

function readTransitionRecordForUnknownOwner(root: string, file: string): TransitionRecord | undefined {
  try {
    const parsed = parseTransitionRecord(readSecureFile(root, file, 4096).toString("utf8"));
    return parsed && path.basename(file) === `transition-${parsed.nonce}.json` ? parsed : undefined;
  } catch (error) { if (isCode(error, "ENOENT")) return undefined; throw error; }
}

function parseTransitionRecord(text: string, owner?: ProcessOwner): TransitionRecord | undefined {
  try {
    const parsed = JSON.parse(text) as Partial<TransitionRecord>;
    const keys = Object.keys(parsed);
    const timestamp = typeof parsed.transitioned_at === "string" ? Date.parse(parsed.transitioned_at) : NaN;
    if (keys.length !== 7 || !["schema_version", "nonce", "operation", "transition_nonce", "pid", "process_start_id", "transitioned_at"].every((key) => keys.includes(key))
      || parsed.schema_version !== 1 || typeof parsed.nonce !== "string"
      || (!UUID_REGEX.test(parsed.nonce) && !(owner && isExactDeadLegacyOwner(owner, parsed.nonce)))
      || (parsed.operation !== "release" && parsed.operation !== "reclaim")
      || typeof parsed.transition_nonce !== "string" || !UUID_REGEX.test(parsed.transition_nonce)
      || !Number.isSafeInteger(parsed.pid) || (parsed.pid as number) <= 0
      || !(parsed.process_start_id === null || (typeof parsed.process_start_id === "string" && isProcessStartIdentity(parsed.process_start_id)))
      || !Number.isFinite(timestamp) || new Date(timestamp).toISOString() !== parsed.transitioned_at) return undefined;
    return parsed as TransitionRecord;
  } catch { return undefined; }
}

type DeleteTombstoneMetadata = { schema_version: 1; operation: "delete"; nonce: string; basename_hash: string; dev: string; ino: string; created_at: string; tomb_name: string };

function parseDeleteTombstoneMetadata(text: string, nonce: string): DeleteTombstoneMetadata | undefined {
  try {
    const value = JSON.parse(text) as Partial<DeleteTombstoneMetadata>;
    const keys = Object.keys(value);
    const timestamp = typeof value.created_at === "string" ? Date.parse(value.created_at) : NaN;
    if (keys.length !== 8 || !["schema_version", "operation", "nonce", "basename_hash", "dev", "ino", "created_at", "tomb_name"].every((key) => keys.includes(key))
      || value.schema_version !== 1 || value.operation !== "delete" || value.nonce !== nonce || value.tomb_name !== `.delete-v1-${nonce}.tomb`
      || typeof value.basename_hash !== "string" || !/^[0-9a-f]{64}$/.test(value.basename_hash)
      || typeof value.dev !== "string" || !/^\d+$/.test(value.dev) || typeof value.ino !== "string" || !/^\d+$/.test(value.ino)
      || !Number.isFinite(timestamp) || new Date(timestamp).toISOString() !== value.created_at) return undefined;
    return value as DeleteTombstoneMetadata;
  } catch { return undefined; }
}

function parseDeleteTombstoneMetadataForRecovery(text: string): DeleteTombstoneMetadata | undefined {
  try {
    const value = JSON.parse(text) as { nonce?: unknown };
    return typeof value.nonce === "string" && UUID_REGEX.test(value.nonce) ? parseDeleteTombstoneMetadata(text, value.nonce) : undefined;
  } catch { return undefined; }
}

function sameIdentityFiles(root: string, dir: string, temp: string, identity: FileObjectIdentity): string[] {
  const matches: string[] = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const candidate = path.join(dir, entry.name);
    if (candidate === temp || !entry.isFile()) continue;
    const stat = secureFileStat(root, candidate);
    if (stat && sameIdentity(stat, identity)) matches.push(candidate);
  }
  return matches;
}

function unlinkVerifiedNow(root: string, file: string): void {
  secureFileStatRequired(root, file);
  fs.unlinkSync(file);
  fsyncDirectory(path.dirname(file));
}

function unlinkRedundantPublisherTemp(root: string, temp: string, identity: FileObjectIdentity, final: string, options: MaintainPublisherTempsOptions): void {
  assertPublishedIdentity(root, final, identity);
  options.beforeRedundantTempStat?.(temp, final);
  const currentTemp = securePublisherTempForCleanup(root, temp, identity, final);
  if (!currentTemp) {
    assertPublishedIdentity(root, final, identity);
    fsyncDirectory(path.dirname(temp));
    return;
  }

  options.beforeRedundantTempUnlink?.(temp, final);
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    try { fs.unlinkSync(temp); break; }
    catch (error) {
      const observed = secureFileStat(root, temp);
      if (!observed) { assertPublishedIdentity(root, final, identity); break; }
      if (!sameIdentity(identity, observed) || !isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
      assertPublishedIdentity(root, final, identity);
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
  fsyncDirectory(path.dirname(temp));
}

function unlinkUnpublishedPublisherTemp(root: string, temp: string, identity: FileObjectIdentity, final: string, options: DurablePublishOptions): void {
  options.beforeTempCleanupStat?.(temp, final);
  const currentTemp = securePublisherTempForCleanup(root, temp, identity);
  if (!currentTemp) {
    fsyncDirectory(path.dirname(temp));
    return;
  }

  options.beforeTempCleanupUnlink?.(temp, final);
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    try { fs.unlinkSync(temp); break; }
    catch (error) {
      const observed = secureFileStat(root, temp);
      if (!observed) break;
      if (!sameIdentity(identity, observed) || !isRetryableWindowsFsContention(error) || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
  fsyncDirectory(path.dirname(temp));
}

function assertPublishedIdentity(root: string, final: string, identity: FileObjectIdentity): void {
  const published = secureFileStatRequired(root, final);
  if (!sameIdentity(identity, published)) throw new Error("Published storage identity changed before temporary cleanup");
}

function securePublisherTempForCleanup(root: string, temp: string, identity: FileObjectIdentity, final?: string): SecureFileStat | undefined {
  const deadline = Date.now() + LOCK_TRANSITION_RETRY_MS;
  while (true) {
    try {
      const current = secureFileStat(root, temp);
      if (!current) return undefined;
      if (!sameIdentity(identity, current)) throw new Error("Publisher temporary identity changed before cleanup");
      return current;
    } catch (error) {
      if (final) assertPublishedIdentity(root, final, identity);
      if (!isRetryableWindowsPathContention(error) && !(process.platform === "win32" && error instanceof Error && error.message === "Storage path escapes root") || Date.now() >= deadline) throw error;
      Atomics.wait(SLEEP, 0, 0, 5);
    }
  }
}

function isExpectedPublicationTempName(name: string): boolean {
  return new RegExp(`^\\.publish-v1-\\d+-\\d+-(?:x[0-9a-f]{16}|[0-9a-f]{16}|unknown)-${UUID_PATTERN}\\.tmp$`).test(name);
}

function finalizeDeletePair(root: string, dir: string, nonce: string, maintenance = false): void {
  const tombPath = path.join(dir, `.delete-v1-${nonce}.tomb`);
  const metadataPath = path.join(dir, `.delete-v1-${nonce}.json`);
  const metadataStat = secureFileStat(root, metadataPath);
  let tombStat = secureFileStat(root, tombPath);
  if (!metadataStat) {
    if (!tombStat || !secureFileStat(root, tombPath)) return;
    throw new Error("Delete tombstone metadata is missing");
  }
  let metadata: DeleteTombstoneMetadata | undefined;
  try { metadata = parseDeleteTombstoneMetadata(readSecureFile(root, metadataPath, 4096).toString("utf8"), nonce); }
  catch (error) {
    if (isCode(error, "ENOENT") && !secureFileStat(root, tombPath) && !secureFileStat(root, metadataPath)) return;
    throw error;
  }
  if (!metadata) throw new Error("Delete tombstone metadata is invalid");
  tombStat = secureFileStat(root, tombPath);
  if (!tombStat && maintenance && Date.now() - Date.parse(metadata.created_at) < DELETE_OPERATION_GRACE_MS) return;
  if (!tombStat) {
    try { unlinkVerifiedNow(root, metadataPath); } catch (error) { if (!isCode(error, "ENOENT")) throw error; }
    return;
  }
  const metadataIdentity = deleteMetadataIdentity(metadata);
  if (!metadataIdentity) throw new Error("Delete tombstone metadata identity is uncertain");
  if (!sameIdentity(tombStat, metadataIdentity)) throw new Error("Delete tombstone identity does not match metadata");
  try { unlinkVerifiedNow(root, tombPath); } catch (error) { if (!isCode(error, "ENOENT")) throw error; }
  try { unlinkVerifiedNow(root, metadataPath); } catch (error) { if (!isCode(error, "ENOENT")) throw error; }
}

function lstatFileObject(file: string): FileObjectStat { return fileObjectStat(fs.lstatSync(file, { bigint: true })); }
function fstatFileObject(handle: number): FileObjectStat { return fileObjectStat(fs.fstatSync(handle, { bigint: true })); }

function fileObjectStat(stat: BigIntStats): FileObjectStat {
  const mtimeMs = boundedBigIntNumber(stat.mtimeMs, -8_640_000_000_000_000, 8_640_000_000_000_000, "mtime");
  return {
    dev: stat.dev,
    ino: stat.ino,
    size: boundedBigIntNumber(stat.size, 0, Number.MAX_SAFE_INTEGER, "size"),
    nlink: boundedBigIntNumber(stat.nlink, 0, Number.MAX_SAFE_INTEGER, "link count"),
    mtime: new Date(mtimeMs),
    mtimeMs,
    isFile: stat.isFile(),
    isDirectory: stat.isDirectory(),
    isSymbolicLink: stat.isSymbolicLink(),
  };
}

function boundedBigIntNumber(value: bigint, minimum: number, maximum: number, field: string): number {
  if (value < BigInt(minimum) || value > BigInt(maximum)) throw new Error(`Storage file ${field} is out of range`);
  return Number(value);
}

function deleteMetadataIdentity(metadata: DeleteTombstoneMetadata): FileObjectIdentity | undefined {
  try {
    const identity = { dev: BigInt(metadata.dev), ino: BigInt(metadata.ino) };
    return identityCertain(identity, process.platform) ? identity : undefined;
  } catch { return undefined; }
}
