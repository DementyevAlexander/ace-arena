import fs from "node:fs";
import path from "node:path";

import { loadPolicyConfig } from "../config/config.js";
import { TestRunOperationStore, testRunOperationStoreDir } from "./test-run-operation-store.js";
import { TestRunStoreAdminError, TEST_RUN_STORE_ADMIN_DEADLINE_MS } from "./test-run-store-admin.js";
import { testRunStorageOptionsFromConfig } from "./test-run-storage-settings.js";
import { configSnapshotEvidence, configureTestRunStorage, readTestRunStorageConfig } from "./test-run-storage-config.js";

export type TestRunStoreAdminCliOptions = {
  action: "status" | "preview-cleanup" | "cleanup" | "configure";
  configPath: string;
  policyPath?: string;
  confirm: boolean;
  previewFingerprint?: string;
  configFingerprint?: string;
  maxBytes?: number;
  maxBindings?: number;
};
type AdminCliIo = { stdout?: (text: string) => void; env?: NodeJS.ProcessEnv; now?: () => Date };

export function parseTestRunStoreAdminOptions(argv: string[]): TestRunStoreAdminCliOptions {
  let action: TestRunStoreAdminCliOptions["action"] | undefined;
  let configPath: string | undefined;
  let policyPath: string | undefined;
  let previewFingerprint: string | undefined;
  let configFingerprint: string | undefined;
  let maxBytes: number | undefined;
  let maxBindings: number | undefined;
  let confirm = false;
  const seen = new Set<string>();
  for (let index = 0; index < argv.length; index++) {
    const flag = argv[index]!;
    if (seen.has(flag)) throw new Error("duplicate_argument");
    seen.add(flag);
    if (flag === "--confirm") { confirm = true; continue; }
    if (flag === "--json") continue;
    if (!["--test-run-store", "--config", "--policy", "--preview-fingerprint", "--config-fingerprint", "--max-bytes", "--max-bindings", "--format"].includes(flag)) throw new Error("invalid_argument");
    const value = argv[++index];
    if (!value || value.startsWith("--") || value.includes("\0") || value.length > 4096) throw new Error("invalid_argument");
    if (flag === "--test-run-store") {
      if (!["status", "preview-cleanup", "cleanup", "configure"].includes(value)) throw new Error("invalid_action");
      action = value as TestRunStoreAdminCliOptions["action"];
    } else if (flag === "--config") configPath = value;
    else if (flag === "--policy") policyPath = value;
    else if (flag === "--preview-fingerprint") previewFingerprint = value;
    else if (flag === "--config-fingerprint") configFingerprint = value;
    else if (flag === "--max-bytes" || flag === "--max-bindings") {
      if (!/^[1-9][0-9]{0,9}$/.test(value) || !Number.isSafeInteger(Number(value))) throw new TestRunStoreAdminError("invalid_limits");
      if (flag === "--max-bytes") maxBytes = Number(value); else maxBindings = Number(value);
    }
    else if (value !== "json") throw new Error("invalid_format");
  }
  // No environment/default config selection: the native UI names the exact owner config.
  if (!action || !configPath || !path.isAbsolute(configPath)) throw new Error("exact_config_required");
  if (policyPath && !path.isAbsolute(policyPath)) throw new Error("exact_policy_required");
  if (!["cleanup", "configure"].includes(action) && (confirm || previewFingerprint !== undefined || configFingerprint !== undefined)) throw new Error("invalid_confirmation_arguments");
  if (action === "cleanup" && (!confirm || !previewFingerprint || !/^sha256:[0-9a-f]{64}$/.test(previewFingerprint))) throw new TestRunStoreAdminError("confirmation_required");
  if (action === "configure" && (!confirm || !configFingerprint || !/^sha256:[0-9a-f]{64}$/.test(configFingerprint))) throw new TestRunStoreAdminError("confirmation_required");
  if (action === "configure" && (maxBytes === undefined || maxBindings === undefined || maxBytes < 1048576 || maxBytes > 1073741824 || maxBindings < 1 || maxBindings > 1000000)) throw new TestRunStoreAdminError("invalid_limits");
  if (action !== "configure" && (maxBytes !== undefined || maxBindings !== undefined || configFingerprint !== undefined) || action === "configure" && previewFingerprint !== undefined) throw new Error("invalid_settings_arguments");
  return { action, configPath, confirm, ...(policyPath ? { policyPath } : {}), ...(previewFingerprint ? { previewFingerprint } : {}), ...(configFingerprint ? { configFingerprint } : {}), ...(maxBytes !== undefined ? { maxBytes } : {}), ...(maxBindings !== undefined ? { maxBindings } : {}) };
}

