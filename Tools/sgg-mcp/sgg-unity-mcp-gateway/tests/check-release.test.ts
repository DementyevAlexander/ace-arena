import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import {
  createReleaseEvidenceTemplate,
  getReleaseContext,
  PLAYMODE_REVIEW_ACTION,
  RELEASE_CATEGORIES,
  RELEASE_EVIDENCE_SCHEMA,
  REQUIRED_RELEASE_ACTIONS,
  REQUIRED_RELEASE_COMMANDS,
  resolveEvidencePath,
  runReleaseCheck,
  validateReleaseEvidence,
// @ts-expect-error The directly executable Node helper has no separate declaration file.
} from "../scripts/check-release.mjs";
// @ts-expect-error The directly executable Node helper has no separate declaration file.
import { RELEASE_UNITY_EDITOR_VERSIONS } from "../scripts/release-matrix.mjs";

const COMMIT = "a".repeat(40);
const VERSION = JSON.parse(fs.readFileSync(path.join(process.cwd(), "package.json"), "utf8")).version;
const NOW = new Date("2026-07-12T12:00:00.000Z");
const PROJECT = "c".repeat(64);
const generatedHash = "b".repeat(64);
const temporaryDirectories: string[] = [];
let root: string;

const context = {
  commit: COMMIT,
  packageVersion: VERSION,
  commitDate: "2026-07-12T10:00:00.000Z",
  runtimeHashes: {
    "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-cli.mjs": generatedHash,
    "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-cli.LEGAL.txt": generatedHash,
    "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-runtime.json": generatedHash,
  },
  complianceHashes: {
    "THIRD_PARTY_NOTICES.md": generatedHash,
    "sbom.cdx.json": generatedHash,
    "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/THIRD_PARTY_NOTICES.md": generatedHash,
    "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/sbom.cdx.json": generatedHash,
  },
};

function sha256(value: string) {
  return crypto.createHash("sha256").update(value).digest("hex");
}

const smokeCommands = [
  "sgg_unity_projects",
  "describe:commands",
  "describe:command:compile.status",
  "compile.status",
  "describe:command:test.run",
  "describe:command:test.run.status",
  "test.run",
  "test.run.status",
].map((command) => ({ command, result: "pass" }));

function smokeTarget(line: "6000.4" | "6000.5", version: string, identity: string) {
  return {
    target_line: line,
    expected_unity_version: version,
    expected_package_version: VERSION,
    project_selector_hash: `sha256:${identity}`,
    project_id: `smoke-${line.replace(".", "-")}`,
    instance_id_hash: `sha256:${(line === "6000.4" ? "1" : "2").repeat(64)}`,
    test_mode: "editmode",
    test_filter: `SGG.UnityMCP.Tests.PackageSmoke${line.replace(".", "")}`,
    test_assembly: "SGG.UnityMCP.Editor.Tests",
    expected_test_count: 2,
    actual_test_count: 2,
    count_matches: true,
    artifact_schema_version: 3,
    terminal_counts_authoritative: true,
    terminal_count_source: "unity_test_runner",
    count_evidence_valid: true,
    raw_terminal_counts: {
      pass_count: 2,
      fail_count: 0,
      skip_count: 0,
      inconclusive_count: 0,
      authoritative_leaf_total: 2,
      assert_count: 16,
      test_case_count: 2,
      test_case_count_source: "unity_test_runner_root.TestCaseCount",
      count_consistency: {
        leaf_sum_matches_total: true,
        assert_count_semantics: "not_a_test_count",
        test_case_count_available: true,
        test_case_count_matches_leaf_total: true,
      },
    },
    counts: { total: 2, passed: 2, failed: 0, skipped: 0 },
    commands: structuredClone(smokeCommands),
    unsafe_active_run: false,
    unsafe_active_reason: null,
    result: "pass",
  };
}

