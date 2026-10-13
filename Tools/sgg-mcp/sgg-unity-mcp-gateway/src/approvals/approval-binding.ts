import { createHash, createHmac, timingSafeEqual } from "node:crypto";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import { commandDescriptorRevision, normalizeEffectiveCommandDescriptor, type EffectiveCommandDescriptorV1 } from "../bridge/bridge-protocol.js";
import { canonicalJsonStringify } from "../security/canonical-json.js";

export const APPROVAL_BINDING_SCHEMA = "sgg.unity_mcp.approval_binding.v2" as const;
export const APPROVAL_BINDING_TYPE = "unity_command_request" as const;
export const APPROVAL_BINDING_TOOL = "sgg_unity_call" as const;
export const APPROVAL_BINDING_TOOLS = [APPROVAL_BINDING_TOOL, "sgg_unity_stale_registry_cleanup"] as const;
export const APPROVAL_BINDING_ALGORITHM = "hmac-sha256" as const;
const APPROVAL_BINDING_DOMAIN = Buffer.from("sgg.unity_mcp.approval_binding.v2\0", "utf8");
const EFFECTIVE_POLICY_REVISION_DOMAIN = Buffer.from("sgg.unity_mcp.effective_policy_revision.v1\0", "utf8");

export type ApprovalPolicyDecision = {
  action: "allow" | "ask" | "deny";
  reason: string;
  pattern?: string;
};

export type ApprovalBindingTool = (typeof APPROVAL_BINDING_TOOLS)[number];

export type ApprovalBindingEnvelope = {
  schema: typeof APPROVAL_BINDING_SCHEMA;
  type: typeof APPROVAL_BINDING_TYPE;
  tool: ApprovalBindingTool;
  project_selector: string;
  project_id: string | null;
  canonical_project_root: string | null;
  instance_id: string | null;
  command: string;
  args: Record<string, unknown>;
  mode: "wait" | "job";
  request_id: string | null;
  commands_hash: string | null;
  risk: CommandDescriptor["risk"];
  idempotency: CommandDescriptor["idempotency"];
  descriptor_revision: string;
  effective_policy_revision: string;
  policy_action: ApprovalPolicyDecision["action"];
  policy_reason: string;
  policy_pattern: string | null;
};

export type ApprovalBindingInput = Omit<ApprovalBindingEnvelope, "schema" | "type" | "tool" | "descriptor_revision" | "effective_policy_revision"> & {
  descriptor: CommandDescriptor;
  tool?: ApprovalBindingTool;
};

export type ApprovalBindingRecord = {
  schema_version: 2;
  algorithm: typeof APPROVAL_BINDING_ALGORITHM;
  key_id: string;
  request_hmac: string;
};

export type EffectiveCommandDescriptor = EffectiveCommandDescriptorV1;
export { commandDescriptorRevision, normalizeEffectiveCommandDescriptor } from "../bridge/bridge-protocol.js";

export function effectivePolicyRevision(decision: ApprovalPolicyDecision): string {
  const normalized = normalizePolicyDecision(decision);
  return `sha256:${createHash("sha256").update(EFFECTIVE_POLICY_REVISION_DOMAIN).update(canonicalJsonStringify(normalized)).digest("hex")}`;
}

export function createApprovalBindingEnvelope(input: ApprovalBindingInput): ApprovalBindingEnvelope {
  const policy = normalizePolicyDecision({ action: input.policy_action, reason: input.policy_reason, ...(input.policy_pattern === null ? {} : { pattern: input.policy_pattern }) });
  return {
    schema: APPROVAL_BINDING_SCHEMA,
    type: APPROVAL_BINDING_TYPE,
    tool: input.tool ?? APPROVAL_BINDING_TOOL,
    project_selector: input.project_selector,
    project_id: input.project_id,
    canonical_project_root: input.canonical_project_root,
    instance_id: input.instance_id,
    command: input.command,
    args: input.args,
    mode: input.mode,
    request_id: input.request_id,
    commands_hash: input.commands_hash,
    risk: input.risk,
    idempotency: input.idempotency,
    descriptor_revision: commandDescriptorRevision(input.descriptor),
    effective_policy_revision: effectivePolicyRevision({ action: policy.action, reason: policy.reason, ...(policy.pattern === null ? {} : { pattern: policy.pattern }) }),
    policy_action: policy.action,
    policy_reason: policy.reason,
    policy_pattern: policy.pattern,
  };
}

