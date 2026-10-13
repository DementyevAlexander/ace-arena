export const TEST_RUN_STATUS_OBSERVATION_CONTRACT = "sgg.unity_mcp.test_run_status_observation.v1";
const MAX_DATE_MS = 8_640_000_000_000_000;
const trustedObservations = new WeakSet<object>();
const GATEWAY_OWNED_OBSERVATION_FIELDS = new Set(["status_observation", "observed_bridge_response", "publication_observation", "durable_issuance_state"]);

/** A failed or backward clock stays unavailable for this read workflow. No
 * substitute clock can turn its samples into credible observation timing. */
export function testRunObservationClock(now: () => number) {
  let last: number | undefined;
  let valid = true;
  return () => {
    if (!valid) return null;
    let value: number;
    try { value = now(); } catch { valid = false; return null; }
    if (!Number.isSafeInteger(value) || value < 0 || value > MAX_DATE_MS || last !== undefined && value < last) {
      valid = false;
      return null;
    }
    last = value;
    return value;
  };
}

export function testRunObservationElapsed(started: number | null, observed: number | null): number | null {
  if (!validEpoch(started) || !validEpoch(observed)) return null;
  const elapsed = observed - started;
  return Number.isSafeInteger(elapsed) && elapsed >= 0 ? elapsed : null;
}

export function persistedTestRunStatusObservation(observedUnixMs: number | null, openedMtimeNs: bigint): Readonly<Record<string, unknown>> {
  let age: number | null = null;
  if (observedUnixMs !== null && Number.isSafeInteger(observedUnixMs) && observedUnixMs >= 0 && observedUnixMs <= MAX_DATE_MS && openedMtimeNs >= 0n) {
    const difference = BigInt(observedUnixMs) * 1_000_000n - openedMtimeNs;
    if (difference >= 0n && difference / 1_000_000n <= BigInt(Number.MAX_SAFE_INTEGER)) age = Number(difference / 1_000_000n);
  }
  const timestampValid = observedUnixMs !== null && Number.isSafeInteger(observedUnixMs) && observedUnixMs >= 0 && observedUnixMs <= MAX_DATE_MS;
  const observation = Object.freeze({
    schema_version: 1,
    contract: TEST_RUN_STATUS_OBSERVATION_CONTRACT,
    source: "gateway_persisted_artifact_read",
    observed_at_utc: timestampValid ? new Date(observedUnixMs).toISOString() : null,
    artifact_snapshot_age_ms: age,
    age_source: age === null ? "unavailable" : "validated_open_handle_mtime",
    age_semantics: "filesystem_snapshot_not_runner_progress",
    duration_semantics: "retained_runner_duration",
  });
  trustedObservations.add(observation);
  return observation;
}

/** Compact projection preserves the exact branded snapshot; no clock resample,
 * string reconstruction or project-authored lookalike can mint it. */
export function trustedTestRunStatusObservation(value: unknown): Readonly<Record<string, unknown>> | undefined {
  return typeof value === "object" && value !== null && trustedObservations.has(value) ? value as Readonly<Record<string, unknown>> : undefined;
}

/** Used at untrusted bridge boundaries, including errors and legacy/unbound
 * routes. Only Gateway-owned observation/publication fields are removed; existing evidence is intact. */
export function withoutTestRunStatusObservation(value: unknown): unknown {
  // Scalar values are not coerced: the surrounding bridge/canonical result
  // validator still rejects non-JSON scalars. Containers must be bounded JSON.
  if (typeof value !== "object" || value === null) return value;
  const ancestors = new Set<object>();
  let nodes = 0;
  let stringChars = 0;
  const invalid = () => { throw new TypeError("Invalid bounded test observation payload"); };
  const text = (value: string, limit: number) => {
    stringChars += value.length;
    if (value.length > limit || stringChars > BRIDGE_PROTOCOL_BOUNDS.jsonTotalStringChars) invalid();
  };
  const visit = (value: unknown, depth: number): unknown => {
    if (++nodes > BRIDGE_PROTOCOL_BOUNDS.jsonNodes || depth > BRIDGE_PROTOCOL_BOUNDS.jsonDepth) return invalid();
    if (value === null || typeof value === "boolean") return value;
    if (typeof value === "string") { text(value, BRIDGE_PROTOCOL_BOUNDS.jsonStringChars); return value; }
    if (typeof value === "number" && Number.isFinite(value)) return value;
    if (typeof value !== "object" || ancestors.has(value)) return invalid();
    ancestors.add(value);
    try {
      if (Array.isArray(value)) {
        if (value.length > BRIDGE_PROTOCOL_BOUNDS.jsonArrayItems) return invalid();
        const keys = Reflect.ownKeys(value);
        if (keys.length !== value.length + 1 || keys.some((key) => typeof key !== "string")) return invalid();
        const output: unknown[] = [];
        for (let index = 0; index < value.length; index++) {
          const property = Object.getOwnPropertyDescriptor(value, String(index));
          if (!property || !property.enumerable || !("value" in property)) return invalid();
          output.push(visit(property.value, depth + 1));
        }
        return output;
      }
      const prototype = Object.getPrototypeOf(value);
      if (prototype !== Object.prototype && prototype !== null) return invalid();
      const keys = Reflect.ownKeys(value);
      if (keys.length > BRIDGE_PROTOCOL_BOUNDS.jsonObjectKeys) return invalid();
      const entries: Array<[string, unknown]> = [];
      for (const key of keys) {
        if (typeof key !== "string") return invalid();
        text(key, BRIDGE_PROTOCOL_BOUNDS.jsonKeyChars);
        // Never evaluate, traverse or validate the discarded reserved value.
        if (GATEWAY_OWNED_OBSERVATION_FIELDS.has(key)) continue;
        const property = Object.getOwnPropertyDescriptor(value, key);
        if (!property?.enumerable || !("value" in property)) return invalid();
        entries.push([key, visit(property.value, depth + 1)]);
      }
      return Object.fromEntries(entries);
    } finally { ancestors.delete(value); }
  };
  return visit(value, 0);
}

function validEpoch(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= MAX_DATE_MS;
}
import { BRIDGE_PROTOCOL_BOUNDS } from "../bridge/bridge-protocol.js";
