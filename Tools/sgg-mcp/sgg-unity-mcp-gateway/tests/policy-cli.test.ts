import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { loadPolicyConfig } from "../src/config/config.js";
import { runApprovalCli } from "../src/approvals/approval-cli.js";
import { ApprovalStore, argsHash } from "../src/approvals/approval-store.js";
import { ApprovalBindingService, commandDescriptorRevision, createApprovalBindingEnvelope, effectivePolicyRevision } from "../src/approvals/approval-binding.js";
import { runPolicyCli } from "../src/policy/policy-cli.js";

const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("approval CLI", () => {
  it("rejects direct create and arbitrary args hashes", () => {
    const errors: string[] = [];
    expect(runApprovalCli(["create", "--dir", "/private", "--args-hash", "sha256:abc"], { stderr: (text) => errors.push(text) })).toBe(1);
    expect(errors.join("")).toContain("Unknown approval command");
  });

  it("lists and promotes schema-v2 requests without internal binding fields", () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-approval-cli-"));
    tempDirs.push(dir);
    const store = new ApprovalStore(dir);
    const descriptor = { id: "asset.import", title: "Import", description: "Import", package: "builtin", risk: "write" as const, idempotency: "idempotent" as const, timeout_ms: 1000, schema: { type: "object" } };
    const rawArgs = { asset_path: "Assets/Secret.prefab", token: "hidden" };
    const metadata = {
      project_id: "game",
      canonical_project_root_hash: null,
      instance_id: "instance-a",
      command: "asset.import",
      command_revision: commandDescriptorRevision(descriptor),
      commands_hash: "catalog-a",
      risk: "write" as const,
      idempotency: "idempotent" as const,
      mode: "wait" as const,
      args_hash: argsHash(rawArgs),
      request_id: "req-1",
      effective_policy_revision: effectivePolicyRevision({ action: "ask", reason: "risk_default" }),
      policy_action: "ask" as const,
      policy_reason: "risk_default",
      policy_pattern: null,
    };
    const envelope = createApprovalBindingEnvelope({ project_selector: "game", project_id: "game", canonical_project_root: null, instance_id: "instance-a", command: "asset.import", args: rawArgs, mode: "wait", request_id: "req-1", commands_hash: "catalog-a", risk: "write", idempotency: "idempotent", descriptor, policy_action: "ask", policy_reason: "risk_default", policy_pattern: null });
    const pending = store.request(metadata, envelope, new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }), { ttl_ms: 60_000 });
    const pendingOutput: string[] = [];
    expect(runApprovalCli(["pending", "--dir", dir], { stdout: (text) => pendingOutput.push(text) })).toBe(0);
    expect(JSON.parse(pendingOutput.join("")).pending_requests).toMatchObject([{ id: pending.id, status: "pending" }]);
    expect(pendingOutput.join("")).not.toMatch(/binding|key_id|request_hmac|canonical_project_root_hash|instance_id|commands_hash|command_revision|Secret|hidden/);

    const approveOutput: string[] = [];
    expect(runApprovalCli(["approve", "--dir", dir, "--pending-id", pending.id], { stdout: (text) => approveOutput.push(text) })).toBe(0);
    expect(approveOutput.join("")).not.toMatch(/binding|key_id|request_hmac|canonical_project_root_hash|instance_id|commands_hash|command_revision/);
    const listOutput: string[] = [];
    expect(runApprovalCli(["list", "--dir", dir], { stdout: (text) => listOutput.push(text) })).toBe(0);
    expect(JSON.parse(listOutput.join("")).approvals).toMatchObject([{ project: "game", command: "asset.import", status: "active" }]);
    expect(listOutput.join("")).not.toMatch(/binding|key_id|request_hmac|canonical_project_root_hash|instance_id|commands_hash|command_revision|Secret|hidden/);
  });
});

