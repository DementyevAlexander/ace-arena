import { afterEach, describe, expect, it, vi } from "vitest";

import {
  TEST_RUN_STATUS_OBSERVATION_CONTRACT,
  persistedTestRunStatusObservation,
  testRunObservationClock,
  testRunObservationElapsed,
  trustedTestRunStatusObservation,
  withoutTestRunStatusObservation,
} from "../src/mcp/test-run-status-observation.js";

const MAX_DATE_MS = 8_640_000_000_000_000;
const NS_PER_MS = 1_000_000n;
const OBSERVED_MS = Date.UTC(2026, 9, 8, 12, 34, 56, 789);
const invalidTimestamps: [string, unknown][] = [
  ["null", null],
  ["negative", -1],
  ["fractional", 0.5],
  ["beyond the Date maximum", MAX_DATE_MS + 1],
  ["safe integer beyond the Date maximum", Number.MAX_SAFE_INTEGER],
  ["unsafe integer", Number.MAX_SAFE_INTEGER + 1],
  ["NaN", Number.NaN],
  ["positive infinity", Number.POSITIVE_INFINITY],
  ["negative infinity", Number.NEGATIVE_INFINITY],
  ["numeric string", "1000"],
  ["undefined", undefined],
  ["boolean", false],
  ["boxed number", new Number(1000)],
];

function expectedObservation(timestamp: string | null, age: number | null) {
  return {
    schema_version: 1,
    contract: "sgg.unity_mcp.test_run_status_observation.v1",
    source: "gateway_persisted_artifact_read",
    observed_at_utc: timestamp,
    artifact_snapshot_age_ms: age,
    age_source: age === null ? "unavailable" : "validated_open_handle_mtime",
    age_semantics: "filesystem_snapshot_not_runner_progress",
    duration_semantics: "retained_runner_duration",
  };
}

function freezeJson(value: unknown): void {
  if (value === null || typeof value !== "object") return;
  for (const child of Object.values(value)) freezeJson(child);
  Object.freeze(value);
}

afterEach(() => vi.restoreAllMocks());

describe("persisted test-run status observation v1 (pure filesystem-snapshot metadata)", () => {
  it("emits exactly the v1 read-observation contract, not runner progress or update age", () => {
    const observation = persistedTestRunStatusObservation(OBSERVED_MS, BigInt(OBSERVED_MS - 123) * NS_PER_MS);

    expect(TEST_RUN_STATUS_OBSERVATION_CONTRACT).toBe("sgg.unity_mcp.test_run_status_observation.v1");
    expect(observation).toStrictEqual(expectedObservation("2026-10-08T12:34:56.789Z", 123));
  });

  it.each([
    [0, "1970-01-01T00:00:00.000Z"],
    [-0, "1970-01-01T00:00:00.000Z"],
    [1, "1970-01-01T00:00:00.001Z"],
    [MAX_DATE_MS, "+275760-09-13T00:00:00.000Z"],
  ])("accepts the inclusive UTC timestamp boundary %s", (observed, utc) => {
    expect(persistedTestRunStatusObservation(observed, 0n))
      .toStrictEqual(expectedObservation(utc, observed === 0 ? 0 : observed));
  });

  it.each(invalidTimestamps)("leaves both timestamp and age unavailable for %s", (_name, observed) => {
    // The cast deliberately tests untyped callers without weakening the public input type.
    expect(persistedTestRunStatusObservation(observed as number | null, 0n))
      .toStrictEqual(expectedObservation(null, null));
  });

  it.each([
    [0n, 0],
    [1n, 0],
    [999_999n, 0],
    [1_000_000n, 1],
    [1_999_999n, 1],
    [2_000_000n, 2],
  ])("floors the exact bigint nanosecond delta %s rather than rounding", (delta, age) => {
    const openedMtimeNs = BigInt(MAX_DATE_MS) * NS_PER_MS - delta;
    expect(persistedTestRunStatusObservation(MAX_DATE_MS, openedMtimeNs))
      .toStrictEqual(expectedObservation("+275760-09-13T00:00:00.000Z", age));
  });

  it.each([
    ["one nanosecond in the future", BigInt(OBSERVED_MS) * NS_PER_MS + 1n],
    ["one millisecond in the future", BigInt(OBSERVED_MS + 1) * NS_PER_MS],
    ["negative opened mtime", -1n],
    ["huge negative opened mtime", -(10n ** 100n)],
    ["huge future opened mtime", 10n ** 100n],
  ])("keeps the valid read timestamp but makes age unavailable for %s", (_name, mtime) => {
    expect(persistedTestRunStatusObservation(OBSERVED_MS, mtime))
      .toStrictEqual(expectedObservation("2026-10-08T12:34:56.789Z", null));
  });

  it("retains the largest admissible age without overflowing the safe integer ceiling", () => {
    const observation = persistedTestRunStatusObservation(MAX_DATE_MS, 0n);
    expect(observation.artifact_snapshot_age_ms).toBe(MAX_DATE_MS);
    expect(Number.isSafeInteger(observation.artifact_snapshot_age_ms)).toBe(true);
    expect(observation.artifact_snapshot_age_ms).toBeLessThanOrEqual(Number.MAX_SAFE_INTEGER);
    expect(persistedTestRunStatusObservation(MAX_DATE_MS, -NS_PER_MS))
      .toStrictEqual(expectedObservation("+275760-09-13T00:00:00.000Z", null));
  });

  it.each([OBSERVED_MS, null])("freezes every field even when the clock is %s", (observed) => {
    const observation = persistedTestRunStatusObservation(observed, 0n);
    const before = { ...observation };

    expect(Object.isFrozen(observation)).toBe(true);
    expect(Reflect.set(observation, "artifact_snapshot_age_ms", 999)).toBe(false);
    expect(Reflect.set(observation, "runner_progress", true)).toBe(false);
    expect(Reflect.deleteProperty(observation, "contract")).toBe(false);
    expect(() => Object.defineProperty(observation, "source", { value: "runner" })).toThrow(TypeError);
    expect(observation).toStrictEqual(before);
  });
});

