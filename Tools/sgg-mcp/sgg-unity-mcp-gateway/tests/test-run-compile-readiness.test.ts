import { describe, expect, it } from "vitest";

import { TEST_RUN_COMPILE_READINESS_CONTRACT, classifyTestRunCompileReadiness } from "../src/mcp/test-run-compile-readiness.js";

type Evidence = Record<string, unknown>;
type State = ReturnType<typeof classifyTestRunCompileReadiness>["state"];
type Scope = "root" | "package" | "inventory" | "barrier" | "background";

const INT_MAX = 2_147_483_647;
const EMPTY_HASH = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
const PACKAGE_HASH = "a".repeat(64);
const OTHER_HASH = "b".repeat(64);
const OPERATION_ID = "c".repeat(32);
const OBSERVED_UTC = "2026-10-08T12:00:00.0000000Z";
const ROOT_FLAGS = ["ready", "editor_ready", "lifecycle_settled", "compilable", "is_compiling", "is_updating", "is_playing", "terminal_compile_failure_without_reload", "package_refresh_required", "package_refresh_wait_can_advance", "stale_package_resolution", "package_metadata_unavailable", "timed_out", "has_compile_errors"];
const COMPILER_QUARTET = ["compiler_source_file_count", "missing_compiler_source_file_count", "unexpected_compiler_source_file_count", "compiler_source_fingerprint"];
const REASONS = ["readiness_shape_invalid", "compile_wait_timed_out", "editor_is_playing", "compile_errors_or_failed_barrier", "background_compilation_failed", "package_metadata_unavailable", "package_inventory_stale", "expected_script_not_imported", "package_refresh_requires_explicit_action", "package_inventory_unavailable", "compile_evidence_unavailable", "package_refresh_pending", "package_update_not_current", "package_inventory_not_current", "ready", "background_compilation_pending", "compile_barrier_settling", "editor_compiling", "editor_updating", "editor_reloading", "unsupported_compile_status"];

// Independent admission projections of the C# producers, not output from the classifier.
function makeReadyDto(compacted = false): Evidence {
  const inventory: Evidence = {
    status: "current", failure_reason: "", mismatch_package: "",
    local_package_count: 0, source_file_count: 0, asset_database_file_count: 0,
    missing_asset_file_count: 0, unexpected_asset_file_count: 0,
    source_fingerprint: EMPTY_HASH, asset_database_fingerprint: EMPTY_HASH, source_state_fingerprint: EMPTY_HASH,
    ...(!compacted ? { compiler_source_file_count: 0, missing_compiler_source_file_count: 0, unexpected_compiler_source_file_count: 0, compiler_source_fingerprint: EMPTY_HASH } : {}),
  };
  return {
    status: "ready", editor_status: "ready",
    ready: true, editor_ready: true, lifecycle_settled: true, compilable: true,
    is_compiling: false, is_updating: false, is_playing: false,
    terminal_compile_failure_without_reload: false,
    package_refresh_required: false, package_refresh_wait_can_advance: false,
    stale_package_resolution: false, package_metadata_unavailable: false,
    timed_out: false, has_compile_errors: false, compile_error_count: 0,
    waited_ms: 0, timeout_ms: 0, last_editor_update_age_ms: 0, status_duration_ms: 0,
    has_unsaved_scene_changes: false, dirty_scene_count: 0, dirty_scenes: [],
    local_package_inventory_diagnostics: null,
    package_update: {
      status: "current", auto_refresh_enabled: "unknown",
      refresh_pending: false, package_refresh_required: false, refresh_wait_can_advance: false,
      stale_package_resolution: false, fingerprint: PACKAGE_HASH, pending_fingerprint: "",
      refresh_count: 0, last_refresh_utc: "", last_observed_utc: "", last_transition_utc: "",
      fingerprint_failure: null, message: compacted ? "~" : "Package/runtime files are current.",
      local_package_code_inventory: inventory,
    },
    compile_barrier: {
      state: "not_required", required: false, operation_id: "", source_instance_id: "",
      requested: false, request_issued: false, started: false, finished: false,
      terminal_compile_result: "unknown", terminal_compile_error_count: 0,
      terminal_compile_observed_utc: "", terminal_compile_failure_without_reload: false,
      reload_started: false, reload_started_utc: "", reload_source_instance_id: "",
      replacement_instance_id: "", post_reload_observed: false, post_reload_observed_utc: "",
      settling: false, reload_pending: false, stable: true,
      requested_utc: "", request_issued_utc: "", started_utc: "", finished_utc: "",
      observed_generation: 0, request_generation: 0, started_generation: 0, finished_generation: 0,
      max_operation_age_ms: 3_600_000, recovery: "", previous_failure: "",
      auto_refresh_enabled: "unknown", failure: "", next_valid_actions: ["compile.wait_ready", "compile.status"],
    },
    background_compiler_progress: {
      observed: false, authoritative: false, active: false, failed: false, blocks_readiness: false,
      state: "unobserved", category: "none", relevant_item_count: 0, active_item_count: 0,
      progress_permille: null, process_id: 12345, catalog_generation: 0,
      compilation_generation: 0, completed_compilation_generation: 0,
      package_fingerprint: "", observed_utc: "", failure_reason: "",
      correlated_native_failure_observed: false, native_failure_category: "none",
      native_failure_observed_utc: "", native_failure_absence_authoritative: false,
      cancellation_attempted: false, work_replayed: false,
    },
  };
}

