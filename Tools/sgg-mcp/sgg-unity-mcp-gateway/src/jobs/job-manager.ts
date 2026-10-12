import type { GatewayResult } from "../mcp/results.js";

export type JobState = "queued" | "running" | "succeeded" | "failed" | "cancel_requested" | "cancelled";

export type JobRecord = {
  job_id: string;
  project: string;
  command: string;
  request_id?: string;
  status: JobState;
  created_at: string;
  updated_at: string;
  started_at?: string;
  completed_at?: string;
  cancel_requested: boolean;
  supports_cancel: boolean;
  result?: GatewayResult;
};

export type JobSnapshot = Omit<JobRecord, "result"> & {
  result?: GatewayResult;
};

export type StartJobInput = {
  project: string;
  command: string;
  request_id?: string;
  supports_cancel?: boolean;
  task: () => Promise<GatewayResult>;
};

export class InMemoryJobManager {
  readonly #jobs = new Map<string, JobRecord>();
  readonly #now: () => Date;
  #nextId = 1;

  constructor(now: () => Date = () => new Date()) {
    this.#now = now;
  }

  start(input: StartJobInput): JobSnapshot {
    const now = this.#timestamp();
    const record: JobRecord = {
      job_id: this.#newJobId(),
      project: input.project,
      command: input.command,
      ...(input.request_id ? { request_id: input.request_id } : {}),
      status: "queued",
      created_at: now,
      updated_at: now,
      cancel_requested: false,
      supports_cancel: input.supports_cancel === true,
    };
    this.#jobs.set(record.job_id, record);

    void this.#run(record, input.task);
    return snapshot(record);
  }

  get(project: string, jobId: string): JobSnapshot | undefined {
    const record = this.#jobs.get(jobId);
    if (!record || record.project !== project) {
      return undefined;
    }

    return snapshot(record);
  }

  cancel(project: string, jobId: string): JobSnapshot | undefined {
    const record = this.#jobs.get(jobId);
    if (!record || record.project !== project) {
      return undefined;
    }

    if (record.status === "queued" || record.status === "running") {
      record.cancel_requested = true;
      record.status = "cancel_requested";
      record.updated_at = this.#timestamp();
    }

    return snapshot(record);
  }

  async #run(record: JobRecord, task: () => Promise<GatewayResult>): Promise<void> {
    this.#mark(record, "running", { started_at: this.#timestamp() });
    try {
      const result = await task();
      record.result = result;
      if (record.cancel_requested && result.status === "error" && result.error.code === "job_cancelled") {
        this.#mark(record, "cancelled", { completed_at: this.#timestamp() });
      } else {
        this.#mark(record, result.status === "ok" ? "succeeded" : "failed", { completed_at: this.#timestamp() });
      }
    } catch (error) {
      record.result = {
        status: "error",
        project: record.project,
        command: record.command,
        error: {
          code: "internal_error",
          message: error instanceof Error ? error.message : "Job failed",
        },
        diagnostics: [],
      };
      this.#mark(record, "failed", { completed_at: this.#timestamp() });
    }
  }

  #mark(record: JobRecord, status: JobState, extra: Partial<JobRecord> = {}): void {
    Object.assign(record, extra);
    if (record.cancel_requested && status === "running") {
      record.status = "cancel_requested";
    } else {
      record.status = status;
    }
    record.updated_at = this.#timestamp();
  }

  #newJobId(): string {
    return `job-${this.#nextId++}`;
  }

  #timestamp(): string {
    return this.#now().toISOString();
  }
}

function snapshot(record: JobRecord): JobSnapshot {
  return {
    job_id: record.job_id,
    project: record.project,
    command: record.command,
    ...(record.request_id ? { request_id: record.request_id } : {}),
    status: record.status,
    created_at: record.created_at,
    updated_at: record.updated_at,
    ...(record.started_at ? { started_at: record.started_at } : {}),
    ...(record.completed_at ? { completed_at: record.completed_at } : {}),
    cancel_requested: record.cancel_requested,
    supports_cancel: record.supports_cancel,
    ...(record.result ? { result: record.result } : {}),
  };
}