describe("test-run observation clock", () => {
  it("uses exactly one supplied sample per valid call and accepts equal or frozen time", () => {
    const fallback = vi.spyOn(Date, "now").mockImplementation(() => { throw new Error("fallback clock forbidden"); });
    const supplier = vi.fn<() => number>()
      .mockReturnValueOnce(0)
      .mockReturnValueOnce(0)
      .mockReturnValueOnce(OBSERVED_MS)
      .mockReturnValueOnce(OBSERVED_MS)
      .mockReturnValueOnce(MAX_DATE_MS)
      .mockReturnValue(MAX_DATE_MS);
    const clock = testRunObservationClock(supplier);

    expect([clock(), clock(), clock(), clock(), clock(), clock()])
      .toStrictEqual([0, 0, OBSERVED_MS, OBSERVED_MS, MAX_DATE_MS, MAX_DATE_MS]);
    expect(supplier).toHaveBeenCalledTimes(6);
    expect(fallback).not.toHaveBeenCalled();
  });

  it.each(invalidTimestamps)("makes %s sticky with no resampling or Date.now fallback", (_name, value) => {
    const fallback = vi.spyOn(Date, "now").mockReturnValue(OBSERVED_MS);
    const supplier = vi.fn<() => number>()
      .mockReturnValueOnce(100)
      .mockReturnValueOnce(value as number)
      .mockReturnValue(200);
    const clock = testRunObservationClock(supplier);

    expect(clock()).toBe(100);
    expect(clock()).toBeNull();
    expect(clock()).toBeNull();
    expect(clock()).toBeNull();
    expect(supplier).toHaveBeenCalledTimes(2);
    expect(fallback).not.toHaveBeenCalled();
  });

  it("does not recover after a backward sample even when subsequent supplied time catches up", () => {
    const fallback = vi.spyOn(Date, "now").mockReturnValue(OBSERVED_MS);
    const supplier = vi.fn<() => number>()
      .mockReturnValueOnce(100)
      .mockReturnValueOnce(99)
      .mockReturnValue(101);
    const clock = testRunObservationClock(supplier);

    expect(clock()).toBe(100);
    expect([clock(), clock(), clock()]).toStrictEqual([null, null, null]);
    expect(supplier).toHaveBeenCalledTimes(2);
    expect(fallback).not.toHaveBeenCalled();
  });

  it.each([false, true])("makes a throwing supplier sticky (preceding valid sample: %s)", (precedingValid) => {
    const fallback = vi.spyOn(Date, "now").mockReturnValue(OBSERVED_MS);
    const supplier = vi.fn<() => number>();
    if (precedingValid) supplier.mockReturnValueOnce(100);
    supplier.mockImplementationOnce(() => { throw new Error("sample unavailable"); }).mockReturnValue(200);
    const clock = testRunObservationClock(supplier);

    if (precedingValid) expect(clock()).toBe(100);
    expect([clock(), clock(), clock()]).toStrictEqual([null, null, null]);
    expect(supplier).toHaveBeenCalledTimes(precedingValid ? 2 : 1);
    expect(fallback).not.toHaveBeenCalled();
  });

  it("produces equal, independently frozen observations from a valid frozen clock", () => {
    const supplier = vi.fn(() => OBSERVED_MS);
    const clock = testRunObservationClock(supplier);
    const mtime = BigInt(OBSERVED_MS) * NS_PER_MS;
    const first = persistedTestRunStatusObservation(clock(), mtime);
    const second = persistedTestRunStatusObservation(clock(), mtime);

    expect(first).toStrictEqual(expectedObservation("2026-10-08T12:34:56.789Z", 0));
    expect(second).toStrictEqual(first);
    expect(second).not.toBe(first);
    expect(Object.isFrozen(first)).toBe(true);
    expect(Object.isFrozen(second)).toBe(true);
    expect(supplier).toHaveBeenCalledTimes(2);
  });
});

