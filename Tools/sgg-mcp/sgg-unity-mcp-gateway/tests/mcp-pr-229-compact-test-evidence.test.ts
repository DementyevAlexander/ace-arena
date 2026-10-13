import { readFileSync } from "node:fs";

import { describe, expect, it } from "vitest";

import { compactNonPassingEvidence } from "../src/mcp/compact-test-evidence.js";

type Data = Record<string, unknown>;

const sourceFields = ["non_passing_evidence_status", "non_passing_tests_truncated", "non_passing_tests"];
const diagnosticLimit = 4;
const scanLimit = 64;

function entry(identity = "Fixture.Case", patch: Data = {}): Data {
  return {
    full_name: identity,
    unique_name: identity,
    assembly: "Fixture.Editor",
    raw_status: "Ignored",
    message: "ok",
    ...patch,
  };
}

function source(entries: unknown = [entry()], patch: Data = {}): Data {
  return {
    non_passing_evidence_status: "complete",
    non_passing_tests_truncated: false,
    non_passing_tests: entries,
    ...patch,
  };
}

function diagnostic(identity = "Fixture.Case", patch: Data = {}): Data {
  return { test: identity, assembly: "Fixture.Editor", raw_status: "Ignored", message: "ok", ...patch };
}

function without(value: Data, ...keys: string[]): Data {
  const result = { ...value };
  for (const key of keys) delete result[key];
  return result;
}

function expectedProjection(
  diagnostics: Data[],
  status = "complete",
  displayTruncated = false,
  upstreamTruncated: boolean | null = false,
): Data {
  return {
    non_passing_evidence_status: status,
    ...(upstreamTruncated === null ? {} : { non_passing_tests_truncated: upstreamTruncated }),
    non_passing_diagnostics: diagnostics,
    non_passing_diagnostics_truncated: displayTruncated,
  };
}

function diagnostics(output: Data): Data[] {
  expect(Array.isArray(output.non_passing_diagnostics)).toBe(true);
  return output.non_passing_diagnostics as Data[];
}

function wellFormedUtf16(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const unit = value.charCodeAt(index);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) {
      return false;
    }
  }
  return true;
}

function expectClippedText(actual: unknown, original: string, limit: number): void {
  expect(typeof actual).toBe("string");
  const text = actual as string;
  expect(text.length).toBeLessThanOrEqual(limit);
  expect(wellFormedUtf16(text)).toBe(true);
  // Preserve recognizable source text without prescribing an ellipsis glyph.
  expect(text.startsWith(original.slice(0, 32))).toBe(true);
}

function freezeJson(value: unknown): void {
  if (value === null || typeof value !== "object") return;
  for (const child of Object.values(value)) freezeJson(child);
  Object.freeze(value);
}

