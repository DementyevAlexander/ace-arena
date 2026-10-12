import { inspect } from "node:util";

import type { ToolName } from "../mcp/contracts.js";
import type { GatewayResult } from "../mcp/results.js";
import { createAuditEvent, type AuditEvent, type AuditRuntimeMetadata, type AuditSink } from "./audit.js";

export class AuditContext {
  readonly #event: AuditEvent;

  private constructor(event: AuditEvent) {
    this.#event = freezeAuditEvent(event);
    Object.freeze(this);
  }

  static create(input: {
    tool: ToolName;
    rawArgs: unknown;
    result: GatewayResult;
    startedAtMs: number;
    now?: () => Date;
    metadata?: AuditRuntimeMetadata;
    runId?: string;
    auditId?: string;
  }): AuditContext {
    return new AuditContext(createAuditEvent(
      input.tool,
      input.rawArgs,
      input.result,
      input.startedAtMs,
      input.now,
      input.metadata,
      input.runId,
      input.auditId,
    ));
  }

  get event(): AuditEvent {
    return this.#event;
  }

  toJSON(): Record<string, unknown> {
    return { tool: this.#event.tool, status: this.#event.status, errorCodePresent: this.#event.error_code !== undefined };
  }

  [inspect.custom](): Record<string, unknown> {
    return this.toJSON();
  }
}

export async function writeInvocationAudit(sink: AuditSink, context: AuditContext): Promise<void> {
  await sink.write(context.event);
}

function freezeAuditEvent(event: AuditEvent): AuditEvent {
  if (event.artifact_refs) Object.freeze(event.artifact_refs);
  return Object.freeze(event);
}
