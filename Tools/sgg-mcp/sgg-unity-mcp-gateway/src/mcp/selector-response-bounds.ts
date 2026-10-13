import type { PublicProjectRecord } from "../registry/project-registry.js";
import type { GatewayResult } from "./results.js";

export const COMPACT_SELECTOR_RECORD_CONTRACT = "sgg.unity_mcp.compact_selector_record.v1";
export const SELECTOR_ERROR_BOUNDS_CONTRACT = "sgg.unity_mcp.selector_error_bounds.v1";
export const MAX_SELECTOR_RECORDS = 16;
export const MAX_SELECTOR_RECORD_BYTES = 32 * 1024;
export const MAX_SELECTOR_ERROR_BYTES = 64 * 1024;
export const MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES = 12 * 1024;
export const COMPACT_NO_LIVE_SELECTOR_ERROR_CONTRACT = "sgg.unity_mcp.compact_no_live_selector_error.v1";

export type CompactSelectorRecord = {
  selector_record_schema_version: 1;
  selector_record_contract: typeof COMPACT_SELECTOR_RECORD_CONTRACT;
  project: string;
  display_name: string | null;
  instance_id: string;
  canonical_project_root: string;
  stale: boolean;
  status: PublicProjectRecord["status"];
  last_heartbeat: string;
  unity_pid: number;
  unity_process_start_utc: string | null;
  heartbeat_generation: number | null;
  has_unsaved_scene_changes: boolean | null;
  dirty_scene_count: number | null;
};

export type SelectorRecordContinuation = {
  tool: "sgg_unity_projects" | "sgg_unity_stale_projects";
  args: Record<string, unknown>;
};

export type SelectorRecordProjection = {
  records: CompactSelectorRecord[];
  summary: {
    selector_record_schema_version: 1;
    selector_record_contract: typeof COMPACT_SELECTOR_RECORD_CONTRACT;
    retained_count: number;
    total_count: number;
    truncated: boolean;
    limits: { max_records: number; max_bytes: number };
    additional_records?: SelectorRecordContinuation;
  };
};

export function compactSelectorRecord(record: PublicProjectRecord): CompactSelectorRecord {
  return {
    selector_record_schema_version: 1,
    selector_record_contract: COMPACT_SELECTOR_RECORD_CONTRACT,
    project: record.project,
    display_name: record.display_name ?? null,
    instance_id: record.instance_id,
    canonical_project_root: record.canonical_project_root,
    stale: record.stale,
    status: record.status,
    last_heartbeat: record.last_heartbeat,
    unity_pid: record.unity_pid,
    unity_process_start_utc: record.unity_process_start_utc || null,
    heartbeat_generation: record.heartbeat_generation ?? null,
    has_unsaved_scene_changes: record.has_unsaved_scene_changes ?? null,
    dirty_scene_count: record.dirty_scene_count ?? null,
  };
}

export function projectSelectorRecords(records: PublicProjectRecord[], additionalRecords: SelectorRecordContinuation): SelectorRecordProjection {
  const projected: CompactSelectorRecord[] = [];
  for (const record of records) {
    if (projected.length >= MAX_SELECTOR_RECORDS) break;
    const candidate = [...projected, compactSelectorRecord(record)];
    if (utf8Bytes(candidate) > MAX_SELECTOR_RECORD_BYTES) break;
    projected.push(candidate[candidate.length - 1]!);
  }
  const truncated = projected.length < records.length;
  return {
    records: projected,
    summary: {
      selector_record_schema_version: 1,
      selector_record_contract: COMPACT_SELECTOR_RECORD_CONTRACT,
      retained_count: projected.length,
      total_count: records.length,
      truncated,
      limits: { max_records: MAX_SELECTOR_RECORDS, max_bytes: MAX_SELECTOR_RECORD_BYTES },
      ...(truncated ? { additional_records: additionalRecords } : {}),
    },
  };
}

export function compactNoLiveStaleSelectorErrorResult(result: GatewayResult, matchesSummary: SelectorRecordProjection["summary"], continuation: SelectorRecordContinuation): GatewayResult {
  if (result.status !== "error" || result.error.code !== "project_stale") return result;
  const details = result.error.details ?? {};
  const totalCount = matchesSummary.total_count;
  const compactSummary = {
    ...matchesSummary,
    retained_count: 0,
    truncated: totalCount > 0,
    bulk_records_omitted: totalCount > 0,
    ...(totalCount > 0 ? { additional_records: continuation } : {}),
  };
  const retainedKeys = [
    "recovery_contract",
    "project_selector",
    "command",
    "command_risk",
    "auto_retry_eligible",
    "auto_retry_attempted",
    "explicit_repeat_required",
    "explicit_repeat_deferred_until_modal_resolution",
    "explicit_repeat_request_id_required",
    "explicit_repeat_request_id_recommended",
    "original_request_id",
    "command_delivery_state",
    "delivery_state",
    "queue_admitted",
    "command_bridge_request_issued",
    "bridge_request_issued",
    "command_replayed",
    "repeatability_warning",
    "explicit_repeat_requirements",
    "recovery_actions",
    "recovery_hint",
  ];
  const compactDetails: Record<string, unknown> = {
    ...Object.fromEntries(retainedKeys.flatMap((key) => details[key] === undefined ? [] : [[key, details[key]]])),
    resolution_code: "project_stale",
    matches: [],
    matches_summary: compactSummary,
    stale_matched_instances: { count: totalCount, evidence_ref: "error.details.matches_summary.total_count" },
    ...(details.test_run_recovery !== undefined ? compactTestRunRecovery(details).test_run_recovery === undefined ? {} : { test_run_recovery: compactTestRunRecovery(details).test_run_recovery } : {}),
  };
  const optionalKeys = ["recovery_hint", "repeatability_warning", "recovery_actions", "explicit_repeat_requirements", "command_risk"];
  let compact = withCompactNoLiveBounds(result, compactDetails, totalCount > 0);
  for (const key of optionalKeys) {
    if (utf8Bytes(compact) <= MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES) break;
    delete compactDetails[key];
    compact = withCompactNoLiveBounds(result, compactDetails, totalCount > 0);
  }
  return compact;
}

