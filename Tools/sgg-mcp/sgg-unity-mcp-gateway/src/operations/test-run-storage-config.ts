import { createHash } from "node:crypto";
import fs, { type BigIntStats } from "node:fs";
import path from "node:path";

import { applyEdits, modify } from "jsonc-parser/lib/esm/main.js";

import { parseExpandedGatewayConfig, parseGatewayConfig, type GatewayConfig } from "../config/config.js";
import { CanonicalPathService } from "../paths/canonical-path.js";
import { openStorageDirectoryCustody, sameFileObjectIdentity, type FileObjectIdentity, type StorageDirectoryCustody } from "../security/safe-storage.js";
import { testRunOperationStoreDir } from "./test-run-operation-store.js";
import { TestRunStoreAdminError, TEST_RUN_STORE_ADMIN_DEADLINE_MS } from "./test-run-store-admin.js";
import { runNativeStorageCommit, type NativeFileEvidence } from "./test-run-storage-native.js";

export type TestRunStorageConfigSnapshot = { source: string; config: GatewayConfig; fingerprint: string; scopeFingerprint: string; stat: BigIntStats; directoryIdentity: FileObjectIdentity };

export function readTestRunStorageConfig(file: string, env: NodeJS.ProcessEnv = process.env): TestRunStorageConfigSnapshot {
  try {
    const components = file.replaceAll("\\", "/").split("/");
    if (!path.isAbsolute(file) || components.some(component => component === "." || component === "..")
      || components.slice(1).some(component => component === "")
      || (process.platform === "win32" && !/^[A-Za-z]:[\\/]/.test(file))) throw new Error("Invalid host path");
    new CanonicalPathService().assertInsideProject(path.dirname(file), file);
  } catch { throw new TestRunStoreAdminError("config_unavailable"); }
  let handle: number | undefined;
  let custody: StorageDirectoryCustody | undefined;
  try {
    const entry = fs.lstatSync(file, { bigint: true });
    if (!entry.isFile() || entry.isSymbolicLink() || entry.nlink !== 1n || entry.size <= 0n || entry.size > 1048576n) throw new TestRunStoreAdminError("config_unavailable");
    custody = openStorageDirectoryCustody(path.dirname(file));
    const pinnedFile = custody.pathFor(path.basename(file));
    handle = fs.openSync(pinnedFile, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0) | (fs.constants.O_NONBLOCK ?? 0));
    const opened = fs.fstatSync(handle, { bigint: true });
    if (!sameSnapshot(entry, opened)) throw new TestRunStoreAdminError("config_changed");
    const buffer = Buffer.alloc(Number(opened.size) + 1);
    let length = 0;
    while (length < buffer.length) {
      const read = fs.readSync(handle, buffer, length, buffer.length - length, null);
      if (read === 0) break;
      length += read;
    }
    const after = fs.fstatSync(handle, { bigint: true });
    const current = fs.lstatSync(pinnedFile, { bigint: true });
    if (!sameSnapshot(opened, after) || !sameSnapshot(entry, current) || !sameSnapshot(after, current) || BigInt(length) !== opened.size) throw new TestRunStoreAdminError("config_changed");
    custody.assertCurrent();
    const source = new TextDecoder("utf-8", { fatal: true }).decode(buffer.subarray(0, length));
    const config = parseExpandedGatewayConfig(source, env);
    const storeDir = testRunOperationStoreDir(config.registry.instances_dir);
    const fingerprint = hash(JSON.stringify([file, storeDir, custody.identity.dev.toString(), custody.identity.ino.toString(), current.dev.toString(), current.ino.toString(), current.size.toString(), current.mtimeNs.toString(), current.ctimeNs.toString(), current.mode.toString(), hash(source)]));
    const scopeFingerprint = hash(JSON.stringify([file, storeDir]));
    return { source, config, fingerprint, scopeFingerprint, stat: current, directoryIdentity: custody.identity };
  } finally { if (handle !== undefined) fs.closeSync(handle); custody?.close(); }
}

/** Compare-and-write only the two owned settings. Requires an explicit exact current-config confirmation. */
export function configureTestRunStorage(file: string, options: { maxBytes: number; maxBindings: number; confirm: boolean; configFingerprint: string; deadlineUnixMs?: number }, env: NodeJS.ProcessEnv = process.env): TestRunStorageConfigSnapshot {
  if (options.confirm !== true || !/^sha256:[0-9a-f]{64}$/.test(options.configFingerprint)) throw new TestRunStoreAdminError("confirmation_required");
  const deadline = Math.min(Date.now() + TEST_RUN_STORE_ADMIN_DEADLINE_MS, options.deadlineUnixMs ?? Number.MAX_SAFE_INTEGER);
  const original = readTestRunStorageConfig(file, env);
  if (original.fingerprint !== options.configFingerprint) throw new TestRunStoreAdminError("config_changed");
  const formattingOptions = { insertSpaces: true, tabSize: 2, eol: original.source.includes("\r\n") ? "\r\n" : "\n" };
  let source = applyEdits(original.source, modify(original.source, ["test_runs", "storage", "max_bytes"], options.maxBytes, { formattingOptions }));
  source = applyEdits(source, modify(source, ["test_runs", "storage", "max_bindings"], options.maxBindings, { formattingOptions }));
  try { parseGatewayConfig(source); } catch { throw new TestRunStoreAdminError("invalid_limits"); }
  runNativeStorageCommit({ operation: "configure", root: path.dirname(file), root_dev: original.directoryIdentity.dev.toString(), root_ino: original.directoryIdentity.ino.toString(), config: configSnapshotEvidence(file, original), original_base64: Buffer.from(original.source).toString("base64"), updated_base64: Buffer.from(source).toString("base64") }, deadline);
  const committed = readTestRunStorageConfig(file, env);
  if (committed.source !== source || committed.scopeFingerprint !== original.scopeFingerprint) throw new TestRunStoreAdminError("config_changed");
  return committed;
}
export function configSnapshotEvidence(file: string, snapshot: TestRunStorageConfigSnapshot): NativeFileEvidence {
  return { path: file, dev: snapshot.stat.dev.toString(), ino: snapshot.stat.ino.toString(), size: snapshot.stat.size.toString(), mtime_ns: snapshot.stat.mtimeNs.toString(), ctime_ns: snapshot.stat.ctimeNs.toString(), content_hash: hash(snapshot.source) };
}
function sameSnapshot(a: BigIntStats, b: BigIntStats): boolean {
  return a.isFile() && b.isFile() && !b.isSymbolicLink() && a.nlink === 1n && b.nlink === 1n && sameFileObjectIdentity(a, b, process.platform)
    && a.size === b.size && a.mtimeNs === b.mtimeNs && a.ctimeNs === b.ctimeNs && a.mode === b.mode;
}
function hash(value: string): string { return `sha256:${createHash("sha256").update(value).digest("hex")}`; }
