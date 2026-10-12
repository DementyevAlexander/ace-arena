export type ProjectQueueOwnerMetadata = {
  command: string;
  risk: string;
  idempotency: string;
  deadline_unix_ms?: number;
};

export type ProjectQueueActiveOwner = ProjectQueueOwnerMetadata & {
  enqueued_at_unix_ms: number;
  admitted_at_unix_ms: number;
};

export class ProjectQueueManager {
  readonly #tails = new Map<string, Promise<void>>();
  readonly #activeOwners = new Map<string, { token: symbol; owner: ProjectQueueActiveOwner }>();
  readonly #now: () => number;

  constructor(now: () => number = Date.now) {
    this.#now = now;
  }

  run<T>(project: string, task: () => Promise<T>, owner?: ProjectQueueOwnerMetadata): Promise<T> {
    const previous = this.#tails.get(project) ?? Promise.resolve();
    const enqueuedAtUnixMs = this.#now();
    const token = Symbol(project);
    const run = previous.catch(() => undefined).then(async () => {
      if (owner) {
        this.#activeOwners.set(project, {
          token,
          owner: {
            command: owner.command,
            risk: owner.risk,
            idempotency: owner.idempotency,
            ...(owner.deadline_unix_ms === undefined ? {} : { deadline_unix_ms: owner.deadline_unix_ms }),
            enqueued_at_unix_ms: enqueuedAtUnixMs,
            admitted_at_unix_ms: this.#now(),
          },
        });
      }
      try {
        return await task();
      } finally {
        if (this.#activeOwners.get(project)?.token === token) this.#activeOwners.delete(project);
      }
    });
    this.#tails.set(
      project,
      run.then(
        () => undefined,
        () => undefined,
      ),
    );
    return run;
  }

  activeOwner(project: string): ProjectQueueActiveOwner | undefined {
    const owner = this.#activeOwners.get(project)?.owner;
    return owner ? { ...owner } : undefined;
  }
}
