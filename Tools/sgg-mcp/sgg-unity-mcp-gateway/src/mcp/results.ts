import type { PolicyManagerSnapshot } from "../policy/policy-manager.js";

export const ERROR_CODES = [
  "invalid_json",
  "missing_required_field",
  "invalid_field_value",
  "selector_not_supported",
  "batch_mode_not_supported",
  "args_json_must_be_object",
  "items_json_must_be_array",
  "schema_validation_failed",
  "project_required",
  "project_required_for_log_kind",
  "project_not_found",
  "project_offline",
  "project_stale",
  "duplicate_project_id",
  "bridge_unreachable",
  "bridge_protocol_error",
  "protocol_version_mismatch",
  "permission_denied",
  "confirmation_required",
  "approval_unavailable",
  "policy_reload_failed",
  "policy_invalid",
  "policy_degraded",
  "invalid_token",
  "unsupported_platform",
  "unsafe_binding_refused",
  "path_not_allowed",
  "path_traversal_rejected",
  "absolute_path_rejected",
  "filename_too_long",
  "command_not_found",
  "command_handler_unavailable",
  "catalog_pending",
  "catalog_stale",
  "catalog_unavailable",
  "schema_unavailable",
  "dependency_missing",
  "feature_disabled",
  "dangerous_code_rejected",
  "dynamic_csharp_result_binding_invalid",
  "dynamic_csharp_result_not_found",
  "dynamic_csharp_result_unavailable",
  "dynamic_csharp_result_invalid",
  "dynamic_csharp_result_owner_mismatch",
  "precondition_required",
  "precondition_failed",
  "serialized_write_failed",
  "compilation_failed",
  "background_compilation_pending",
  "background_compilation_failed",
  "background_compilation_unavailable",
  "compile_freshness_unavailable",
  "compile_freshness_unverified",
  "package_refresh_required",
  "local_package_inventory_stale",
  "local_package_inventory_unavailable",
  "blocked_external_scene_reload",
  "blocked_project_downgrade_prompt",
  "capture_target_unavailable",
  "test_run_dirty_scene_blocked",
  "test_run_scene_state_unavailable",
  "unity_test_run_busy",
  "test_filter_resolution_pending",
  "test_filter_resolution_unavailable",
  "test_filter_unresolved",
  "test_filter_ambiguous",
  "test_run_status_unavailable",
  "unity_compiling",
  "unity_reloading",
  "unity_busy",
  "unity_exception",
  "unity_api_thread_error",
  "type_not_found",
  "member_not_found",
  "menu_path_not_found",
  "asset_not_found",
  "scene_not_found",
  "package_not_found",
  "package_operation_not_found",
  "job_not_supported",
  "job_not_found",
  "build_run_not_found",
  "menu_operation_not_found",
  "menu_operation_not_eligible",
  "menu_operation_exists",
  "menu_operation_receipt_invalid",
  "menu_operation_receipt_unavailable",
  "quit_not_observed",
  "quit_uncertain",
  "job_cancelled",
  "job_failed",
  "job_timeout",
  "job_not_cancellable",
  "queue_timeout",
  "command_timeout",
  "bridge_timeout",
  "replacement_timeout",
  "mcp_session_not_found",
  "internal_error",
] as const;

export type ErrorCode = (typeof ERROR_CODES)[number];

export type Diagnostic = {
  severity: "info" | "warning" | "error";
  source: "gateway" | "bridge" | "unity";
  message: string;
};

export type BatchItemExecution = {
  index: number;
  state: "completed" | "delivered" | "not_dispatched" | "not_started" | "uncertain";
  queued: boolean;
  admitted: boolean;
  bridge_request_issued: boolean;
  response_delivered: boolean;
  automatic_replay_attempted: boolean;
};

export type GatewayError = {
  code: ErrorCode;
  message: string;
  details?: Record<string, unknown>;
};

export type GatewayOk<TData = unknown> = {
  status: "ok";
  request_id?: string;
  project?: string;
  command?: string;
  data: TData;
  diagnostics: Diagnostic[];
  next_valid_actions?: string[];
  artifact_refs?: string[];
  truncated?: boolean;
  policy_decision?: unknown;
  batch_item?: BatchItemExecution;
};

export type GatewayFailure = {
  status: "error";
  request_id?: string;
  project?: string;
  command?: string;
  error: GatewayError;
  diagnostics: Diagnostic[];
  next_valid_actions?: string[];
  artifact_refs?: string[];
  truncated?: boolean;
  policy_decision?: unknown;
  policy_status?: PolicyManagerSnapshot;
  batch_item?: BatchItemExecution;
};

export type GatewayResult<TData = unknown> = GatewayOk<TData> | GatewayFailure;

export function ok<TData>(input: Omit<GatewayOk<TData>, "status" | "diagnostics"> & { diagnostics?: Diagnostic[] }): GatewayOk<TData> {
  return {
    status: "ok",
    diagnostics: [],
    ...resultHintsFromData(input.data),
    ...input,
  };
}

export function fail(input: Omit<GatewayFailure, "status" | "diagnostics"> & { diagnostics?: Diagnostic[] }): GatewayFailure {
  return {
    status: "error",
    diagnostics: [],
    ...resultHintsFromError(input.error.code),
    ...input,
  };
}

export function isErrorCode(value: string): value is ErrorCode {
  return ERROR_CODES.includes(value as ErrorCode);
}

