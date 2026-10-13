import { inspect } from "node:util";

import type { InvocationContextSnapshot } from "./invocation-context.js";
import type { GatewayResult } from "./results.js";

type BlockedInvocation = { state: "blocked"; result: GatewayResult };

export type ResolvedCommand<T> =
  | { state: "resolved"; value: T }
  | BlockedInvocation;

export type PolicyDecision<T> =
  | { state: "permitted"; value: T }
  | BlockedInvocation;

export type ApprovalDecision =
  | { state: "approved" }
  | BlockedInvocation;

export class ReadyInvocationPlan<T> {
  readonly state = "ready" as const;
  readonly invocation: InvocationContextSnapshot;
  readonly #resolved: T;

  constructor(invocation: InvocationContextSnapshot, resolved: T) {
    this.invocation = invocation;
    this.#resolved = resolved;
    Object.freeze(this);
  }

  get resolved(): T {
    return this.#resolved;
  }

  toJSON(): Record<string, unknown> {
    return { state: this.state, invocation: this.invocation.toJSON() };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export type InvocationPlan<T> = ReadyInvocationPlan<T> | BlockedInvocation;

export type InvocationPipelineStages<T> = {
  resolve: () => Promise<ResolvedCommand<T>>;
  applyPolicy: (resolved: T) => PolicyDecision<T>;
  validate: (permitted: T) => InvocationPlan<T>;
  approve: (invocation: InvocationContextSnapshot) => ApprovalDecision;
};

export async function buildInvocationPlan<T>(stages: InvocationPipelineStages<T>): Promise<InvocationPlan<T>> {
  const resolved = await stages.resolve();
  if (resolved.state === "blocked") return resolved;

  const policy = stages.applyPolicy(resolved.value);
  if (policy.state === "blocked") return policy;

  const validated = stages.validate(policy.value);
  if (validated.state === "blocked") return validated;

  const approval = stages.approve(validated.invocation);
  if (approval.state === "blocked") return approval;

  return validated;
}
