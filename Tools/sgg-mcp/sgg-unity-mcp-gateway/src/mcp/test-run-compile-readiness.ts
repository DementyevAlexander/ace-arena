/** Strict source-evidence admission for the explicitly opted-in test wait route.
 * This is deliberately separate from legacy compile/recovery predicates. */
export const TEST_RUN_COMPILE_READINESS_CONTRACT = "sgg.unity_mcp.test_run_compile_readiness.v1";

export type TestRunCompileReadiness = {
  state: "ready" | "waitable" | "blocked" | "invalid";
  reason: string;
};

type Evidence = Record<string, unknown>;
const INT_MAX = 2_147_483_647;
const HASH = /^[0-9a-f]{64}$/;
const ROOT_FLAGS = ["ready", "editor_ready", "lifecycle_settled", "compilable", "is_compiling", "is_updating", "is_playing", "terminal_compile_failure_without_reload", "package_refresh_required", "package_refresh_wait_can_advance", "stale_package_resolution", "package_metadata_unavailable", "timed_out", "has_compile_errors"];
const BARRIER_FLAGS = ["required", "requested", "request_issued", "started", "finished", "terminal_compile_failure_without_reload", "reload_started", "post_reload_observed", "settling", "reload_pending", "stable"];
const BACKGROUND_FLAGS = ["observed", "authoritative", "active", "failed", "blocks_readiness", "correlated_native_failure_observed", "native_failure_absence_authoritative", "cancellation_attempted", "work_replayed"];
const COMPILER_QUARTET = ["compiler_source_file_count", "missing_compiler_source_file_count", "unexpected_compiler_source_file_count", "compiler_source_fingerprint"];

export function classifyTestRunCompileReadiness(value: unknown): TestRunCompileReadiness {
  try { return classify(value); }
  catch { return result("invalid", "readiness_shape_invalid"); }
}

function classify(value: unknown): TestRunCompileReadiness {
  if (!record(value) || !flags(value, ROOT_FLAGS) || !text(value, "status", 64) || !text(value, "editor_status", 64)
    || !integer(value, "compile_error_count", INT_MAX)
    || !["waited_ms", "timeout_ms", "last_editor_update_age_ms", "status_duration_ms"].every((key) => integer(value, key, INT_MAX))) return invalid();
  const update = value.package_update;
  const barrier = value.compile_barrier;
  const background = value.background_compiler_progress;
  if (!["package_update", "compile_barrier", "background_compiler_progress"].every((key) => own(value, key))
    || !record(update) || !record(barrier) || !record(background)
    || !packageShape(update) || !barrierShape(barrier) || !backgroundShape(background)) return invalid();
  const inventory = update.local_package_code_inventory as Evidence;
  if (value.package_refresh_required !== update.package_refresh_required || update.refresh_pending !== update.package_refresh_required
    || value.package_refresh_wait_can_advance !== update.refresh_wait_can_advance || value.stale_package_resolution !== update.stale_package_resolution
    || value.editor_ready !== (value.editor_status === "ready")
    || value.compilable !== (!value.has_compile_errors && !value.package_refresh_required && !background.failed)
    || value.lifecycle_settled !== barrier.stable
    || value.terminal_compile_failure_without_reload !== barrier.terminal_compile_failure_without_reload
    || (value.has_compile_errors === false && value.compile_error_count !== 0)) return invalid();

  const expectedReady = value.editor_ready === true && barrier.stable === true && value.has_compile_errors === false
    && value.package_refresh_required === false && background.blocks_readiness === false;
  if (value.status !== "expected_script_not_imported" && value.ready !== expectedReady) return invalid();
  if (value.status === "expected_script_not_imported" && value.ready !== false) return invalid();

  if (value.timed_out) return result("blocked", "compile_wait_timed_out");
  if (value.is_playing) return result("blocked", "editor_is_playing");
  if (value.has_compile_errors || value.terminal_compile_failure_without_reload || barrier.terminal_compile_result === "failed") return result("blocked", "compile_errors_or_failed_barrier");
  if (background.correlated_native_failure_observed || background.failed) return result("blocked", "background_compilation_failed");
  if (value.package_metadata_unavailable || update.fingerprint_failure !== null) return result("blocked", "package_metadata_unavailable");
  if (value.stale_package_resolution || inventory.status === "stale") return result("blocked", "package_inventory_stale");
  if (value.status === "expected_script_not_imported") return result("blocked", "expected_script_not_imported");

  const currentInventory = inventoryCurrent(inventory);
  if (value.package_refresh_required) {
    if (!["package_refresh_required", "unavailable"].includes(String(update.status)) || value.status !== "package_refresh_required") return invalid();
    if (!value.package_refresh_wait_can_advance) return result("blocked", "package_refresh_requires_explicit_action");
    const initialInventory = inventory.status === "unavailable" && inventory.failure_reason === "not_scanned";
    if (!currentInventory && !initialInventory) return result("blocked", "package_inventory_unavailable");
    if (unavailableBarrier(barrier) || background.observed && !background.authoritative) return result("blocked", "compile_evidence_unavailable");
    return result("waitable", "package_refresh_pending");
  }

  if (update.status !== "current" || update.pending_fingerprint !== "" || update.refresh_wait_can_advance !== false
    || !hash(update.fingerprint)) return result("blocked", "package_update_not_current");
  if (!currentInventory) return result("blocked", inventory.status === "unavailable" ? "package_inventory_unavailable" : "package_inventory_not_current");
  if (unavailableBarrier(barrier) || background.observed && !background.authoritative) return result("blocked", "compile_evidence_unavailable");
  if (background.observed && background.package_fingerprint !== update.fingerprint) return invalid();

  if (value.status === "ready") {
    if (!value.ready || !value.editor_ready || !value.lifecycle_settled || !value.compilable
      || value.is_compiling || value.is_updating || background.blocks_readiness) return invalid();
    return result("ready", "ready");
  }
  if (value.ready) return invalid();
  if (value.status === "background_compilation_pending" && background.observed === true && background.authoritative === true
    && background.blocks_readiness === true && ["active", "settling"].includes(String(background.state))) return result("waitable", "background_compilation_pending");
  if (value.status === "settling" && barrier.settling === true && !barrier.stable) return result("waitable", "compile_barrier_settling");
  if (value.status === "compiling" && value.editor_status === "compiling" && value.is_compiling === true && !value.editor_ready) return result("waitable", "editor_compiling");
  if (value.status === "updating" && value.editor_status === "updating" && value.is_updating === true && !value.editor_ready) return result("waitable", "editor_updating");
  if (value.status === "reloading" && value.editor_status === "reloading" && !value.editor_ready) return result("waitable", "editor_reloading");
  return result("blocked", "unsupported_compile_status");
}

