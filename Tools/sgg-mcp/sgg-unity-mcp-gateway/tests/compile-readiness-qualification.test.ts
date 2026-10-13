import { afterEach, describe, expect, it, vi } from "vitest";

import {
  COMPILE_READINESS_QUALIFICATION_CONTRACT,
  safeCompileReadinessQualification,
} from "../src/mcp/compile-readiness-qualification.js";

const expectedQualification = Object.freeze({
  schema_version: 1,
  contract: "sgg.unity_mcp.compile_readiness_qualification.v1",
  readiness_scope: "editor_package_lifecycle_script_evidence_and_background_progress",
  compile_diagnostics_scope: "script_compiler_and_console_cs_error_marker",
  shader_asset_readiness: "unknown",
  console_history_complete: false,
  console_history_durable: false,
  explicit_compilation_evidence: "inspect_compile_barrier_not_inferred_from_ready_clean",
} as const);

const qualificationKeys = Object.keys(expectedQualification);

function qualificationSource(): Record<string, unknown> {
  return { ...expectedQualification };
}

const alteredFields: [string, string, unknown][] = [
  ["an old schema version", "schema_version", 0],
  ["a new schema version", "schema_version", 2],
  ["a coerced schema version", "schema_version", "1"],
  ["an unknown contract", "contract", "sgg.unity_mcp.unknown.v1"],
  ["a v2 contract", "contract", "sgg.unity_mcp.compile_readiness_qualification.v2"],
  ["ready_clean as readiness scope", "readiness_scope", "ready_clean"],
  ["unknown readiness scope", "readiness_scope", "unknown"],
  ["shader compiler diagnostic scope", "compile_diagnostics_scope", "shader_compiler"],
  ["unknown CS diagnostic scope", "compile_diagnostics_scope", "script_compiler_and_console_cs_unknown"],
  ["ready shader assets", "shader_asset_readiness", "ready"],
  ["warning shader assets", "shader_asset_readiness", "warning"],
  ["complete Console history", "console_history_complete", true],
  ["a coerced Console completeness flag", "console_history_complete", "false"],
  ["durable Console history", "console_history_durable", true],
  ["a numeric Console durability flag", "console_history_durable", 0],
  ["no explicit compilation evidence required", "explicit_compilation_evidence", "not_required"],
  ["compilation evidence inferred from ready_clean", "explicit_compilation_evidence", "inferred_from_ready_clean"],
  ["unknown explicit compilation evidence", "explicit_compilation_evidence", "unknown"],
];

class QualificationInstance {
  constructor() {
    Object.assign(this, qualificationSource());
  }
}

const invalidContainers: [string, unknown][] = [
  ["legacy absence", undefined],
  ["null", null],
  ["a boolean", false],
  ["a number", 1],
  ["a string", expectedQualification.contract],
  ["a symbol", Symbol("qualification")],
  ["a bigint", 1n],
  ["a function with all eight fields", Object.assign(() => undefined, qualificationSource())],
  ["an array with all eight fields", Object.assign([], qualificationSource())],
  ["a Date with all eight fields", Object.assign(new Date(0), qualificationSource())],
  ["a Map with all eight fields", Object.assign(new Map(), qualificationSource())],
  ["a class instance with all eight fields", new QualificationInstance()],
];

afterEach(() => vi.restoreAllMocks());