function withCompactNoLiveBounds(result: Extract<GatewayResult, { status: "error" }>, details: Record<string, unknown>, truncated: boolean): GatewayResult {
  let measuredBytes = 0;
  let compact: GatewayResult = result;
  for (let iteration = 0; iteration < 4; iteration += 1) {
    compact = {
      ...result,
      ...(truncated ? { truncated: true } : {}),
      error: {
        ...result.error,
        details: {
          ...details,
          compact_response_bounds: {
            contract: COMPACT_NO_LIVE_SELECTOR_ERROR_CONTRACT,
            max_bytes: MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES,
            measured_bytes: measuredBytes,
          },
        },
      },
    };
    const nextMeasuredBytes = utf8Bytes(compact);
    if (nextMeasuredBytes === measuredBytes) break;
    measuredBytes = nextMeasuredBytes;
  }
  return compact;
}

export function selectorRecordReference(record: PublicProjectRecord, evidenceRef: string): Record<string, unknown> {
  return { project: record.project, instance_id: record.instance_id, evidence_ref: evidenceRef };
}

export function dirtySceneSummary(records: PublicProjectRecord[], evidenceRef: string): Record<string, unknown> {
  let knownUnsavedCount = 0;
  let unknownUnsavedCount = 0;
  let dirtySceneCount = 0;
  for (const record of records) {
    if (record.has_unsaved_scene_changes === true) knownUnsavedCount += 1;
    if (record.has_unsaved_scene_changes === undefined) unknownUnsavedCount += 1;
    dirtySceneCount += record.dirty_scene_count ?? 0;
  }
  return {
    instance_count: records.length,
    known_unsaved_instance_count: knownUnsavedCount,
    unknown_unsaved_instance_count: unknownUnsavedCount,
    dirty_scene_count: dirtySceneCount,
    dirty_scene_paths_omitted: true,
    evidence_ref: evidenceRef,
  };
}

export function boundSelectorErrorResult(result: GatewayResult): GatewayResult {
  const compactProjection = result.status === "error" ? compactNoLiveProjection(result.error.details) : undefined;
  if (compactProjection) return compactNoLiveStaleSelectorErrorResult(result, compactProjection.matchesSummary, compactProjection.continuation);
  if (result.status !== "error" || !isSelectorBoundedDetails(result.error.details)) return result;
  if (utf8Bytes(result) <= MAX_SELECTOR_ERROR_BYTES) return result;

  const details = { ...result.error.details };
  const omittedSections: string[] = [];
  const optionalSections = [
    "native_modal_recovery",
    "stale_editor_observation",
    "visible_progress_recovery",
    "bridge_bootstrap_observation",
    "bridge_reload_trace_tail",
    "dirty_scene_state",
    "normalized_from_error",
    "lifecycle_recovery",
    "recovery_call_sequence",
    "recovery_actions",
    "recovery_hint",
    "retry_request_template",
    "explicit_repeat_workflow",
  ];

  for (const section of optionalSections) {
    if (details[section] === undefined) continue;
    delete details[section];
    omittedSections.push(section);
    const bounded = withBoundsMetadata(result, details, omittedSections);
    if (utf8Bytes(bounded) <= MAX_SELECTOR_ERROR_BYTES) return bounded;
  }

  let compactDetails = essentialSelectorErrorDetails(details);
  omittedSections.push("nonessential_error_details");
  if (compactDetails.current_candidates_summary !== undefined) {
    compactDetails = omitRetainedCurrentCandidates(compactDetails);
    if (details.current_candidate_instances !== undefined) omittedSections.push("current_candidate_instances");
    if (details.current_candidate_instance !== undefined) omittedSections.push("current_candidate_instance");
  }
  if (compactDetails.test_run_recovery !== undefined) {
    compactDetails = compactTestRunRecovery(compactDetails);
    omittedSections.push("nonessential_test_run_recovery");
  }
  let bounded = withBoundsMetadata(result, compactDetails, omittedSections);
  if (utf8Bytes(bounded) <= MAX_SELECTOR_ERROR_BYTES) return bounded;

  const withoutRetainedMatches = omitRetainedMatches(compactDetails);
  omittedSections.push("retained_matches");
  bounded = withBoundsMetadata(result, withoutRetainedMatches, omittedSections);
  if (utf8Bytes(bounded) <= MAX_SELECTOR_ERROR_BYTES) return bounded;

  return withBoundsMetadata(result, withoutRetainedMatches, omittedSections);
}

