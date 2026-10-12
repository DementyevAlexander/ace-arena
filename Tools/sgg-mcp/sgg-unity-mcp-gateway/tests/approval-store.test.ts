import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn, type ChildProcess } from "node:child_process";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalBindingService, commandDescriptorRevision, createApprovalBindingEnvelope, effectivePolicyRevision } from "../src/approvals/approval-binding.js";
import { ApprovalStore, ApprovalStoreError, argsHash, canonicalProjectRootHash, type ApprovalSafeMetadata } from "../src/approvals/approval-store.js";
import type { CommandDescriptor } from "../src/bridge/bridge-client.js";

const dirs: string[] = [];
const now = new Date("2026-07-11T10:00:00.000Z");
const service = new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) });
const storageChildFixture = path.join(process.cwd(), "tests", "fixtures", "storage-child.ts");
const approvalChildFixture = path.join(process.cwd(), "tests", "fixtures", "approval-store-child.ts");

afterEach(() => {
  for (const dir of dirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("schema-v3 approval store", () => {
  it("persists the exact internal identity shape while every public result hides identity and binding internals", () => {
    const dir = tempDir();
    const store = new ApprovalStore(dir);
    const invocation = approvalInvocation({ args: { token: "raw-secret", path: "C:/private/root" } });
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
    assertPublicSafe(pending);

    const pendingPath = path.join(dir, "pending", `${pending.id}.json`);
    const disk = JSON.parse(fs.readFileSync(pendingPath, "utf8")) as Record<string, unknown>;
    expect(Object.keys(disk).sort()).toEqual([
      "args_hash", "binding", "canonical_project_root_hash", "command", "command_revision", "commands_hash", "created_at", "effective_policy_revision", "expires_at", "id", "idempotency", "instance_id", "mode", "policy_action", "policy_pattern", "policy_reason", "project_id", "record_type", "request_id", "risk", "schema_version", "scope", "status", "updated_at",
    ].sort());
    expect(disk).toMatchObject({ schema_version: 3, record_type: "approval_request", status: "pending", project_id: "game", canonical_project_root_hash: canonicalProjectRootHash("c:/private/game"), instance_id: "instance-a", command: "asset.import", command_revision: invocation.metadata.command_revision, commands_hash: "catalog-a", risk: "write", idempotency: "idempotent", mode: "wait", request_id: "request-a", policy_action: "ask", policy_reason: "project_rule", policy_pattern: "asset.import", scope: "single_call_only" });
    expect(JSON.stringify(disk)).not.toMatch(/raw-secret|C:\/private\/root|c:\/private\/game|project_selector/);
    expect(disk.binding).toMatchObject({ key_id: expect.stringMatching(/^sha256:/), request_hmac: expect.stringMatching(/^[0-9a-f]{64}$/) });

    const approved = store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now });
    assertPublicSafe(approved);
    assertPublicSafe(store.list({ now }));
    const consumed = store.consume(invocation.metadata, invocation.envelope, service, now);
    expect(consumed).toEqual({ status: "consumed" });
    assertPublicSafe(consumed);

    const activeText = fs.readdirSync(dir).filter((name) => name.endsWith(".json")).map((name) => fs.readFileSync(path.join(dir, name), "utf8")).join("");
    expect(activeText).not.toMatch(/raw-secret|c:\/private\/game|project_selector/);
  });

  it("reuses an identical valid pending record without extending any timestamp and replaces only an expired record", () => {
    const dir = tempDir();
    const store = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    const first = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
    const repeated = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 900_000, now: new Date(now.getTime() + 30_000) });
    expect(repeated).toMatchObject({ id: first.id, created_at: first.created_at, updated_at: first.updated_at, expires_at: first.expires_at });

    const replacementTime = new Date(now.getTime() + 60_001);
    const replacement = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 120_000, now: replacementTime });
    expect(replacement.id).toBe(first.id);
    expect(replacement.created_at).toBe(replacementTime.toISOString());
    expect(replacement.expires_at).toBe(new Date(replacementTime.getTime() + 120_000).toISOString());
    expect(fs.existsSync(path.join(dir, "claims")) ? fs.readdirSync(path.join(dir, "claims")) : []).toHaveLength(0);
  });

  it("allows only one pending promotion and one active consumption across store instances", () => {
    const dir = tempDir();
    const firstStore = new ApprovalStore(dir);
    const secondStore = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    const pending = firstStore.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
    firstStore.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now });
    expect(() => secondStore.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now })).toThrow(ApprovalStoreError);
    expect(firstStore.consume(invocation.metadata, invocation.envelope, service, now)).toEqual({ status: "consumed" });
    expect(secondStore.consume(invocation.metadata, invocation.envelope, service, now)).toEqual({ status: "not_found" });
  });

  it("atomically gives an exact pending generation to only one native accepter", () => {
    const dir = tempDir();
    const firstStore = new ApprovalStore(dir);
    const secondStore = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    const pending = firstStore.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
    const generation = { id: pending.id, created_at: pending.created_at, updated_at: pending.updated_at, expires_at: pending.expires_at };

    expect(firstStore.claimPendingGeneration(generation, { ...invocation, bindingService: service }, now)).toEqual({ status: "claimed" });
    expect(secondStore.claimPendingGeneration(generation, { ...invocation, bindingService: service }, now)).toEqual({ status: "not_found" });
  });

  it("does not claim a replacement generation and never revives a context-invalidated generation", () => {
    const store = new ApprovalStore(tempDir());
    const invocation = approvalInvocation();
    const first = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 1, now });
    const firstGeneration = { id: first.id, created_at: first.created_at, updated_at: first.updated_at, expires_at: first.expires_at };
    const replacementNow = new Date(now.getTime() + 2);
    const replacement = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now: replacementNow });

    expect(store.claimPendingGeneration(firstGeneration, { ...invocation, bindingService: service }, replacementNow)).toEqual({ status: "not_found" });
    expect(store.listPending({ now: replacementNow })).toMatchObject([{ id: replacement.id, created_at: replacement.created_at }]);

    const changed = approvalInvocation({ commands_hash: "catalog-b" });
    const replacementGeneration = { id: replacement.id, created_at: replacement.created_at, updated_at: replacement.updated_at, expires_at: replacement.expires_at };
    expect(store.claimPendingGeneration(replacementGeneration, { ...changed, bindingService: service }, replacementNow)).toEqual({ status: "context_changed" });
    expect(store.claimPendingGeneration(replacementGeneration, { ...invocation, bindingService: service }, replacementNow)).toEqual({ status: "not_found" });
  }, 15_000);

  it("uses metadata only to narrow, then timing-safe verification after claim", () => {
    const dir = tempDir();
    const store = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
    store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now });
    const activePath = path.join(dir, fs.readdirSync(dir).find((name) => name.endsWith(".json"))!);
    const record = JSON.parse(fs.readFileSync(activePath, "utf8"));
    record.binding.request_hmac = "b".repeat(64);
    fs.writeFileSync(activePath, JSON.stringify(record));

    expect(store.consume(invocation.metadata, invocation.envelope, service, now)).toEqual({ status: "not_found" });
    expect(fs.existsSync(activePath)).toBe(false);
    expect(fs.readdirSync(path.join(dir, "claims"))).toHaveLength(0);
    expect(fs.readdirSync(path.join(dir, "quarantine"))).toHaveLength(1);
  });

  it("post-claim expiry fails closed and leaves the approval non-authorizing", () => {
    const store = new ApprovalStore(tempDir());
    const invocation = approvalInvocation();
    const old = new Date(now.getTime() - 1000);
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now: old });
    store.approvePending({ pending_id: pending.id, ttl_ms: 1, now: old });
    expect(store.consume(invocation.metadata, invocation.envelope, service, now)).toEqual({ status: "expired" });
    expect(store.consume(invocation.metadata, invocation.envelope, service, now)).toEqual({ status: "not_found" });
  });

  it("strictly rejects extra fields, invalid enums, malformed hashes, and non-canonical timestamps", () => {
    for (const mutation of [
      { extra: true },
      { risk: "admin" },
      { idempotency: "maybe" },
      { mode: "batch" },
      { command_revision: "sha256:bad" },
      { canonical_project_root_hash: "sha256:bad" },
      { created_at: "2026-07-11 10:00:00Z" },
    ]) {
      const dir = tempDir();
      const store = new ApprovalStore(dir);
      const invocation = approvalInvocation();
      const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
      store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now });
      const activePath = path.join(dir, fs.readdirSync(dir).find((name) => name.endsWith(".json"))!);
      const record = JSON.parse(fs.readFileSync(activePath, "utf8"));
      fs.writeFileSync(activePath, JSON.stringify({ ...record, ...mutation }));
      expect(store.list({ includeExpired: true, now })).toEqual([]);
    }
  }, 30_000);

  it("never authorizes prior v1/v2 records or old bindings and atomically quarantines them", () => {
    const dir = tempDir();
    const pendingDir = path.join(dir, "pending");
    fs.mkdirSync(pendingDir, { mode: 0o700 });
    const legacy = { schema_version: 1, id: "legacy", approval_type: "unity_command", project: "game", command: "asset.import", args_hash: "sha256:legacy", scope: "single_call_only", created_at: now.toISOString(), expires_at: new Date(now.getTime() + 60_000).toISOString() };
    fs.writeFileSync(path.join(dir, "legacy.json"), JSON.stringify(legacy));
    fs.writeFileSync(path.join(pendingDir, "legacy.json"), JSON.stringify({ ...legacy, record_type: "approval_request", status: "pending" }));
    fs.writeFileSync(path.join(dir, "legacy-v2.json"), JSON.stringify({ ...legacy, schema_version: 2 }));
    fs.writeFileSync(path.join(pendingDir, "legacy-v2.json"), JSON.stringify({ ...legacy, schema_version: 2, record_type: "approval_request", status: "pending" }));
    const store = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    expect(store.consume(invocation.metadata, invocation.envelope, service, now)).toEqual({ status: "not_found" });
    expect(store.listPending({ includeExpired: true, now })).toEqual([]);
    expect(fs.readdirSync(path.join(dir, "quarantine"))).toHaveLength(4);
  });

  it("quarantines a schema-v3 record carrying an old binding without migration", () => {
    const dir = tempDir();
    const store = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
    const pendingPath = path.join(dir, "pending", `${pending.id}.json`);
    const record = JSON.parse(fs.readFileSync(pendingPath, "utf8"));
    record.binding.schema_version = 1;
    fs.writeFileSync(pendingPath, JSON.stringify(record));

    expect(store.listPending({ includeExpired: true, now })).toEqual([]);
    expect(fs.readdirSync(path.join(dir, "quarantine"))).toHaveLength(1);
  });

  it("bounds corrupted quarantine and reports only aggregate store status", () => {
    const dir = tempDir();
    const store = new ApprovalStore(dir, { maxQuarantineFiles: 2, maxQuarantineBytes: 1024, quarantineMaxAgeMs: 60_000, now: () => now });
    fs.mkdirSync(dir, { recursive: true });
    for (let index = 0; index < 3; index += 1) fs.writeFileSync(path.join(dir, `corrupt-${index}.json`), "{not-json");

    expect(store.list({ now })).toEqual([]);
    expect(store.status(now)).toEqual({ schema_version: 1, state: "ok", records: 0, bytes: 0, quarantine_files: 2, quarantine_bytes: 18 });
    expect(JSON.stringify(store.status(now))).not.toMatch(/path|binding|project|command/i);
  });

  it("fails closed on record capacity without consuming an otherwise valid approval", () => {
    const dir = tempDir();
    const store = new ApprovalStore(dir, { maxRecords: 1 });
    const invocation = approvalInvocation();
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now });
    store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now });
    const activeName = fs.readdirSync(dir).find((name) => name.endsWith(".json"))!;
    const duplicate = JSON.parse(fs.readFileSync(path.join(dir, activeName), "utf8"));
    duplicate.id = "capacity-duplicate";
    fs.writeFileSync(path.join(dir, "capacity-duplicate.json"), JSON.stringify(duplicate));

    expect(() => store.consume(invocation.metadata, invocation.envelope, service, now)).toThrow(ApprovalStoreError);
    expect(store.status(now).state).toBe("capacity_exhausted");
    expect(fs.readdirSync(dir).filter((name) => name.endsWith(".json"))).toHaveLength(2);
  });

  it("returns the latest status snapshot without touching storage", () => {
    const parent = tempDir();
    const root = path.join(parent, "not-created");
    const store = new ApprovalStore(root);

    expect(store.snapshotStatus()).toEqual({ schema_version: 1, state: "ok", records: 0, bytes: 0, quarantine_files: 0, quarantine_bytes: 0 });
    expect(fs.existsSync(root)).toBe(false);
    expect(store.status(now).state).toBe("ok");
    expect(fs.existsSync(root)).toBe(true);
  });

  it("rejects linked roots, managed components, and JSON files without following them", () => {
    const parent = tempDir();
    const outsideDir = path.join(parent, "outside");
    fs.mkdirSync(outsideDir);
    const linkedRoot = path.join(parent, "linked-root");
    fs.symlinkSync(outsideDir, linkedRoot, "dir");
    expect(() => new ApprovalStore(linkedRoot).list()).toThrow(ApprovalStoreError);

    for (const component of ["pending", "claims", "quarantine"]) {
      const root = path.join(parent, `store-${component}`);
      fs.mkdirSync(root);
      fs.symlinkSync(outsideDir, path.join(root, component), "dir");
      expect(new ApprovalStore(root).status(now).state).toBe("unavailable");
    }

    const root = path.join(parent, "store-file");
    fs.mkdirSync(root);
    const outsideFile = path.join(parent, "outside.json");
    fs.writeFileSync(outsideFile, "{}\n");
    fs.symlinkSync(outsideFile, path.join(root, "linked.json"));
    expect(() => new ApprovalStore(root).list()).toThrow(ApprovalStoreError);
    expect(fs.readFileSync(outsideFile, "utf8")).toBe("{}\n");
  });

  it("keeps a live child claim and quarantines it only after TTL and confirmed child exit", async () => {
    const dir = tempDir();
    const clock = new Date();
    const store = new ApprovalStore(dir, { claimTtlMs: 10 });
    const invocation = approvalInvocation();
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now: clock });
    store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now: clock });
    const activePath = path.join(dir, fs.readdirSync(dir).find((name) => name.endsWith(".json"))!);
    // Native owner observation may take longer than a 200ms timer. Keep the
    // child provably live until the assertion, then explicitly confirm its exit.
    const release = path.join(dir, "claim-release.signal");
    const child = startStorageChild(["claim", dir, activePath], { ...process.env, SGG_TEST_RELEASE_FILE: release });
    let claimPath: string;
    try {
      claimPath = await childOutputLine(child);
      expect(store.status(new Date(clock.getTime() + 30_000)).records).toBe(1);
      expect(fs.existsSync(claimPath)).toBe(true);
    } finally {
      fs.writeFileSync(release, "release");
      await childProcessExit(child);
    }
    expect(store.status(new Date(clock.getTime() + 30_000)).records).toBe(0);
    expect(fs.existsSync(claimPath)).toBe(false);
    expect(fs.readdirSync(path.join(dir, "quarantine"))).toHaveLength(1);
  });

  it("reclaims a claim abandoned by a crashed child without reactivating it", async () => {
    const dir = tempDir();
    const clock = new Date();
    const store = new ApprovalStore(dir, { claimTtlMs: 10 });
    const invocation = approvalInvocation();
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now: clock });
    store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now: clock });
    const activePath = path.join(dir, fs.readdirSync(dir).find((name) => name.endsWith(".json"))!);
    const child = startStorageChild(["claim", dir, activePath]);
    const claimPath = await childOutputLine(child);
    await childProcessExit(child);

    expect(store.status(new Date(clock.getTime() + 30_000)).records).toBe(0);
    expect(fs.existsSync(claimPath)).toBe(false);
    expect(store.consume(invocation.metadata, invocation.envelope, service, new Date(clock.getTime() + 30_000))).toEqual({ status: "not_found" });
  });

  it("allows one child-process promotion and one child-process consumption", async () => {
    const dir = tempDir();
    const clock = new Date();
    const store = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now: clock });

    const promoted = await Promise.all([runApprovalChild("promote", dir, pending.id, clock), runApprovalChild("promote", dir, pending.id, clock)]);
    expect(promoted.sort()).toEqual(["promoted", "unavailable"]);
    const consumed = await Promise.all([runApprovalChild("consume", dir, "unused", clock), runApprovalChild("consume", dir, "unused", clock)]);
    expect(consumed.sort()).toEqual(["consumed", "not_found"]);
  }, 30_000);

  it("recovers a crashed active-approval delete without reauthorizing it", async () => {
    const dir = tempDir();
    const clock = new Date();
    const store = new ApprovalStore(dir);
    const invocation = approvalInvocation();
    const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now: clock });
    store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now: clock });
    const activePath = path.join(dir, fs.readdirSync(dir).find((name) => name.endsWith(".json"))!);
    const child = startStorageChild(["delete-crash", dir, activePath]);
    await childProcessExit(child);
    expect(fs.readdirSync(dir).filter((name) => name.startsWith(".delete-v1-"))).toHaveLength(2);

    expect(store.status(clock).records).toBe(0);
    expect(store.consume(invocation.metadata, invocation.envelope, service, clock)).toEqual({ status: "not_found" });
    expect(fs.readdirSync(dir).filter((name) => name.startsWith(".delete-v1-"))).toEqual([]);
  });

  it("recovers delete-metadata publication crashes without duplicating approval authority", async () => {
    for (const mode of ["delete-metadata-temp-crash", "delete-metadata-link-crash"]) {
      const dir = tempDir();
      const clock = new Date();
      const store = new ApprovalStore(dir);
      const invocation = approvalInvocation();
      const pending = store.request(invocation.metadata, invocation.envelope, service, { ttl_ms: 60_000, now: clock });
      store.approvePending({ pending_id: pending.id, ttl_ms: 60_000, now: clock });
      const activePath = path.join(dir, fs.readdirSync(dir).find((name) => name.endsWith(".json"))!);
      const child = startStorageChild([mode, dir, activePath]);
      await childProcessExit(child);

      expect(store.status(clock).records).toBe(1);
      expect(fs.readdirSync(dir).filter((name) => name.startsWith(".publish-v1-") || name.startsWith(".delete-v1-"))).toEqual([]);
      expect(store.consume(invocation.metadata, invocation.envelope, service, clock)).toEqual({ status: "consumed" });
      expect(store.consume(invocation.metadata, invocation.envelope, service, clock)).toEqual({ status: "not_found" });
    }
  }, 30_000);
});

