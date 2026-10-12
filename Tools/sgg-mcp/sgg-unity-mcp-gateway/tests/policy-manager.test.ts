import fs, { type BigIntStats } from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import type { PolicyConfig } from "../src/config/config.js";
import { inspectCurrentPolicyFile, inspectCurrentPolicyFileResult, PolicyLoadError, PolicyManager } from "../src/policy/policy-manager.js";

const dirs: string[] = [];

afterEach(() => {
  for (const dir of dirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("PolicyManager", () => {
  it("represents unconfigured state without a policy", () => {
    const manager = new PolicyManager();
    expect(manager.policy).toBeUndefined();
    expect(manager.snapshot()).toEqual({
      state: "unconfigured", configured: false, degraded: false, effective_policy_revision: null, loaded_at: null,
      last_attempt_at: null, last_error_kind: null, last_error_code: null, degraded_since: null,
    });
  });

  it("loads a configured policy and exposes a canonical revision but no source or raw hash", () => {
    const file = policyFile(policy("deny"));
    const manager = new PolicyManager(file, { now: () => new Date("2026-07-11T10:00:00.000Z") });
    const snapshot = manager.snapshot();
    expect(manager.policy?.defaults.execute).toBe("deny");
    expect(snapshot).toMatchObject({ state: "active", configured: true, loaded_at: "2026-07-11T10:00:00.000Z" });
    expect(snapshot.effective_policy_revision).toMatch(/^sha256:[0-9a-f]{64}$/);
    expect(JSON.stringify(snapshot)).not.toContain(file);
    expect(JSON.stringify(snapshot)).not.toContain("projects");
  });

  it("does not reread or reparse an unchanged successful file identity", () => {
    const file = policyFile(policy("deny"));
    let reads = 0;
    const fileSystem = {
      statSync: (source: string, options: { bigint: true }) => fs.statSync(source, options),
      readFileSync: (source: string) => { reads += 1; return fs.readFileSync(source); },
    };
    const manager = new PolicyManager(file, { fileSystem });
    expect(manager.reload()).toBe(false);
    expect(manager.reload()).toBe(false);
    expect(reads).toBe(1);
  });

  it("uses canonical policy content for the effective revision", () => {
    const first = policyFile(policy("allow"));
    const second = policyFile(policy("allow"));
    fs.writeFileSync(second, JSON.stringify({ projects: {}, defaults: { network: "deny", destructive: "ask", execute: "allow", write: "ask", read: "allow" }, schema_version: 1 }));
    expect(new PolicyManager(first).effectivePolicyRevision).toBe(new PolicyManager(second).effectivePolicyRevision);
  });

  it("keeps policy B as last-known-good after A to B to invalid and recovers on valid replacement", () => {
    const file = policyFile(policy("deny"));
    const manager = new PolicyManager(file);
    replace(file, policy("allow"));
    expect(manager.reload()).toBe(true);
    expect(manager.policy?.defaults.execute).toBe("allow");
    const revisionB = manager.effectivePolicyRevision;

    replaceRaw(file, "{ private-policy-content");
    expect(manager.reload()).toBe(false);
    expect(manager.policy?.defaults.execute).toBe("allow");
    expect(manager.effectivePolicyRevision).toBe(revisionB);
    expect(manager.snapshot()).toMatchObject({ state: "degraded", last_error_code: "policy_invalid", last_error_kind: "invalid_json" });

    replace(file, policy("deny"));
    expect(manager.reload()).toBe(true);
    expect(manager.degraded).toBe(false);
    expect(manager.policy?.defaults.execute).toBe("deny");
  });

  it.each([
    ["missing", { statSync: () => { throw errno("ENOENT", "secret/path"); }, readFileSync: () => Buffer.alloc(0) }, "policy_reload_failed", "missing"],
    ["unreadable", { statSync: () => { throw errno("EACCES", "secret/path"); }, readFileSync: () => Buffer.alloc(0) }, "policy_reload_failed", "unreadable"],
    ["invalid JSON", stableFile(Buffer.from("secret invalid json")), "policy_invalid", "invalid_json"],
    ["invalid schema", stableFile(Buffer.from("{\"private\":\"secret\"}")), "policy_invalid", "invalid_schema"],
    ["oversized", oversizedFile(), "policy_reload_failed", "too_large"],
  ] as const)("throws a typed safe startup error for %s", (_name, fileSystem, code, kind) => {
    let thrown: unknown;
    try {
      new PolicyManager("/private/policy-secret.json", { fileSystem });
    } catch (error) {
      thrown = error;
    }
    expect(thrown).toBeInstanceOf(PolicyLoadError);
    expect(thrown).toMatchObject({ code, kind });
    expect(String(thrown)).not.toContain("private");
    expect(String(thrown)).not.toContain("secret");
    expect(JSON.stringify(thrown)).not.toContain("policy-secret.json");
  });

  it("retries one unstable stat-read-stat attempt and accepts the atomic replacement", () => {
    const rawA = Buffer.from(JSON.stringify(policy("deny")));
    const rawB = Buffer.from(JSON.stringify(policy("allow")));
    const first = fakeStats(1n, rawA.byteLength);
    const second = fakeStats(2n, rawB.byteLength);
    const stats = [first, second, second, second];
    let reads = 0;
    const manager = new PolicyManager("policy.json", {
      fileSystem: {
        statSync: () => stats.shift() ?? second,
        readFileSync: () => { reads += 1; return reads === 1 ? rawA : rawB; },
      },
    });
    expect(reads).toBe(2);
    expect(manager.policy?.defaults.execute).toBe("allow");
    expect(manager.degraded).toBe(false);
  });

  it("fails safely after the single retry when reads remain unstable", () => {
    const raw = Buffer.from(JSON.stringify(policy("allow")));
    let inode = 0n;
    const fileSystem = { statSync: () => fakeStats(++inode, raw.byteLength), readFileSync: () => raw };
    expect(() => new PolicyManager("policy.json", { fileSystem })).toThrowError(expect.objectContaining({ code: "policy_reload_failed", kind: "unstable" }));
    expect(inode).toBe(4n);
  });

  it("keeps the original degraded_since across repeated failures", () => {
    const file = policyFile(policy("allow"));
    const times = [new Date("2026-07-11T10:00:00.000Z"), new Date("2026-07-11T10:01:00.000Z"), new Date("2026-07-11T10:02:00.000Z")];
    const manager = new PolicyManager(file, { now: () => times.shift() ?? new Date("2026-07-11T10:03:00.000Z") });
    fs.rmSync(file);
    manager.reload();
    const degradedSince = manager.snapshot().degraded_since;
    manager.reload();
    expect(manager.snapshot().degraded_since).toBe(degradedSince);
  });

  it.each([
    ["missing", { statSync: () => { throw errno("ENOENT", "private missing path"); }, readFileSync: () => Buffer.alloc(0) }, "missing", "policy_reload_failed", "missing"],
    ["unreadable", { statSync: () => { throw errno("EACCES", "private denied path"); }, readFileSync: () => Buffer.alloc(0) }, "unreadable", "policy_reload_failed", "unreadable"],
    ["invalid JSON", stableFile(Buffer.from("private invalid content")), "invalid", "policy_invalid", "invalid_json"],
    ["invalid schema", stableFile(Buffer.from("{\"private\":\"content\"}")), "invalid", "policy_invalid", "invalid_schema"],
  ] as const)("classifies offline current-file inspection as %s without pretending it is an LKG", (_name, fileSystem, status, code, kind) => {
    const inspection = inspectCurrentPolicyFile("/private/policy.json", { fileSystem, now: () => new Date("2026-07-11T11:00:00.000Z") });
    expect(inspection).toMatchObject({ inspection: "current_file", current_file_status: status, state: "error", configured: true, degraded: false, effective_policy_revision: null, last_error_code: code, last_error_kind: kind });
    expect(JSON.stringify(inspection)).not.toMatch(/private|content|policy\.json|Zod|EACCES|ENOENT/);
  });

  it("returns the validated replacement policy from the same bounded stable inspection", () => {
    const rawA = Buffer.from(JSON.stringify(policy("deny")));
    const rawB = Buffer.from(JSON.stringify(policy("allow")));
    const first = fakeStats(1n, rawA.byteLength);
    const second = fakeStats(2n, rawB.byteLength);
    const stats = [first, second, second, second];
    let reads = 0;

    const result = inspectCurrentPolicyFileResult("private-policy.json", {
      fileSystem: {
        statSync: () => stats.shift() ?? second,
        readFileSync: () => { reads += 1; return reads === 1 ? rawA : rawB; },
      },
      now: () => new Date("2026-07-11T11:00:00.000Z"),
    });

    expect(reads).toBe(2);
    expect(result.inspection).toMatchObject({ current_file_status: "valid", state: "active", degraded: false });
    expect(result.policy?.defaults.execute).toBe("allow");
    expect(JSON.stringify(result.inspection)).not.toContain("private-policy.json");
  });
});

function policy(execute: "allow" | "deny"): PolicyConfig {
  return { schema_version: 1, defaults: { read: "allow", write: "ask", execute, destructive: "ask", network: "deny" }, projects: {} };
}

function policyFile(value: PolicyConfig): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-policy-manager-"));
  dirs.push(dir);
  const file = path.join(dir, "policy.json");
  fs.writeFileSync(file, JSON.stringify(value));
  return file;
}

function replace(file: string, value: PolicyConfig): void { replaceRaw(file, JSON.stringify(value)); }
function replaceRaw(file: string, value: string): void {
  const temporary = `${file}.tmp`;
  fs.writeFileSync(temporary, value);
  fs.renameSync(temporary, file);
}
function errno(code: string, message: string): NodeJS.ErrnoException { return Object.assign(new Error(message), { code }); }
function stableFile(raw: Buffer) {
  const stats = fakeStats(1n, raw.byteLength);
  return { statSync: () => stats, readFileSync: () => raw };
}
function oversizedFile() {
  const stats = fakeStats(1n, 1024 * 1024 + 1);
  return { statSync: () => stats, readFileSync: () => Buffer.alloc(0) };
}
function fakeStats(ino: bigint, size: number): BigIntStats {
  return { dev: 1n, ino, size: BigInt(size), mtimeNs: ino, ctimeNs: ino } as BigIntStats;
}
