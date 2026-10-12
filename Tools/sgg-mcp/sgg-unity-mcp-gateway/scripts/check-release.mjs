#!/usr/bin/env node

import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { pathToFileURL } from "node:url";

import {
  RELEASE_UNITY_6000_4_VERSION,
  RELEASE_UNITY_6000_5_VERSION,
  RELEASE_UNITY_EDITOR_VERSIONS,
} from "./release-matrix.mjs";

export const RELEASE_EVIDENCE_SCHEMA = "harden-07c.release-evidence.v1";
export const DEFAULT_RELEASE_EVIDENCE_PATH = "tmp/harden-07c-release-evidence.json";
export const DEFAULT_MAX_EVIDENCE_AGE_HOURS = 168;
export const RELEASE_CATEGORIES = Object.freeze([
  "latest_static_validation",
  "runtime_reproducibility",
  "notices_sbom_drift",
  "unity_editor_compile_matrix",
  "windows_smoke",
  "wsl_smoke",
  "unity_6000_4_smoke",
  "unity_6000_5_smoke",
  "editmode_suite",
  "playmode_suite",
  "unity_package_clean_install_smoke",
  "unity_package_update_smoke",
]);
export const REQUIRED_RELEASE_ACTIONS = Object.freeze({
  latest_static_validation: Object.freeze(["npm:check-static"]),
  runtime_reproducibility: Object.freeze(["npm:build-gateway-runtime", "npm:check-gateway-runtime"]),
  notices_sbom_drift: Object.freeze(["npm:check-notices"]),
  unity_editor_compile_matrix: Object.freeze(["npm:check-unity-editor-matrix"]),
  windows_smoke: Object.freeze(["manual:windows-smoke"]),
  wsl_smoke: Object.freeze(["manual:wsl-smoke"]),
  unity_6000_4_smoke: Object.freeze(["npm:check-unity-smoke:6000.4-target"]),
  unity_6000_5_smoke: Object.freeze(["npm:check-unity-smoke:6000.5-target"]),
  editmode_suite: Object.freeze(["manual:unity-editmode-suite"]),
  playmode_suite: Object.freeze(["manual:unity-playmode-suite"]),
  unity_package_clean_install_smoke: Object.freeze(["manual:unity-package-clean-install-smoke"]),
  unity_package_update_smoke: Object.freeze(["manual:unity-package-update-smoke"]),
});
export const REQUIRED_RELEASE_COMMANDS = Object.freeze({
  latest_static_validation: Object.freeze(["npm run check:static"]),
  runtime_reproducibility: Object.freeze(["npm run build:gateway-runtime", "npm run check:gateway-runtime"]),
  notices_sbom_drift: Object.freeze(["npm run check:notices"]),
  unity_editor_compile_matrix: Object.freeze(["npm run check:unity-editor-matrix"]),
  windows_smoke: Object.freeze([]),
  wsl_smoke: Object.freeze([]),
  unity_6000_4_smoke: Object.freeze(["npm run check:unity-smoke"]),
  unity_6000_5_smoke: Object.freeze(["npm run check:unity-smoke"]),
  editmode_suite: Object.freeze([]),
  playmode_suite: Object.freeze([]),
  unity_package_clean_install_smoke: Object.freeze([]),
  unity_package_update_smoke: Object.freeze([]),
});
export const PLAYMODE_REVIEW_ACTION = "release-change-review:playmode-applicability";

const UNITY_CATEGORIES = new Set(RELEASE_CATEGORIES.slice(3));
const SMOKE_CATEGORIES = new Set(["unity_6000_4_smoke", "unity_6000_5_smoke"]);
const RUNTIME_HASH_PATHS = Object.freeze([
  "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-cli.mjs",
  "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-cli.LEGAL.txt",
  "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-runtime.json",
]);
const COMPLIANCE_HASH_PATHS = Object.freeze([
  "THIRD_PARTY_NOTICES.md",
  "sbom.cdx.json",
  "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/THIRD_PARTY_NOTICES.md",
  "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/sbom.cdx.json",
]);
const SHA256_RE = /^[a-f0-9]{64}$/;
const UNITY_VERSION_RE = /^(?:20\d{2}|6000)\.\d+\.\d+[abfp]\d+$/;
const SAFE_RELATIVE_RE = /^[A-Za-z0-9._/-]+$/;
const ACTION_RE = /^[a-z0-9][a-z0-9:.-]{2,100}$/;
const RESULT_VALUES = new Set(["pass", "fail", "not_run"]);

function ownKeys(value, expected, label, errors) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    errors.push(`${label} must be an object`);
    return false;
  }
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  for (const key of actual.filter((key) => !wanted.includes(key))) errors.push(`${label} has unknown field ${key}`);
  for (const key of wanted.filter((key) => !actual.includes(key))) errors.push(`${label} is missing field ${key}`);
  return actual.length === wanted.length && actual.every((key, index) => key === wanted[index]);
}