describe("compile-readiness qualification v1 (pure source-authored scope metadata)", () => {
  it("exports the exact contract and projects exactly its eight constant fields", () => {
    const result = safeCompileReadinessQualification(qualificationSource());

    expect(COMPILE_READINESS_QUALIFICATION_CONTRACT)
      .toBe("sgg.unity_mcp.compile_readiness_qualification.v1");
    expect(result).toStrictEqual(expectedQualification);
    expect(Reflect.ownKeys(result!)).toHaveLength(8);
    expect(Reflect.ownKeys(result!).sort()).toStrictEqual([...qualificationKeys].sort());
    for (const key of qualificationKeys) {
      const descriptor = Object.getOwnPropertyDescriptor(result!, key);
      expect(descriptor?.enumerable).toBe(true);
      expect(descriptor).toHaveProperty("value", expectedQualification[key as keyof typeof expectedQualification]);
      expect(descriptor).not.toHaveProperty("get");
      expect(descriptor).not.toHaveProperty("set");
    }
  });

  it("accepts a JSON-cloned Unity-authored DTO without a gateway-owned identity or brand", () => {
    const clone: unknown = JSON.parse(JSON.stringify(qualificationSource()));

    expect(safeCompileReadinessQualification(clone)).toStrictEqual(expectedQualification);
    expect(safeCompileReadinessQualification(JSON.parse(JSON.stringify(clone))))
      .toStrictEqual(expectedQualification);
  });

  it("accepts an exact null-prototype source and returns the canonical content", () => {
    const source: Record<string, unknown> = Object.assign(Object.create(null), qualificationSource());

    expect(Object.getPrototypeOf(source)).toBeNull();
    expect(safeCompileReadinessQualification(source)).toStrictEqual(expectedQualification);
    expect(Object.getPrototypeOf(source)).toBeNull();
  });

  it("does not require source fields to arrive in canonical insertion order", () => {
    const source = Object.fromEntries(Object.entries(qualificationSource()).reverse());

    expect(safeCompileReadinessQualification(source)).toStrictEqual(expectedQualification);
  });

  it("accepts frozen, sealed, and non-writable enumerable data properties", () => {
    const nonWritable = qualificationSource();
    for (const key of qualificationKeys) {
      Object.defineProperty(nonWritable, key, { writable: false, configurable: false });
    }
    for (const source of [Object.freeze(qualificationSource()), Object.seal(qualificationSource()), nonWritable]) {
      expect(safeCompileReadinessQualification(source)).toStrictEqual(expectedQualification);
    }
  });

  it("does not freeze or mutate its caller and does not retain mutable source fields", () => {
    const source = qualificationSource();
    const before = Object.getOwnPropertyDescriptors(source);
    const result = safeCompileReadinessQualification(source);

    expect(result).toStrictEqual(expectedQualification);
    expect(result).not.toBe(source);
    expect(Object.getOwnPropertyDescriptors(source)).toStrictEqual(before);
    expect(Object.isFrozen(source)).toBe(false);
    source.shader_asset_readiness = "ready";
    delete source.contract;
    source.is_ready = true;
    expect(result).toStrictEqual(expectedQualification);
    expect(safeCompileReadinessQualification(source)).toBeUndefined();
  });

  it("freezes the projected content against edits, removal, extra flags, and redefinition", () => {
    const result = safeCompileReadinessQualification(qualificationSource());

    expect(result).toStrictEqual(expectedQualification);
    expect(Object.isFrozen(result)).toBe(true);
    expect(Reflect.set(result!, "shader_asset_readiness", "ready")).toBe(false);
    expect(Reflect.set(result!, "is_ready", true)).toBe(false);
    expect(Reflect.deleteProperty(result!, "console_history_complete")).toBe(false);
    expect(() => Object.defineProperty(result!, "contract", { value: "changed" })).toThrow(TypeError);
    expect(result).toStrictEqual(expectedQualification);
  });

  it("repeats and reprojects identical scope content without sampling Date.now", () => {
    const clock = vi.spyOn(Date, "now").mockImplementation(() => {
      throw new Error("qualification must not sample the clock");
    });
    const source = qualificationSource();
    const first = safeCompileReadinessQualification(source);

    for (const value of [source, first, JSON.parse(JSON.stringify(first)), qualificationSource()]) {
      const result = safeCompileReadinessQualification(value);
      expect(result).toStrictEqual(expectedQualification);
      expect(Object.isFrozen(result)).toBe(true);
    }
    expect(clock).not.toHaveBeenCalled();
  });

  it("contains only bounded negative scope, never admission or positive compilation proof", () => {
    const result = safeCompileReadinessQualification(qualificationSource());

    expect(result).toStrictEqual(expectedQualification);
    expect(Object.values(result!)).not.toContain(true);
    expect(result?.shader_asset_readiness).toBe("unknown");
    expect(result?.console_history_complete).toBe(false);
    expect(result?.console_history_durable).toBe(false);
    expect(result?.explicit_compilation_evidence)
      .toBe("inspect_compile_barrier_not_inferred_from_ready_clean");
    for (const field of ["is_ready", "admitted", "dynamic_csharp_ready", "loaded_dll", "shader_ready", "console_backfill"]) {
      expect(result).not.toHaveProperty(field);
    }
  });

  it.each([
    ["ready_clean", false, 0, 0],
    ["compile_errors", true, 3, 2],
  ] as const)("leaves surrounding %s status, errors, and counts untouched", (status, hasErrors, errors, warnings) => {
    const source = Object.freeze({
      status,
      ready: status === "ready_clean",
      has_compile_errors: hasErrors,
      compile_error_count: errors,
      compile_warning_count: warnings,
      readiness_qualification: Object.freeze(qualificationSource()),
    });
    const before = JSON.stringify(source);

    expect(safeCompileReadinessQualification(source.readiness_qualification)).toStrictEqual(expectedQualification);
    // This helper projects the qualification itself, not the enclosing readiness DTO.
    expect(safeCompileReadinessQualification(source)).toBeUndefined();
    expect(JSON.stringify(source)).toBe(before);
  });

  it("does not synthesize qualification for legacy or malformed enclosing readiness results", () => {
    for (const source of [
      { status: "ready_clean", compile_error_count: 0 },
      { status: "ready_clean", compile_error_count: 0, readiness_qualification: {} },
      { status: "compile_errors", compile_error_count: 3, readiness_qualification: null },
    ]) {
      const before = JSON.stringify(source);
      const qualification = "readiness_qualification" in source ? source.readiness_qualification : undefined;

      expect(safeCompileReadinessQualification(qualification)).toBeUndefined();
      expect(JSON.stringify(source)).toBe(before);
    }
  });
});