function resultHintsFromData(data: unknown): Pick<GatewayOk, "artifact_refs" | "next_valid_actions" | "truncated"> {
  if (!isRecord(data)) {
    return {};
  }

  return {
    ...(isStringArray(data.artifact_refs) ? { artifact_refs: data.artifact_refs } : {}),
    ...(isStringArray(data.next_valid_actions) ? { next_valid_actions: data.next_valid_actions } : {}),
    ...(typeof data.truncated === "boolean" ? { truncated: data.truncated } : {}),
  };
}

function resultHintsFromError(code: ErrorCode): Pick<GatewayFailure, "next_valid_actions"> {
  const nextValidActions = nextValidActionsForErrorCode(code);
  return nextValidActions.length > 0 ? { next_valid_actions: nextValidActions } : {};
}

function nextValidActionsForErrorCode(code: ErrorCode): string[] {
  switch (code) {
    case "catalog_stale":
      return ["call_sgg_unity_describe"];
    case "project_offline":
    case "project_stale":
    case "project_not_found":
      return ["call_sgg_unity_projects", "inspect_project_status"];
    case "duplicate_project_id":
      return ["call_sgg_unity_projects", "inspect_project_status", "choose_a_unique_instance_id"];
    case "permission_denied":
      return ["do_not_bypass_policy", "choose_allowed_command", "ask_user_to_update_policy"];
    case "policy_degraded":
      return ["restore_valid_policy", "retry_explicitly_after_policy_recovery"];
    case "confirmation_required":
      return ["ask_user_for_confirmation", "choose_safer_command"];
    case "feature_disabled":
      return ["call_sgg_unity_describe", "ask_user_to_enable_feature_flag"];
    case "compile_freshness_unavailable":
      return ["call_compile.status_with_sgg_unity_call", "inspect_compile_barrier_state", "retry_compile.refresh_and_wait_after_state_recovers"];
    case "compile_freshness_unverified":
      return ["call_compile.status_with_sgg_unity_call", "wait_for_post_refresh_compilation_generation", "retry_compile.refresh_and_wait_explicitly_if_needed"];
    case "blocked_external_scene_reload":
      return ["ask_operator_to_choose_reload_or_ignore", "do_not_repeat_compile.refresh_and_wait", "call_sgg_unity_projects_after_modal_resolution"];
    case "blocked_project_downgrade_prompt":
      return ["review_project_downgrade_warning", "ask_gate_diagnostics.unity_project_downgrade.continue", "do_not_auto_continue_or_quit", "require_fresh_project_state_after_resolution"];
    case "capture_target_unavailable":
      return ["call_editor.windows.list", "call_editor.window.inspect", "do_not_substitute_main_window_or_desktop_pixels_for_editorwindow_identity"];
    case "test_filter_resolution_pending":
      return ["call_test.list", "wait_for_authoritative_test_catalog", "do_not_repeat_test.run"];
    case "test_filter_resolution_unavailable":
      return ["call_test.framework.status", "call_compile.refresh_and_wait", "do_not_repeat_test.run"];
    case "test_run_status_unavailable":
      return ["inspect_the_exact_test_run_operation_binding", "do_not_repeat_test.run_until_delivery_state_is_resolved", "call_diagnostics.report_problem"];
    case "test_filter_unresolved":
      return ["call_test.list", "use_an_exact_authoritative_test_filter", "do_not_use_reflection_filter_fallback"];
    case "test_filter_ambiguous":
      return ["call_test.list", "narrow_the_test_filter", "use_an_exact_authoritative_test_filter"];
    case "unity_test_run_busy":
      return ["poll_active_test_run_status", "inspect_the_active_test_run_artifact", "do_not_repeat_test.run"];
    case "bridge_unreachable":
    case "bridge_timeout":
    case "command_timeout":
      return ["call_sgg_unity_projects", "retry_after_short_delay", "inspect_gateway_health"];
    case "replacement_timeout":
      return ["call_sgg_unity_projects", "inspect_project_status", "retry_explicitly_if_still_intended"];
    case "unity_reloading":
      return ["call_sgg_unity_projects", "wait_for_bridge_lifecycle_status_to_become_ready", "retry_after_short_delay", "inspect_unity_editor_for_reload_modal_or_freeze"];
    case "bridge_protocol_error":
      return ["call_sgg_unity_projects", "call_compile.status_with_sgg_unity_call", "inspect_bridge_protocol_error_details", "call_diagnostics.recovery.suggest"];
    case "protocol_version_mismatch":
      return ["update_gateway_or_unity_package", "restart_gateway_and_unity_bridge"];
    case "invalid_token":
      return ["check_gateway_or_bridge_token", "restart_gateway_or_unity_bridge"];
    case "unsupported_platform":
      return ["use_supported_unity_editor_platform", "choose_safer_command"];
    case "type_not_found":
      return ["check_type_name", "call_unity_api_reflect_search"];
    case "member_not_found":
      return ["check_member_name", "call_unity_api_reflect_type"];
    case "menu_path_not_found":
      return ["compile.wait_ready", "assets.refresh", "editor.menu.list", "editor.menu.exists"];
    case "path_not_allowed":
    case "path_traversal_rejected":
    case "absolute_path_rejected":
      return ["use_unity_relative_path", "remove_path_traversal"];
    default:
      return [];
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}
