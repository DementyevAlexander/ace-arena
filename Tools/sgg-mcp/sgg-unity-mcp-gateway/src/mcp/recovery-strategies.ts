import { inspect } from "node:util";

import type { GatewayResult } from "./results.js";

export type RecoveryStrategyKind = "read" | "risk_aware" | "compile_refresh" | "validation_after_edit" | "no_automatic_recovery";

export class RecoveryContext {
  readonly target: "describe" | "call";
  readonly command: string | undefined;
  readonly mode: "wait" | "job" | undefined;
  readonly acceptedPending: boolean;

  private constructor(input: { target: "describe" | "call"; command?: string; mode?: "wait" | "job"; acceptedPending?: boolean }) {
    this.target = input.target;
    this.command = input.command;
    this.mode = input.mode;
    this.acceptedPending = input.acceptedPending === true;
    Object.freeze(this);
  }

  static describe(): RecoveryContext {
    return new RecoveryContext({ target: "describe" });
  }

  static call(input: { command: string; mode: "wait" | "job"; acceptedPending: boolean }): RecoveryContext {
    if (input.command.trim().length === 0) {
      throw new TypeError("Recovery command must be non-empty");
    }
    return new RecoveryContext({ target: "call", ...input });
  }

  toJSON(): Record<string, unknown> {
    return {
      target: this.target,
      ...(this.command ? { command: this.command } : {}),
      ...(this.mode ? { mode: this.mode } : {}),
      acceptedPending: this.acceptedPending,
    };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export class RecoveryStrategyResult {
  readonly strategy: RecoveryStrategyKind;
  readonly context: RecoveryContext;
  readonly #result: GatewayResult;

  constructor(strategy: RecoveryStrategyKind, context: RecoveryContext, result: GatewayResult) {
    this.strategy = strategy;
    this.context = context;
    this.#result = result;
    Object.freeze(this);
  }

  get result(): GatewayResult {
    return this.#result;
  }

  toJSON(): Record<string, unknown> {
    return { strategy: this.strategy, context: this.context.toJSON(), status: this.#result.status };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export type RecoveryHandlers = Readonly<Record<RecoveryStrategyKind, () => Promise<GatewayResult>>>;

export interface RecoveryStrategy {
  readonly kind: RecoveryStrategyKind;
  execute(context: RecoveryContext, handlers: RecoveryHandlers): Promise<RecoveryStrategyResult>;
}

abstract class BaseRecoveryStrategy implements RecoveryStrategy {
  abstract readonly kind: RecoveryStrategyKind;

  async execute(context: RecoveryContext, handlers: RecoveryHandlers): Promise<RecoveryStrategyResult> {
    return new RecoveryStrategyResult(this.kind, context, await handlers[this.kind]());
  }
}

export class ReadRecoveryStrategy extends BaseRecoveryStrategy {
  readonly kind = "read" as const;
}

export class RiskAwareRecoveryStrategy extends BaseRecoveryStrategy {
  readonly kind = "risk_aware" as const;
}

export class CompileRefreshRecoveryStrategy extends BaseRecoveryStrategy {
  readonly kind = "compile_refresh" as const;
}

export class ValidationAfterEditRecoveryStrategy extends BaseRecoveryStrategy {
  readonly kind = "validation_after_edit" as const;
}

export class NoAutomaticRecoveryStrategy extends BaseRecoveryStrategy {
  readonly kind = "no_automatic_recovery" as const;
}

const READ_RECOVERY_COMMANDS = new Set(["compile.wait_ready", "compile.errors.recent", "compile.status", "test.run.status", "editor.menu.operation.status", "job.artifact.read", "logs.console.read", "logs.console.query"]);
const READ = new ReadRecoveryStrategy();
const RISK_AWARE = new RiskAwareRecoveryStrategy();
const COMPILE_REFRESH = new CompileRefreshRecoveryStrategy();
const VALIDATION_AFTER_EDIT = new ValidationAfterEditRecoveryStrategy();
const NO_AUTOMATIC_RECOVERY = new NoAutomaticRecoveryStrategy();

export function selectRecoveryStrategy(context: RecoveryContext): RecoveryStrategy {
  if (context.target === "describe") {
    return READ;
  }
  if (context.mode !== "wait") {
    return NO_AUTOMATIC_RECOVERY;
  }
  if (context.command === "compile.refresh_and_wait") {
    return COMPILE_REFRESH;
  }
  if (context.command === "validation.after_edit") {
    return VALIDATION_AFTER_EDIT;
  }
  if (!context.acceptedPending && context.command && READ_RECOVERY_COMMANDS.has(context.command)) {
    return READ;
  }
  if (!context.acceptedPending) {
    return RISK_AWARE;
  }
  return NO_AUTOMATIC_RECOVERY;
}

export function isReadRecoveryCommand(command: string): boolean {
  return READ_RECOVERY_COMMANDS.has(command);
}