function approvalInvocation(overrides: { args?: Record<string, unknown>; commands_hash?: string | null } = {}) {
  const descriptor = command();
  const args = overrides.args ?? { asset_path: "Assets/Foo.prefab" };
  const commandsHash = overrides.commands_hash === undefined ? "catalog-a" : overrides.commands_hash;
  return {
    metadata: {
      project_id: "game",
      canonical_project_root_hash: canonicalProjectRootHash("c:/private/game"),
      instance_id: "instance-a",
      command: descriptor.id,
      command_revision: commandDescriptorRevision(descriptor),
      commands_hash: commandsHash,
      risk: descriptor.risk,
      idempotency: descriptor.idempotency,
      mode: "wait",
      request_id: "request-a",
      args_hash: argsHash(args),
      effective_policy_revision: effectivePolicyRevision({ action: "ask", reason: "project_rule", pattern: "asset.import" }),
      policy_action: "ask",
      policy_reason: "project_rule",
      policy_pattern: "asset.import",
    } satisfies ApprovalSafeMetadata,
    envelope: createApprovalBindingEnvelope({ project_selector: "original selector", project_id: "game", canonical_project_root: "c:/private/game", instance_id: "instance-a", command: descriptor.id, args, mode: "wait", request_id: "request-a", commands_hash: commandsHash, risk: descriptor.risk, idempotency: descriptor.idempotency, descriptor, policy_action: "ask", policy_reason: "project_rule", policy_pattern: "asset.import" }),
  };
}

