import { inspect } from "node:util";

import type { GatewayResult } from "./results.js";

export type InvocationRoute = "blocked" | "local" | "official" | "bridge";

export class InvocationOutcome {
  readonly route: InvocationRoute;
  readonly result: GatewayResult;
  readonly bridgeDurationMs: number | undefined;

  constructor(route: InvocationRoute, result: GatewayResult, bridgeDurationMs?: number) {
    this.route = route;
    this.result = result;
    this.bridgeDurationMs = bridgeDurationMs;
    Object.freeze(this);
  }

  toJSON(): Record<string, unknown> {
    return {
      route: this.route,
      status: this.result.status,
      bridgeDurationPresent: this.bridgeDurationMs !== undefined,
    };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export type InvocationExecutionStages = {
  refreshPolicy: () => void;
  degradedGate: () => GatewayResult | undefined;
  invokeLocal: () => GatewayResult | Promise<GatewayResult | undefined> | undefined;
  official: boolean;
  invokeOfficial: () => Promise<InvocationOutcome>;
  invokeBridge: () => Promise<InvocationOutcome>;
};

export async function executePreparedInvocation(stages: InvocationExecutionStages): Promise<InvocationOutcome> {
  stages.refreshPolicy();
  const blocked = stages.degradedGate();
  if (blocked) return new InvocationOutcome("blocked", blocked);

  const local = await stages.invokeLocal();
  if (local) return new InvocationOutcome("local", local);

  return stages.official ? stages.invokeOfficial() : stages.invokeBridge();
}