function at(dto: Evidence, scope: Scope): Evidence {
  if (scope === "root") return dto;
  if (scope === "inventory") return (dto.package_update as Evidence).local_package_code_inventory as Evidence;
  const key = { package: "package_update", barrier: "compile_barrier", background: "background_compiler_progress" }[scope];
  return dto[key] as Evidence;
}

function observedBackground(dto: Evidence, state: "complete" | "active" | "settling" = "complete"): Evidence {
  const active = state === "active";
  Object.assign(at(dto, "background"), {
    observed: true, authoritative: true, active, failed: false, blocks_readiness: state !== "complete",
    state, category: active ? "compute_variant_compile" : "none",
    relevant_item_count: active ? 1 : 0, active_item_count: active ? 1 : 0, progress_permille: active ? 500 : null,
    catalog_generation: 8, compilation_generation: 5, completed_compilation_generation: 5,
    package_fingerprint: PACKAGE_HASH, observed_utc: OBSERVED_UTC,
  });
  if (state !== "complete") Object.assign(dto, { status: "background_compilation_pending", editor_status: "busy", editor_ready: false, ready: false });
  return dto;
}

function verifiedBarrier(dto: Evidence, required = false): Evidence {
  Object.assign(at(dto, "barrier"), {
    state: "verified", required, requested: true, operation_id: OPERATION_ID, source_instance_id: "game-old",
    request_issued: true, started: true, finished: true, terminal_compile_result: "clean",
    reload_started: true, post_reload_observed: true,
    requested_utc: "2026-10-08T11:59:00.0000000Z", request_issued_utc: "2026-10-08T11:59:01.0000000Z",
    started_utc: "2026-10-08T11:59:02.0000000Z", finished_utc: "2026-10-08T11:59:03.0000000Z",
    terminal_compile_observed_utc: "2026-10-08T11:59:03.0000000Z", reload_started_utc: "2026-10-08T11:59:02.0000000Z",
    reload_source_instance_id: "game-old", replacement_instance_id: "game-new", post_reload_observed_utc: OBSERVED_UTC,
    observed_generation: 5, request_generation: 5, started_generation: 5, finished_generation: 5,
    failure: "none", recovery: "none", previous_failure: "none",
  });
  return dto;
}

function pendingBarrier(dto: Evidence): Evidence {
  Object.assign(at(dto, "barrier"), {
    state: "pending", requested: true, operation_id: OPERATION_ID, source_instance_id: "game-old",
    request_issued: true, started: true, finished: false, stable: false, settling: true,
    requested_utc: "2026-10-08T11:59:00.0000000Z", request_issued_utc: "2026-10-08T11:59:01.0000000Z",
    started_utc: "2026-10-08T11:59:02.0000000Z", observed_generation: 5, request_generation: 5, started_generation: 5,
    failure: "none", recovery: "none", previous_failure: "none",
  });
  Object.assign(dto, { status: "settling", ready: false, lifecycle_settled: false });
  return dto;
}

function packagePending(dto: Evidence, canAdvance = true): Evidence {
  Object.assign(at(dto, "package"), { status: "package_refresh_required", refresh_pending: true, package_refresh_required: true, refresh_wait_can_advance: canAdvance });
  Object.assign(dto, { status: "package_refresh_required", editor_status: "updating", editor_ready: false, ready: false, compilable: false, package_refresh_required: true, package_refresh_wait_can_advance: canAdvance });
  return dto;
}

function unavailableInventory(dto: Evidence, reason: string): Evidence {
  Object.assign(at(dto, "inventory"), {
    status: "unavailable", failure_reason: reason, source_fingerprint: "", asset_database_fingerprint: "",
    source_state_fingerprint: "", compiler_source_fingerprint: "",
  });
  return dto;
}

function classified(value: unknown) {
  const result = classifyTestRunCompileReadiness(value);
  expect(Object.keys(result).sort()).toEqual(["reason", "state"]);
  expect(["ready", "waitable", "blocked", "invalid"]).toContain(result.state);
  expect(REASONS).toContain(result.reason);
  return result;
}