describe("test-run observation elapsed time", () => {
  it.each([
    [0, 0, 0],
    [100, 100, 0],
    [100, 101, 1],
    [0, MAX_DATE_MS, MAX_DATE_MS],
    [MAX_DATE_MS, MAX_DATE_MS, 0],
    [MAX_DATE_MS - 1, MAX_DATE_MS, 1],
  ])("computes elapsed time for valid bounded endpoints %s -> %s", (started, observed, elapsed) => {
    expect(testRunObservationElapsed(started, observed)).toBe(elapsed);
  });

  it.each(invalidTimestamps)("rejects %s endpoints even when subtraction would yield a valid duration", (_name, invalid) => {
    const value = invalid as number | null;
    expect(testRunObservationElapsed(value, value)).toBeNull();
    expect(testRunObservationElapsed(value, MAX_DATE_MS)).toBeNull();
    expect(testRunObservationElapsed(0, value)).toBeNull();
  });

  it.each([[1, 0], [MAX_DATE_MS, 0], [101, 100]])("rejects backward elapsed time %s -> %s", (started, observed) => {
    expect(testRunObservationElapsed(started, observed)).toBeNull();
  });
});

describe("privately branded test-run observations", () => {
  it.each([OBSERVED_MS, null])("projects the exact factory identity without mutation or a clock call (%s)", (observed) => {
    const observation = persistedTestRunStatusObservation(observed, 0n);
    const before = { ...observation };
    const fallback = vi.spyOn(Date, "now").mockImplementation(() => { throw new Error("no projection clock"); });

    expect(trustedTestRunStatusObservation(observation)).toBe(observation);
    expect(trustedTestRunStatusObservation(observation)).toBe(observation);
    expect(observation).toStrictEqual(before);
    expect(Object.isFrozen(observation)).toBe(true);
    expect(fallback).not.toHaveBeenCalled();
  });

  it("rejects JSON, structured and plain clones, inherited fields and a proxy of the branded object", () => {
    const observation = persistedTestRunStatusObservation(OBSERVED_MS, 0n);
    const imitations: unknown[] = [
      JSON.parse(JSON.stringify(observation)),
      structuredClone(observation),
      { ...observation },
      Object.freeze({ ...observation }),
      expectedObservation("2026-10-08T12:34:56.789Z", OBSERVED_MS),
      Object.create(observation),
      new Proxy(observation, {}),
      { status_observation: observation },
      [observation],
      null, undefined, false, 1, "sgg.unity_mcp.test_run_status_observation.v1",
    ];

    for (const imitation of imitations) expect(trustedTestRunStatusObservation(imitation)).toBeUndefined();
    expect(trustedTestRunStatusObservation(observation)).toBe(observation);
  });

  it("does not trust an identical genuine observation branded by another module instance", async () => {
    vi.resetModules();
    const foreignModule = await import("../src/mcp/test-run-status-observation.js");
    const foreign = foreignModule.persistedTestRunStatusObservation(OBSERVED_MS, 0n);
    const local = persistedTestRunStatusObservation(OBSERVED_MS, 0n);

    expect(foreign).toStrictEqual(local);
    expect(foreignModule.trustedTestRunStatusObservation(foreign)).toBe(foreign);
    expect(trustedTestRunStatusObservation(foreign)).toBeUndefined();
    expect(foreignModule.trustedTestRunStatusObservation(local)).toBeUndefined();
    expect(trustedTestRunStatusObservation(local)).toBe(local);
  });
});