describe("compile-readiness qualification strict source validation", () => {
  it.each(invalidContainers)("rejects %s rather than manufacturing scope metadata", (_name, value) => {
    expect(safeCompileReadinessQualification(value)).toBeUndefined();
  });

  it.each(alteredFields)("rejects %s", (_name, field, value) => {
    expect(safeCompileReadinessQualification({ ...qualificationSource(), [field]: value })).toBeUndefined();
  });

  it.each(qualificationKeys)("rejects a missing own %s field", (key) => {
    const source = qualificationSource();
    delete source[key];

    expect(safeCompileReadinessQualification(source)).toBeUndefined();
  });

  it("rejects every source getter without invoking it, even if it would return the expected value", () => {
    for (const key of qualificationKeys) {
      const getter = vi.fn(() => expectedQualification[key as keyof typeof expectedQualification]);
      const source = qualificationSource();
      Object.defineProperty(source, key, { enumerable: true, configurable: true, get: getter });

      expect(safeCompileReadinessQualification(source)).toBeUndefined();
      expect(getter).not.toHaveBeenCalled();
    }
  });

  it("rejects throwing own and extra getters without invoking either", () => {
    const getter = vi.fn(() => { throw new Error("source accessor must not execute"); });
    for (const key of ["schema_version", "extra"]) {
      const source = qualificationSource();
      Object.defineProperty(source, key, { enumerable: true, get: getter });

      expect(safeCompileReadinessQualification(source)).toBeUndefined();
    }
    expect(getter).not.toHaveBeenCalled();
  });

  it("rejects setter-only fields without invoking the setter", () => {
    const setter = vi.fn();
    const source = qualificationSource();
    Object.defineProperty(source, "shader_asset_readiness", { enumerable: true, set: setter });

    expect(safeCompileReadinessQualification(source)).toBeUndefined();
    expect(setter).not.toHaveBeenCalled();
  });

  it("rejects each non-enumerable required field rather than accepting hidden metadata", () => {
    for (const key of qualificationKeys) {
      const source = qualificationSource();
      Object.defineProperty(source, key, { enumerable: false });

      expect(safeCompileReadinessQualification(source)).toBeUndefined();
    }
  });

  it("rejects inherited required fields and exact own fields on a custom prototype", () => {
    expect(safeCompileReadinessQualification(Object.create(expectedQualification))).toBeUndefined();
    const source = Object.assign(Object.create({ inherited_extra: "not JSON own data" }), qualificationSource());

    expect(safeCompileReadinessQualification(source)).toBeUndefined();
  });

  it("rejects inherited source accessors without invoking them", () => {
    const getter = vi.fn(() => { throw new Error("inherited accessor must not execute"); });
    const prototype = Object.defineProperty({}, "schema_version", { enumerable: true, get: getter });
    const source = Object.create(prototype, Object.getOwnPropertyDescriptors(qualificationSource()));
    delete source.schema_version;

    expect(safeCompileReadinessQualification(source)).toBeUndefined();
    expect(getter).not.toHaveBeenCalled();
  });

  it.each([
    ["an undefined extra field", "extra", undefined],
    ["a positive readiness flag", "is_ready", true],
    ["a boolean diagnostic count", "compile_error_count", true],
  ] as [string, string, unknown][])("rejects %s even if the eight qualification fields match", (_name, key, value) => {
    expect(safeCompileReadinessQualification({ ...qualificationSource(), [key]: value })).toBeUndefined();
  });

  it("rejects non-enumerable extras on both ordinary and null-prototype containers", () => {
    for (const source of [qualificationSource(), Object.assign(Object.create(null), qualificationSource())]) {
      Object.defineProperty(source, "extra", { value: undefined, enumerable: false });

      expect(safeCompileReadinessQualification(source)).toBeUndefined();
    }
  });

  it("rejects enumerable and non-enumerable symbol keys", () => {
    for (const enumerable of [false, true]) {
      const source = qualificationSource();
      Object.defineProperty(source, Symbol("extra"), { value: undefined, enumerable });

      expect(safeCompileReadinessQualification(source)).toBeUndefined();
    }
  });

  it("rejects non-JSON, nested, and boxed field values without coercion", () => {
    const coerce = vi.fn(() => "unknown");
    const values: unknown[] = [
      undefined, null, Number.NaN, Number.POSITIVE_INFINITY, 1n, Symbol("unknown"),
      () => "unknown", [], {}, new String("unknown"), { [Symbol.toPrimitive]: coerce },
    ];
    for (const value of values) {
      expect(safeCompileReadinessQualification({ ...qualificationSource(), shader_asset_readiness: value }))
        .toBeUndefined();
    }
    expect(coerce).not.toHaveBeenCalled();
  });

  it("does not invoke a toJSON hook to normalize or repair a malformed source", () => {
    const toJSON = vi.fn(qualificationSource);
    const source = { ...qualificationSource(), toJSON };

    expect(safeCompileReadinessQualification(source)).toBeUndefined();
    expect(safeCompileReadinessQualification({ toJSON })).toBeUndefined();
    expect(toJSON).not.toHaveBeenCalled();
  });
});