function sha256File(filePath) {
  return crypto.createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
}

function currentHashes(root, relativePaths) {
  return Object.fromEntries(relativePaths.map((relativePath) => [relativePath, sha256File(path.join(root, relativePath))]));
}

function isCanonicalUtc(value) {
  if (typeof value !== "string") return false;
  const date = new Date(value);
  return Number.isFinite(date.getTime()) && date.toISOString() === value;
}

function validateSafeText(value, label, errors) {
  if (typeof value !== "string" || value.length === 0 || value.length > 1000 || /[\r\n\0]/.test(value)) {
    errors.push(`${label} must be a nonempty bounded single-line string`);
    return;
  }
  if (/(?:-----BEGIN [A-Z ]*PRIVATE KEY-----|\bAKIA[0-9A-Z]{16}\b|\b(?:gh[opusr]|github_pat)_[A-Za-z0-9_]{20,}\b|bearer\s+\S+|--(?:api[-_]?key|access[-_]?key|client[-_]?secret|token|password|passwd|secret|authorization|credential|username|user)(?:[=\s]+)\S+|(?:api[_-]?key|access[_-]?key|client[_-]?secret|token|password|passwd|secret|authorization|credential|username|user)\s*[=:]\s*\S+)/i.test(value)) {
    errors.push(`${label} must not contain secret or credential material`);
  }
  if (/https?:\/\//i.test(value)) errors.push(`${label} must not contain an HTTP(S) URL or endpoint`);
  if (/(?:^|[\s='"(])(?:[A-Za-z]:[\\/]|\\\\[^\\]|\/\/[^/\s]|\/[^/\s])/.test(value)) {
    errors.push(`${label} must not contain an absolute or UNC machine path`);
  }
}

function isSafeRelative(value) {
  return typeof value === "string" && value.length > 0 && value.length <= 300 && SAFE_RELATIVE_RE.test(value) &&
    !path.posix.isAbsolute(value) && !path.win32.isAbsolute(value) && !value.split("/").some((part) => part === ".." || part === "");
}

function isContained(rootReal, candidateReal) {
  const relative = path.relative(rootReal, candidateReal);
  return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative));
}

function resolveContainedPath(root, relativePath, options = {}) {
  if (!isSafeRelative(relativePath)) throw new Error("must be a safe repository-relative path");
  const rootReal = fs.realpathSync(root);
  const candidate = path.resolve(root, relativePath);
  let lexical = root;
  for (const component of relativePath.split("/")) {
    lexical = path.join(lexical, component);
    let stat;
    try {
      stat = fs.lstatSync(lexical);
    } catch (error) {
      if (error?.code === "ENOENT") break;
      throw error;
    }
    if (stat.isSymbolicLink()) throw new Error("must not contain any symlink component");
    const resolvedComponent = fs.realpathSync(lexical);
    if (!isContained(rootReal, resolvedComponent)) throw new Error("must not escape the repository through a junction or reparse point");
  }
  let existing = candidate;
  while (!fs.existsSync(existing)) {
    const parent = path.dirname(existing);
    if (parent === existing) throw new Error("has no contained existing parent");
    existing = parent;
  }
  if (!isContained(rootReal, fs.realpathSync(existing))) throw new Error("must not escape the repository through a symlink");
  if (options.mustExist) {
    if (!fs.existsSync(candidate)) throw new Error("does not exist");
    const candidateReal = fs.realpathSync(candidate);
    if (!isContained(rootReal, candidateReal)) throw new Error("must not escape the repository through a symlink");
    if (options.regularFile && !fs.statSync(candidateReal).isFile()) throw new Error("must resolve to a regular file");
    return candidateReal;
  }
  return candidate;
}

function isDualSmokePair(left, right) {
  return new Set([left, right]).size === 2 && [left, right].every((category) => category === "unity_6000_4_smoke" || category === "unity_6000_5_smoke");
}

function validateArtifacts(artifacts, label, errors, requireEvidence, root, globalRefs, globalPaths, category) {
  if (!Array.isArray(artifacts)) {
    errors.push(`${label} must be an array`);
    return;
  }
  if (requireEvidence && artifacts.length === 0) errors.push(`${label} must contain locally verified evidence for a pass result`);
  for (const [index, artifact] of artifacts.entries()) {
    const itemLabel = `${label}[${index}]`;
    if (!ownKeys(artifact, ["kind", "ref", "verification_path", "sha256"], itemLabel, errors)) continue;
    validateSafeText(artifact.ref, `${itemLabel}.ref`, errors);
    validateSafeText(artifact.verification_path, `${itemLabel}.verification_path`, errors);
    if (artifact.kind !== "repo" && artifact.kind !== "external") errors.push(`${itemLabel}.kind must be repo or external`);
    if (artifact.kind === "repo" && (!isSafeRelative(artifact.ref) || artifact.ref !== artifact.verification_path)) {
      errors.push(`${itemLabel} repo ref must equal its safe repository-relative verification_path`);
    }
    if (artifact.kind === "external" && !/^external:[A-Za-z0-9._/-]+$/.test(artifact.ref)) errors.push(`${itemLabel}.ref must be a safe external logical ID beginning external:`);
    if (artifact.kind === "external" && (typeof artifact.verification_path !== "string" || !artifact.verification_path.startsWith("tmp/"))) errors.push(`${itemLabel}.verification_path for external evidence must be under ignored tmp/`);
    const priorRef = globalRefs.get(artifact.ref);
    const priorPath = globalPaths.get(artifact.verification_path);
    const sharedSmoke = priorRef && priorPath && isDualSmokePair(priorRef.category, category) && isDualSmokePair(priorPath.category, category) &&
      priorRef.path === artifact.verification_path && priorRef.sha256 === artifact.sha256 && priorPath.ref === artifact.ref && priorPath.sha256 === artifact.sha256;
    if (priorRef && !sharedSmoke) errors.push(`${itemLabel}.ref is reused across release evidence`);
    if (priorPath && !sharedSmoke) errors.push(`${itemLabel}.verification_path is reused across release evidence`);
    if (!priorRef) globalRefs.set(artifact.ref, { category, path: artifact.verification_path, sha256: artifact.sha256 });
    if (!priorPath) globalPaths.set(artifact.verification_path, { category, ref: artifact.ref, sha256: artifact.sha256 });
    if (!SHA256_RE.test(artifact.sha256)) errors.push(`${itemLabel}.sha256 must be lowercase SHA-256`);
    try {
      const localPath = resolveContainedPath(root, artifact.verification_path, { mustExist: true, regularFile: true });
      if (SHA256_RE.test(artifact.sha256) && sha256File(localPath) !== artifact.sha256) errors.push(`${itemLabel}.sha256 does not match verification_path bytes`);
    } catch (error) {
      errors.push(`${itemLabel}.verification_path ${error.message}`);
    }
  }
}

function validateCombinedSmokeArtifact(smoke4, smoke5, root, context, errors) {
  if (!smoke4 || !smoke5 || !Array.isArray(smoke4.artifacts) || !Array.isArray(smoke5.artifacts)) return;
  const shared = smoke4.artifacts.filter((left) => smoke5.artifacts.some((right) =>
    left?.ref === right?.ref && left?.verification_path === right?.verification_path && left?.sha256 === right?.sha256));
  if (shared.length !== 1) {
    errors.push("Unity 6000.4 and 6000.5 smoke records must share exactly one combined HARDEN-07B artifact");
    return;
  }
  const artifact = shared[0];
  let value;
  try {
    const filePath = resolveContainedPath(root, artifact.verification_path, { mustExist: true, regularFile: true });
    value = JSON.parse(fs.readFileSync(filePath, "utf8"));
  } catch (error) {
    errors.push(`combined HARDEN-07B smoke artifact is invalid JSON: ${error.message}`);
    return;
  }
  if (!ownKeys(value, ["schema_version", "evidence_kind", "date_utc", "commit", "package_version", "cleanup", "latest_static", "unity_6000_4_smoke", "unity_6000_5_smoke", "full_release_matrix"], "combined HARDEN-07B smoke artifact", errors)) return;
  if (value.schema_version !== 2 || value.evidence_kind !== "harden-07b-bounded-unity-smoke") errors.push("combined HARDEN-07B smoke artifact must have schema_version 2 and the expected evidence_kind");
  if (!isCanonicalUtc(value.date_utc)) errors.push("combined HARDEN-07B smoke artifact date_utc must be canonical UTC");
  else {
    if (new Date(value.date_utc).getTime() < new Date(context.commitDate).getTime()) errors.push("combined HARDEN-07B smoke artifact date_utc must not predate the current commit");
    if (value.date_utc !== smoke4.utc_date || value.date_utc !== smoke5.utc_date) errors.push("combined HARDEN-07B smoke artifact date_utc must match both smoke release records");
  }
  if (value.commit !== context.commit) errors.push("combined HARDEN-07B smoke artifact commit must equal current full HEAD");
  if (value.package_version !== context.packageVersion) errors.push("combined HARDEN-07B smoke artifact package_version must equal current package version");
  if (!ownKeys(value.cleanup, ["result"], "combined HARDEN-07B smoke artifact cleanup", errors) || value.cleanup.result !== "pass") errors.push("combined HARDEN-07B smoke artifact cleanup must be exact pass");
  if (!ownKeys(value.latest_static, ["result", "reason"], "combined HARDEN-07B smoke artifact latest_static", errors) || value.latest_static.result !== "not_run") errors.push("combined HARDEN-07B smoke artifact latest_static must be not_run");
  else validateSafeText(value.latest_static.reason, "combined HARDEN-07B smoke artifact latest_static.reason", errors);
  if (!ownKeys(value.full_release_matrix, ["result", "reason"], "combined HARDEN-07B smoke artifact full_release_matrix", errors) || value.full_release_matrix.result !== "not_run") errors.push("combined HARDEN-07B smoke artifact full_release_matrix must be not_run");
  else validateSafeText(value.full_release_matrix.reason, "combined HARDEN-07B smoke artifact full_release_matrix.reason", errors);
  for (const [category, record] of [["unity_6000_4_smoke", smoke4], ["unity_6000_5_smoke", smoke5]]) {
    const target = value[category];
    const targetLabel = `combined HARDEN-07B smoke artifact ${category}`;
    if (!ownKeys(target, ["target_line", "expected_unity_version", "expected_package_version", "project_selector_hash", "project_id", "instance_id_hash", "test_mode", "test_filter", "test_assembly", "expected_test_count", "actual_test_count", "count_matches", "artifact_schema_version", "terminal_counts_authoritative", "terminal_count_source", "count_evidence_valid", "raw_terminal_counts", "counts", "commands", "unsafe_active_run", "unsafe_active_reason", "result"], targetLabel, errors)) continue;
    if (target.target_line !== (category === "unity_6000_4_smoke" ? "6000.4" : "6000.5")) errors.push(`${targetLabel} target_line is incorrect`);
    if (target?.result !== "pass") errors.push(`combined HARDEN-07B smoke artifact ${category} must pass`);
    if (record.unity_versions?.length !== 1 || target?.expected_unity_version !== record.unity_versions[0]) errors.push(`combined HARDEN-07B smoke artifact ${category} Unity version must match its release record`);
    if (target?.expected_package_version !== context.packageVersion) errors.push(`combined HARDEN-07B smoke artifact ${category} package version must match current package`);
    if (target.test_mode !== "editmode") errors.push(`${targetLabel} test_mode must be editmode`);
    if (typeof target.test_filter !== "string" || target.test_filter.length === 0 || target.test_filter.length > 160 || /[\\/\r\n]|https?:\/\//i.test(target.test_filter)) errors.push(`${targetLabel} test_filter must be nonempty, bounded, URL-free, and path-free`);
    else validateSafeText(target.test_filter, `${targetLabel}.test_filter`, errors);
    if (typeof target.test_assembly !== "string" || target.test_assembly.length === 0 || target.test_assembly.length > 160 || !/^[A-Za-z0-9_.-]+$/.test(target.test_assembly)) errors.push(`${targetLabel} test_assembly must be a bounded safe assembly name`);
    if (!Number.isInteger(target.expected_test_count) || target.expected_test_count <= 0) errors.push(`${targetLabel} expected_test_count must be a positive integer`);
    if (!Number.isInteger(target.actual_test_count) || target.actual_test_count <= 0) errors.push(`${targetLabel} actual_test_count must be a positive integer`);
    if (target.count_matches !== true || target.actual_test_count !== target.expected_test_count) errors.push(`${targetLabel} count_matches must be true and actual_test_count must equal expected_test_count`);
    if (target.artifact_schema_version !== 3 || target.terminal_counts_authoritative !== true || target.terminal_count_source !== "unity_test_runner") errors.push(`${targetLabel} must attest authoritative schema-v3 unity_test_runner terminal counts`);
    if (target.count_evidence_valid !== true) errors.push(`${targetLabel} must attest validated raw authoritative terminal count evidence`);
    validateRawSmokeCounts(target, targetLabel, errors);
    if (target.test_mode !== record.test_mode || target.test_filter !== record.test_filter || target.test_assembly !== record.test_assembly || target.expected_test_count !== record.expected_test_count) errors.push(`${targetLabel} test mode/filter/assembly/expected count must match its release record cardinality contract`);
    if (typeof target.project_id !== "string" || target.project_id.length === 0 || target.project_id.length > 160) errors.push(`${targetLabel} project_id must be nonempty and bounded`);
    else validateSafeText(target.project_id, `${targetLabel}.project_id`, errors);
    if (!/^sha256:[a-f0-9]{64}$/.test(target.project_selector_hash) || !/^sha256:[a-f0-9]{64}$/.test(target.instance_id_hash)) errors.push(`${targetLabel} selector and instance identities must be sha256 hashes`);
    if (record.project_identity_sha256 !== target.project_selector_hash?.slice("sha256:".length)) errors.push(`${targetLabel} project selector hash must bind release category project_identity_sha256`);
    if (!ownKeys(target.counts, ["total", "passed", "failed", "skipped"], `${targetLabel}.counts`, errors) ||
      ![target.counts?.total, target.counts?.passed, target.counts?.failed, target.counts?.skipped].every(Number.isInteger) ||
      target.counts.total <= 0 || target.counts.passed !== target.counts.total || target.counts.failed !== 0 || target.counts.skipped !== 0) {
      errors.push(`${targetLabel} counts must be nonzero all-pass integer counts with no failed or skipped tests`);
    }
    if (target.counts?.total !== target.actual_test_count) errors.push(`${targetLabel} counts.total must equal actual_test_count`);
    if (target.unsafe_active_run !== false || target.unsafe_active_reason !== null) errors.push(`${targetLabel} must have no unsafe active run`);
    validateSmokeCommandHistory(target.commands, targetLabel, errors);
  }
}

function validateRawSmokeCounts(target, label, errors) {
  const raw = target.raw_terminal_counts;
  if (!ownKeys(raw, ["pass_count", "fail_count", "skip_count", "inconclusive_count", "authoritative_leaf_total", "assert_count", "test_case_count", "test_case_count_source", "count_consistency"], `${label}.raw_terminal_counts`, errors)) return;
  const countFields = [raw.pass_count, raw.fail_count, raw.skip_count, raw.inconclusive_count, raw.authoritative_leaf_total, raw.assert_count];
  if (!countFields.every((value) => Number.isSafeInteger(value) && value >= 0)) {
    errors.push(`${label}.raw_terminal_counts must contain bounded nonnegative integer root aggregates and assert_count`);
    return;
  }
  const leafTotal = raw.pass_count + raw.fail_count + raw.skip_count + raw.inconclusive_count;
  if (!Number.isSafeInteger(leafTotal)
    || leafTotal !== raw.authoritative_leaf_total
    || leafTotal !== target.actual_test_count
    || leafTotal !== target.counts?.total
    || raw.pass_count !== target.counts?.passed
    || raw.fail_count !== target.counts?.failed
    || raw.skip_count + raw.inconclusive_count !== target.counts?.skipped) {
    errors.push(`${label}.raw_terminal_counts must equal collapsed passed, failed, skipped, actual, and total counts`);
  }
  const consistency = raw.count_consistency;
  if (!ownKeys(consistency, ["leaf_sum_matches_total", "assert_count_semantics", "test_case_count_available", "test_case_count_matches_leaf_total"], `${label}.raw_terminal_counts.count_consistency`, errors)) return;
  if (consistency.leaf_sum_matches_total !== true || consistency.assert_count_semantics !== "not_a_test_count") errors.push(`${label}.raw_terminal_counts count consistency must identify assert_count as not a test count and confirm the leaf sum`);
  if (consistency.test_case_count_available === true) {
    if (!Number.isSafeInteger(raw.test_case_count) || raw.test_case_count < 0 || raw.test_case_count_source !== "unity_test_runner_root.TestCaseCount" || consistency.test_case_count_matches_leaf_total !== (raw.test_case_count === leafTotal)) errors.push(`${label}.raw_terminal_counts optional test_case_count evidence is inconsistent`);
  } else if (consistency.test_case_count_available !== false || raw.test_case_count !== null || raw.test_case_count_source !== null || consistency.test_case_count_matches_leaf_total !== null) {
    errors.push(`${label}.raw_terminal_counts absent test_case_count must use explicit null evidence`);
  }
}

function validateSmokeCommandHistory(commands, label, errors) {
  const prefix = [
    "sgg_unity_projects",
    "describe:commands",
    "describe:command:compile.status",
    "compile.status",
    "describe:command:test.run",
    "describe:command:test.run.status",
    "test.run",
  ];
  if (!Array.isArray(commands) || commands.length < prefix.length + 1) {
    errors.push(`${label} commands must contain the complete ordered smoke history and at least one status poll`);
    return;
  }
  const names = [];
  for (const [index, command] of commands.entries()) {
    const commandLabel = `${label}.commands[${index}]`;
    if (!ownKeys(command, ["command", "result"], commandLabel, errors)) continue;
    if (command.result !== "pass" || typeof command.command !== "string") errors.push(`${commandLabel} must be an exact passing command record`);
    names.push(command.command);
  }
  if (prefix.some((name, index) => names[index] !== name) || names.slice(prefix.length).length < 1 || names.slice(prefix.length).some((name) => name !== "test.run.status")) {
    errors.push(`${label} commands must match the required ordered sequence with only status polls after test.run`);
  }
}

function validateHashMap(value, expectedPaths, current, label, errors) {
  if (!ownKeys(value, expectedPaths, label, errors)) return;
  for (const relativePath of expectedPaths) {
    if (!SHA256_RE.test(value[relativePath])) errors.push(`${label}.${relativePath} must be lowercase SHA-256`);
    else if (value[relativePath] !== current[relativePath]) errors.push(`${label}.${relativePath} does not match the current generated file`);
  }
}

export function getReleaseContext(root = process.cwd(), options = {}) {
  const git = options.git ?? ((args) => execFileSync("git", args, { cwd: root, encoding: "utf8" }).trim());
  const dirty = git(["status", "--porcelain=v1", "--untracked-files=all"]);
  if (dirty) throw new Error("release evidence requires a clean git index and worktree; ignored tmp evidence is allowed");
  const commit = git(["rev-parse", "HEAD"]);
  const commitDate = new Date(git(["show", "-s", "--format=%cI", "HEAD"])).toISOString();
  const packageVersion = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8")).version;
  return {
    commit,
    packageVersion,
    commitDate,
    runtimeHashes: currentHashes(root, RUNTIME_HASH_PATHS),
    complianceHashes: currentHashes(root, COMPLIANCE_HASH_PATHS),
  };
}

export function validateReleaseEvidence(record, options = {}) {
  const root = options.root ?? process.cwd();
  const now = options.now ?? new Date();
  const maxAgeHours = options.maxAgeHours ?? DEFAULT_MAX_EVIDENCE_AGE_HOURS;
  const context = options.context ?? getReleaseContext(root);
  const errors = [];
  if (!Number.isFinite(maxAgeHours) || maxAgeHours <= 0 || maxAgeHours > 8760) errors.push("max evidence age must be greater than 0 and at most 8760 hours");
  if (!ownKeys(record, ["schema_version", "commit", "package_version", "release_outcome", "records"], "release evidence", errors)) return errors;
  if (record.schema_version !== RELEASE_EVIDENCE_SCHEMA) errors.push(`schema_version must be ${RELEASE_EVIDENCE_SCHEMA}`);
  if (record.commit !== context.commit) errors.push(`commit must equal current git commit ${context.commit}`);
  if (record.package_version !== context.packageVersion) errors.push(`package_version must equal current package version ${context.packageVersion}`);
  if (!ownKeys(record.release_outcome, ["result"], "release_outcome", errors)) {
    // Keep collecting independent record failures.
  } else if (!RESULT_VALUES.has(record.release_outcome.result)) errors.push("release_outcome.result must be pass, fail, or not_run");
  if (!Array.isArray(record.records)) return [...errors, "records must be an array"];

  const categories = new Set();
  const artifactRefs = new Map();
  const artifactPaths = new Map();
  const commands = new Map();
  const recordsByCategory = new Map();
  for (const [index, evidence] of record.records.entries()) {
    const label = `records[${index}]`;
    const extra = [];
    if (evidence?.category === "runtime_reproducibility" || evidence?.category === "notices_sbom_drift") extra.push("generated_hashes");
    if (UNITY_CATEGORIES.has(evidence?.category)) extra.push("unity_versions", "project_identity_sha256");
    if (SMOKE_CATEGORIES.has(evidence?.category)) extra.push("test_mode", "test_filter", "test_assembly", "expected_test_count");
    if (!ownKeys(evidence, ["category", "applicable", "applicability_reason", "utc_date", "commit", "package_version", "result", "actions", "commands", "artifacts", ...extra], label, errors)) continue;
    if (!RELEASE_CATEGORIES.includes(evidence.category)) errors.push(`${label}.category is unknown`);
    if (categories.has(evidence.category)) errors.push(`duplicate category ${evidence.category}`);
    categories.add(evidence.category);
    recordsByCategory.set(evidence.category, evidence);
    if (typeof evidence.applicable !== "boolean") errors.push(`${label}.applicable must be boolean`);
    if (evidence.category !== "playmode_suite" && evidence.applicable !== true) errors.push(`${label} must be applicable; only playmode_suite may be inapplicable`);
    validateSafeText(evidence.applicability_reason, `${label}.applicability_reason`, errors);
    if (!isCanonicalUtc(evidence.utc_date)) errors.push(`${label}.utc_date must be an exact canonical UTC timestamp`);
    else {
      const timestamp = new Date(evidence.utc_date).getTime();
      if (timestamp < new Date(context.commitDate).getTime()) errors.push(`${label}.utc_date must not predate the current commit`);
      if (timestamp > now.getTime()) errors.push(`${label}.utc_date must not be in the future`);
      if (Number.isFinite(maxAgeHours) && now.getTime() - timestamp > maxAgeHours * 3_600_000) errors.push(`${label}.utc_date is stale`);
    }
    if (evidence.commit !== context.commit) errors.push(`${label}.commit must equal current git commit ${context.commit}`);
    if (evidence.package_version !== context.packageVersion) errors.push(`${label}.package_version must equal current package version ${context.packageVersion}`);
    if (!RESULT_VALUES.has(evidence.result)) errors.push(`${label}.result must be pass, fail, or not_run`);
    if (evidence.applicable === false && evidence.result !== "not_run") errors.push(`${label} must be not_run when inapplicable`);
    if (evidence.applicable === true && evidence.result !== "pass") errors.push(`${label} must pass because it is applicable`);

    if (!Array.isArray(evidence.actions)) errors.push(`${label}.actions must be an array`);
    else {
      if (new Set(evidence.actions).size !== evidence.actions.length) errors.push(`${label}.actions contains duplicates`);
      evidence.actions.forEach((action) => { if (!ACTION_RE.test(action)) errors.push(`${label}.actions contains an invalid action ID`); });
      const requiredActions = evidence.category === "playmode_suite" && evidence.applicable === false ? [PLAYMODE_REVIEW_ACTION] : REQUIRED_RELEASE_ACTIONS[evidence.category] ?? [];
      for (const action of requiredActions) if (!evidence.actions.includes(action)) errors.push(`${label}.actions must include ${action}`);
    }
    if (!Array.isArray(evidence.commands)) errors.push(`${label}.commands must be an array`);
    else {
      if (new Set(evidence.commands).size !== evidence.commands.length) errors.push(`${label}.commands contains duplicates`);
      if ((evidence.result === "pass" || evidence.category === "playmode_suite" && evidence.applicable === false) && evidence.commands.length === 0) errors.push(`${label}.commands must list exact commands or review actions`);
      evidence.commands.forEach((command, commandIndex) => {
        validateSafeText(command, `${label}.commands[${commandIndex}]`, errors);
        const priorCategory = commands.get(command);
        if (priorCategory && !(command === "npm run check:unity-smoke" && isDualSmokePair(priorCategory, evidence.category))) errors.push(`${label}.commands[${commandIndex}] is reused across release evidence`);
        if (!priorCategory) commands.set(command, evidence.category);
      });
      for (const command of REQUIRED_RELEASE_COMMANDS[evidence.category] ?? []) if (evidence.applicable !== false && !evidence.commands.includes(command)) errors.push(`${label}.commands must include ${command}`);
    }
    const requireArtifact = evidence.result === "pass" || evidence.category === "playmode_suite" && evidence.applicable === false;
    validateArtifacts(evidence.artifacts, `${label}.artifacts`, errors, requireArtifact, root, artifactRefs, artifactPaths, evidence.category);

    if (evidence.category === "playmode_suite" && evidence.applicable === false) {
      if (typeof evidence.applicability_reason !== "string" || evidence.applicability_reason.length < 20 || /placeholder|set true|todo|tbd|decide|pending/i.test(evidence.applicability_reason)) errors.push(`${label}.applicability_reason must be a concrete non-placeholder PlayMode decision`);
      if (!Array.isArray(evidence.artifacts) || evidence.artifacts.length !== 1) errors.push(`${label} must contain one distinct locally verified PlayMode decision artifact`);
    }
    if (UNITY_CATEGORIES.has(evidence.category)) {
      if (!Array.isArray(evidence.unity_versions) || evidence.unity_versions.length === 0 || evidence.unity_versions.some((version) => !UNITY_VERSION_RE.test(version))) errors.push(`${label}.unity_versions must contain exact Unity patch versions`);
      else if (new Set(evidence.unity_versions).size !== evidence.unity_versions.length) errors.push(`${label}.unity_versions contains duplicates`);
      if (!SHA256_RE.test(evidence.project_identity_sha256)) errors.push(`${label}.project_identity_sha256 must be a lowercase SHA-256 identity`);
      if ((evidence.result === "pass" || evidence.applicable !== false) && /^0+$/.test(evidence.project_identity_sha256)) errors.push(`${label}.project_identity_sha256 must identify the tested project`);
      if (evidence.category === "unity_editor_compile_matrix" && JSON.stringify(evidence.unity_versions) !== JSON.stringify(RELEASE_UNITY_EDITOR_VERSIONS)) errors.push(`${label}.unity_versions must equal the canonical release matrix in order: ${RELEASE_UNITY_EDITOR_VERSIONS.join(", ")}`);
      if (evidence.category === "unity_6000_4_smoke" && (evidence.unity_versions.length !== 1 || evidence.unity_versions[0] !== RELEASE_UNITY_6000_4_VERSION)) errors.push(`${label} must contain current exact Unity baseline ${RELEASE_UNITY_6000_4_VERSION}`);
      if (evidence.category === "unity_6000_5_smoke" && (evidence.unity_versions.length !== 1 || evidence.unity_versions[0] !== RELEASE_UNITY_6000_5_VERSION)) errors.push(`${label} must contain current exact Unity baseline ${RELEASE_UNITY_6000_5_VERSION}`);
      if (SMOKE_CATEGORIES.has(evidence.category)) {
        if (evidence.test_mode !== "editmode") errors.push(`${label}.test_mode must be editmode`);
        if (typeof evidence.test_filter !== "string" || evidence.test_filter.length === 0 || evidence.test_filter.length > 160 || /[\\/\r\n]|https?:\/\//i.test(evidence.test_filter)) errors.push(`${label}.test_filter must be nonempty, bounded, URL-free, and path-free`);
        if (typeof evidence.test_assembly !== "string" || evidence.test_assembly.length === 0 || evidence.test_assembly.length > 160 || !/^[A-Za-z0-9_.-]+$/.test(evidence.test_assembly)) errors.push(`${label}.test_assembly must be a bounded safe assembly name`);
        if (!Number.isInteger(evidence.expected_test_count) || evidence.expected_test_count <= 0) errors.push(`${label}.expected_test_count must be a positive integer`);
      }
    }
    if (evidence.category === "runtime_reproducibility") validateHashMap(evidence.generated_hashes, RUNTIME_HASH_PATHS, context.runtimeHashes, `${label}.generated_hashes`, errors);
    if (evidence.category === "notices_sbom_drift") validateHashMap(evidence.generated_hashes, COMPLIANCE_HASH_PATHS, context.complianceHashes, `${label}.generated_hashes`, errors);
  }
  for (const category of RELEASE_CATEGORIES) if (!categories.has(category)) errors.push(`missing category ${category}`);
  validateCombinedSmokeArtifact(recordsByCategory.get("unity_6000_4_smoke"), recordsByCategory.get("unity_6000_5_smoke"), root, context, errors);
  if (record.records.length !== RELEASE_CATEGORIES.length) errors.push(`records must contain exactly ${RELEASE_CATEGORIES.length} categories`);
  if (record.release_outcome?.result !== "pass") errors.push("release_outcome.result must be pass");
  return errors;
}

function unityVersionsFor(category) {
  if (category === "unity_editor_compile_matrix") return [...RELEASE_UNITY_EDITOR_VERSIONS];
  if (category === "unity_6000_4_smoke") return [RELEASE_UNITY_6000_4_VERSION];
  return [RELEASE_UNITY_6000_5_VERSION];
}

export function createReleaseEvidenceTemplate(context, options = {}) {
  const generatedAt = (options.now ?? new Date()).toISOString();
  return {
    schema_version: RELEASE_EVIDENCE_SCHEMA,
    commit: context.commit,
    package_version: context.packageVersion,
    release_outcome: { result: "not_run" },
    records: RELEASE_CATEGORIES.map((category) => {
      const record = {
        category,
        applicable: category !== "playmode_suite",
        applicability_reason: category === "playmode_suite" ? "PLACEHOLDER: record a concrete release-change PlayMode applicability decision." : "Required HARDEN-07C release evidence.",
        utc_date: generatedAt,
        commit: context.commit,
        package_version: context.packageVersion,
        result: "not_run",
        actions: [],
        commands: [],
        artifacts: [],
      };
      if (category === "runtime_reproducibility") record.generated_hashes = { ...context.runtimeHashes };
      if (category === "notices_sbom_drift") record.generated_hashes = { ...context.complianceHashes };
      if (UNITY_CATEGORIES.has(category)) {
        record.unity_versions = unityVersionsFor(category);
        record.project_identity_sha256 = "0".repeat(64);
      }
      if (SMOKE_CATEGORIES.has(category)) {
        record.test_mode = "editmode";
        record.test_filter = null;
        record.test_assembly = null;
        record.expected_test_count = null;
      }
      return record;
    }),
  };
}

export function resolveEvidencePath(args = process.argv.slice(2), env = process.env, root = process.cwd(), options = {}) {
  const positional = args.filter((arg) => !arg.startsWith("--"));
  const supplied = positional[0] ?? env.SGG_RELEASE_EVIDENCE_PATH ?? DEFAULT_RELEASE_EVIDENCE_PATH;
  return resolveContainedPath(root, supplied, { mustExist: options.mustExist === true, regularFile: options.mustExist === true });
}

export function runReleaseCheck(options = {}) {
  const root = options.root ?? process.cwd();
  const context = options.context ?? getReleaseContext(root);
  let evidencePath;
  try {
    const args = options.evidencePath === undefined ? options.args : [options.evidencePath];
    evidencePath = resolveEvidencePath(args, options.env, root, { mustExist: true });
  }
  catch (error) { return { ok: false, errors: [`evidence path ${error.message}`] }; }
  if (!fs.existsSync(evidencePath)) return { ok: false, errors: [`evidence file is missing: ${path.relative(root, evidencePath)}`] };
  let record;
  try { record = JSON.parse(fs.readFileSync(evidencePath, "utf8")); }
  catch (error) { return { ok: false, errors: [`evidence file is malformed JSON: ${error.message}`] }; }
  const rawAge = options.maxAgeHours ?? options.env?.SGG_RELEASE_EVIDENCE_MAX_AGE_HOURS ?? process.env.SGG_RELEASE_EVIDENCE_MAX_AGE_HOURS;
  const maxAgeHours = rawAge === undefined ? DEFAULT_MAX_EVIDENCE_AGE_HOURS : Number(rawAge);
  const errors = validateReleaseEvidence(record, { root, now: options.now, maxAgeHours, context });
  return { ok: errors.length === 0, errors };
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  try {
    const result = runReleaseCheck();
    if (!result.ok) {
      for (const error of result.errors) console.error(`[check-release] ${error}`);
      process.exitCode = 1;
    } else console.log(`[check-release] ok: ${RELEASE_CATEGORIES.length} categories passed for ${RELEASE_UNITY_EDITOR_VERSIONS.join(", ")}`);
  } catch (error) {
    console.error(`[check-release] ${error instanceof Error ? error.message : String(error)}`);
    process.exitCode = 1;
  }
}