function packageShape(value: Evidence): boolean {
  if (!flags(value, ["refresh_pending", "package_refresh_required", "refresh_wait_can_advance", "stale_package_resolution"])
    || !text(value, "status", 32) || !text(value, "fingerprint", 96) || !text(value, "pending_fingerprint", 96)
    || !integer(value, "refresh_count", INT_MAX) || !own(value, "fingerprint_failure")) return false;
  if (value.fingerprint_failure !== null) {
    if (!record(value.fingerprint_failure) || !["evidence_class", "reason", "file"].every((key) => text(value.fingerprint_failure as Evidence, key, 64))) return false;
  }
  if (!["current", "unavailable", "package_refresh_required", "stale_package_resolution"].includes(String(value.status))) return false;
  return own(value, "local_package_code_inventory") && record(value.local_package_code_inventory) && inventoryShape(value.local_package_code_inventory);
}

function inventoryShape(value: Evidence): boolean {
  if (!text(value, "status", 32) || !["current", "stale", "unavailable"].includes(String(value.status))
    || !text(value, "failure_reason", 64) || !text(value, "mismatch_package", 128)
    || !integer(value, "local_package_count", 64)
    || !["source_file_count", "asset_database_file_count", "missing_asset_file_count", "unexpected_asset_file_count"].every((key) => integer(value, key, 16_384))
    || !["source_fingerprint", "asset_database_fingerprint", "source_state_fingerprint"].every((key) => text(value, key, 128))) return false;
  const compilerKeys = COMPILER_QUARTET.filter((key) => own(value, key)).length;
  return compilerKeys === 0 || compilerKeys === 4
    && COMPILER_QUARTET.slice(0, 3).every((key) => integer(value, key, 16_384)) && text(value, "compiler_source_fingerprint", 128);
}

function inventoryCurrent(value: Evidence): boolean {
  return value.status === "current" && value.failure_reason === "" && value.mismatch_package === ""
    && value.missing_asset_file_count === 0 && value.unexpected_asset_file_count === 0
    && value.source_file_count === value.asset_database_file_count && hash(value.source_fingerprint)
    && value.source_fingerprint === value.asset_database_fingerprint && hash(value.source_state_fingerprint)
    && (!own(value, "compiler_source_file_count") || value.missing_compiler_source_file_count === 0
      && value.unexpected_compiler_source_file_count === 0 && hash(value.compiler_source_fingerprint));
}

function barrierShape(value: Evidence): boolean {
  if (!flags(value, BARRIER_FLAGS) || !text(value, "state", 32) || !text(value, "operation_id", 32)
    || !text(value, "source_instance_id", 256) || !text(value, "failure", 128)
    || !text(value, "terminal_compile_result", 16) || !["unknown", "clean", "failed"].includes(String(value.terminal_compile_result))
    || !integer(value, "terminal_compile_error_count", 100)) return false;
  if (value.requested) {
    if (!/^[0-9a-f]{32}$/.test(String(value.operation_id)) || String(value.source_instance_id).trim().length === 0) return false;
  } else if (value.operation_id !== "" || value.source_instance_id !== "" || value.request_issued || value.started || value.finished
    || value.reload_started || value.post_reload_observed || value.terminal_compile_result !== "unknown" || value.terminal_compile_error_count !== 0) return false;
  if (value.started && !value.request_issued || value.finished && !value.started) return false;
  const verified = value.state === "verified" && value.requested === true && value.request_issued === true && value.started === true && value.finished === true;
  if (value.state === "verified" && !verified || value.state === "not_required" && (value.required || value.requested)
    || value.terminal_compile_result === "clean" && value.terminal_compile_error_count !== 0) return false;
  const superseded = supersededBarrier(value);
  const reloadPending = verified && value.terminal_compile_result === "clean" && !value.post_reload_observed;
  const settling = value.state === "pending" || reloadPending || verified && value.terminal_compile_result === "unknown";
  const stable = !value.required && (value.state === "not_required" || superseded) || verified && value.terminal_compile_result === "clean" && value.post_reload_observed === true;
  const terminalFailure = verified && value.terminal_compile_result === "failed" && !value.reload_started;
  return value.reload_pending === reloadPending && value.settling === settling && value.stable === stable
    && value.terminal_compile_failure_without_reload === terminalFailure && !(stable && unavailableBarrier(value));
}

