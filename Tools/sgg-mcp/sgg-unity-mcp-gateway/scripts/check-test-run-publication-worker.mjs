#!/usr/bin/env node
// Standalone, owned-storage smoke for the actual single-file bundled worker.
// This script never calls Unity or starts an MCP server.
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { Worker } from "node:worker_threads";

const entry = path.resolve(process.argv[2] ?? "unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/gateway-cli.mjs");
assert.equal(path.extname(entry), ".mjs");
assert.ok(fs.statSync(entry).isFile());
// Windows may share a WSL-installed node_modules. Use a separately built fixed
// parent API there; the actual worker still runs only the production CLI bundle.
const api = process.argv[3] ? await import(pathToFileURL(path.resolve(process.argv[3])).href)
  : await (await import("tsx/esm/api")).tsImport("./test-run-publication-smoke-api.ts", import.meta.url);
const { TestRunOperationStorePublisher, TestRunOperationStore, testRunCanonicalProjectRootHash,
  CanonicalPathService, acquireStorageLock, assertTestRunOperationWorkerData, resolveTestRunOperationStoreLimits } = api;
const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-bundled-publication-worker-"));
const store = new TestRunOperationStore(path.join(root, "operations"));
const publisher = new TestRunOperationStorePublisher({ storeDir: store.dir, workerEntry: pathToFileURL(entry) });
const retainedCount = Number(process.argv[4] ?? 0);
assert.ok(Number.isSafeInteger(retainedCount) && retainedCount >= 0 && retainedCount <= 4096, "retained fixture count must be bounded");
const startBudgetMs = retainedCount > 0 ? 60000 : 10000;
const retained = [];
const timings = {};
let lease;
let lock;
let issuance;
try {
  if (retainedCount > 0) {
    const source = fs.readFileSync(path.join(path.dirname(entry), "..", "Commands", "BuildTestAutomationCommands.cs"), "utf8");
    const descriptor = source.slice(source.indexOf('[McpCommand(\n            "test.run"'), source.indexOf("public static CommandExecutionResult TestRun"));
    assert.match(descriptor, /TimeoutMs\s*=\s*60000/);
    const seed = makeBinding("00000000");
    store.publish(seed);
    const reservation = fs.readFileSync(path.join(store.dir, `${seed.operation_id}.reserve`));
    for (let index = 0; index < retainedCount; index++) {
      const binding = index === 0 ? seed : makeBinding(index.toString(16).padStart(8, "0"));
      const file = path.join(store.dir, `${binding.operation_id}.json`);
      const reserve = path.join(store.dir, `${binding.operation_id}.reserve`);
      if (index > 0) {
        fs.writeFileSync(file, `${JSON.stringify(binding, null, 2)}\n`, { flag: "wx", mode: 0o600 });
        fs.writeFileSync(reserve, reservation, { flag: "wx", mode: 0o600 });
      }
      for (const candidate of [file, reserve]) retained.push({ file: candidate, hash: fileHash(candidate), mtimeNs: fs.statSync(candidate, { bigint: true }).mtimeNs });
    }
  }
  const bootstrapData = { role: "sgg.test_run_operation_store", protocol: 1,
    nonce: "11111111-1111-4111-8111-111111111111", dir: store.dir, limits: resolveTestRunOperationStoreLimits() };
  assert.doesNotThrow(() => assertTestRunOperationWorkerData(bootstrapData));
  assert.doesNotThrow(() => assertTestRunOperationWorkerData({ ...bootstrapData, dir: path.parse(store.dir).root }));
  for (const dir of ["relative", `${store.dir}${path.sep}`, `${store.dir}${path.sep}.${path.sep}child`])
    assert.throws(() => assertTestRunOperationWorkerData({ ...bootstrapData, dir }));
  if (process.platform === "win32") assert.throws(() => assertTestRunOperationWorkerData({ ...bootstrapData, dir: "\\owned-fixture" }));
  assert.equal(publisher.snapshotStatus().bindings, 0);
  const binding = makeBinding("1234abcd");
  let guards = 0;
  let stageStarted = performance.now();
  lease = await publisher.prepare(binding, { deadlineUnixMs: Date.now() + startBudgetMs, beforeCommit: () => { guards += 1; return true; } });
  timings.prepare_ms = Math.round(performance.now() - stageStarted);
  assert.ok(timings.prepare_ms < startBudgetMs);
  assert.ok(guards > 0);
  assert.deepEqual(store.lookup(binding.operation_id), { status: "found", binding });
  assert.deepEqual(store.lookupRequestIssuance(binding), { status: "reserved" });
  assert.equal(publisher.snapshotStatus().bindings, retainedCount + 1);
  const attempt = new Date();
  lock = acquireStorageLock(store.dir, ".store.lock");
  let settled = false;
  issuance = lease.publishRequestIssuance(attempt);
  void issuance.then(() => { settled = true; }, () => { settled = true; });
  await new Promise((resolve) => setTimeout(resolve, 100));
  assert.equal(settled, false, "the actual worker must wait while the caller owns the store lock");
  lock.release();
  lock = undefined;
  stageStarted = performance.now();
  const marker = await issuance;
  timings.issuance_after_unlock_ms = Math.round(performance.now() - stageStarted);
  assert.equal(marker.issued_at, attempt.toISOString(), "late publication must preserve the original attempt timestamp");
  assert.deepEqual(store.lookupRequestIssuance(binding), { status: "found", marker });
  const evidence = { bridge_error_code: "package_refresh_required", reason: "package_refresh_required", package_update: {
    status: "stale_package_resolution", refresh_pending: true, package_refresh_required: true,
    package_refresh_wait_can_advance: false, stale_package_resolution: true, fingerprint: "owned-current-fingerprint",
    pending_fingerprint: "owned-pending-fingerprint", last_transition_utc: new Date().toISOString(), local_package_code_inventory: {
      status: "current", failure_reason: "", mismatch_package: "", local_package_count: 1, source_file_count: 2,
      asset_database_file_count: 2, missing_asset_file_count: 0, unexpected_asset_file_count: 0, compiler_source_file_count: 2,
      missing_compiler_source_file_count: 0, unexpected_compiler_source_file_count: 0, source_fingerprint: "owned-source",
      asset_database_fingerprint: "owned-assets", source_state_fingerprint: "owned-state", compiler_source_fingerprint: "owned-compiler" } } };
  const receipt = await lease.publishTerminalNoExecution(marker, evidence);
  assert.deepEqual(store.lookupTerminalNoExecution(binding, marker), { status: "found", receipt });
  assert.equal(receipt.operation_id, binding.operation_id);
  lease.release();
  lease = undefined;
  const refusedBinding = makeBinding("5678abcd");
  await assert.rejects(publisher.prepare(refusedBinding, { deadlineUnixMs: Date.now() + startBudgetMs, beforeCommit: () => false }));
  assert.deepEqual(store.lookup(refusedBinding.operation_id), { status: "not_found" });
  for (const suffix of [".reserve", ".issued", ".terminal"])
    assert.equal(fs.existsSync(path.join(store.dir, `${refusedBinding.operation_id}${suffix}`)), false);
  await publisher.close();
  await rejectInvalidWorkerRole();
  for (const candidate of retained) {
    assert.equal(fileHash(candidate.file), candidate.hash);
    assert.equal(fs.statSync(candidate.file, { bigint: true }).mtimeNs, candidate.mtimeNs);
  }
  if (retainedCount > 0) console.log(JSON.stringify({ retained_bindings: retainedCount, protected_files: retained.length, supported_start_budget_ms: startBudgetMs, timings, retained_bytes_and_mtimes_unchanged: true, unity_invoked: false }));
  console.log("[check-test-run-publication-worker] ok: bundled binding/reservation, original timestamp, lock wait, terminal, pre-commit denial, invalid role; Unity not invoked");
} finally {
  lock?.release();
  if (issuance) await issuance.catch(() => undefined);
  lease?.release();
  await publisher.close();
  fs.rmSync(root, { recursive: true, force: true });
}