export function runTestRunStoreAdminCli(argv: string[], io: AdminCliIo = {}): number {
  const deadlineUnixMs = Date.now() + TEST_RUN_STORE_ADMIN_DEADLINE_MS;
  const stdout = io.stdout ?? ((text: string) => process.stdout.write(text));
  let options: TestRunStoreAdminCliOptions;
  try { options = parseTestRunStoreAdminOptions(argv); }
  catch (error) {
    stdout(`${JSON.stringify({ schema_version: 1, status: "error", error: { code: error instanceof TestRunStoreAdminError ? error.code : "invalid_arguments" } })}\n`);
    return 1;
  }
  try {
    const env = io.env ?? process.env;
    let snapshot = readTestRunStorageConfig(options.configPath, env);
    if (options.policyPath) { assertConfigFile(options.policyPath); loadPolicyConfig(options.policyPath); }
    if (Date.now() >= deadlineUnixMs) throw new TestRunStoreAdminError("deadline_exceeded");
    if (options.action === "configure") snapshot = configureTestRunStorage(options.configPath, { maxBytes: options.maxBytes!, maxBindings: options.maxBindings!, confirm: options.confirm, configFingerprint: options.configFingerprint!, deadlineUnixMs }, env);
    const config = snapshot.config;
    const store = new TestRunOperationStore(testRunOperationStoreDir(config.registry.instances_dir), { ...testRunStorageOptionsFromConfig(config), adminFingerprintScope: `${snapshot.scopeFingerprint}:${snapshot.fingerprint}`, ...(io.now ? { now: io.now } : {}) });
    let data: object;
    if (options.action === "configure") {
      // A completed settings write must not be misreported as failed merely because capacity scanning is unavailable.
      try { data = store.inspectStorage(deadlineUnixMs); }
      catch (error) {
        const settings = testRunStorageOptionsFromConfig(config);
        data = { bytes: null, bindings: null, max_bytes: settings.maxBytes, max_bindings: settings.maxBindings, retention_days: settings.retentionMs / 86400000,
          capacity_state: "unavailable", usage: { bytes_ratio: null, bindings_ratio: null }, scope: "gateway_test_run_owner_metadata", settings_source: "saved_config_restart_required",
          storage_error_code: error instanceof TestRunStoreAdminError ? error.code : "storage_unavailable" };
      }
    } else data = options.action === "status" ? store.inspectStorage(deadlineUnixMs) : options.action === "preview-cleanup" ? store.previewCleanup(deadlineUnixMs)
      : store.cleanupMetadata({ confirm: options.confirm, previewFingerprint: options.previewFingerprint!, nativeConfig: configSnapshotEvidence(options.configPath, snapshot), beforeCommit: () => readTestRunStorageConfig(options.configPath, env).fingerprint === snapshot.fingerprint }, deadlineUnixMs);
    if (readTestRunStorageConfig(options.configPath, env).fingerprint !== snapshot.fingerprint) throw new TestRunStoreAdminError("config_changed");
    stdout(`${JSON.stringify({ schema_version: 1, status: "ok", action: options.action, data: { ...data, config_fingerprint: snapshot.fingerprint, scope_fingerprint: snapshot.scopeFingerprint, ...(options.action === "configure" ? { restart_required: true, configuration_saved: true } : {}) } })}\n`);
    return 0;
  } catch (error) {
    // No owner paths, raw metadata, policy contents, tokens or exception strings leave this route.
    stdout(`${JSON.stringify({ schema_version: 1, status: "error", action: options.action, error: { code: error instanceof TestRunStoreAdminError ? error.code : "storage_unavailable" } })}\n`);
    return 1;
  }
}
function assertConfigFile(file: string): void {
  const stat = fs.lstatSync(file);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size <= 0 || stat.size > 1024 * 1024) throw new Error("config_unavailable");
}