// Source-local pure projection fixtures: no Unity run, historical receipt,
// execution, artifact access, or reported-cause/recovery claim is involved.
describe("MCP-PR-229 compact non-passing evidence (portable pure projection)", () => {
  it("preserves legacy absence and ignores inherited upstream headers", () => {
    expect(compactNonPassingEvidence({})).toEqual({});
    expect(compactNonPassingEvidence({
      total: 20, passed: 0, failed: 0, skipped: 20, success: true,
      failed_tests: [], failure_summary: { failed: 0 }, artifact_refs: ["existing.json"],
    })).toEqual({});

    const inherited = Object.create(source()) as Data;
    inherited.total = 20;
    expect(compactNonPassingEvidence(inherited)).toEqual({});
    const ownStatus = Object.assign(Object.create(source()), { non_passing_evidence_status: "partial" }) as Data;
    expect(compactNonPassingEvidence(ownStatus)).toEqual(expectedProjection([], "partial", true, null));
  });

  it("preserves the four source witnesses and marks missing or invalid status unavailable", () => {
    for (const status of ["complete", "partial", "truncated", "unavailable"]) {
      expect(compactNonPassingEvidence(source([], { non_passing_evidence_status: status })))
        .toEqual(expectedProjection([], status, status === "truncated"));
    }

    const invalidStatuses: unknown[] = [undefined, null, "", "Complete", "partial ", "unknown", false, 1, [], {}];
    for (const status of invalidStatuses) {
      expect(compactNonPassingEvidence(source([entry()], { non_passing_evidence_status: status })))
        .toEqual(expectedProjection([diagnostic()], "unavailable", true));
    }
    expect(compactNonPassingEvidence(without(source(), "non_passing_evidence_status")))
      .toEqual(expectedProjection([diagnostic()], "unavailable", true));
  });

  it("copies only a valid upstream Boolean without coercion or promoting partial coverage", () => {
    for (const flag of [false, true]) {
      expect(compactNonPassingEvidence(source([entry()], {
        non_passing_evidence_status: "partial", non_passing_tests_truncated: flag,
      }))).toEqual(expectedProjection([diagnostic()], "partial", flag, flag));
    }

    for (const flag of [undefined, null, 0, 1, "false", "true", [], {}]) {
      const result = compactNonPassingEvidence(source([entry()], { non_passing_tests_truncated: flag }));
      expect(result).toEqual(expectedProjection([diagnostic()], "complete", true, null));
      expect(Object.hasOwn(result, "non_passing_tests_truncated")).toBe(false);
    }
    expect(compactNonPassingEvidence(without(source(), "non_passing_tests_truncated")))
      .toEqual(expectedProjection([diagnostic()], "complete", true, null));
    expect(compactNonPassingEvidence({ non_passing_tests_truncated: false }))
      .toEqual(expectedProjection([], "unavailable", true));
  });

  it("marks absent or non-array source records as display loss without changing a valid witness", () => {
    expect(compactNonPassingEvidence(without(source(), "non_passing_tests")))
      .toEqual(expectedProjection([], "complete", true));
    expect(compactNonPassingEvidence({ non_passing_evidence_status: "complete" }))
      .toEqual(expectedProjection([], "complete", true, null));

    for (const value of [undefined, null, false, 17, "[]", {}, { 0: entry(), length: 1 }]) {
      expect(compactNonPassingEvidence(source([], { non_passing_tests: value })))
        .toEqual(expectedProjection([], "complete", true));
      expect(compactNonPassingEvidence({ non_passing_tests: value }))
        .toEqual(expectedProjection([], "unavailable", true, null));
    }
    expect(compactNonPassingEvidence({ non_passing_tests: [] }))
      .toEqual(expectedProjection([], "unavailable", true, null));
  });

  it("accepts only the six exact non-passing heads and preserves the entire raw status", () => {
    for (const head of ["Failed", "Failure", "Error", "Inconclusive", "Skipped", "Ignored"]) {
      const suffixed = `${head}:reason \"quoted\"\\path 雪:Passed`;
      const rows = [entry(`${head}.Bare`, { raw_status: head }), entry(`${head}.Qualified`, { raw_status: suffixed })];
      expect(compactNonPassingEvidence(source(rows))).toEqual(expectedProjection([
        diagnostic(`${head}.Bare`, { raw_status: head }),
        diagnostic(`${head}.Qualified`, { raw_status: suffixed }),
      ]));
    }
  });

  it("rejects passed, suite, lookalike, wrong-type and malformed raw statuses and keeps later valid rows", () => {
    const invalidStatuses: unknown[] = [
      undefined, null, 17, true, [], {}, new String("Failed"),
      "", "Passed", "Passed:Failed", "Suite", "Suite:Failed", "NotFailed", "Failedish", "Failureish",
      "Errorish", "Skippedish", "Ignoredish", "Inconclusiveish", "failed", "IGNORED", " Ignored", "Ignored ",
      "prefix:Failed", "Failed\nIgnored", "Ignored:\ud800", "Ignored:\udc00",
    ];
    const rows = invalidStatuses.map((raw_status, index) => entry(`Invalid.Status${index}`, { raw_status }));
    rows.push(entry("Valid.AfterStatuses", { raw_status: "Inconclusive:NoResult" }));
    expect(compactNonPassingEvidence(source(rows))).toEqual(expectedProjection([
      diagnostic("Valid.AfterStatuses", { raw_status: "Inconclusive:NoResult" }),
    ], "complete", true));
  });

  it("selects an observed exact identity without trimming it or inventing companion fields", () => {
    const exactFull = "  Exact.Full  ";
    expect(compactNonPassingEvidence(source([entry(exactFull)])))
      .toEqual(expectedProjection([diagnostic(exactFull)]));

    for (const row of [without(entry("Full.Only"), "unique_name"), entry("Full.Only", { unique_name: undefined })]) {
      expect(compactNonPassingEvidence(source([row])))
        .toEqual(expectedProjection([diagnostic("Full.Only")]));
    }
    for (const row of [without(entry("Unique.Only"), "full_name"), entry("Unique.Only", { full_name: undefined })]) {
      expect(compactNonPassingEvidence(source([row])))
        .toEqual(expectedProjection([diagnostic("Unique.Only")]));
    }
    expect(compactNonPassingEvidence(source([entry("Full.Exact", { unique_name: "Unique.Exact" })])))
      .toEqual(expectedProjection([diagnostic("Full.Exact", { unique_name: "Unique.Exact" })]));
    expect(compactNonPassingEvidence(source([entry(" \t ", { unique_name: "  Unique.Exact  " })])))
      .toEqual(expectedProjection([diagnostic("  Unique.Exact  ")], "complete", true));
    expect(compactNonPassingEvidence(source([entry("Full.Exact", { unique_name: " \t " })])))
      .toEqual(expectedProjection([diagnostic("Full.Exact")], "complete", true));

    for (const row of [without(entry(), "assembly"), entry("Fixture.Case", { assembly: "" })]) {
      expect(compactNonPassingEvidence(source([row])))
        .toEqual(expectedProjection([without(diagnostic(), "assembly")]));
    }
    expect(compactNonPassingEvidence(source([entry("Fixture.Case", { assembly: "  " })])))
      .toEqual(expectedProjection([diagnostic("Fixture.Case", { assembly: "  " })]));
  });

  it("admits exact identity, assembly and raw-status limits but does not clip over-limit rows into validity", () => {
    const full = "F".repeat(2048);
    const unique = "U".repeat(2048);
    const assembly = "A".repeat(256);
    const rawStatus = `Skipped:${"s".repeat(112)}`;
    const output = compactNonPassingEvidence(source([entry(full, { unique_name: unique, assembly, raw_status: rawStatus })]));
    const projected = diagnostics(output);
    expect(projected).toHaveLength(1);
    const row = projected[0]!;
    expect(Object.keys(row).sort()).toEqual(["assembly", "message", "raw_status", "test", "unique_name"]);
    expectClippedText(row.test, full, 160);
    expectClippedText(row.unique_name, unique, 160);
    expectClippedText(row.assembly, assembly, 160);
    expect(row.raw_status).toBe(rawStatus);
    expect(row.message).toBe("ok");
    expect(output).toEqual(expectedProjection(projected, "complete", true));

    for (const patch of [
      { full_name: "F".repeat(2049) }, { unique_name: "U".repeat(2049) },
      { assembly: "A".repeat(257) }, { raw_status: `Skipped:${"s".repeat(113)}` },
    ]) {
      expect(compactNonPassingEvidence(source([entry("Invalid.Boundary", patch), entry("Valid.Sibling")])))
        .toEqual(expectedProjection([diagnostic("Valid.Sibling")], "complete", true));
    }
  });

  it("skips malformed records and identity or assembly faults instead of breaking or falling back to Name", () => {
    const malformedRows: unknown[] = [
      undefined, null, false, 17, "Fixture.NotAnObject", [], Object.assign([], entry("Array.WithProperties")),
      { name: "Name.MustNotBecomeIdentity", raw_status: "Ignored" },
      { Name: "Name.MustNotBecomeIdentity", raw_status: "Ignored" },
      without(entry(), "full_name", "unique_name"),
      entry("", { unique_name: "" }), entry(" \t ", { unique_name: "\r\n" }),
    ];
    for (const field of ["full_name", "unique_name"]) {
      for (const value of [null, false, 17, [], {}, new String("Looks.Exact"), "bad\ud800", "bad\udc00", `${"valid".repeat(40)}\ud800`]) {
        malformedRows.push(entry("Other.Valid.Identity", { [field]: value }));
      }
    }
    // Only identity companions allow provided undefined; assembly must either
    // be absent or be an exact, valid string.
    for (const assembly of [null, false, 17, [], {}, "bad\ud800", "bad\udc00", `${"a".repeat(200)}\ud800`, undefined]) {
      malformedRows.push(entry("Valid.Identity.BadAssembly", { assembly }));
    }
    for (const malformed of malformedRows) {
      expect(compactNonPassingEvidence(source([malformed, entry("Valid.AfterMalformed")])))
        .toEqual(expectedProjection([diagnostic("Valid.AfterMalformed")], "complete", true));
    }
    expect(compactNonPassingEvidence(source([
      null, entry("Valid.One"), { raw_status: "Ignored" }, entry("Valid.Two"), [], entry("Valid.Three"),
    ]))).toEqual(expectedProjection([
      diagnostic("Valid.One"), diagnostic("Valid.Two"), diagnostic("Valid.Three"),
    ], "complete", true));
  });

  it("omits only a malformed message and validates only its bounded retained prefix", () => {
    expect(compactNonPassingEvidence(source([without(entry(), "message")])))
      .toEqual(expectedProjection([without(diagnostic(), "message")]));
    for (const message of [undefined, null, false, 17, [], {}, "bad\ud800", "bad\udc00", `bad\ud800${"m".repeat(300)}`]) {
      expect(compactNonPassingEvidence(source([entry("Retained.Identity", { message })])))
        .toEqual(expectedProjection([without(diagnostic("Retained.Identity"), "message")], "complete", true));
    }
    for (const message of ["m".repeat(240), `${"m".repeat(238)}🚀`]) {
      expect(compactNonPassingEvidence(source([entry("Fixture.Case", { message })])))
        .toEqual(expectedProjection([diagnostic("Fixture.Case", { message })]));
    }
    for (const message of [`${"m".repeat(239)}🚀tail`, `${"m".repeat(240)}${"\ud800".repeat(10000)}`]) {
      const output = compactNonPassingEvidence(source([entry("Retained.Identity", { message })]));
      const projected = diagnostics(output);
      expect(projected).toHaveLength(1);
      expectClippedText(projected[0]!.message, message, 240);
      expect(without(projected[0]!, "message")).toEqual(without(diagnostic("Retained.Identity"), "message"));
      expect(output).toEqual(expectedProjection(projected, "complete", true));
    }
  });

  it("keeps exactly four short records without loss and flags a fifth without changing the source witness", () => {
    const rows = Array.from({ length: diagnosticLimit + 1 }, (_, index) => entry(`Capacity.Case${index}`));
    const kept = rows.slice(0, diagnosticLimit).map((_, index) => diagnostic(`Capacity.Case${index}`));
    expect(compactNonPassingEvidence(source(rows.slice(0, diagnosticLimit))))
      .toEqual(expectedProjection(kept));
    expect(compactNonPassingEvidence(source(rows))).toEqual(expectedProjection(kept, "complete", true));
    expect(compactNonPassingEvidence(source(rows, { non_passing_evidence_status: "partial" })))
      .toEqual(expectedProjection(kept, "partial", true));
  });

  it("deduplicates the collision-free exact raw tuple before display trimming or capacity", () => {
    const base = entry("Full.Base", { unique_name: "Unique.Base", assembly: "Assembly.Base", raw_status: "Skipped:First" });
    const baseDiagnostic = diagnostic("Full.Base", { unique_name: "Unique.Base", assembly: "Assembly.Base", raw_status: "Skipped:First" });
    const changes: [Data, Data][] = [
      [{ assembly: "Assembly.Other" }, { assembly: "Assembly.Other" }],
      [{ full_name: "Full.Other" }, { test: "Full.Other" }],
      [{ unique_name: "Unique.Other" }, { unique_name: "Unique.Other" }],
      [{ raw_status: "Skipped:Second" }, { raw_status: "Skipped:Second" }],
    ];
    for (const [rowPatch, diagnosticPatch] of changes) {
      expect(compactNonPassingEvidence(source([base, { ...base, ...rowPatch }, { ...base, message: "not part of identity" }])))
        .toEqual(expectedProjection([baseDiagnostic, { ...baseDiagnostic, ...diagnosticPatch }]));
    }

    expect(compactNonPassingEvidence(source([
      entry("C", { assembly: "A|B", unique_name: "U" }),
      entry("B|C", { assembly: "A", unique_name: "U" }),
    ]))).toEqual(expectedProjection([
      diagnostic("C", { assembly: "A|B", unique_name: "U" }),
      diagnostic("B|C", { assembly: "A", unique_name: "U" }),
    ]));

    const prefix = "n".repeat(160);
    const clipped = compactNonPassingEvidence(source([
      entry(`${prefix}.First`, { unique_name: "" }), entry(`${prefix}.Second`, { unique_name: "" }),
    ]));
    const distinct = diagnostics(clipped);
    expect(distinct).toHaveLength(2);
    for (const row of distinct) {
      expectClippedText(row.test, prefix, 160);
      expect(Object.hasOwn(row, "unique_name")).toBe(false);
    }
    expect(clipped).toEqual(expectedProjection(distinct, "complete", true));

    const differentRawUnique = compactNonPassingEvidence(source([
      entry(`${prefix}.Full`, { unique_name: `${prefix}.Unique` }),
    ]));
    const uniqueDisplay = diagnostics(differentRawUnique);
    expect(uniqueDisplay).toHaveLength(1);
    expectClippedText(uniqueDisplay[0]!.test, `${prefix}.Full`, 160);
    expectClippedText(uniqueDisplay[0]!.unique_name, `${prefix}.Unique`, 160);
    expect(differentRawUnique).toEqual(expectedProjection(uniqueDisplay, "complete", true));

    const whitespaceTuples = diagnostics(compactNonPassingEvidence(source([
      entry(" ", { unique_name: "Observed.Unique" }), entry("\t", { unique_name: "Observed.Unique" }),
    ])));
    expect(whitespaceTuples).toHaveLength(2);
    expect(whitespaceTuples.map((row) => row.test)).toEqual(["Observed.Unique", "Observed.Unique"]);
  });

  it("does not treat duplicates after a full retained store as additional capacity loss", () => {
    const rows = Array.from({ length: diagnosticLimit }, (_, index) => entry(`Retained.Case${index}`));
    const kept = rows.map((_, index) => diagnostic(`Retained.Case${index}`));
    expect(compactNonPassingEvidence(source([...rows, ...rows, ...rows])))
      .toEqual(expectedProjection(kept));
    const repeated = Array.from({ length: scanLimit }, () => entry("Only.One"));
    expect(compactNonPassingEvidence(source(repeated))).toEqual(expectedProjection([diagnostic("Only.One")]));
    expect(compactNonPassingEvidence(source([...repeated, entry("Only.One")])))
      .toEqual(expectedProjection([diagnostic("Only.One")], "complete", true));
  });

  it("scans row 64 but never row 65 and marks all uninspected tail records as display loss", () => {
    const rows = [...Array.from({ length: scanLimit - 1 }, () => entry("First.Identity")), entry("Row64.Identity")];
    const kept = [diagnostic("First.Identity"), diagnostic("Row64.Identity")];
    expect(compactNonPassingEvidence(source(rows))).toEqual(expectedProjection(kept));
    expect(compactNonPassingEvidence(source([...rows, entry("Row65.MustNotAppear")])))
      .toEqual(expectedProjection(kept, "complete", true));
    expect(compactNonPassingEvidence(source([...Array.from({ length: scanLimit }, () => null), entry("No.LookAhead")])))
      .toEqual(expectedProjection([], "complete", true));

    let tailReads = 0;
    const guarded = [...rows, entry("Uninspected.Tail")];
    Object.defineProperty(guarded, scanLimit, {
      enumerable: true,
      get: () => { tailReads++; throw new Error("synthetic read beyond the 64-row scan limit"); },
    });
    Object.freeze(guarded);
    expect(compactNonPassingEvidence(source(guarded))).toEqual(expectedProjection(kept, "complete", true));
    expect(tailReads).toBe(0);
  });

  it("clips diagnostic text without splitting surrogate pairs or altering raw status", () => {
    const full = `${"F".repeat(159)}🚀tail`;
    const unique = `${"U".repeat(159)}🚀tail`;
    const assembly = `${"A".repeat(159)}🚀tail`;
    const message = `${"M".repeat(239)}🚀tail`;
    const rawStatus = "Inconclusive:🚀:exact 雪";
    const output = compactNonPassingEvidence(source([
      entry(full, { unique_name: unique, assembly, message, raw_status: rawStatus }),
    ], { non_passing_evidence_status: "partial" }));
    const projected = diagnostics(output);
    expect(projected).toHaveLength(1);
    const row = projected[0]!;
    expectClippedText(row.test, full, 160);
    expectClippedText(row.unique_name, unique, 160);
    expectClippedText(row.assembly, assembly, 160);
    expectClippedText(row.message, message, 240);
    expect(row.raw_status).toBe(rawStatus);
    expect(output).toEqual(expectedProjection(projected, "partial", true));

    const fittingFull = `${"F".repeat(158)}🚀`;
    const fittingUnique = `${"U".repeat(158)}🚀`;
    const fittingAssembly = `${"A".repeat(158)}🚀`;
    const fittingMessage = `${"M".repeat(238)}🚀`;
    expect(compactNonPassingEvidence(source([entry(fittingFull, {
      unique_name: fittingUnique, assembly: fittingAssembly, message: fittingMessage, raw_status: rawStatus,
    })]))).toEqual(expectedProjection([diagnostic(fittingFull, {
      unique_name: fittingUnique, assembly: fittingAssembly, message: fittingMessage, raw_status: rawStatus,
    })]));
  });

  it("leaves frozen canonical inputs untouched and projects no unrelated data, actions or recovery metadata", () => {
    const input = source([entry("Fixture.Case", {
      name: "Not.Authorized.Identity", stack_trace: "must-not-project-stack", outcome: "Passed",
      success: false, total: 999, actions: ["must-not-project-action"], next_valid_actions: ["must-not-project-next-action"],
      recovery_hint: "must-not-project-recovery", artifact_refs: ["must-not-project-reference"],
    })], {
      total: 20, passed: 0, failed: 0, skipped: 20, success: true,
      failed_tests: [{ test: "existing.failed.diagnostic" }], failure_summary: { failed: 0 },
      artifact_refs: ["existing-artifact.json"], next_valid_actions: ["existing-action"],
      lifecycle: "completed", result: "Passed", operation_id: "unchanged-owner", execute_dispatch_count: 1,
      write_replay_allowed: false, filter: "must-not-invent-filter-identity", extra: { nested: ["unchanged"] },
    });
    const canonicalClone = JSON.parse(JSON.stringify(input)) as Data;
    freezeJson(input);
    expect(Object.isFrozen(input)).toBe(true);
    expect(Object.isFrozen(input.non_passing_tests)).toBe(true);
    expect(Object.isFrozen((input.non_passing_tests as Data[])[0])).toBe(true);
    expect(compactNonPassingEvidence(input)).toEqual(expectedProjection([diagnostic()]));
    expect(compactNonPassingEvidence(canonicalClone)).toEqual(expectedProjection([diagnostic()]));
    expect(input).toEqual(canonicalClone);

    const nullPrototype = Object.assign(Object.create(null), source([Object.assign(Object.create(null), entry())])) as Data;
    expect(compactNonPassingEvidence(nullPrototype)).toEqual(expectedProjection([diagnostic()]));
    const inheritedRow = Object.assign(Object.create({
      full_name: "Inherited.MustNotSupplyIdentity", assembly: "Inherited.Assembly", message: "Inherited.Message",
    }), { unique_name: "Own.Unique", raw_status: "Ignored" }) as Data;
    expect(compactNonPassingEvidence(source([inheritedRow])))
      .toEqual(expectedProjection([{ test: "Own.Unique", raw_status: "Ignored" }]));
    expect(sourceFields.every((key) => Object.hasOwn(input, key))).toBe(true);
  });

  const projectedSource = (rows: unknown = [diagnostic()], patch: Data = {}): Data => ({
    response_mode: "compact",
    non_passing_evidence_status: "complete",
    non_passing_tests_truncated: false,
    non_passing_diagnostics: rows,
    non_passing_diagnostics_truncated: false,
    ...patch,
  });

  it("reprojects compact evidence idempotently without deduplicating collisions of clipped display identities", () => {
    const prefix = "p".repeat(160);
    for (const longIdentities of [false, true]) {
      const rows = Array.from({ length: diagnosticLimit }, (_, index) => longIdentities
        ? entry(`${prefix}.Exact${index}`, { unique_name: `${prefix}.Unique${index}` })
        : entry(`Idempotent.Case${index}`));
      const first = compactNonPassingEvidence(source(rows));
      const firstDiagnostics = diagnostics(first);
      expect(firstDiagnostics).toHaveLength(diagnosticLimit);
      if (longIdentities) {
        for (const row of firstDiagnostics) {
          expectClippedText(row.test, prefix, 160);
          expectClippedText(row.unique_name, prefix, 160);
        }
        // All four presentations are identical, but their original exact
        // identities differ. The outer delivery must preserve every record.
        expect(new Set(firstDiagnostics.map((row) => JSON.stringify(row))).size).toBe(1);
      } else {
        expect(firstDiagnostics).toEqual(rows.map((_, index) => diagnostic(`Idempotent.Case${index}`)));
      }
      expect(first).toEqual(expectedProjection(firstDiagnostics, "complete", longIdentities));

      const delivery: Data = { ...first, response_mode: "compact" };
      const snapshot = JSON.parse(JSON.stringify(delivery)) as Data;
      freezeJson(delivery);
      expect(Object.isFrozen(delivery.non_passing_diagnostics)).toBe(true);
      const second = compactNonPassingEvidence(delivery);
      expect(second).toEqual(first);
      expect(diagnostics(second)).toHaveLength(diagnosticLimit);
      expect(delivery).toEqual(snapshot);
      expect(compactNonPassingEvidence({ ...second, response_mode: "compact" })).toEqual(first);
    }
  });

  it("bounds and validates already-projected rows, skips faults, and strips unrelated or inherited display fields", () => {
    const inheritedExtras = Object.assign(Object.create({
      unique_name: "Inherited.Unique", assembly: "Inherited.Assembly", message: "Inherited.Message",
    }), {
      test: "Own.Display", raw_status: "Ignored:Platform", stack_trace: "must-not-project-stack",
      actions: ["must-not-project-action"], next_valid_actions: ["must-not-project-next-action"],
      success: false, total: 999, recovery_hint: "must-not-project-recovery",
    }) as Data;
    Object.freeze(inheritedExtras);
    const cleanOwn = { test: "Own.Display", raw_status: "Ignored:Platform" };
    expect(compactNonPassingEvidence(projectedSource([inheritedExtras])))
      .toEqual(expectedProjection([cleanOwn]));

    for (const head of ["Failed", "Failure", "Error", "Inconclusive", "Skipped", "Ignored"]) {
      const row = diagnostic("Projected.Valid", { raw_status: `${head}:exact 🚀` });
      expect(compactNonPassingEvidence(projectedSource([row]))).toEqual(expectedProjection([row]));
    }
    const malformed: unknown[] = [
      undefined, null, false, 17, "not a display row", [],
      without(diagnostic(), "test"), without(diagnostic(), "raw_status"),
      { full_name: "Not.A.Display.Identity", unique_name: "No.Fallback", raw_status: "Ignored" },
      Object.assign(Object.create({ test: "Inherited.Identity" }), { raw_status: "Ignored" }),
    ];
    for (const test of [undefined, null, false, "", " \t ", "t".repeat(161), "bad\ud800", "bad\udc00"]) {
      malformed.push(diagnostic("Invalid.Test", { test }));
    }
    for (const raw_status of [undefined, null, false, 17, [], {}, "", "Passed", "Suite:Failed", "NotFailed", "Ignoredish", "prefix:Ignored", "Skipped:" + "s".repeat(113), "Ignored:\ud800"]) {
      malformed.push(diagnostic("Invalid.Status", { raw_status }));
    }
    for (const field of ["unique_name", "assembly"]) {
      for (const value of [undefined, null, false, 17, "x".repeat(161), "bad\ud800", "bad\udc00"]) {
        malformed.push(diagnostic("Invalid.Companion", { [field]: value }));
      }
    }
    for (const row of malformed) {
      expect(compactNonPassingEvidence(projectedSource([row, diagnostic("Valid.AfterDisplayFault")])))
        .toEqual(expectedProjection([diagnostic("Valid.AfterDisplayFault")], "complete", true));
    }
    for (const rows of [undefined, null, false, 17, "[]", {}]) {
      expect(compactNonPassingEvidence(projectedSource([], { non_passing_diagnostics: rows })))
        .toEqual(expectedProjection([], "complete", true));
    }

    for (const message of [undefined, null, false, 17, "bad\ud800", `bad\udc00${"m".repeat(300)}`]) {
      expect(compactNonPassingEvidence(projectedSource([diagnostic("Retained.Display", { message })])))
        .toEqual(expectedProjection([without(diagnostic("Retained.Display"), "message")], "complete", true));
    }
    for (const message of [`${"m".repeat(239)}🚀tail`, `${"m".repeat(240)}\ud800`]) {
      const output = compactNonPassingEvidence(projectedSource([diagnostic("Retained.Display", { message })]));
      const kept = diagnostics(output);
      expect(kept).toHaveLength(1);
      expectClippedText(kept[0]!.message, message, 240);
      expect(without(kept[0]!, "message")).toEqual(without(diagnostic("Retained.Display"), "message"));
      expect(output).toEqual(expectedProjection(kept, "complete", true));
    }

    const fitting = diagnostic("t".repeat(160), {
      unique_name: "u".repeat(160), assembly: "a".repeat(160),
      raw_status: "Skipped:" + "s".repeat(112), message: "m".repeat(240),
    });
    expect(compactNonPassingEvidence(projectedSource([fitting]))).toEqual(expectedProjection([fitting]));
    const rows = Array.from({ length: diagnosticLimit + 1 }, (_, index) => diagnostic(`Display.Case${index}`));
    const kept = rows.slice(0, diagnosticLimit);
    const frozen = projectedSource(rows, { success: true, total: 20, next_valid_actions: ["unchanged"] });
    const snapshot = JSON.parse(JSON.stringify(frozen)) as Data;
    freezeJson(frozen);
    expect(compactNonPassingEvidence(frozen)).toEqual(expectedProjection(kept, "complete", true));
    expect(frozen).toEqual(snapshot);
    expect(compactNonPassingEvidence(projectedSource([
      null, diagnostic("Within.One"), false, diagnostic("Within.Two"), diagnostic("Outside.MustNotFillCapacity"),
    ]))).toEqual(expectedProjection([diagnostic("Within.One"), diagnostic("Within.Two")], "complete", true));

    let tailReads = 0;
    const guarded = [...rows];
    Object.defineProperty(guarded, diagnosticLimit, {
      enumerable: true,
      get: () => { tailReads++; throw new Error("synthetic read beyond four already-projected rows"); },
    });
    Object.freeze(guarded);
    expect(compactNonPassingEvidence(projectedSource(guarded)))
      .toEqual(expectedProjection(kept, "complete", true));
    expect(tailReads).toBe(0);
  });

  it("always prefers an own canonical array over poisoned compact display data", () => {
    const canonical = source([entry("Canonical.Observed")], {
      response_mode: "compact", non_passing_diagnostics_truncated: true,
      non_passing_diagnostics: [diagnostic("Poisoned.Display", { raw_status: "Passed", actions: ["must-not-project"] })],
    });
    const snapshot = JSON.parse(JSON.stringify(canonical)) as Data;
    freezeJson(canonical);
    expect(compactNonPassingEvidence(canonical)).toEqual(expectedProjection([diagnostic("Canonical.Observed")]));
    expect(canonical).toEqual(snapshot);

    for (const value of [undefined, null, false, "not an array"]) {
      expect(compactNonPassingEvidence({ ...canonical, non_passing_tests: value }))
        .toEqual(expectedProjection([], "complete", true));
    }
    expect(compactNonPassingEvidence({ ...canonical, non_passing_tests: [] }))
      .toEqual(expectedProjection([]));

    let poisonedReads = 0;
    const guarded = source([entry("Canonical.Guarded")], { response_mode: "compact", non_passing_diagnostics_truncated: true });
    freezeJson(guarded.non_passing_tests);
    Object.defineProperty(guarded, "non_passing_diagnostics", {
      enumerable: true,
      get: () => { poisonedReads++; throw new Error("canonical data must not inspect the display fallback"); },
    });
    Object.freeze(guarded);
    expect(compactNonPassingEvidence(guarded)).toEqual(expectedProjection([diagnostic("Canonical.Guarded")]));
    expect(poisonedReads).toBe(0);
  });

  it("fails closed on missing or invalid projected witnesses and requires own compact display opt-in fields", () => {
    const rows = [diagnostic("Projected.Witness")];
    const base = projectedSource(rows);
    const snapshot = JSON.parse(JSON.stringify(base)) as Data;
    freezeJson(base);
    for (const status of ["complete", "partial", "truncated", "unavailable"]) {
      for (const sourceFlag of [false, true]) {
        for (const priorDisplayFlag of [false, true]) {
          expect(compactNonPassingEvidence(projectedSource(rows, {
            non_passing_evidence_status: status, non_passing_tests_truncated: sourceFlag,
            non_passing_diagnostics_truncated: priorDisplayFlag,
          }))).toEqual(expectedProjection(rows, status, priorDisplayFlag || sourceFlag || status === "truncated", sourceFlag));
        }
      }
    }
    for (const status of [undefined, null, false, 17, "Complete", "unknown", [], {}]) {
      expect(compactNonPassingEvidence({ ...base, non_passing_evidence_status: status }))
        .toEqual(expectedProjection(rows, "unavailable", true));
    }
    expect(compactNonPassingEvidence(without(base, "non_passing_evidence_status")))
      .toEqual(expectedProjection(rows, "unavailable", true));
    for (const flag of [undefined, null, 0, 1, "false", "true", [], {}]) {
      const output = compactNonPassingEvidence({ ...base, non_passing_tests_truncated: flag });
      expect(output).toEqual(expectedProjection(rows, "complete", true, null));
      expect(Object.hasOwn(output, "non_passing_tests_truncated")).toBe(false);
      expect(compactNonPassingEvidence({ ...base, non_passing_diagnostics_truncated: flag }))
        .toEqual(expectedProjection(rows, "complete", true));
    }
    expect(compactNonPassingEvidence(without(base, "non_passing_tests_truncated")))
      .toEqual(expectedProjection(rows, "complete", true, null));
    expect(compactNonPassingEvidence(without(base, "non_passing_diagnostics_truncated")))
      .toEqual(expectedProjection(rows, "complete", true));
    expect(compactNonPassingEvidence(without(base, ...sourceFields))).toEqual({});

    const inheritedStatus = Object.assign(Object.create({ non_passing_evidence_status: "complete" }), without(base, "non_passing_evidence_status")) as Data;
    expect(compactNonPassingEvidence(inheritedStatus)).toEqual(expectedProjection(rows, "unavailable", true));
    const inheritedFlag = Object.assign(Object.create({ non_passing_tests_truncated: false }), without(base, "non_passing_tests_truncated")) as Data;
    expect(compactNonPassingEvidence(inheritedFlag)).toEqual(expectedProjection(rows, "complete", true, null));
    const inheritedDisplayFlag = Object.assign(Object.create({ non_passing_diagnostics_truncated: false }), without(base, "non_passing_diagnostics_truncated")) as Data;
    expect(compactNonPassingEvidence(inheritedDisplayFlag)).toEqual(expectedProjection(rows, "complete", true));
    const inheritedHeaders = Object.assign(Object.create(source()), without(base, ...sourceFields)) as Data;
    expect(compactNonPassingEvidence(inheritedHeaders)).toEqual({});

    for (const response_mode of [undefined, null, false, "full", "Compact"]) {
      expect(compactNonPassingEvidence({ ...base, response_mode })).toEqual(expectedProjection([], "complete", true));
    }
    const inheritedMode = Object.assign(Object.create({ response_mode: "compact" }), without(base, "response_mode")) as Data;
    expect(compactNonPassingEvidence(inheritedMode)).toEqual(expectedProjection([], "complete", true));
    const inheritedRows = Object.assign(Object.create({ non_passing_diagnostics: rows }), without(base, "non_passing_diagnostics")) as Data;
    expect(compactNonPassingEvidence(inheritedRows)).toEqual(expectedProjection([], "complete", true));
    expect(Object.isFrozen(base.non_passing_diagnostics)).toBe(true);
    expect(base).toEqual(snapshot);
  });

  it("rejects oversized projected identities before whitespace or UTF-16 inspection and bounds header hashing", () => {
    for (const test of [`${" ".repeat(160)}\ud800`, `${"t".repeat(160)}\udc00`]) {
      expect(test.length).toBe(161);
      const input = projectedSource([diagnostic("Oversized.Display", { test }), diagnostic("Bounded.AfterReject")]);
      freezeJson(input);
      expect(compactNonPassingEvidence(input))
        .toEqual(expectedProjection([diagnostic("Bounded.AfterReject")], "complete", true));
    }
    const fitting = diagnostic(`${"t".repeat(158)}🚀`);
    expect(compactNonPassingEvidence(projectedSource([fitting]))).toEqual(expectedProjection([fitting]));
    expect(compactNonPassingEvidence(projectedSource([diagnostic()], {
      non_passing_evidence_status: `complete${"x".repeat(10000)}`,
    }))).toEqual(expectedProjection([diagnostic()], "unavailable", true));

    // Functional rejection alone cannot observe how much primitive string work
    // preceded it. Read just this module's guard order instead of patching any
    // shared String prototype or introducing timing-based bounded-work claims.
    const implementation = readFileSync(new URL("../src/mcp/compact-test-evidence.ts", import.meta.url), "utf8");
    const displayBody = /function reprojectDisplay\b[\s\S]*?(?=\nfunction ownValue\b)/.exec(implementation)?.[0];
    expect(displayBody).toBeDefined();
    const guard = /if\s*\(\s*typeof test\b[\s\S]*?\)\s*\{\s*truncated\s*=\s*true;\s*continue;/.exec(displayBody!)?.[0];
    expect(guard).toBeDefined();
    expect(guard).toMatch(/typeof test\s*!==\s*["']string["']\s*\|\|\s*test\.length\s*>\s*160\s*\|\|\s*!test\.trim\(\)\s*\|\|\s*!validUtf16\(test\)/);
    const lengthCheck = /test\.length\s*>\s*160/.exec(displayBody!);
    const firstTrim = /test\.trim\(\)/.exec(displayBody!);
    const firstUtf16Inspection = /validUtf16\(test\)/.exec(displayBody!);
    expect(lengthCheck).not.toBeNull();
    expect(firstTrim).not.toBeNull();
    expect(firstUtf16Inspection).not.toBeNull();
    expect(lengthCheck!.index).toBeLessThan(firstTrim!.index);
    expect(lengthCheck!.index).toBeLessThan(firstUtf16Inspection!.index);

    const statusGuard = /\bconst status\s*=\s*([^;]+);/.exec(implementation)?.[1];
    expect(statusGuard).toBeDefined();
    expect(statusGuard).toMatch(/typeof sourceStatus\s*===\s*["']string["']\s*&&\s*sourceStatus\.length\s*<=\s*11\s*&&\s*EVIDENCE_STATUSES\.has\(sourceStatus\)/);
    const headerLengthCheck = /sourceStatus\.length\s*<=\s*11/.exec(implementation);
    const firstHeaderHash = /EVIDENCE_STATUSES\.has\(sourceStatus\)/.exec(implementation);
    expect(headerLengthCheck).not.toBeNull();
    expect(firstHeaderHash).not.toBeNull();
    expect(headerLengthCheck!.index).toBeLessThan(firstHeaderHash!.index);
  });
});