function fileHash(file) { return createHash("sha256").update(fs.readFileSync(file)).digest("hex"); }

function makeBinding(suffix) {
  const created = new Date();
  const stamp = created.toISOString().replace(/[-:]/g, "");
  const id = `test-${stamp.slice(0, 8)}-${stamp.slice(9, 15)}-${suffix}`;
  const canonical = new CanonicalPathService().normalizeProjectRoot(root);
  return { schema_version: 1, contract: "sgg.unity_mcp.test_run_operation_owner.v1", command: "test.run", operation_id: id,
    project: "owned-smoke", original_project_selector: "owned-smoke", origin_project_root: root, canonical_project_root: canonical,
    canonical_project_root_hash: testRunCanonicalProjectRootHash(canonical), origin_instance_id: "owned-instance", origin_unity_pid: process.pid,
    origin_unity_process_start_utc: new Date(created.getTime() - 1000).toISOString(), caller_args_hash: `sha256:${"a".repeat(64)}`,
    artifact_path: `Library/SGGUnityMcp/job-artifacts/test-runs/${id}.json`, artifact_schema_version: 3,
    package_id: "com.sungeargames.unity-mcp", package_version: "owned-smoke", commands_hash: "owned-hash", catalog_generation: 1,
    artifact_schema_capability: "test_run_artifact_v3", created_at: created.toISOString() };
}

async function rejectInvalidWorkerRole() {
  const env = {};
  for (const [key, value] of Object.entries(process.env))
    if (/^(SystemRoot|windir|Path|PATHEXT|ComSpec|TEMP|TMP)$/i.test(key) && typeof value === "string") env[key] = value;
  const worker = new Worker(pathToFileURL(entry), { workerData: { role: "invalid-private-role" }, env, execArgv: [], stdout: true, stderr: true });
  let outputBytes = 0;
  worker.stdout.on("data", (chunk) => { outputBytes += chunk.byteLength; });
  worker.stderr.on("data", (chunk) => { outputBytes += chunk.byteLength; });
  try {
    const result = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("Invalid bundled worker did not fail closed")), 5000);
      let failed = false;
      worker.once("error", () => { failed = true; });
      worker.once("exit", (code) => { clearTimeout(timer); resolve({ code, failed }); });
    });
    assert.equal(result.failed, true);
    assert.notEqual(result.code, 0);
    assert.equal(outputBytes, 0, "invalid role must not fall through to CLI help or server startup");
  } finally { await worker.terminate(); }
}