describe("policy CLI", () => {
  it("applies a profile to a project policy file", () => {
    const policyPath = tempPolicyPath();
    const templatePath = path.join(process.cwd(), "config", "policy.example.json");
    const output: string[] = [];

    const exitCode = runPolicyCli([
      "apply-profile",
      "--policy", policyPath,
      "--from-policy", templatePath,
      "--project", "sample-game",
      "--profile", "safe-writes",
    ], { stdout: (text) => output.push(text) });

    expect(exitCode).toBe(0);
    const policy = loadPolicyConfig(policyPath);
    expect(policy.projects["sample-game"]?.allow).toContain("assets.refresh");
    expect(policy.projects["sample-game"]?.ask).toContain("gameobject.*");
    expect(policy.projects["sample-game"]?.ask).toContain("editor.menu.execute");
    expect(policy.projects["sample-game"]?.ask).toContain("editor.quit");
    expect(JSON.parse(output.join(""))).toMatchObject({ project: "sample-game", profile: "safe-writes", dry_run: false });
  });

  it("applies test-validation with bounded allow rules and ask-gated test execution", () => {
    const policyPath = tempPolicyPath();
    const output: string[] = [];

    const exitCode = runPolicyCli([
      "apply-profile",
      "--policy", policyPath,
      "--project", "game",
      "--profile", "test-validation",
    ], { stdout: (text) => output.push(text) });

    expect(exitCode).toBe(0);
    const policy = loadPolicyConfig(policyPath);
    expect(policy.projects.game?.allow).toEqual(expect.arrayContaining([
      "compile.status",
      "compile.wait_ready",
      "validation.preflight",
      "logs.*",
      "editor.status",
      "build.settings",
      "build.profile.list",
      "test.framework.status",
      "test.list",
      "test.run.status",
      "job.artifact.read",
      "artifact.list",
      "artifact.metadata",
    ]));
    expect(policy.projects.game?.ask).toEqual(expect.arrayContaining(["assets.refresh", "compile.refresh_and_wait", "validation.after_edit", "test.run", "editor.quit"]));
    expect(policy.projects.game?.ask).toHaveLength(6);
    expect(policy.projects.game?.allow).not.toContain("test.run");
    expect(policy.projects.game?.allow).not.toContain("validation.after_edit");
    expect(policy.projects.game?.allow).not.toContain("build.run");
    expect(policy.projects.game?.allow).not.toContain("*");
    expect(JSON.parse(output.join(""))).toMatchObject({
      project: "game",
      profile: "test-validation",
      features: ["build_test_automation"],
      dry_run: false,
    });
  });

  it("previews changes without writing", () => {
    const policyPath = tempPolicyPath();
    const output: string[] = [];

    const exitCode = runPolicyCli([
      "set-rule",
      "--policy", policyPath,
      "--project", "game",
      "--action", "ask",
      "--command", "package.install",
      "--dry-run",
    ], { stdout: (text) => output.push(text) });

    expect(exitCode).toBe(0);
    expect(fs.existsSync(policyPath)).toBe(false);
    expect(JSON.parse(output.join(""))).toMatchObject({ dry_run: true, project: "game", action: "ask", command: "package.install" });
  });

  it("requires explicit risk confirmation for high-risk allow rules", () => {
    const policyPath = tempPolicyPath();
    const errors: string[] = [];

    const exitCode = runPolicyCli([
      "set-rule",
      "--policy", policyPath,
      "--project", "game",
      "--action", "allow",
      "--command", "editor.execute_csharp",
    ], { stderr: (text) => errors.push(text) });

    expect(exitCode).toBe(1);
    expect(errors.join("")).toContain("--confirm-risk");
    expect(fs.existsSync(policyPath)).toBe(false);
  });

  it("requires explicit risk confirmation for allow-all profile", () => {
    const policyPath = tempPolicyPath();
    const errors: string[] = [];

    const exitCode = runPolicyCli([
      "apply-profile",
      "--policy", policyPath,
      "--project", "game",
      "--profile", "allow-all",
    ], { stderr: (text) => errors.push(text) });

    expect(exitCode).toBe(1);
    expect(errors.join("")).toContain("--confirm-risk");
    expect(fs.existsSync(policyPath)).toBe(false);
  });

  it("applies allow-all only with explicit risk confirmation", () => {
    const policyPath = tempPolicyPath();
    const output: string[] = [];

    const exitCode = runPolicyCli([
      "apply-profile",
      "--policy", policyPath,
      "--project", "game",
      "--profile", "allow-all",
      "--confirm-risk",
    ], { stdout: (text) => output.push(text) });

    expect(exitCode).toBe(0);
    const policy = loadPolicyConfig(policyPath);
    expect(policy.projects.game).toEqual({ allow: ["*"], ask: [], deny: [] });
    expect(JSON.parse(output.join(""))).toMatchObject({ project: "game", profile: "allow-all", dry_run: false });
  });
});

function tempPolicyPath(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-policy-cli-"));
  tempDirs.push(dir);
  return path.join(dir, "policy.json");
}