describe("untrusted test-run status observation stripping", () => {
  it.each(["observed_bridge_response", "publication_observation", "durable_issuance_state"])("discards the new Gateway-owned %s field without traversing its untrusted value", (key) => {
    const nested: Record<string, unknown> = { keep: "project value" };
    Object.defineProperty(nested, key, { enumerable: true, get() { throw new Error("Reserved getter was evaluated"); } });
    const forged: Record<string, unknown> = {};
    forged.circular = forged;
    const data = { [key]: forged, keep: false, nested: [nested, { [key]: forged, keep: 0 }] };
    expect(withoutTestRunStatusObservation(data)).toEqual({ keep: false, nested: [{ keep: "project value" }, { keep: 0 }] });
  });

  it("strips only the reserved key recursively through data, errors, nested arrays, legacy and live payloads", () => {
    const forged = expectedObservation("2026-10-08T12:34:56.789Z", 0);
    const retained = {
      total: 12, passed: 8, failed: 1, skipped: 3,
      success: false, completed: true, duration: 17.25, duration_ms: 17_250,
      run_id: "retained-run", ownership: { project: "fixture", owns_run: false },
      approval_required: true, approval: { state: "pending" },
      automatic_replay_attempted: false, automatic_command_replay_attempted: false,
      request_id: "retained-request", checkpoint_id: "retained-checkpoint", plan_id: "retained-plan",
      status_observation_hint: "not the reserved key",
      Status_Observation: "case-sensitive unrelated key",
      message: "status_observation is an ordinary string here",
      nullable: null, zero: 0, empty: "", enabled: false,
    };
    const input = {
      status_observation: forged,
      data: {
        ...retained, status_observation: forged,
        legacy: { ...retained, status_observation: forged },
        live: { status: "running", ...retained, status_observation: forged },
        nested: [null, false, 0, "status_observation", [
          { ...retained, status_observation: forged },
          { details: { status_observation: forged, duration: 3 } },
        ]],
      },
      error: { code: "fixture_error", details: { ...retained, status_observation: forged } },
      diagnostics: [{ severity: "warning", status_observation: forged }],
    };
    const before = structuredClone(input);
    freezeJson(input);

    expect(withoutTestRunStatusObservation(input)).toStrictEqual({
      data: {
        ...retained,
        legacy: retained,
        live: { status: "running", ...retained },
        nested: [null, false, 0, "status_observation", [retained, { details: { duration: 3 } }]],
      },
      error: { code: "fixture_error", details: retained },
      diagnostics: [{ severity: "warning" }],
    });
    expect(input).toStrictEqual(before);
  });

  it("does not treat a trusted factory observation as permission to keep the reserved key at an untrusted boundary", () => {
    const observation = persistedTestRunStatusObservation(OBSERVED_MS, 0n);
    const input = { status_observation: observation, data: [{ status_observation: observation, duration: 1 }] };

    expect(withoutTestRunStatusObservation(input)).toStrictEqual({ data: [{ duration: 1 }] });
    expect(trustedTestRunStatusObservation(observation)).toBe(observation);
    expect(input.status_observation).toBe(observation);
  });

  it("drops the reserved key without evaluating its untrusted accessor", () => {
    const getter = vi.fn(() => { throw new Error("discarded observation must not be read"); });
    const nested = Object.defineProperty({ duration: 1, approval_required: true }, "status_observation", {
      enumerable: true,
      get: getter,
    });

    expect(withoutTestRunStatusObservation({ data: [nested] }))
      .toStrictEqual({ data: [{ duration: 1, approval_required: true }] });
    expect(getter).not.toHaveBeenCalled();
    expect(Object.hasOwn(nested, "status_observation")).toBe(true);
  });

  it("preserves unrelated scalars exactly and leaves legacy absence absent", () => {
    for (const value of [null, undefined, false, true, 0, -0, 17.5, Number.NaN, "", "status_observation", 123n]) {
      expect(withoutTestRunStatusObservation(value)).toBe(value);
    }
    const legacy = { data: { duration: 0, total: 0, success: false }, error: null, approval_required: false };
    expect(withoutTestRunStatusObservation(legacy)).toStrictEqual(legacy);
    expect(withoutTestRunStatusObservation([])).toStrictEqual([]);
  });

  it("preserves special own property names without prototype pollution", () => {
    const input = JSON.parse('{"__proto__":{"status_observation":{},"retained":true},"constructor":{"status_observation":{},"duration":5},"prototype":"retained","status_observation":{}}');
    const expected = JSON.parse('{"__proto__":{"retained":true},"constructor":{"duration":5},"prototype":"retained"}');
    const result = withoutTestRunStatusObservation(input) as Record<string, unknown>;

    // Compare own data explicitly: a data-valued `constructor` confuses the
    // assertion library's constructor-identity check, not the JSON contract.
    expect(Object.keys(result)).toStrictEqual(Object.keys(expected));
    for (const key of Object.keys(expected)) expect(result[key]).toStrictEqual(expected[key]);
    expect(Object.hasOwn(result, "__proto__")).toBe(true);
    expect(Object.getPrototypeOf(result)).toBe(Object.prototype);
    expect(Object.hasOwn(Object.prototype, "retained")).toBe(false);
  });

  it.each([new Date(0), new Map(), new Set(), new (class { count = 1; })()])("rejects non-plain containers instead of JSON-coercing %s", (value) => {
    expect(() => withoutTestRunStatusObservation({ data: value })).toThrow("Invalid bounded test observation payload");
  });

  it("rejects cycles, sparse arrays and non-reserved accessors without following them", () => {
    const cycle: Record<string, unknown> = {};
    cycle.self = cycle;
    const getter = vi.fn(() => 1);
    const accessor = Object.defineProperty({}, "count", { enumerable: true, get: getter });
    for (const value of [cycle, new Array(1), accessor, { value: NaN }, { value: 1n }, { value: undefined }]) {
      expect(() => withoutTestRunStatusObservation(value)).toThrow("Invalid bounded test observation payload");
    }
    expect(getter).not.toHaveBeenCalled();
  });

  it("enforces existing JSON depth/width/string bounds without returning truncated evidence", () => {
    let deep: unknown = {};
    for (let i = 0; i < 33; i++) deep = { nested: deep };
    for (const value of [deep, Array(4097).fill(0), Object.fromEntries(Array.from({ length: 2049 }, (_, i) => [String(i), 0])),
      { value: "x".repeat(262145) }, { ["x".repeat(257)]: 0 }]) {
      expect(() => withoutTestRunStatusObservation(value)).toThrow("Invalid bounded test observation payload");
    }
  });

  it("counts aliased plain subtrees against the existing total-node budget", () => {
    const branch = Array(12).fill(false);
    expect(() => withoutTestRunStatusObservation({ items: Array(4096).fill(branch) })).toThrow("Invalid bounded test observation payload");
    expect(withoutTestRunStatusObservation({ items: [branch, branch] })).toEqual({ items: [branch, branch] });
  });

  it("bounds total string characters as well as each string", () => {
    expect(() => withoutTestRunStatusObservation({ items: Array(4).fill("x".repeat(262144)) })).toThrow("Invalid bounded test observation payload");
  });
});