function command(): CommandDescriptor {
  return { id: "asset.import", title: "Import", description: "Import asset", package: "builtin", risk: "write", idempotency: "idempotent", timeout_ms: 1000, schema: { type: "object" } };
}

function assertPublicSafe(value: unknown): void {
  expect(JSON.stringify(value)).not.toMatch(/binding|key_id|request_hmac|canonical_project_root_hash|instance_id|commands_hash|command_revision/);
}

function tempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-approval-store-"));
  dirs.push(dir);
  return dir;
}

function startStorageChild(args: string[], env: NodeJS.ProcessEnv = process.env): ChildProcess { return spawn(process.execPath, ["--import", "tsx", storageChildFixture, ...args], { env, stdio: ["ignore", "pipe", "pipe"] }); }
function childOutputLine(child: ChildProcess): Promise<string> { return new Promise((resolve, reject) => { child.stdout?.once("data", (data) => resolve(String(data).trim())); child.stderr?.once("data", (data) => reject(new Error(String(data)))); child.once("error", reject); }); }
function childProcessExit(child: ChildProcess): Promise<void> { return new Promise((resolve, reject) => { if (child.exitCode !== null) resolve(); else { child.once("exit", (code) => code === 0 ? resolve() : reject(new Error(`child exited ${code}`))); child.once("error", reject); } }); }
function runApprovalChild(mode: string, dir: string, value: string, now: Date): Promise<string> { return new Promise((resolve, reject) => { const child = spawn(process.execPath, ["--import", "tsx", approvalChildFixture, mode, dir, value, now.toISOString()], { stdio: ["ignore", "pipe", "pipe"] }); let stdout = ""; let stderr = ""; child.stdout.on("data", (data) => { stdout += String(data); }); child.stderr.on("data", (data) => { stderr += String(data); }); child.once("error", reject); child.once("exit", (code) => code === 0 && !stderr ? resolve(stdout.trim()) : reject(new Error(stderr || `child exited ${code}`))); }); }