function expectState(value: unknown, state: State, label?: string): void {
  expect(classified(value).state, label).toBe(state);
}

function expectRefused(value: unknown, label?: string): void {
  expect(["blocked", "invalid"], label).toContain(classified(value).state);
}

const ROOT_OTHER_FIELDS = ["status", "editor_status", "compile_error_count", "waited_ms", "timeout_ms", "last_editor_update_age_ms", "status_duration_ms", "package_update", "compile_barrier", "background_compiler_progress"];
const REQUIRED_NESTED_FIELDS: Array<{ scope: Scope; keys: string[] }> = [
  { scope: "package", keys: ["status", "refresh_pending", "package_refresh_required", "refresh_wait_can_advance", "stale_package_resolution", "fingerprint", "pending_fingerprint", "refresh_count", "fingerprint_failure", "local_package_code_inventory"] },
  { scope: "inventory", keys: ["status", "failure_reason", "mismatch_package", "local_package_count", "source_file_count", "asset_database_file_count", "missing_asset_file_count", "unexpected_asset_file_count", "source_fingerprint", "asset_database_fingerprint", "source_state_fingerprint", ...COMPILER_QUARTET] },
  { scope: "barrier", keys: ["state", "required", "operation_id", "source_instance_id", "requested", "request_issued", "started", "finished", "terminal_compile_result", "terminal_compile_error_count", "terminal_compile_failure_without_reload", "reload_started", "post_reload_observed", "settling", "reload_pending", "stable", "failure"] },
  { scope: "background", keys: ["observed", "authoritative", "active", "failed", "blocks_readiness", "state", "category", "relevant_item_count", "active_item_count", "progress_permille", "process_id", "catalog_generation", "compilation_generation", "completed_compilation_generation", "package_fingerprint", "observed_utc", "failure_reason", "correlated_native_failure_observed", "native_failure_category", "native_failure_observed_utc", "native_failure_absence_authoritative", "cancellation_attempted", "work_replayed"] },
];