export function approvalBindingKeyId(key: Uint8Array): string {
  assertApprovalKey(key);
  return `sha256:${createHash("sha256").update(key).digest("hex")}`;
}

export function createApprovalBinding(envelope: ApprovalBindingEnvelope, key: Uint8Array): ApprovalBindingRecord {
  assertApprovalKey(key);
  return {
    schema_version: 2,
    algorithm: APPROVAL_BINDING_ALGORITHM,
    key_id: approvalBindingKeyId(key),
    request_hmac: requestHmac(envelope, key).toString("hex"),
  };
}

export function verifyApprovalBinding(envelope: ApprovalBindingEnvelope, binding: unknown, key: Uint8Array): boolean {
  if (key.byteLength !== 32) {
    return false;
  }
  const expected = requestHmac(envelope, key);
  const parsed = parseBinding(binding);
  const supplied = parsed ? Buffer.from(parsed.request_hmac, "hex") : Buffer.alloc(expected.length);
  const macMatches = timingSafeEqual(expected, supplied);
  return macMatches && parsed !== undefined && parsed.key_id === approvalBindingKeyId(key);
}

export class ApprovalBindingService {
  constructor(readonly keyProvider: ApprovalHmacKeyProvider) {}

  create(envelope: ApprovalBindingEnvelope): ApprovalBindingRecord {
    return createApprovalBinding(envelope, this.keyProvider.getKey());
  }

  verify(envelope: ApprovalBindingEnvelope, binding: unknown): boolean {
    return verifyApprovalBinding(envelope, binding, this.keyProvider.getKey());
  }
}

function assertApprovalKey(key: Uint8Array): void {
  if (key.byteLength !== 32) {
    throw new TypeError("Approval HMAC key must be exactly 32 bytes");
  }
}

export interface ApprovalHmacKeyProvider {
  getKey(): Buffer;
}

function requestHmac(envelope: ApprovalBindingEnvelope, key: Uint8Array): Buffer {
  return createHmac("sha256", key).update(APPROVAL_BINDING_DOMAIN).update(canonicalJsonStringify(envelope), "utf8").digest();
}

function parseBinding(value: unknown): ApprovalBindingRecord | undefined {
  try {
    if (typeof value !== "object" || value === null || Array.isArray(value)) {
      return undefined;
    }
    const prototype = Object.getPrototypeOf(value) as unknown;
    if (prototype !== Object.prototype && prototype !== null) {
      return undefined;
    }
    const keys = Reflect.ownKeys(value);
    if (keys.length !== 4 || keys.some((key) => typeof key !== "string")) {
      return undefined;
    }
    const binding = value as Partial<ApprovalBindingRecord>;
    if (binding.schema_version !== 2 || binding.algorithm !== APPROVAL_BINDING_ALGORITHM) {
      return undefined;
    }
    if (typeof binding.key_id !== "string" || !/^sha256:[0-9a-f]{64}$/.test(binding.key_id)) {
      return undefined;
    }
    if (typeof binding.request_hmac !== "string" || !/^[0-9a-f]{64}$/.test(binding.request_hmac)) {
      return undefined;
    }
    if (!Object.hasOwn(binding, "schema_version") || !Object.hasOwn(binding, "algorithm") || !Object.hasOwn(binding, "key_id") || !Object.hasOwn(binding, "request_hmac")) {
      return undefined;
    }
    return binding as ApprovalBindingRecord;
  } catch {
    return undefined;
  }
}

function normalizePolicyDecision(decision: ApprovalPolicyDecision): { action: ApprovalPolicyDecision["action"]; reason: string; pattern: string | null } {
  return { action: decision.action, reason: decision.reason, pattern: decision.pattern ?? null };
}