function supersededBarrier(value: Evidence): boolean {
  return value.state === "abandoned" && value.requested === true
    && ["runtime_replaced_before_compilation_started", "runtime_replaced_without_correlated_reload_proof"].includes(String(value.failure));
}

function unavailableBarrier(value: Evidence): boolean {
  return !["not_required", "verified", "pending", "abandoned"].includes(String(value.state))
    || value.state === "abandoned" && (value.required === true || !supersededBarrier(value));
}

function backgroundShape(value: Evidence): boolean {
  if (!flags(value, BACKGROUND_FLAGS) || !text(value, "state", 32) || !text(value, "category", 32)
    || !["none", "compute_variant_compile", "shader_variant_compile", "native_compiler", "unknown"].includes(String(value.category))
    || !integer(value, "process_id", INT_MAX) || value.process_id === 0
    || !integer(value, "relevant_item_count", 256) || !integer(value, "active_item_count", 256)
    || (value.active_item_count as number) > (value.relevant_item_count as number)
    || !["catalog_generation", "compilation_generation", "completed_compilation_generation"].every((key) => integer(value, key, Number.MAX_SAFE_INTEGER))
    || (value.completed_compilation_generation as number) > (value.compilation_generation as number)
    || !own(value, "progress_permille") || value.progress_permille !== null && !integer(value, "progress_permille", 1000)
    || !text(value, "package_fingerprint", 128) || !text(value, "observed_utc", 64) || !text(value, "failure_reason", 128)
    || !text(value, "native_failure_category", 64) || !text(value, "native_failure_observed_utc", 64)
    || value.native_failure_absence_authoritative !== false || value.cancellation_attempted !== false || value.work_replayed !== false) return false;
  const blocks = value.observed === true && (value.authoritative !== true || value.active === true || value.failed === true || value.state !== "complete");
  if (value.blocks_readiness !== blocks) return false;
  if (value.observed === false) {
    return value.authoritative === false && value.active === false && value.failed === false && value.state === "unobserved" && value.category === "none"
      && value.relevant_item_count === 0 && value.active_item_count === 0 && value.progress_permille === null
      && value.catalog_generation === 0 && value.compilation_generation === 0 && value.completed_compilation_generation === 0
      && value.package_fingerprint === "" && value.observed_utc === "" && value.failure_reason === ""
      && value.correlated_native_failure_observed === false && value.native_failure_category === "none" && value.native_failure_observed_utc === "";
  }
  if (!timestamp(value.observed_utc) || value.package_fingerprint !== "" && !hash(value.package_fingerprint)) return false;
  if (value.correlated_native_failure_observed === true && (value.native_failure_category === "none" || !timestamp(value.native_failure_observed_utc))) return false;
  if (value.state === "complete") return value.authoritative === true && value.active === false && value.failed === false && value.active_item_count === 0;
  if (value.state === "active") return value.authoritative === true && value.active === true;
  if (value.state === "settling") return value.authoritative === true && value.active === false && value.active_item_count === 0;
  return true;
}

function record(value: unknown): value is Evidence { return typeof value === "object" && value !== null && !Array.isArray(value); }
function own(value: Evidence, key: string): boolean { return Object.hasOwn(value, key); }
function flags(value: Evidence, keys: string[]): boolean { return keys.every((key) => own(value, key) && typeof value[key] === "boolean"); }
function text(value: Evidence, key: string, max: number): boolean { return own(value, key) && typeof value[key] === "string" && (value[key] as string).length <= max; }
function integer(value: Evidence, key: string, max: number): boolean { return own(value, key) && typeof value[key] === "number" && Number.isSafeInteger(value[key]) && (value[key] as number) >= 0 && (value[key] as number) <= max; }
function hash(value: unknown): boolean { return typeof value === "string" && HASH.test(value); }
function timestamp(value: unknown): boolean {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(value)) return false;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) && new Date(parsed).toISOString().slice(0, 19) === value.slice(0, 19);
}
function invalid(): TestRunCompileReadiness { return result("invalid", "readiness_shape_invalid"); }
function result(state: TestRunCompileReadiness["state"], reason: string): TestRunCompileReadiness { return { state, reason }; }