function allPass() {
  const record = createReleaseEvidenceTemplate(context, { now: NOW });
  record.release_outcome.result = "pass";
  for (const evidence of record.records) {
    const content = `verified ${evidence.category}\n`;
    const verificationPath = `tmp/release-evidence/${evidence.category}.log`;
    fs.mkdirSync(path.dirname(path.join(root, verificationPath)), { recursive: true });
    fs.writeFileSync(path.join(root, verificationPath), content);
    evidence.actions = [...REQUIRED_RELEASE_ACTIONS[evidence.category]];
    evidence.commands = REQUIRED_RELEASE_COMMANDS[evidence.category].length > 0
      ? [...REQUIRED_RELEASE_COMMANDS[evidence.category]]
      : [`operator-action ${evidence.category}`];
    evidence.artifacts = [{
      kind: "external",
      ref: `external:verified/${evidence.category}`,
      verification_path: verificationPath,
      sha256: sha256(content),
    }];
    if (evidence.category === "playmode_suite") {
      evidence.applicable = false;
      evidence.applicability_reason = "No runtime, scene, player, or frame-dependent behavior changed in this release.";
      evidence.actions = [PLAYMODE_REVIEW_ACTION];
      evidence.commands = ["review release changes for PlayMode applicability"];
      continue;
    }
    evidence.result = "pass";
    if ("project_identity_sha256" in evidence) evidence.project_identity_sha256 = PROJECT;
    if (evidence.category === "unity_6000_4_smoke" || evidence.category === "unity_6000_5_smoke") {
      const line = evidence.category === "unity_6000_4_smoke" ? "6000.4" : "6000.5";
      evidence.test_mode = "editmode";
      evidence.test_filter = `SGG.UnityMCP.Tests.PackageSmoke${line.replace(".", "")}`;
      evidence.test_assembly = "SGG.UnityMCP.Editor.Tests";
      evidence.expected_test_count = 2;
    }
  }
  const combined = {
    schema_version: 2,
    evidence_kind: "harden-07b-bounded-unity-smoke",
    date_utc: NOW.toISOString(),
    commit: COMMIT,
    package_version: VERSION,
    cleanup: { result: "pass" },
    latest_static: { result: "not_run", reason: "not run by check:unity-smoke" },
    unity_6000_4_smoke: smokeTarget("6000.4", "6000.4.10f1", "4".repeat(64)),
    unity_6000_5_smoke: smokeTarget("6000.5", "6000.5.3f1", "5".repeat(64)),
    full_release_matrix: { result: "not_run", reason: "outside HARDEN-07B scope" },
  };
  const combinedText = `${JSON.stringify(combined, null, 2)}\n`;
  const combinedPath = "tmp/release-evidence/harden-07b-unity-smoke.json";
  fs.writeFileSync(path.join(root, combinedPath), combinedText);
  const combinedArtifact = {
    kind: "external",
    ref: "external:harden-07b/unity-smoke",
    verification_path: combinedPath,
    sha256: sha256(combinedText),
  };
  for (const category of ["unity_6000_4_smoke", "unity_6000_5_smoke"]) {
    const smoke = record.records.find((item: any) => item.category === category)!;
    smoke.commands = ["npm run check:unity-smoke"];
    smoke.artifacts = [structuredClone(combinedArtifact)];
    smoke.project_identity_sha256 = category === "unity_6000_4_smoke" ? "4".repeat(64) : "5".repeat(64);
  }
  return record;
}

function mutateCombined(record: ReturnType<typeof allPass>, mutate: (value: any) => void) {
  const artifact = record.records[6].artifacts[0];
  const filePath = path.join(root, artifact.verification_path);
  const value = JSON.parse(fs.readFileSync(filePath, "utf8"));
  mutate(value);
  const text = `${JSON.stringify(value, null, 2)}\n`;
  fs.writeFileSync(filePath, text);
  for (const index of [6, 7]) record.records[index].artifacts[0].sha256 = sha256(text);
}

function errors(record: ReturnType<typeof allPass>, overrides = {}) {
  return validateReleaseEvidence(record, { root, context, now: NOW, maxAgeHours: 24, ...overrides });
}

beforeEach(() => {
  root = fs.mkdtempSync(path.join(os.tmpdir(), "release-evidence-test-"));
  temporaryDirectories.push(root);
});

