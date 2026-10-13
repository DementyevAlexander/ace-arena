import { createHash } from "node:crypto";
import { inspect } from "node:util";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import { commandDescriptorRevision } from "../bridge/bridge-protocol.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";

export class InvocationIdentity {
  readonly projectSelector: string;
  readonly project: string;
  readonly command: string;
  readonly descriptorRevision: string;

  private constructor(input: { projectSelector: string; project: string; command: string; descriptorRevision: string }) {
    this.projectSelector = requireNonempty(input.projectSelector, "project selector");
    this.project = requireNonempty(input.project, "project");
    this.command = requireNonempty(input.command, "command");
    this.descriptorRevision = requireNonempty(input.descriptorRevision, "descriptor revision");
    Object.freeze(this);
  }

  static create(input: { projectSelector: string; project: string; command: string; descriptor: CommandDescriptor }): InvocationIdentity {
    if (input.descriptor.id !== input.command) {
      throw new TypeError("Invocation command must match the resolved descriptor");
    }
    return new InvocationIdentity({
      projectSelector: input.projectSelector,
      project: input.project,
      command: input.command,
      descriptorRevision: commandDescriptorRevision(input.descriptor),
    });
  }

  toJSON(): Record<string, unknown> {
    return {
      projectHash: createHash("sha256").update("sgg.unity_mcp.invocation_project.v1\0").update(this.project, "utf8").digest("hex"),
      command: this.command,
      descriptorRevision: this.descriptorRevision,
    };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export class InvocationContextSnapshot {
  readonly identity: InvocationIdentity;
  readonly commandsHash: string | null;
  readonly catalogGeneration: number | null;
  readonly #descriptor: CommandDescriptor;
  readonly #record: HeartbeatRecord | undefined;

  private constructor(input: { identity: InvocationIdentity; descriptor: CommandDescriptor; commandsHash: string | null; record?: HeartbeatRecord }) {
    const commandsHash = requireNullableNonempty(input.commandsHash, "commands hash");
    if (input.descriptor.id !== input.identity.command || commandDescriptorRevision(input.descriptor) !== input.identity.descriptorRevision) {
      throw new TypeError("Invocation descriptor must match invocation identity");
    }
    if (input.record && input.record.project !== input.identity.project) {
      throw new TypeError("Invocation heartbeat project must match invocation identity");
    }
    if (input.record && commandsHash === null) {
      throw new TypeError("Heartbeat-backed invocation must include a commands hash");
    }
    this.identity = input.identity;
    this.commandsHash = commandsHash;
    this.catalogGeneration = input.record?.catalog_generation ?? null;
    this.#descriptor = input.descriptor;
    this.#record = input.record;
    Object.freeze(this);
  }

  static create(input: { projectSelector: string; project: string; command: string; descriptor: CommandDescriptor; commandsHash: string | null; record?: HeartbeatRecord }): InvocationContextSnapshot {
    const descriptor = cloneAndFreezePlainData(input.descriptor);
    const record = input.record ? freezeInvocationRecord(input.record) : undefined;
    const identity = InvocationIdentity.create({ ...input, descriptor });
    return new InvocationContextSnapshot({
      identity,
      descriptor,
      commandsHash: input.commandsHash,
      ...(record ? { record } : {}),
    });
  }

  get descriptor(): CommandDescriptor {
    return this.#descriptor;
  }

  get record(): HeartbeatRecord | undefined {
    return this.#record;
  }

  toJSON(): Record<string, unknown> {
    return {
      identity: this.identity.toJSON(),
      commandsHashPresent: this.commandsHash !== null,
      catalogGenerationPresent: this.catalogGeneration !== null,
      heartbeatPresent: this.#record !== undefined,
    };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export function freezeInvocationRecord(record: HeartbeatRecord): HeartbeatRecord {
  return cloneAndFreezePlainData(record);
}

function requireNonempty(value: string, name: string): string {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw new TypeError(`Invocation ${name} must be a non-empty string`);
  }
  return value;
}

function requireNullableNonempty(value: string | null, name: string): string | null {
  return value === null ? null : requireNonempty(value, name);
}

function cloneAndFreezePlainData<T>(value: T, clones = new WeakMap<object, unknown>()): T {
  if (value === null || value === undefined || typeof value === "string" || typeof value === "number" || typeof value === "boolean") {
    return value;
  }
  if (typeof value !== "object") {
    throw new TypeError("Invocation snapshots accept plain data only");
  }

  const existing = clones.get(value);
  if (existing !== undefined) {
    return existing as T;
  }

  if (Array.isArray(value)) {
    const clone: unknown[] = new Array(value.length);
    clones.set(value, clone);
    for (const key of Reflect.ownKeys(value)) {
      if (key === "length") continue;
      if (typeof key !== "string" || !/^(0|[1-9][0-9]*)$/.test(key) || Number(key) >= value.length) {
        throw new TypeError("Invocation snapshot arrays must contain indexed plain data only");
      }
      const property = Object.getOwnPropertyDescriptor(value, key);
      if (!property || !("value" in property)) {
        throw new TypeError("Invocation snapshots do not accept accessors");
      }
      clone[Number(key)] = cloneAndFreezePlainData(property.value, clones);
    }
    return Object.freeze(clone) as T;
  }

  const prototype = Object.getPrototypeOf(value) as unknown;
  if (prototype !== Object.prototype && prototype !== null) {
    throw new TypeError("Invocation snapshots accept plain objects only");
  }
  const clone = Object.create(prototype) as Record<string, unknown>;
  clones.set(value, clone);
  for (const key of Reflect.ownKeys(value)) {
    if (typeof key !== "string") {
      throw new TypeError("Invocation snapshots do not accept symbol properties");
    }
    const property = Object.getOwnPropertyDescriptor(value, key);
    if (!property || !("value" in property)) {
      throw new TypeError("Invocation snapshots do not accept accessors");
    }
    Object.defineProperty(clone, key, {
      value: cloneAndFreezePlainData(property.value, clones),
      enumerable: property.enumerable === true,
      configurable: false,
      writable: false,
    });
  }
  return Object.freeze(clone) as T;
}