function compactNoLiveProjection(details: Record<string, unknown> | undefined): { matchesSummary: SelectorRecordProjection["summary"]; continuation: SelectorRecordContinuation } | undefined {
  if (!details || !isRecord(details.compact_no_live_selector_projection)) return undefined;
  const projection = details.compact_no_live_selector_projection;
  if (!isRecord(projection.matches_summary) || !isRecord(projection.continuation)) return undefined;
  const summary = projection.matches_summary;
  const continuation = projection.continuation;
  if (summary.selector_record_schema_version !== 1
    || summary.selector_record_contract !== COMPACT_SELECTOR_RECORD_CONTRACT
    || typeof summary.retained_count !== "number"
    || typeof summary.total_count !== "number"
    || typeof summary.truncated !== "boolean"
    || !isRecord(summary.limits)
    || (continuation.tool !== "sgg_unity_projects" && continuation.tool !== "sgg_unity_stale_projects")
    || !isRecord(continuation.args)) return undefined;
  return {
    matchesSummary: summary as SelectorRecordProjection["summary"],
    continuation: continuation as SelectorRecordContinuation,
  };
}

function withBoundsMetadata(result: Extract<GatewayResult, { status: "error" }>, details: Record<string, unknown>, omittedSections: string[]): GatewayResult {
  let measuredBytes = 0;
  let bounded: GatewayResult = result;
  for (let iteration = 0; iteration < 4; iteration += 1) {
    bounded = {
      ...result,
      error: {
        ...result.error,
        details: {
          ...details,
          response_bounds: {
            contract: SELECTOR_ERROR_BOUNDS_CONTRACT,
            truncated: true,
            max_bytes: MAX_SELECTOR_ERROR_BYTES,
            measured_bytes: measuredBytes,
            omitted_sections: [...new Set(omittedSections)],
          },
        },
      },
      truncated: true,
    };
    const nextMeasuredBytes = utf8Bytes(bounded);
    if (nextMeasuredBytes === measuredBytes) break;
    measuredBytes = nextMeasuredBytes;
  }
  return bounded;
}

function essentialSelectorErrorDetails(details: Record<string, unknown>): Record<string, unknown> {
  const keys = [
    "project_selector",
    "resolution_code",
    "matches",
    "matches_summary",
    "current_candidates_summary",
    "command_delivery_state",
    "delivery_state",
    "queue_admitted",
    "command_bridge_request_issued",
    "bridge_request_issued",
    "command_replayed",
    "automatic_retry_allowed",
    "auto_retry_eligible",
    "auto_retry_attempted",
    "explicit_repeat_required",
    "test_run_recovery",
    "recovery_contract",
  ];
  return Object.fromEntries(keys.flatMap((key) => details[key] === undefined ? [] : [[key, details[key]]]));
}

function omitRetainedCurrentCandidates(details: Record<string, unknown>): Record<string, unknown> {
  const currentCandidatesSummary = isRecord(details.current_candidates_summary)
    ? { ...details.current_candidates_summary, retained_count: 0, truncated: true }
    : undefined;
  return {
    ...details,
    ...(currentCandidatesSummary ? { current_candidates_summary: currentCandidatesSummary } : {}),
  };
}

function omitRetainedMatches(details: Record<string, unknown>): Record<string, unknown> {
  const matchesSummary = isRecord(details.matches_summary)
    ? { ...details.matches_summary, retained_count: 0, truncated: true }
    : undefined;
  return {
    ...details,
    ...(details.matches !== undefined ? { matches: [] } : {}),
    ...(matchesSummary ? { matches_summary: matchesSummary } : {}),
  };
}

function compactTestRunRecovery(details: Record<string, unknown>): Record<string, unknown> {
  if (!isRecord(details.test_run_recovery)) return details;
  const recovery = details.test_run_recovery;
  const keys = [
    "recovery_contract",
    "project_selector",
    "command",
    "resolution_code",
    "operation_id",
    "test_run_replayed",
    "write_command_repeated",
    "command_request_replayed",
    "explicit_repeat_required",
  ];
  return {
    ...details,
    test_run_recovery: Object.fromEntries(keys.flatMap((key) => recovery[key] === undefined ? [] : [[key, recovery[key]]])),
  };
}

function isSelectorBoundedDetails(details: Record<string, unknown> | undefined): boolean {
  if (!details) return false;
  if (details.matches_summary !== undefined || details.current_candidates_summary !== undefined) return true;
  const contract = details.recovery_contract;
  return typeof contract === "string" && contract.startsWith("sgg.unity_mcp.") && contract.endsWith(".v2");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function utf8Bytes(value: unknown): number {
  return Buffer.byteLength(JSON.stringify(value), "utf8");
}