afterEach(() => {
  for (const directory of temporaryDirectories.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

describe("HARDEN-07C release evidence", () => {
  it("accepts a complete all-pass fixture backed by distinct local files", () => {
    expect(errors(allPass())).toEqual([]);
  });

  it("fails context acquisition before reading HEAD files when git is dirty", () => {
    const calls: string[][] = [];
    expect(() => getReleaseContext(root, { git: (args: string[]) => {
      calls.push(args);
      return " M package.json";
    } })).toThrow("clean git index and worktree");
    expect(calls).toEqual([["status", "--porcelain=v1", "--untracked-files=all"]]);
  });

  it("rejects unknown fields, missing records, and duplicate categories", () => {
    const record = allPass() as ReturnType<typeof allPass> & { surprise?: boolean };
    record.surprise = true;
    expect(errors(record).join(" ")).toContain("unknown field surprise");
    delete record.surprise;
    record.records.pop();
    record.records.push(structuredClone(record.records[0]));
    expect(errors(record).join(" ")).toMatch(/duplicate category latest_static_validation.*missing category unity_package_update_smoke/);
  });

  it.each(["fail", "not_run"])("rejects an applicable %s record", (result) => {
    const record = allPass();
    record.records[0].result = result;
    expect(errors(record).join(" ")).toContain("must pass because it is applicable");
  });

  it("rejects wrong commit/package and timestamps before commit, stale, or future", () => {
    const record = allPass();
    record.commit = "d".repeat(40);
    record.records[0].package_version = "old";
    record.records[1].utc_date = "2026-07-12T09:59:59.999Z";
    record.records[2].utc_date = "2026-07-10T00:00:00.000Z";
    record.records[3].utc_date = "2026-07-12T12:00:00.001Z";
    expect(errors(record).join(" ")).toMatch(/current git commit|current package version|must not predate|is stale|must not be in the future/);
  });

  it("uses injected generation time without claiming execution", () => {
    const generatedAt = new Date("2026-07-12T11:22:33.000Z");
    const template = createReleaseEvidenceTemplate(context, { now: generatedAt });
    expect(template.schema_version).toBe(RELEASE_EVIDENCE_SCHEMA);
    expect(template.records.every((item: any) => item.utc_date === generatedAt.toISOString())).toBe(true);
    expect(template.records.every((item: any) => item.result === "not_run" && item.actions.length === 0 && item.commands.length === 0 && item.artifacts.length === 0)).toBe(true);
    expect(errors(template as ReturnType<typeof allPass>).length).toBeGreaterThan(0);
  });

  it.each([
    RELEASE_UNITY_EDITOR_VERSIONS.slice(0, -1),
    [...RELEASE_UNITY_EDITOR_VERSIONS, "6000.5.3f1"],
    [...RELEASE_UNITY_EDITOR_VERSIONS].reverse(),
    [...RELEASE_UNITY_EDITOR_VERSIONS.slice(0, 2), "6000.9.9f1", ...RELEASE_UNITY_EDITOR_VERSIONS.slice(2)],
  ])("rejects missing, duplicate, reordered, or extra matrix versions", (versions) => {
    const record = allPass();
    record.records.find((item: any) => item.category === "unity_editor_compile_matrix")!.unity_versions = [...versions];
    expect(errors(record).join(" ")).toContain("canonical release matrix in order");
  });

  it("does not accept static evidence or arbitrary action IDs for a Unity category", () => {
    const record = allPass();
    const matrix = record.records.find((item: any) => item.category === "unity_editor_compile_matrix")!;
    matrix.actions = ["npm:check-static"];
    matrix.commands = ["npm run check:static"];
    expect(errors(record).join(" ")).toMatch(/npm:check-unity-editor-matrix|npm run check:unity-editor-matrix/);
  });

  it("requires real artifact bytes and rejects arbitrary hashes or nonexistent files", () => {
    const record = allPass();
    record.records[3].artifacts[0].sha256 = "d".repeat(64);
    record.records[4].artifacts[0].verification_path = "tmp/release-evidence/missing.log";
    expect(errors(record).join(" ")).toMatch(/does not match verification_path bytes|does not exist/);
  });

  it("enforces repo ref/path consistency and external tmp verification", () => {
    const record = allPass();
    record.records[0].artifacts[0] = {
      ...record.records[0].artifacts[0],
      kind: "repo",
      ref: "evidence/static.log",
      verification_path: "tmp/release-evidence/latest_static_validation.log",
    };
    record.records[1].artifacts[0].verification_path = "evidence/external.log";
    expect(errors(record).join(" ")).toMatch(/repo ref must equal|external evidence must be under ignored tmp/);
  });

  it("rejects reused refs, paths, commands, and duplicate action IDs across evidence", () => {
    const record = allPass();
    record.records[1].artifacts[0].ref = record.records[0].artifacts[0].ref;
    record.records[1].artifacts[0].verification_path = record.records[0].artifacts[0].verification_path;
    record.records[2].commands.push(record.records[2].commands[0]);
    record.records[3].actions.push(record.records[3].actions[0]);
    record.records[4].commands = [...record.records[5].commands];
    expect(errors(record).join(" ")).toMatch(/ref is reused|verification_path is reused|commands contains duplicates|commands\[0\] is reused|actions contains duplicates/);
  });

  it("allows sharing only the valid combined HARDEN-07B artifact between smoke targets", () => {
    const record = allPass();
    expect(errors(record)).toEqual([]);
    record.records[4].artifacts = [structuredClone(record.records[6].artifacts[0])];
    expect(errors(record).join(" ")).toMatch(/ref is reused|verification_path is reused/);
  });

  it("rejects shared smoke evidence unless its combined JSON binds both targets", () => {
    const record = allPass();
    mutateCombined(record, (value) => {
      value.commit = "d".repeat(40);
      value.unity_6000_5_smoke.result = "fail";
      value.full_release_matrix.result = "pass";
    });
    expect(errors(record).join(" ")).toMatch(/current full HEAD|unity_6000_5_smoke must pass|full_release_matrix must be not_run/);
  });

  it.each([
    ["zero counts", (value: any) => { value.unity_6000_4_smoke.counts = { total: 0, passed: 0, failed: 0, skipped: 0 }; }, /nonzero all-pass/],
    ["skipped counts", (value: any) => { value.unity_6000_5_smoke.counts.skipped = 1; }, /no failed or skipped/],
    ["missing commands", (value: any) => { value.unity_6000_4_smoke.commands = []; }, /complete ordered smoke history/],
    ["unsafe active run", (value: any) => { value.unity_6000_5_smoke.unsafe_active_run = true; value.unity_6000_5_smoke.unsafe_active_reason = "still active"; }, /no unsafe active run/],
    ["wrong identity", (value: any) => { value.unity_6000_4_smoke.project_selector_hash = `sha256:${"9".repeat(64)}`; }, /must bind release category/],
    ["wrong date", (value: any) => { value.date_utc = "2026-07-12T11:59:59.000Z"; }, /must match both smoke release records/],
    ["path filter", (value: any) => { value.unity_6000_5_smoke.test_filter = "Assets/Tests/Smoke"; }, /path-free/],
    ["legacy artifact schema", (value: any) => { value.unity_6000_4_smoke.artifact_schema_version = 1; }, /schema-v3/],
    ["untrusted terminal counts", (value: any) => { value.unity_6000_4_smoke.terminal_counts_authoritative = false; }, /schema-v3/],
    ["wrong terminal source", (value: any) => { value.unity_6000_4_smoke.terminal_count_source = "legacy_artifact_v1"; }, /schema-v3/],
    ["invalid raw count evidence", (value: any) => { value.unity_6000_4_smoke.count_evidence_valid = false; }, /validated raw authoritative/],
    ["tampered raw pass count", (value: any) => { value.unity_6000_4_smoke.raw_terminal_counts.pass_count = 1; }, /must equal collapsed/],
    ["coordinated raw fail shift", (value: any) => { value.unity_6000_4_smoke.raw_terminal_counts.pass_count = 1; value.unity_6000_4_smoke.raw_terminal_counts.fail_count = 1; }, /must equal collapsed/],
    ["release target count mismatch", (value: any) => { value.unity_6000_4_smoke.counts.passed = 1; }, /must equal collapsed/],
    ["tampered expected count", (value: any) => { value.unity_6000_5_smoke.expected_test_count = 3; }, /cardinality contract/],
    ["tampered actual count", (value: any) => { value.unity_6000_5_smoke.actual_test_count = 3; }, /actual_test_count must equal expected_test_count/],
    ["false count match", (value: any) => { value.unity_6000_5_smoke.count_matches = false; }, /count_matches must be true/],
    ["wrong assembly", (value: any) => { value.unity_6000_5_smoke.test_assembly = "Other.Tests"; }, /cardinality contract/],
  ])("deep smoke validation rejects %s", (_name, mutate, expected) => {
    const record = allPass();
    mutateCombined(record, mutate as (value: any) => void);
    expect(errors(record).join(" ")).toMatch(expected as RegExp);
  });

  it("rejects smoke release-record cardinality mutation independently of hash-bound smoke evidence", () => {
    const record = allPass();
    const smoke = record.records.find((item: any) => item.category === "unity_6000_4_smoke")!;
    smoke.expected_test_count = 3;
    expect(errors(record).join(" ")).toContain("cardinality contract");
  });

  it("requires the exact current smoke editor baselines", () => {
    const record = allPass();
    record.records.find((item: any) => item.category === "unity_6000_4_smoke")!.unity_versions = ["6000.4.7f1"];
    record.records.find((item: any) => item.category === "unity_6000_5_smoke")!.unity_versions = ["6000.5.0f1"];
    expect(errors(record).join(" ")).toMatch(/6000\.4\.10f1.*6000\.5\.3f1/);
  });

  it("requires a concrete verified PlayMode decision when inapplicable", () => {
    const record = allPass();
    const playmode = record.records.find((item: any) => item.category === "playmode_suite")!;
    playmode.applicability_reason = "PLACEHOLDER: decide later";
    playmode.actions = [];
    playmode.artifacts = [];
    expect(errors(record).join(" ")).toMatch(/playmode-applicability|non-placeholder|decision artifact/);
  });

  it("requires actual PlayMode action, project identity, and artifact when applicable", () => {
    const record = allPass();
    const playmode = record.records.find((item: any) => item.category === "playmode_suite")!;
    playmode.applicable = true;
    playmode.applicability_reason = "Runtime behavior changed and requires the complete PlayMode suite.";
    playmode.result = "pass";
    playmode.actions = ["manual:unity-playmode-suite"];
    playmode.project_identity_sha256 = PROJECT;
    expect(errors(record)).toEqual([]);
    playmode.project_identity_sha256 = "0".repeat(64);
    playmode.artifacts = [];
    expect(errors(record).join(" ")).toMatch(/identify the tested project|locally verified evidence/);
  });

  it("compares runtime and compliance attestations to current generated hashes", () => {
    const record = allPass();
    record.records.find((item: any) => item.category === "runtime_reproducibility")!.generated_hashes[Object.keys(context.runtimeHashes)[0]] = "d".repeat(64);
    record.records.find((item: any) => item.category === "notices_sbom_drift")!.generated_hashes["sbom.cdx.json"] = "e".repeat(64);
    expect(errors(record).filter((error: string) => error.includes("current generated file"))).toHaveLength(2);
  });

  it.each([
    "/tmp/evidence.json",
    "../evidence.json",
    "C:\\Users\\person\\evidence.json",
    "\\\\server\\share\\evidence.json",
  ])("rejects an unsafe evidence path %s", (candidate) => {
    expect(() => resolveEvidencePath([candidate], {}, root)).toThrow("safe repository-relative path");
  });

  it("rejects every symlink component, including contained output aliases", () => {
    const outside = fs.mkdtempSync(path.join(os.tmpdir(), "release-outside-"));
    temporaryDirectories.push(outside);
    fs.writeFileSync(path.join(outside, "evidence.json"), "{}");
    fs.symlinkSync(outside, path.join(root, "escape"), "dir");
    expect(() => resolveEvidencePath(["escape/evidence.json"], {}, root, { mustExist: true })).toThrow("symlink");
    const target = path.join(root, "tracked-local-target.json");
    fs.writeFileSync(target, "do not overwrite");
    fs.symlinkSync(target, path.join(root, "output-alias.json"), "file");
    expect(() => resolveEvidencePath(["output-alias.json"], {}, root)).toThrow("symlink");
    expect(fs.readFileSync(target, "utf8")).toBe("do not overwrite");
    fs.symlinkSync(path.join(root, "not-created-by-generator.json"), path.join(root, "dangling-output.json"), "file");
    expect(() => resolveEvidencePath(["dangling-output.json"], {}, root)).toThrow("symlink");
    expect(fs.existsSync(path.join(root, "not-created-by-generator.json"))).toBe(false);
    fs.mkdirSync(path.join(root, "real-parent"));
    fs.symlinkSync(path.join(root, "real-parent"), path.join(root, "contained-parent"), "dir");
    expect(() => resolveEvidencePath(["contained-parent/new.json"], {}, root)).toThrow("symlink");
    const record = allPass();
    record.records[0].artifacts[0].verification_path = "escape/evidence.json";
    expect(errors(record).join(" ")).toContain("symlink");
  });

  it("rejects secrets, credentials, arbitrary absolute paths, UNC paths, and private keys", () => {
    const record = allPass();
    record.records[0].commands = [
      "API_KEY=abcd tool",
      "tool --api-key abcd",
      "tool --token=abcd",
      "tool --password abcd",
      "tool /opt/private/result",
      "tool \\\\server\\share",
      "tool https://private.example.invalid/api",
      "-----BEGIN PRIVATE KEY-----",
    ];
    record.records[1].applicability_reason = "See http://internal.invalid/result";
    expect(errors(record).join(" ")).toMatch(/secret or credential|absolute or UNC|HTTP\(S\) URL/);
  });

  it("fails closed for missing and malformed contained evidence files", () => {
    expect(runReleaseCheck({ root, evidencePath: "tmp/missing.json", context, now: NOW }).errors[0]).toContain("does not exist");
    fs.mkdirSync(path.join(root, "tmp"));
    fs.writeFileSync(path.join(root, "tmp/bad.json"), "{");
    expect(runReleaseCheck({ root, evidencePath: "tmp/bad.json", context, now: NOW }).errors[0]).toContain("malformed JSON");
  });
});
