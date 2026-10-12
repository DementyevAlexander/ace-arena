import { EventEmitter } from "node:events";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalStore } from "../src/approvals/approval-store.js";
import { FileAuditSink } from "../src/observability/audit.js";
import { OperationalLogger, type OperationalLogWriter } from "../src/observability/operational-logger.js";
import { PolicyManager } from "../src/policy/policy-manager.js";
import { ProjectRegistry } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];

class CaptureWriter extends EventEmitter implements OperationalLogWriter {
  readonly lines: string[] = [];
  write(line: string): boolean { this.lines.push(line); return true; }
}

afterEach(() => {
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("operational lifecycle instrumentation", () => {
  it("emits bounded registry, policy, approval, and audit transitions without secrets", () => {
    const parent = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-op-integration-"));
    tempDirs.push(parent);
    const writer = new CaptureWriter();
    const logger = new OperationalLogger({ level: "debug", writer, now: () => new Date("2026-07-12T10:20:30.456Z") });

    const instancesDir = path.join(parent, "instances-private-secret");
    const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, logger });
    expect(registry.listProjects()).toEqual([]);
    fs.mkdirSync(instancesDir, { mode: 0o700 });
    expect(registry.listProjects()).toEqual([]);

    const policyPath = path.join(parent, "policy-private-secret.json");
    const validPolicy = JSON.stringify({
      schema_version: 1,
      defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
      trusted_packages: [],
      untrusted_package_default: "ask",
      projects: {},
    });
    fs.writeFileSync(policyPath, validPolicy);
    const policy = new PolicyManager(policyPath, { logger });
    fs.writeFileSync(policyPath, "{");
    expect(policy.reload()).toBe(false);
    fs.writeFileSync(policyPath, validPolicy);
    expect(policy.reload()).toBe(true);

    const approvalRoot = path.join(parent, "approval-private-secret");
    fs.writeFileSync(approvalRoot, "not-a-directory");
    expect(new ApprovalStore(approvalRoot, { logger }).status().state).toBe("unavailable");

    const auditRoot = path.join(parent, "audit-private-secret");
    fs.writeFileSync(auditRoot, "not-a-directory");
    const audit = new FileAuditSink(auditRoot, { logger });
    expect(audit.snapshotStatus()).toEqual({ schema_version: 1, state: "ok", files: 0, total_bytes: 0, rotation_period: "utc_day" });
    expect(writer.lines.map((line) => JSON.parse(line).event_code)).not.toContain("SGG-OP-AUDIT-STATE-001");
    expect(audit.status().state).toBe("unavailable");
    expect(audit.snapshotStatus().state).toBe("unavailable");
    fs.rmSync(auditRoot);
    fs.mkdirSync(auditRoot, { mode: 0o700 });
    expect(audit.status().state).toBe("ok");
    fs.rmSync(auditRoot, { recursive: true });
    fs.writeFileSync(auditRoot, "not-a-directory-again");
    expect(audit.status().state).toBe("unavailable");

    const output = writer.lines.join("");
    const codes = writer.lines.map((line) => JSON.parse(line).event_code);
    expect(codes).toEqual(expect.arrayContaining([
      "SGG-OP-REGISTRY-STATE-001",
      "SGG-OP-REGISTRY-RECOVERED-001",
      "SGG-OP-POLICY-DEGRADED-001",
      "SGG-OP-POLICY-RECOVERED-001",
      "SGG-OP-APPROVAL-STATE-001",
    ]));
    expect(codes.filter((code) => code === "SGG-OP-AUDIT-STATE-001")).toHaveLength(2);
    expect(codes.filter((code) => code === "SGG-OP-AUDIT-RECOVERED-001")).toHaveLength(1);
    expect(output).not.toContain("private-secret");
    expect(output).not.toContain("not-a-directory");
    expect(output).not.toContain(policyPath);
  });
});
