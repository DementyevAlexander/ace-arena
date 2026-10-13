/** Text-only clients must not mistake a bounded projection for a complete result. */
export const CANONICAL_TEXT_MAX_BYTES = 512 * 1024;

export function canonicalTextProjection(payload: Record<string, unknown>, maxBytes = CANONICAL_TEXT_MAX_BYTES): string {
  const requested = Number.isSafeInteger(maxBytes) ? maxBytes : CANONICAL_TEXT_MAX_BYTES;
  const limit = Math.max(1_024, Math.min(requested, CANONICAL_TEXT_MAX_BYTES));
  let canonical: string;
  try {
    canonical = JSON.stringify(payload);
    if (typeof canonical !== "string") throw new Error("Missing canonical JSON");
  } catch {
    return JSON.stringify({
      compatibility_projection: "truncated",
      reason: "canonical_payload_not_serializable",
      max_bytes: limit,
      structured_content_preserved: true,
      truncated: true,
    });
  }
  const bytes = Buffer.byteLength(canonical, "utf8");
  if (bytes <= limit) return canonical;

  const projection: Record<string, unknown> = {
    compatibility_projection: "truncated",
    reason: "canonical_payload_exceeds_legacy_text_limit",
    canonical_bytes: bytes,
    max_bytes: limit,
    structured_content_preserved: true,
    truncated: true,
  };
  const add = (key: string, value: unknown): void => {
    if (value === undefined) return;
    projection[key] = value;
    if (Buffer.byteLength(JSON.stringify(projection), "utf8") > limit) delete projection[key];
  };
  add("original_status", boundedString(own(payload, "status")));
  add("request_id", boundedString(own(payload, "request_id")));
  add("project", boundedString(own(payload, "project")));
  add("command", boundedString(own(payload, "command")));

  const error = own(payload, "error");
  if (isRecord(error)) {
    const visibleError: Record<string, unknown> = {};
    for (const key of ["code", "message"]) {
      const value = boundedString(own(error, key));
      if (value !== undefined) visibleError[key] = value;
    }
    const details = own(error, "details");
    const visibleDetails: Record<string, unknown> = {};
    if (isRecord(details)) visibleError.details = visibleDetails;
    add("error", visibleError);
    if (isRecord(details) && projection.error) {
      // Only bounded recovery guidance, never a partial arbitrary payload.
      for (const key of [
        "operation_id", "run_id", "job_id", "request_executed", "bridge_request_issued",
        "automatic_replay_attempted", "write_command_repeated", "client_reconnect_required", "new_session_required",
        "schema_lookup", "approval_request", "approval_workflow", "native_mcp_elicitation",
        "status_request", "status_lookup", "recovery", "recovery_actions", "next_valid_actions",
      ]) {
        const value = boundedRecoveryValue(own(details, key), 0);
        if (value === undefined) continue;
        visibleDetails[key] = value;
        if (Buffer.byteLength(JSON.stringify(projection), "utf8") > limit) delete visibleDetails[key];
      }
    }
  }
  add("next_valid_actions", boundedRecoveryValue(own(payload, "next_valid_actions"), 0));
  add("artifact_refs", boundedRecoveryValue(own(payload, "artifact_refs"), 0));
  const data = own(payload, "data");
  if (isRecord(data)) add("cursor", boundedString(own(data, "cursor") ?? own(data, "next_cursor")));
  return JSON.stringify(projection);
}

function boundedRecoveryValue(value: unknown, depth: number): unknown {
  if (typeof value === "string") return boundedString(value);
  if (typeof value === "boolean" || value === null) return value;
  if (typeof value === "number") return Number.isFinite(value) ? value : undefined;
  if (depth >= 4) return undefined;
  if (Array.isArray(value)) return value.slice(0, 8).map((item) => boundedRecoveryValue(item, depth + 1)).filter((item) => item !== undefined);
  if (!isRecord(value)) return undefined;
  const bounded: Record<string, unknown> = {};
  const keys = Object.keys(value);
  if (keys.length > 16) return undefined;
  for (const key of keys) {
    if (Buffer.byteLength(key, "utf8") > 128 || key === "__proto__") return undefined;
    const child = boundedRecoveryValue(own(value, key), depth + 1);
    // An incomplete status/schema/approval instruction must not look executable.
    if (child === undefined) return undefined;
    bounded[key] = child;
  }
  return bounded;
}

function boundedString(value: unknown): string | undefined {
  // Do not turn a truncated operation ID or command into a plausible exact value.
  return typeof value === "string" && value.length > 0 && Buffer.byteLength(value, "utf8") <= 512 ? value : undefined;
}

function own(value: Record<string, unknown>, key: string): unknown {
  const descriptor = Object.getOwnPropertyDescriptor(value, key);
  return descriptor && "value" in descriptor ? descriptor.value : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