describe("test.run compile-readiness source DTO classification", () => {
  it("advertises the exact opt-in contract without depending on a bridge or dispatcher", () => {
    expect(TEST_RUN_COMPILE_READINESS_CONTRACT).toBe("sgg.unity_mcp.test_run_compile_readiness.v1");
  });

  it.each([false, true])("accepts current zero-package inventory with compacted=%s and idle Unobserved evidence", (compacted) => {
    expectState(makeReadyDto(compacted), "ready");
  });

  it.each([false, true])("accepts an already verified clean barrier with required=%s without issuing a refresh", (required) => {
    expectState(verifiedBarrier(makeReadyDto(), required), "ready");
  });

  it.each(["runtime_replaced_before_compilation_started", "runtime_replaced_without_correlated_reload_proof"])("accepts a non-required source-superseded barrier: %s", (failure) => {
    const dto = makeReadyDto();
    Object.assign(at(dto, "barrier"), { state: "abandoned", requested: true, operation_id: OPERATION_ID, source_instance_id: "game-old", failure });
    expectState(dto, "ready");
  });

  it("accepts matching source/asset evidence without equating independent state/compiler fingerprint domains", () => {
    const dto = makeReadyDto();
    Object.assign(at(dto, "inventory"), { local_package_count: 2, source_file_count: 5, asset_database_file_count: 5, source_fingerprint: PACKAGE_HASH, asset_database_fingerprint: PACKAGE_HASH, source_state_fingerprint: OTHER_HASH, compiler_source_file_count: 3, compiler_source_fingerprint: "d".repeat(64) });
    expectState(dto, "ready");
  });

  it.each(ROOT_FLAGS)("requires an own root boolean %s", (key) => {
    const dto = makeReadyDto();
    delete dto[key];
    expectState(dto, "invalid", key);
  });

  it.each(ROOT_FLAGS)("does not coerce the root boolean %s", (key) => {
    const dto = makeReadyDto();
    dto[key] = String(dto[key]);
    expectState(dto, "invalid", key);
  });

  it("does not accept inherited root booleans", () => {
    for (const key of ROOT_FLAGS) {
      const dto = makeReadyDto();
      Object.setPrototypeOf(dto, { [key]: dto[key] });
      delete dto[key];
      expectState(dto, "invalid", key);
    }
  });

  it.each(["missing", "wrong_type", "inherited"])("requires own root scalars and evidence objects: %s", (mutation) => {
    for (const key of ROOT_OTHER_FIELDS) {
      const dto = makeReadyDto();
      if (mutation === "inherited") Object.setPrototypeOf(dto, { [key]: dto[key] });
      if (mutation === "wrong_type") dto[key] = Array.isArray(dto[key]) || typeof dto[key] === "object" ? [] : null;
      else delete dto[key];
      expectState(dto, "invalid", `${mutation}: ${key}`);
    }
  });

  for (const mutation of ["missing", "wrong_type", "inherited"] as const) {
    it.each(REQUIRED_NESTED_FIELDS)(`requires own $scope evidence fields: ${mutation}`, ({ scope, keys }) => {
      for (const key of keys) {
        const dto = makeReadyDto();
        const target = at(dto, scope);
        if (mutation === "inherited") Object.setPrototypeOf(target, { [key]: target[key] });
        if (mutation === "wrong_type") target[key] = typeof target[key] === "string" ? false : "not-the-source-type";
        else delete target[key];
        expectState(dto, "invalid", `${scope}.${key}: ${mutation}`);
      }
    });
  }

  it.each([
    { label: "null", value: null }, { label: "undefined", value: undefined },
    { label: "array", value: [] }, { label: "string", value: "ready" },
    { label: "number", value: 1 }, { label: "boolean", value: true },
  ])("refuses non-record input $label", ({ value }) => {
    expectState(value, "invalid");
  });

  it.each(["compiling", "updating", "reloading"])("waits for a clean source Editor state: %s", (status) => {
    const dto = makeReadyDto();
    Object.assign(dto, { status, editor_status: status, ready: false, editor_ready: false, is_compiling: status === "compiling", is_updating: status === "updating" });
    expectState(dto, "waitable");
  });

  it("waits for a pending barrier while the Editor is ready and compilable", () => {
    const dto = pendingBarrier(makeReadyDto());
    expect(dto).toMatchObject({ editor_ready: true, compilable: true, ready: false, lifecycle_settled: false });
    expectState(dto, "waitable");
  });

  it("waits for verified clean compilation whose post-reload observation is still pending", () => {
    const dto = verifiedBarrier(makeReadyDto());
    Object.assign(at(dto, "barrier"), { post_reload_observed: false, post_reload_observed_utc: "", replacement_instance_id: "", reload_pending: true, settling: true, stable: false });
    Object.assign(dto, { status: "settling", ready: false, lifecycle_settled: false });
    expectState(dto, "waitable");
  });

  it.each(["active", "settling"] as const)("waits for the actual observed background producer state %s", (state) => {
    // BackgroundCompilerProgress.Evaluate emits active:false during its clear-settle window.
    expectState(observedBackground(makeReadyDto(), state), "waitable");
  });

  it("uses background-pending status ahead of a simultaneously settling barrier", () => {
    expectState(observedBackground(pendingBarrier(makeReadyDto()), "active"), "waitable");
  });

  it.each([false, true])("waits for advanceable package validation with compacted current inventory=%s", (compacted) => {
    const dto = packagePending(makeReadyDto(compacted));
    expect(dto.compilable).toBe(false);
    expectState(dto, "waitable");
  });

  it("waits for explicit not_scanned inventory during initial unavailable package validation", () => {
    const dto = unavailableInventory(packagePending(makeReadyDto()), "not_scanned");
    Object.assign(at(dto, "package"), { status: "unavailable", fingerprint: "", pending_fingerprint: "" });
    expectState(dto, "waitable");
  });

  it("also accepts explicit not_scanned after the monitor publishes package_refresh_required", () => {
    expectState(unavailableInventory(packagePending(makeReadyDto()), "not_scanned"), "waitable");
  });

  it("blocks a timed-out DTO even when the source readiness aggregate remains true", () => {
    const dto = makeReadyDto();
    dto.timed_out = true;
    expect(classified(dto)).toEqual({ state: "blocked", reason: "compile_wait_timed_out" });
  });

  it("blocks Play Mode", () => {
    const dto = makeReadyDto();
    Object.assign(dto, { status: "playing", editor_status: "playing", is_playing: true, editor_ready: false, ready: false });
    expectState(dto, "blocked");
  });

  it("blocks compiler errors with a source-consistent compilable/ready aggregate", () => {
    const dto = makeReadyDto();
    Object.assign(dto, { status: "compilation_failed", ready: false, compilable: false, has_compile_errors: true, compile_error_count: 1 });
    expectState(dto, "blocked");
  });

  it("blocks terminal failed compilation without claiming post-reload verification", () => {
    const dto = verifiedBarrier(makeReadyDto(), true);
    Object.assign(at(dto, "barrier"), { terminal_compile_result: "failed", terminal_compile_error_count: 1, terminal_compile_failure_without_reload: true, reload_started: false, reload_started_utc: "", reload_source_instance_id: "", post_reload_observed: false, post_reload_observed_utc: "", replacement_instance_id: "", stable: false });
    Object.assign(dto, { status: "compilation_failed", ready: false, lifecycle_settled: false, compilable: false, has_compile_errors: true, compile_error_count: 1, terminal_compile_failure_without_reload: true });
    expectState(dto, "blocked");
  });

  it("blocks native-failure evidence during otherwise active background work", () => {
    const dto = observedBackground(makeReadyDto(), "active");
    Object.assign(at(dto, "background"), { correlated_native_failure_observed: true, native_failure_category: "compute_variant_compile", native_failure_observed_utc: OBSERVED_UTC });
    expectState(dto, "blocked");
  });

  it("blocks failed background work with no active items", () => {
    const dto = observedBackground(makeReadyDto());
    Object.assign(at(dto, "background"), { state: "failed", category: "compute_variant_compile", failed: true, blocks_readiness: true, relevant_item_count: 1, failure_reason: "correlated_background_compiler_failure", correlated_native_failure_observed: true, native_failure_category: "compute_variant_compile", native_failure_observed_utc: OBSERVED_UTC });
    Object.assign(dto, { status: "background_compilation_failed", editor_status: "busy", editor_ready: false, ready: false, compilable: false });
    expectState(dto, "blocked");
  });

  it("blocks observed non-authoritative background evidence", () => {
    const dto = observedBackground(makeReadyDto());
    Object.assign(at(dto, "background"), { state: "unavailable", category: "unknown", authoritative: false, blocks_readiness: true, failure_reason: "progress_api_unavailable" });
    Object.assign(dto, { status: "background_compilation_unavailable", editor_status: "busy", editor_ready: false, ready: false });
    expectState(dto, "blocked");
  });

  it("blocks metadata unavailability even when package waiting can advance", () => {
    const dto = packagePending(makeReadyDto());
    at(dto, "package").status = "unavailable";
    Object.assign(dto, { status: "package_metadata_unavailable", package_metadata_unavailable: true });
    expectState(dto, "blocked");
  });

  it("blocks source fingerprint failure without interpreting it as an initial scan", () => {
    const dto = unavailableInventory(packagePending(makeReadyDto()), "snapshot_unavailable");
    Object.assign(at(dto, "package"), { status: "unavailable", fingerprint_failure: { evidence_class: "package_runtime", reason: "missing_file", file: "package_root" } });
    expectState(dto, "blocked");
  });

  it("blocks stale package resolution despite both advance flags", () => {
    const dto = packagePending(makeReadyDto());
    Object.assign(at(dto, "package"), { status: "stale_package_resolution", stale_package_resolution: true });
    Object.assign(dto, { status: "stale_package_resolution", stale_package_resolution: true });
    expectState(dto, "blocked");
  });

  it("blocks stale inventory during an explicit refresh convergence window", () => {
    const dto = packagePending(makeReadyDto());
    Object.assign(at(dto, "inventory"), { status: "stale", mismatch_package: "com.example.game", missing_asset_file_count: 1, local_package_count: 1, source_file_count: 1, source_fingerprint: OTHER_HASH });
    expectState(dto, "blocked");
  });

  it("blocks package changes requiring a new explicit action", () => {
    expectState(packagePending(makeReadyDto(), false), "blocked");
  });

  it("does not normalize a compacted blank unavailable reason to not_scanned", () => {
    expectState(unavailableInventory(packagePending(makeReadyDto()), ""), "blocked");
  });

  it("requires the unavailable inventory reason to be present", () => {
    const dto = unavailableInventory(packagePending(makeReadyDto()), "not_scanned");
    delete at(dto, "inventory").failure_reason;
    expectState(dto, "invalid");
  });

  it.each(["snapshot_unavailable", "budget_exhausted", "read_exception"])("does not wait on other unavailable inventory reasons: %s", (reason) => {
    expectState(unavailableInventory(packagePending(makeReadyDto()), reason), "blocked");
  });

  it.each(["unavailable", "failed", "unknown"])("blocks unavailable barrier evidence: %s", (state) => {
    const dto = makeReadyDto();
    Object.assign(at(dto, "barrier"), { state, stable: false, failure: "barrier_cache_not_initialized" });
    if (state === "failed") Object.assign(at(dto, "barrier"), { requested: true, operation_id: OPERATION_ID, source_instance_id: "game-old", failure: "runtime_replaced_before_compilation_started" });
    Object.assign(dto, { status: "compile_freshness_unavailable", ready: false, lifecycle_settled: false });
    expectState(dto, "blocked");
  });

  it("does not admit a required abandoned barrier", () => {
    const dto = makeReadyDto();
    Object.assign(at(dto, "barrier"), { state: "abandoned", required: true, requested: true, operation_id: OPERATION_ID, source_instance_id: "game-old", failure: "runtime_replaced_before_compilation_started", stable: false });
    Object.assign(dto, { status: "compile_freshness_unavailable", ready: false, lifecycle_settled: false });
    expectState(dto, "blocked");
  });

  it("blocks an unsupported source status rather than normalizing it to ready", () => {
    const dto = makeReadyDto();
    Object.assign(dto, { status: "future_status", editor_status: "future_status", editor_ready: false, ready: false });
    expectState(dto, "blocked");
  });

  it("blocks expected-script readiness failure without requiring an explicit barrier write", () => {
    const dto = makeReadyDto();
    Object.assign(dto, { status: "expected_script_not_imported", ready: false });
    expectState(dto, "blocked");
  });

  const numericFields: Array<[Scope, string, number]> = [
    ...["compile_error_count", "waited_ms", "timeout_ms", "last_editor_update_age_ms", "status_duration_ms"].map((key): [Scope, string, number] => ["root", key, INT_MAX]),
    ["package", "refresh_count", INT_MAX], ["inventory", "local_package_count", 64],
    ...["source_file_count", "asset_database_file_count", "missing_asset_file_count", "unexpected_asset_file_count", ...COMPILER_QUARTET.slice(0, 3)].map((key): [Scope, string, number] => ["inventory", key, 16_384]),
    ["barrier", "terminal_compile_error_count", 100], ["background", "process_id", INT_MAX],
    ["background", "relevant_item_count", 256], ["background", "active_item_count", 256],
    ...["catalog_generation", "compilation_generation", "completed_compilation_generation"].map((key): [Scope, string, number] => ["background", key, Number.MAX_SAFE_INTEGER]),
    ["background", "progress_permille", 1000],
  ];

  it.each(numericFields)("rejects out-of-source-bounds %s.%s (maximum %i)", (scope, key, max) => {
    for (const value of [-1, 0.5, Number.NaN, Number.POSITIVE_INFINITY, max + 1, Number.MAX_SAFE_INTEGER + 1, "0"]) {
      const dto = makeReadyDto();
      at(dto, scope)[key] = value;
      expectState(dto, "invalid", `${scope}.${key}: ${String(value)}`);
    }
  });

  it("accepts the producer inventory maxima without requiring positive compiler/source equivalence", () => {
    const dto = makeReadyDto();
    Object.assign(at(dto, "inventory"), { local_package_count: 64, source_file_count: 16_384, asset_database_file_count: 16_384, source_fingerprint: PACKAGE_HASH, asset_database_fingerprint: PACKAGE_HASH, source_state_fingerprint: "d".repeat(64), compiler_source_file_count: 8192, compiler_source_fingerprint: OTHER_HASH });
    expectState(dto, "ready");
  });

  it("accepts nonnegative C# int timing boundaries without inventing a smaller DTO limit", () => {
    const dto = makeReadyDto();
    for (const key of ["waited_ms", "timeout_ms", "last_editor_update_age_ms", "status_duration_ms"]) dto[key] = INT_MAX;
    at(dto, "package").refresh_count = INT_MAX;
    expectState(dto, "ready");
  });

  it("accepts observed complete evidence at the safe generation and item-count bounds", () => {
    const dto = observedBackground(makeReadyDto());
    Object.assign(at(dto, "background"), { process_id: INT_MAX, relevant_item_count: 256, catalog_generation: Number.MAX_SAFE_INTEGER, compilation_generation: Number.MAX_SAFE_INTEGER, completed_compilation_generation: Number.MAX_SAFE_INTEGER });
    expectState(dto, "ready");
  });

  it.each([null, 0, 1000])("accepts nullable/bounded active background progress %s", (progress_permille) => {
    const dto = observedBackground(makeReadyDto(), "active");
    Object.assign(at(dto, "background"), { relevant_item_count: 256, active_item_count: 256, progress_permille });
    expectState(dto, "waitable");
  });

  it.each(COMPILER_QUARTET)("rejects a partially omitted compiler evidence quartet: %s", (key) => {
    const dto = makeReadyDto();
    delete at(dto, "inventory")[key];
    expectState(dto, "invalid");
  });

  it.each(["missing_compiler_source_file_count", "unexpected_compiler_source_file_count"])("does not admit a current label with nonzero %s", (key) => {
    const dto = makeReadyDto();
    at(dto, "inventory")[key] = 1;
    expectRefused(dto);
  });

  it("refuses current inventory with inconsistent coverage, fingerprints, or failure metadata", () => {
    for (const mutation of [
      { source_file_count: 1 }, { asset_database_file_count: 1 },
      { missing_asset_file_count: 1 }, { unexpected_asset_file_count: 1 },
      { source_fingerprint: OTHER_HASH }, { asset_database_fingerprint: OTHER_HASH },
      { mismatch_package: "com.example.game" }, { failure_reason: "not_scanned" },
      { status: "future_inventory_status" },
    ]) {
      const dto = makeReadyDto();
      Object.assign(at(dto, "inventory"), mutation);
      expectRefused(dto);
    }
  });

  it.each([0, 1, 2, 3, 4, 5, 6, 7])("accepts source UTC observation timestamps with %i fractional digits", (digits) => {
    const dto = observedBackground(makeReadyDto());
    at(dto, "background").observed_utc = `2026-10-08T12:00:00${digits === 0 ? "" : `.${"0".repeat(digits)}`}Z`;
    expectState(dto, "ready");
  });

  it.each(["", "2026-02-30T12:00:00Z", "2026-10-08T12:00:00.00000000Z", "2026-10-08T12:00:00+00:00", "not-a-date"])("rejects unsupported observation timestamps %s", (observed_utc) => {
    const dto = observedBackground(makeReadyDto());
    at(dto, "background").observed_utc = observed_utc;
    expectState(dto, "invalid");
  });

  it.each<[Scope, string]>([
    ["package", "fingerprint"], ["inventory", "source_fingerprint"],
    ["inventory", "asset_database_fingerprint"], ["inventory", "source_state_fingerprint"],
    ["inventory", "compiler_source_fingerprint"], ["background", "package_fingerprint"],
  ])("does not admit malformed plain SHA-256 evidence at %s.%s", (scope, key) => {
    for (const hash of ["", "a".repeat(63), "a".repeat(65), "A".repeat(64), "g".repeat(64), `sha256:${PACKAGE_HASH}`, "private-canary-hash"]) {
      const dto = observedBackground(makeReadyDto());
      at(dto, scope)[key] = hash;
      expectRefused(dto, `${scope}.${key}`);
    }
  });

  it.each([
    ["root required", (dto: Evidence) => { dto.package_refresh_required = true; }],
    ["nested pending", (dto: Evidence) => { at(dto, "package").refresh_pending = true; }],
    ["top advance", (dto: Evidence) => { dto.package_refresh_wait_can_advance = true; }],
    ["nested advance", (dto: Evidence) => { at(dto, "package").refresh_wait_can_advance = true; }],
    ["top stale", (dto: Evidence) => { dto.stale_package_resolution = true; }],
    ["nested stale", (dto: Evidence) => { at(dto, "package").stale_package_resolution = true; }],
    ["editor ready", (dto: Evidence) => { dto.editor_ready = false; }],
    ["compilable", (dto: Evidence) => { dto.compilable = false; }],
    ["lifecycle settled", (dto: Evidence) => { dto.lifecycle_settled = false; }],
    ["terminal failure", (dto: Evidence) => { dto.terminal_compile_failure_without_reload = true; }],
    ["ready aggregate", (dto: Evidence) => { dto.ready = false; }],
    ["stale error counter", (dto: Evidence) => { dto.compile_error_count = 1; }],
  ] as const)("rejects inconsistent duplicated/aggregate evidence: %s", (_label, mutate) => {
    const dto = makeReadyDto();
    mutate(dto);
    expectState(dto, "invalid");
  });

  it.each(["is_compiling", "is_updating"])("rejects a ready DTO still reporting %s", (key) => {
    const dto = makeReadyDto();
    dto[key] = true;
    expectState(dto, "invalid");
  });

  it("refuses observed progress from a different package fingerprint", () => {
    const dto = observedBackground(makeReadyDto());
    at(dto, "background").package_fingerprint = OTHER_HASH;
    expectState(dto, "invalid");
  });

  it("refuses active item counts above relevant items and completed generations ahead of compilation", () => {
    for (const mutation of [{ active_item_count: 2 }, { completed_compilation_generation: 6 }, { process_id: 0 }]) {
      const dto = observedBackground(makeReadyDto(), "active");
      Object.assign(at(dto, "background"), mutation);
      expectState(dto, "invalid");
    }
  });

  it.each(["native_failure_absence_authoritative", "cancellation_attempted", "work_replayed"])("rejects unsupported background authority/action flag %s", (key) => {
    const dto = makeReadyDto();
    at(dto, "background")[key] = true;
    expectState(dto, "invalid");
  });

  it("requires category and timestamp when native failure is claimed", () => {
    for (const mutation of [{ native_failure_category: "none" }, { native_failure_observed_utc: "" }]) {
      const dto = observedBackground(makeReadyDto(), "active");
      Object.assign(at(dto, "background"), { correlated_native_failure_observed: true, native_failure_category: "compute_variant_compile", native_failure_observed_utc: OBSERVED_UTC }, mutation);
      expectState(dto, "invalid");
    }
  });

  it("rejects non-default Unobserved fields rather than treating absent observations as proof", () => {
    for (const mutation of [{ authoritative: true }, { category: "unknown" }, { relevant_item_count: 1 }, { progress_permille: 0 }, { catalog_generation: 1 }, { observed_utc: OBSERVED_UTC }, { package_fingerprint: PACKAGE_HASH }, { failure_reason: "private-canary-reason" }]) {
      const dto = makeReadyDto();
      Object.assign(at(dto, "background"), mutation);
      expectState(dto, "invalid");
    }
  });

  it("does not accept phase flags without a requested barrier or a valid 32-hex operation identity", () => {
    for (const mutation of [{ request_issued: true }, { started: true }, { finished: true }, { post_reload_observed: true }]) {
      const dto = makeReadyDto();
      Object.assign(at(dto, "barrier"), mutation);
      expectState(dto, "invalid");
    }
    for (const operation_id of ["", "a".repeat(31), "a".repeat(33), "A".repeat(32), "private-canary-operation"]) {
      const dto = verifiedBarrier(makeReadyDto());
      at(dto, "barrier").operation_id = operation_id;
      expectState(dto, "invalid");
    }
  });

  it("requires a bounded nonblank barrier source identity", () => {
    for (const source_instance_id of ["", " ", "x".repeat(257)]) {
      const dto = verifiedBarrier(makeReadyDto());
      at(dto, "barrier").source_instance_id = source_instance_id;
      expectState(dto, "invalid");
    }
    const dto = verifiedBarrier(makeReadyDto());
    at(dto, "barrier").source_instance_id = "x".repeat(256);
    expectState(dto, "ready");
  });

  it("refuses inconsistent barrier-derived flags", () => {
    for (const mutation of [{ request_issued: false }, { started: false }, { finished: false }, { terminal_compile_error_count: 1 }, { stable: false }, { settling: true }, { reload_pending: true }, { terminal_compile_failure_without_reload: true }]) {
      const dto = verifiedBarrier(makeReadyDto());
      Object.assign(at(dto, "barrier"), mutation);
      expectState(dto, "invalid");
    }
  });

  it("does not accept a pending fingerprint or empty-object fingerprint failure as current evidence", () => {
    for (const mutation of [{ pending_fingerprint: OTHER_HASH }, { fingerprint_failure: {} }]) {
      const dto = makeReadyDto();
      Object.assign(at(dto, "package"), mutation);
      expectRefused(dto);
    }
  });

  it("keeps source-compacted package timestamps and configuration telemetry out of admission", () => {
    const dto = makeReadyDto(true);
    for (const key of ["last_refresh_utc", "last_observed_utc", "last_transition_utc", "auto_refresh_enabled", "message"]) delete at(dto, "package")[key];
    expectState(dto, "ready");
  });

  it("does not read diagnostic, detail, or expected-script getters", () => {
    const dto = makeReadyDto();
    let reads = 0;
    const throwIfRead = () => { reads += 1; throw new Error("private-canary-diagnostic"); };
    for (const key of ["compile_diagnostics", "compilation_lifecycle", "compile_progress_window", "local_package_inventory_diagnostics", "expected_script_imports", "diagnostics", "details"]) Object.defineProperty(dto, key, { enumerable: true, get: throwIfRead });
    Object.defineProperty(at(dto, "inventory"), "details", { enumerable: true, get: throwIfRead });
    expectState(dto, "ready");
    expect(reads).toBe(0);
  });

  it("catches critical getter exceptions without copying exception text into its result", () => {
    for (const [scope, key] of [["root", "ready"], ["package", "fingerprint"], ["inventory", "status"], ["barrier", "state"], ["background", "observed"]] as const) {
      const dto = makeReadyDto();
      Object.defineProperty(at(dto, scope), key, { enumerable: true, get: () => { throw new Error("private-canary-exception"); } });
      expect(classified(dto)).toEqual({ state: "invalid", reason: "readiness_shape_invalid" });
    }
  });

  it("does not mutate deeply frozen source evidence or its diagnostic arrays", () => {
    const dto = observedBackground(verifiedBarrier(makeReadyDto()));
    dto.compile_diagnostics = { errors: [{ message: "private-canary-message" }] };
    at(dto, "inventory").details = [{ path: "Packages/com.example.game/Code.cs" }];
    const before = JSON.stringify(dto);
    const freeze = (value: unknown): void => {
      if (typeof value !== "object" || value === null) return;
      for (const child of Object.values(value)) freeze(child);
      Object.freeze(value);
    };
    freeze(dto);
    expectState(dto, "ready");
    expect(JSON.stringify(dto)).toBe(before);
  });
});
