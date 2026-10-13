import { spawn, type ChildProcess } from "node:child_process";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalStore } from "../src/approvals/approval-store.js";
import { FileAuditSink, createAuditEvent } from "../src/observability/audit.js";
import { acquireStorageLock, durableUnlinkFile, maintainDeleteTombstones, maintainPublisherTemps, ownerIdentityTag, readSecureFile, sameFileObjectIdentity, writeNewFileDurable } from "../src/security/safe-storage.js";

const roots: string[] = [];
const windowsIt = process.platform === "win32" ? it : it.skip;
const fixture = path.join(process.cwd(), "tests", "fixtures", "storage-child.ts");

afterEach(() => { for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true }); });

describe("native Windows safe storage", () => {
  windowsIt("accepts the native path/descriptor device asymmetry across storage consumers", () => {
    const root = tempRoot();
    const existing = path.join(root, "existing.json");
    fs.writeFileSync(existing, "existing");
    const pathStat = fs.lstatSync(existing, { bigint: true });
    const handle = fs.openSync(existing, fs.constants.O_RDONLY);
    const descriptorStat = fs.fstatSync(handle, { bigint: true });
    fs.closeSync(handle);

    expect(pathStat.ino).toBe(descriptorStat.ino);
    expect(pathStat.ino).not.toBe(0n);
    expect(pathStat.dev === descriptorStat.dev || ((pathStat.dev === 0n) !== (descriptorStat.dev === 0n))).toBe(true);
    expect(sameFileObjectIdentity(pathStat, descriptorStat, "win32")).toBe(true);
    expect(readSecureFile(root, existing).toString("utf8")).toBe("existing");

    const published = path.join(root, "published.json");
    writeNewFileDurable(root, published, "published");
    expect(readSecureFile(root, published).toString("utf8")).toBe("published");

    const deleted = path.join(root, "deleted.json");
    fs.writeFileSync(deleted, "deleted");
    const deletedIdentity = fs.lstatSync(deleted, { bigint: true });
    expect(() => durableUnlinkFile(root, deleted, { afterRename: () => { throw new Error("injected crash"); } })).toThrow("injected crash");
    const deleteMetadataPath = path.join(root, fs.readdirSync(root).find((name) => name.startsWith(".delete-v1-") && name.endsWith(".json"))!);
    const deleteMetadata = JSON.parse(fs.readFileSync(deleteMetadataPath, "utf8"));
    expect(BigInt(deleteMetadata.ino)).toBe(deletedIdentity.ino);
    maintainDeleteTombstones(root, root);
    expect(fs.readdirSync(root).filter((name) => name.startsWith(".delete-v1-"))).toEqual([]);

    const approvals = new ApprovalStore(path.join(root, "approvals"));
    expect(approvals.listPending()).toEqual([]);
    expect(approvals.status().state).toBe("ok");

    const lock = acquireStorageLock(root, ".native.lock");
    lock.release();
    expect(fs.readdirSync(root).filter((name) => name.includes(".native.lock"))).toEqual([]);
  });

  windowsIt("keeps audit rotation healthy without residual lock generations", () => {
    const root = tempRoot();
    const auditDir = path.join(root, "audit");
    let clock = new Date("2026-07-12T23:59:00.000Z");
    const sink = new FileAuditSink(auditDir, { now: () => clock });
    const event = createAuditEvent("sgg_unity_projects", {}, { status: "ok", data: {}, diagnostics: [] }, clock.getTime(), () => clock);
    sink.write(event);
    clock = new Date("2026-07-13T00:01:00.000Z");
    sink.write({ ...event, timestamp: clock.toISOString(), run_id: "after-rotation" });

    expect(sink.status()).toMatchObject({ state: "ok", files: 2, rotation_period: "utc_day" });
    expect(fs.readdirSync(auditDir).filter((name) => name.startsWith("audit-") && name.endsWith(".ndjson"))).toHaveLength(1);
    expect(fs.readdirSync(auditDir).filter((name) => name.includes(".audit.lock.gen-") || name.includes(".audit.lock.tomb-") || name.includes(".audit.lock.transition-gate"))).toEqual([]);
  });

  windowsIt("retries only revalidated Windows rename contention and resumes persistent release contention", async () => {
    const root = tempRoot();
    let generationAttempts = 0;
    let releaseAttempts = 0;
    const lock = acquireStorageLock(root, ".native.lock", {
      beforeGenerationRename: () => { if (generationAttempts++ === 0) throw windowsContention(); },
      beforeTransitionRename: () => { if (releaseAttempts++ === 0) throw windowsContention(); },
    });
    lock.release();
    expect(generationAttempts).toBeGreaterThanOrEqual(2);
    expect(releaseAttempts).toBeGreaterThanOrEqual(2);
    expect(fs.readdirSync(root).filter((name) => name.includes(".native.lock"))).toEqual([]);

    let persistentAttempts = 0;
    const pending = acquireStorageLock(root, ".native.lock", {
      beforeTransitionRename: () => { persistentAttempts += 1; throw windowsContention(); },
    });
    pending.release();
    expect(persistentAttempts).toBeGreaterThan(1);
    expect(fs.readdirSync(path.join(root, ".native.lock")).filter((name) => name.startsWith("transition-"))).toHaveLength(1);
    const resumed = acquireStorageLock(root, ".native.lock", { timeoutMs: 2_000, staleAfterMs: 10 });
    expect(resumed.diagnostics).toMatchObject({ owner_generation_state: "live", automatic_recovery: "resumed_interrupted_transition" });
    resumed.release();
    expect(fs.readdirSync(root).filter((name) => name.includes(".native.lock"))).toEqual([]);

    const crashed = startChild(["lock", root, "crash"]);
    await childLine(crashed);
    await childExit(crashed);
    let reclaimAttempts = 0;
    const reclaimed = acquireStorageLock(root, ".test.lock", {
      timeoutMs: 2_000,
      staleAfterMs: 0,
      beforeTransitionRename: () => { if (reclaimAttempts++ === 0) throw windowsContention(); },
    });
    reclaimed.release();
    expect(reclaimAttempts).toBeGreaterThanOrEqual(2);
    expect(fs.readdirSync(root).filter((name) => name.includes(".test.lock"))).toEqual([]);
  }, 30_000);

  windowsIt("keeps live exact and legacy Node owners but reclaims a reused non-Node PID generation", async () => {
    const liveRoot = tempRoot();
    const releaseSignal = path.join(liveRoot, "release.signal");
    const liveOwner = startChild(["lock-signal", liveRoot, releaseSignal]);
    expect(await childLine(liveOwner)).toBe("acquired");

    expect(() => acquireStorageLock(liveRoot, ".test.lock", { timeoutMs: 250, staleAfterMs: 0 })).toThrow("Storage lock unavailable");
    expect(fs.existsSync(path.join(liveRoot, ".test.lock", "owner.json"))).toBe(true);
    fs.writeFileSync(releaseSignal, "release");
    await childExit(liveOwner);

    const legacyRoot = tempRoot();
    const legacyExit = path.join(legacyRoot, "legacy.exit");
    const legacyHolder = startChild(["transition-owner-hold", legacyRoot, legacyExit]);
    const legacyHolderOwner = JSON.parse(await childLine(legacyHolder));
    const legacyLock = path.join(legacyRoot, ".test.lock");
    fs.mkdirSync(legacyLock, { mode: 0o700 });
    fs.writeFileSync(path.join(legacyLock, "owner.json"), `${JSON.stringify({ ...legacyHolderOwner, process_start_id: "uptime:100000", acquired_at: new Date(Date.now() - 60_000).toISOString() })}\n`);
    expect(() => acquireStorageLock(legacyRoot, ".test.lock", { timeoutMs: 500, staleAfterMs: 0 })).toThrow("Storage lock unavailable");
    expect(fs.existsSync(path.join(legacyLock, "owner.json"))).toBe(true);
    fs.writeFileSync(legacyExit, "exit");
    await childExit(legacyHolder);

    const reusedRoot = tempRoot();
    const holderExit = path.join(reusedRoot, "holder.exit");
    const powershell = path.join(process.env.SystemRoot!, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    const holder = spawn(powershell, ["-NoProfile", "-NonInteractive", "-Command", "while (-not (Test-Path -LiteralPath $env:SGG_EXIT_SIGNAL)) { Start-Sleep -Milliseconds 20 }"], {
      env: { ...process.env, SGG_EXIT_SIGNAL: holderExit },
      stdio: ["ignore", "pipe", "pipe"],
    });
    await childStarted(holder);
    const staleOwner = {
      schema_version: 1,
      nonce: crypto.randomUUID(),
      pid: holder.pid!,
      process_start_id: "uptime:100000",
      acquired_at: new Date(Date.now() - 60_000).toISOString(),
    };

    const boundedLockRoot = tempRoot();
    const boundedLockPath = path.join(boundedLockRoot, ".test.lock");
    fs.mkdirSync(boundedLockPath, { mode: 0o700 });
    fs.writeFileSync(path.join(boundedLockPath, "owner.json"), `${JSON.stringify(staleOwner)}\n`);
    let started = Date.now();
    expect(() => acquireStorageLock(boundedLockRoot, ".test.lock", { timeoutMs: 25, staleAfterMs: 0 })).toThrow("Storage lock unavailable");
    expect(Date.now() - started).toBeLessThan(1_000);
    expect(fs.existsSync(boundedLockPath)).toBe(true);

    await new Promise((resolve) => setTimeout(resolve, 300));
    const boundedGateRoot = tempRoot();
    const boundedGatePath = path.join(boundedGateRoot, ".test.lock.transition-gate");
    fs.mkdirSync(boundedGatePath, { mode: 0o700 });
    fs.writeFileSync(path.join(boundedGatePath, "owner.json"), `${JSON.stringify(staleOwner)}\n`);
    started = Date.now();
    expect(() => acquireStorageLock(boundedGateRoot, ".test.lock", { timeoutMs: 25, staleAfterMs: 0 })).toThrow("transition gate is uncertain");
    expect(Date.now() - started).toBeLessThan(1_000);
    expect(fs.existsSync(boundedGatePath)).toBe(true);

    await new Promise((resolve) => setTimeout(resolve, 300));
    const boundedTempRoot = tempRoot();
    const boundedTempName = `.publish-v1-${Date.now() - 60_000}-${holder.pid!}-${ownerIdentityTag(staleOwner)}-${crypto.randomUUID()}.tmp`;
    fs.writeFileSync(path.join(boundedTempRoot, boundedTempName), "uncertain-owner");
    started = Date.now();
    expect(() => maintainPublisherTemps(boundedTempRoot, boundedTempRoot, { deadlineUnixMs: Date.now() + 25 })).toThrow("Publisher temporary owner is uncertain");
    expect(Date.now() - started).toBeLessThan(1_000);
    expect(fs.existsSync(path.join(boundedTempRoot, boundedTempName))).toBe(true);

    await new Promise((resolve) => setTimeout(resolve, 300));
    const lockPath = path.join(reusedRoot, ".test.lock");
    fs.mkdirSync(lockPath, { mode: 0o700 });
    fs.writeFileSync(path.join(lockPath, "owner.json"), `${JSON.stringify(staleOwner)}\n`);
    const tempName = `.publish-v1-${Date.now() - 60_000}-${holder.pid!}-${ownerIdentityTag(staleOwner)}-${crypto.randomUUID()}.tmp`;
    fs.writeFileSync(path.join(reusedRoot, tempName), "abandoned");

    const replacement = acquireStorageLock(reusedRoot, ".test.lock", { timeoutMs: 5_000, staleAfterMs: 0 });
    expect(replacement.diagnostics).toMatchObject({ owner_generation_state: "replaced", automatic_recovery: "reclaimed_replaced_owner" });
    expect(fs.existsSync(path.join(reusedRoot, tempName))).toBe(false);
    replacement.release();
    fs.writeFileSync(holderExit, "exit");
    await childExit(holder);
  }, 30_000);
});

function tempRoot(): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-windows-storage-"));
  roots.push(root);
  return root;
}

function windowsContention(code = "EPERM"): NodeJS.ErrnoException { return Object.assign(new Error("injected Windows rename contention"), { code }); }
function startChild(args: string[]): ChildProcess { return spawn(process.execPath, ["--import", "tsx", fixture, ...args], { stdio: ["ignore", "pipe", "pipe"] }); }
function childStarted(child: ChildProcess): Promise<void> { return new Promise((resolve, reject) => { if (child.pid) resolve(); else { child.once("spawn", resolve); child.once("error", reject); } }); }
function childLine(child: ChildProcess): Promise<string> { return new Promise((resolve, reject) => { child.stdout?.once("data", (data) => resolve(String(data).trim())); child.stderr?.once("data", (data) => reject(new Error(String(data)))); child.once("error", reject); }); }
function childExit(child: ChildProcess): Promise<void> { return new Promise((resolve, reject) => { if (child.exitCode !== null) resolve(); else { child.once("exit", (code) => code === 0 ? resolve() : reject(new Error(`child exited ${code}`))); child.once("error", reject); } }); }
