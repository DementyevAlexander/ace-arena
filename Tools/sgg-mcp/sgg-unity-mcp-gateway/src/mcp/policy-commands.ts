import type { CommandDescriptor } from "../bridge/bridge-client.js";
import { evaluatePolicy } from "../policy/policy.js";
import { policyManagerSnapshot } from "../policy/policy-manager.js";
import type { DispatcherContext } from "./dispatcher.js";
import { isCommandFeatureEnabled } from "./feature-flags.js";
import type { GatewayError } from "./results.js";

const FEATURE_FLAG = "sgg_mcp_policy";
const COMMAND_ID_PATTERN = "^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$";

export const POLICY_COMMANDS: CommandDescriptor[] = [
  {
    id: "policy.explain",
    title: "Policy explain",
    description: "Explain feature, visibility, and policy outcomes for a command without executing it or consuming approvals.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        command: { type: "string", minLength: 3, maxLength: 160, pattern: COMMAND_ID_PATTERN },
      },
      required: ["command"],
      additionalProperties: false,
    },
    tags: ["policy"],
    capability: "policy",
    result_shape: "object_details",
    output_limit: { max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type PolicyCommandResult = { status: "ok"; data: unknown } | { status: "error"; error: GatewayError };

export function findPolicyCommand(commandId: string): CommandDescriptor | undefined {
  return POLICY_COMMANDS.find((command) => command.id === commandId);
}

export function runPolicyExplain(args: Record<string, unknown>, context: DispatcherContext, project: string, target: CommandDescriptor | undefined): PolicyCommandResult {
  const commandId = typeof args.command === "string" ? args.command : "";
  if (!target) {
    return error("command_not_found", `Command '${commandId}' is not available for project '${project}'`);
  }

  if (context.policyManager?.degraded === true && target.risk !== "read") {
    const lastKnownGoodDecision = evaluatePolicy(context.policy, project, target);
    return {
      status: "ok",
      data: {
        ...explain(commandId, target, "policy_degraded", isCommandFeatureEnabled(target, context.features), false, context, project, false),
        authorization_current: false,
        last_known_good_policy_decision: lastKnownGoodDecision,
      },
    };
  }

  if (!isCommandFeatureEnabled(target, context.features)) {
    return { status: "ok", data: explain(commandId, target, "feature_disabled", false, false, context, project) };
  }

  const decision = evaluatePolicy(context.policy, project, target);
  if (decision.action === "deny") {
    return { status: "ok", data: explain(commandId, target, "permission_denied", true, false, context, project) };
  }

  if (decision.action === "ask") {
    return { status: "ok", data: explain(commandId, target, "confirmation_required", true, true, context, project) };
  }

  return { status: "ok", data: explain(commandId, target, "allowed", true, false, context, project) };
}

function explain(commandId: string, target: CommandDescriptor, outcome: string, featureEnabled: boolean, requiresConfirmation: boolean, context: DispatcherContext, project: string, currentAuthorization = true): Record<string, unknown> {
  const decision = featureEnabled && currentAuthorization ? evaluatePolicy(context.policy, project, target) : undefined;
  return {
    command: commandId,
    outcome,
    visibility: outcome === "allowed" || outcome === "confirmation_required" ? "visible" : "hidden",
    invocation: outcome,
    feature_enabled: featureEnabled,
    ...(target.feature_flag ? { feature_flag: target.feature_flag } : {}),
    requires_confirmation: requiresConfirmation,
    policy_decision: decision ?? null,
    policy_status: policyManagerSnapshot(context.policyManager, context.policy !== undefined),
    descriptor: target,
    executed: false,
    approval_consumed: false,
    mutated: false,
  };
}

function error(code: GatewayError["code"], message: string): PolicyCommandResult {
  return { status: "error", error: { code, message } };
}
