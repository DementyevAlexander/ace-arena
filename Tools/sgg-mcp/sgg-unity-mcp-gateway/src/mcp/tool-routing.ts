import { inspect } from "node:util";

export type InvocationMode = "wait" | "job";
export type BatchMode = "sequential" | "parallel_by_project";
export type JobAction = "status" | "cancel";
export type LogKind = "console" | "bridge" | "jobs";

export class InvocationRoute {
  readonly mode: InvocationMode;

  constructor(mode: InvocationMode) {
    this.mode = mode;
    Object.freeze(this);
  }

  execute<TWait, TJob>(handlers: { wait: () => TWait; job: () => TJob }): TWait | TJob {
    return this.mode === "wait" ? handlers.wait() : handlers.job();
  }

  toJSON(): Record<string, unknown> {
    return { mode: this.mode };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export class BatchRoute {
  readonly mode: BatchMode;
  readonly itemCount: number;

  constructor(mode: BatchMode, itemCount: number) {
    this.mode = mode;
    this.itemCount = itemCount;
    Object.freeze(this);
  }

  execute<TSequential, TParallel>(handlers: { sequential: () => TSequential; parallelByProject: () => TParallel }): TSequential | TParallel {
    return this.mode === "sequential" ? handlers.sequential() : handlers.parallelByProject();
  }

  toJSON(): Record<string, unknown> {
    return { mode: this.mode, itemCount: this.itemCount };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export class JobRoute {
  readonly action: JobAction;

  constructor(action: JobAction) {
    this.action = action;
    Object.freeze(this);
  }

  execute<TStatus, TCancel>(handlers: { status: () => TStatus; cancel: () => TCancel }): TStatus | TCancel {
    return this.action === "status" ? handlers.status() : handlers.cancel();
  }

  toJSON(): Record<string, unknown> {
    return { action: this.action };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export class LogsRoute {
  readonly kind: LogKind;
  readonly limit: number;

  constructor(kind: LogKind, limit: number) {
    this.kind = kind;
    this.limit = limit;
    Object.freeze(this);
  }

  execute<T>(read: (kind: LogKind, limit: number) => T): T {
    return read(this.kind, this.limit);
  }

  toJSON(): Record<string, unknown> {
    return { kind: this.kind, limit: this.limit };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}
