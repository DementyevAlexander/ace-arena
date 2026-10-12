import { ApprovalBindingService, commandDescriptorRevision, createApprovalBindingEnvelope, effectivePolicyRevision } from "../../src/approvals/approval-binding.js";
import { ApprovalStore, argsHash, canonicalProjectRootHash, type ApprovalSafeMetadata } from "../../src/approvals/approval-store.js";
import type { CommandDescriptor } from "../../src/bridge/bridge-client.js";

const [mode, dir, value, timestamp] = process.argv.slice(2);
if (!mode || !dir || !timestamp) throw new Error("mode, dir, and timestamp are required");
const now = new Date(timestamp);
const store = new ApprovalStore(dir);

try {
  if (mode === "promote") {
    if (!value) throw new Error("pending id is required");
    store.approvePending({ pending_id: value, ttl_ms: 60_000, now });
    process.stdout.write("promoted\n");
  } else if (mode === "consume") {
    const invocation = approvalInvocation();
    const service = new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) });
    process.stdout.write(`${store.consume(invocation.metadata, invocation.envelope, service, now).status}\n`);
  } else throw new Error("unknown mode");
} catch {
  process.stdout.write("unavailable\n");
}

function approvalInvocation() {
  const descriptor: CommandDescriptor = { id: "asset.import", title: "Import", description: "Import asset", package: "builtin", risk: "write", idempotency: "idempotent", timeout_ms: 1000, schema: { type: "object" } };
  const args = { asset_path: "Assets/Foo.prefab" };
  const metadata = {
    project_id: "game", canonical_project_root_hash: canonicalProjectRootHash("c:/private/game"), instance_id: "instance-a", command: descriptor.id,
    command_revision: commandDescriptorRevision(descriptor), commands_hash: "catalog-a", risk: descriptor.risk, idempotency: descriptor.idempotency,
    mode: "wait", request_id: "request-a", args_hash: argsHash(args), effective_policy_revision: effectivePolicyRevision({ action: "ask", reason: "project_rule", pattern: "asset.import" }),
    policy_action: "ask", policy_reason: "project_rule", policy_pattern: "asset.import",
  } satisfies ApprovalSafeMetadata;
  return { metadata, envelope: createApprovalBindingEnvelope({ project_selector: "original selector", project_id: "game", canonical_project_root: "c:/private/game", instance_id: "instance-a", command: descriptor.id, args, mode: "wait", request_id: "request-a", commands_hash: "catalog-a", risk: descriptor.risk, idempotency: descriptor.idempotency, descriptor, policy_action: "ask", policy_reason: "project_rule", policy_pattern: "asset.import" }) };
}
