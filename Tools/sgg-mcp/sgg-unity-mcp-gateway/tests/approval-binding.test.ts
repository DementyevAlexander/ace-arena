import { createHmac } from "node:crypto";

import { describe, expect, it } from "vitest";

import {
  APPROVAL_BINDING_SCHEMA,
  ApprovalBindingService,
  approvalBindingKeyId,
  commandDescriptorRevision,
  createApprovalBinding,
  createApprovalBindingEnvelope,
  normalizeEffectiveCommandDescriptor,
  verifyApprovalBinding,
  type ApprovalBindingEnvelope,
} from "../src/approvals/approval-binding.js";
import type { CommandDescriptor } from "../src/bridge/bridge-client.js";
import { canonicalJsonStringify } from "../src/security/canonical-json.js";

const key = Buffer.alloc(32, 0x41);

describe("approval binding", () => {
  it("binds the unredacted full canonical request in a versioned record", () => {
    const envelope = makeEnvelope();
    expect(envelope).toMatchObject({
      schema: APPROVAL_BINDING_SCHEMA,
      type: "unity_command_request",
      tool: "sgg_unity_call",
      project_selector: "Game Display Name",
      project_id: "game",
      canonical_project_root: "c:/work/game",
      instance_id: "instance-a",
      command: "asset.import",
      args: { token: "secret-a", nested: { b: 2, a: 1 } },
      mode: "wait",
      request_id: "request-a",
      commands_hash: "commands-a",
      risk: "write",
      idempotency: "idempotent",
    });
    expect(createApprovalBinding(envelope, key)).toEqual({
      schema_version: 2,
      algorithm: "hmac-sha256",
      key_id: approvalBindingKeyId(key),
      request_hmac: expect.stringMatching(/^[0-9a-f]{64}$/),
    });
  });

  it("allows explicit null routing identity for no-heartbeat commands", () => {
    const envelope = makeEnvelope({ project_id: null, canonical_project_root: null, instance_id: null, commands_hash: null });
    const binding = createApprovalBinding(envelope, key);
    expect(verifyApprovalBinding(envelope, binding, key)).toBe(true);
  });

  it("is canonical but remains sensitive to unredacted secrets", () => {
    const original = makeEnvelope();
    const reordered = makeEnvelope({ args: { nested: { a: 1, b: 2 }, token: "secret-a" } });
    const changed = makeEnvelope({ args: { token: "secret-b", nested: { b: 2, a: 1 } } });
    expect(createApprovalBinding(original, key).request_hmac).toBe(createApprovalBinding(reordered, key).request_hmac);
    expect(createApprovalBinding(original, key).request_hmac).not.toBe(createApprovalBinding(changed, key).request_hmac);
  });

  it("uses explicit domain separation", () => {
    const envelope = makeEnvelope();
    const undomained = createHmac("sha256", key).update(canonicalJsonStringify(envelope)).digest("hex");
    expect(createApprovalBinding(envelope, key).request_hmac).not.toBe(undomained);
  });

  it("rejects mutation of every envelope field", () => {
    const original = makeEnvelope();
    const binding = createApprovalBinding(original, key);
    const mutations: ApprovalBindingEnvelope[] = [
      { ...original, schema: "other" as typeof original.schema },
      { ...original, type: "other" as typeof original.type },
      { ...original, tool: "other" as typeof original.tool },
      { ...original, project_selector: "other" }, { ...original, project_id: null },
      { ...original, canonical_project_root: null }, { ...original, instance_id: null },
      { ...original, command: "other" }, { ...original, args: { token: "other" } },
      { ...original, mode: "job" }, { ...original, request_id: null },
      { ...original, commands_hash: null }, { ...original, risk: "destructive" },
      { ...original, idempotency: "unsafe" }, { ...original, descriptor_revision: "sha256:other" },
      { ...original, effective_policy_revision: "sha256:other" }, { ...original, policy_action: "allow" },
      { ...original, policy_reason: "other" }, { ...original, policy_pattern: null },
    ];
    for (const mutation of mutations) {
      expect(verifyApprovalBinding(mutation, binding, key)).toBe(false);
    }
  });

  it("normalizes an explicit effective descriptor allowlist with null optionals", () => {
    const normalized = normalizeEffectiveCommandDescriptor(descriptor());
    expect(normalized).toMatchObject({ tags: null, capability: null, supports_job: null, wrapper_wait: null, source: null, trust: null });
    expect(Object.keys(normalized).sort()).toEqual([
      "capability", "description", "feature_flag", "id", "idempotency", "manifest_path", "output_limit", "package", "package_id",
      "package_version", "requires_editor_status", "result_shape", "risk", "schema", "source", "supports_cancel", "supports_job", "tags",
      "timeout_ms", "title", "trust", "wrapper_wait",
    ].sort());
  });

  it("changes descriptor revision for every effective field and ignores unknown extras", () => {
    const base = descriptor();
    const revision = commandDescriptorRevision(base);
    const mutations: CommandDescriptor[] = [
      { ...base, id: "other" }, { ...base, title: "other" }, { ...base, description: "other" }, { ...base, package: "other" },
      { ...base, risk: "destructive" }, { ...base, idempotency: "unsafe" }, { ...base, timeout_ms: 2001 },
      { ...base, schema: { type: "object", properties: { x: { type: "string" } } } }, { ...base, tags: ["x"] }, { ...base, capability: "x" },
      { ...base, requires_editor_status: ["editing"] }, { ...base, result_shape: "x" }, { ...base, output_limit: { max: 1 } },
      { ...base, supports_job: true }, { ...base, supports_cancel: true }, { ...base, feature_flag: "x" }, { ...base, source: "builtin" },
      { ...base, wrapper_wait: { mode: "wait", wait_for_replacement_ms: { min_ms: 1, max_ms: 45_000, deadline_scope: "pre_dispatch_only" } } },
      { ...base, package_id: "x" }, { ...base, package_version: "1" }, { ...base, manifest_path: "x" }, { ...base, trust: "builtin" },
    ];
    for (const mutation of mutations) {
      expect(commandDescriptorRevision(mutation)).not.toBe(revision);
    }
    expect(commandDescriptorRevision({ ...base, arbitrary_extra: "ignored" } as CommandDescriptor)).toBe(revision);
    expect(commandDescriptorRevision({ ...base, schema: { required: ["asset_path"], type: "object" } })).toBe(revision);
  });

  it("returns false for malformed records while still accepting a valid record", () => {
    const envelope = makeEnvelope();
    const valid = createApprovalBinding(envelope, key);
    expect(verifyApprovalBinding(envelope, valid, key)).toBe(true);
    for (const malformed of [
      null, "bad", {}, { ...valid, schema_version: 1 }, { ...valid, algorithm: "sha1" }, { ...valid, key_id: "bad" },
      { ...valid, request_hmac: "00" }, { ...valid, request_hmac: `${valid.request_hmac.slice(0, -1)}g` }, { ...valid, extra: true },
       { ...valid, [Symbol("extra")]: true }, Object.assign(Object.create({ schema_version: 2 }), { algorithm: valid.algorithm, key_id: valid.key_id, request_hmac: valid.request_hmac }),
    ]) {
      expect(verifyApprovalBinding(envelope, malformed, key)).toBe(false);
    }
    expect(verifyApprovalBinding(envelope, valid, Buffer.alloc(31))).toBe(false);
    expect(verifyApprovalBinding(envelope, valid, Buffer.alloc(32, 0x42))).toBe(false);
  });

  it("loads service key lazily and never returns raw payload or key material", () => {
    let calls = 0;
    const service = new ApprovalBindingService({ getKey: () => { calls += 1; return key; } });
    expect(calls).toBe(0);
    const binding = service.create(makeEnvelope({ args: { password: "never-output-this" } }));
    expect(calls).toBe(1);
    expect(JSON.stringify(binding)).not.toContain("never-output-this");
    expect(JSON.stringify(binding)).not.toContain(key.toString("hex"));
  });
});

function makeEnvelope(overrides: Partial<ApprovalBindingEnvelope> = {}): ApprovalBindingEnvelope {
  return { ...createApprovalBindingEnvelope({
    project_selector: "Game Display Name", project_id: "game", canonical_project_root: "c:/work/game", instance_id: "instance-a",
    command: "asset.import", args: { token: "secret-a", nested: { b: 2, a: 1 } }, mode: "wait", request_id: "request-a",
    commands_hash: "commands-a", risk: "write", idempotency: "idempotent", descriptor: descriptor(),
    policy_action: "ask", policy_reason: "project_rule", policy_pattern: "asset.import",
  }), ...overrides };
}

function descriptor(): CommandDescriptor {
  return { id: "asset.import", title: "Import", description: "Import one asset", package: "builtin", risk: "write", idempotency: "idempotent", timeout_ms: 2000, schema: { type: "object", required: ["asset_path"] } };
}
