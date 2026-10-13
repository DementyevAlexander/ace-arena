import { execFile as nodeExecFile, execFileSync as nodeExecFileSync } from "node:child_process";
import { createHash, randomUUID } from "node:crypto";
import { KnownPopupPermissionChanged } from "../approvals/known-unity-popups.js";

const WINDOWS_TOOL_TIMEOUT_MS = 5000;
const WINDOWS_TOOL_CLEANUP_GRACE_MS = 250;
const WINDOWS_TOOL_MINIMUM_BUDGET_MS = 50;
const WINDOWS_TOOL_MAX_OUTPUT_BYTES = 128 * 1024;
const UNITY_IMAGE_NAME = "Unity.exe";
const UNITY_IMAGE_NAMES = [UNITY_IMAGE_NAME, "Unity"];
const TASKLIST_RU_OEM866_NO_MATCH_SHA256 = "046bcd0a03406d18f8e0b046fc4eab2d73c584954ac0638434f2aea2474952d8";
const UNITY_RECOVERY_PROMPT_ACTIONS = ["do_not_restore", "discard_recovery", "close_missing_signature"] as const;
const UNITY_LICENSING_CLIENT_CRASH_CODE = "unity_licensing_client_crash_modal";
const CORRUPTED_LIBRARY_MODAL_CODE = "blocked_corrupted_library";
const SAFE_MODE_MODAL_CODE = "blocked_safe_mode_prompt";
const MISSING_SIGNATURE_MODAL_CODE = "blocked_missing_signature_packages";
const EXTERNAL_SCENE_RELOAD_MODAL_CODE = "blocked_external_scene_reload";
const PROJECT_DOWNGRADE_MODAL_CODE = "blocked_project_downgrade_prompt";
const DUPLICATE_PROJECT_INSTANCE_MODAL_CODE = "blocked_duplicate_project_instance";
const SCOPED_REGISTRY_IMPORT_MODAL_CODE = "blocked_scoped_registry_import";
const SCENE_BACKUP_RECOVERY_MODAL_CODE = "blocked_scene_backup_recovery";
const LOW_DISK_SPACE_MODAL_CODE = "blocked_low_disk_space";
const DOMAIN_RELOAD_PROGRESS_CODE = "blocked_domain_reload_progress";
const DOMAIN_RELOAD_PROGRESS_STALL_SECONDS = 120;
const MAX_EXTERNAL_SCENE_PATHS = 10;
const MAX_EXTERNAL_SCENE_PATH_LENGTH = 512;

class PopupEvidenceExpired extends Error {}

// Remember the exact callback-origin exception, not merely its exported type:
// a responder throwing after possible delivery must never resurrect evidence.
function createPopupInteractionGuard(expiresAtMs: number, beforeInteraction: (() => void) | undefined, now: (() => Date) | undefined) {
  let permissionFailure: KnownPopupPermissionChanged | undefined;
  const stillValid = () => {
    const current = now?.().getTime() ?? Date.now();
    return Number.isFinite(current) && current < expiresAtMs;
  };
  return {
    check: () => {
      if (!stillValid()) throw new PopupEvidenceExpired();
      try { beforeInteraction?.(); } catch (error) {
        if (error instanceof KnownPopupPermissionChanged) permissionFailure = error;
        throw error;
      }
      if (!stillValid()) throw new PopupEvidenceExpired();
    },
    shouldRestore: (error: unknown) => permissionFailure !== undefined && error === permissionFailure && stillValid(),
  };
}
const EXTERNAL_SCENE_EVIDENCE_TOKEN_TTL_MS = 60_000;
const PROJECT_DOWNGRADE_EVIDENCE_TOKEN_TTL_MS = 60_000;
const SCOPED_REGISTRY_CLOSE_PLAN_TTL_MS = 60_000;
const SAFE_MODE_ENTER_PLAN_TTL_MS = 60_000;
const MAX_SAFE_MODE_ATTEMPTED_POPUPS = 64;
const SCENE_BACKUP_KEEP_PLAN_TTL_MS = 60_000;
const MAX_SCENE_BACKUP_KEEP_TRACKED_POPUPS = 64;
const PROJECT_UPGRADE_TITLE = "Project Upgrade Required";
const PROJECT_DOWNGRADE_TITLE = "Project Downgrade Required";
const PROJECT_DOWNGRADE_CLASSES = ["#32770", "UnityContainerWndClass"] as const;
const DUPLICATE_PROJECT_INSTANCE_TITLE = "Error!";
const DUPLICATE_PROJECT_INSTANCE_CLASSES = ["#32770", "UnityContainerWndClass"] as const;
const SCOPED_REGISTRY_IMPORT_TITLE = "Importing a scoped registry";
const SCOPED_REGISTRY_IMPORT_BODY = "A new scoped registry is now available in the Package Manager.";
const SCENE_BACKUP_RECOVERY_TITLE = "Recovering Scene Backups";
const SCENE_BACKUP_RECOVERY_BODY = "Scene backups from a previous Editor session have been detected. Your scene might have been backed up when an Editor instance did not close correctly. \nDo you want to copy and preserve these backups in Assets/_Recovery/?";
const SCENE_BACKUP_DETECTED_TITLE = "Scene Backup Detected";
const SCENE_BACKUP_DETECTED_BODY = "Scene backups from a previous Editor session have been detected. Unity attempts to backup scenes when an Editor instance does not close correctly.\nDo you want to keep these scene backups in Assets/_Recovery/?";
const LOW_DISK_SPACE_TITLE = "Low disk space";
const LOW_DISK_SPACE_BODY = "The project is on a drive with diskspace below 1.00 GB bytes.\nPlease free up some space for Unity to work correctly.";

type UnityBlockingNativeModalCode = typeof UNITY_LICENSING_CLIENT_CRASH_CODE | typeof CORRUPTED_LIBRARY_MODAL_CODE | typeof SAFE_MODE_MODAL_CODE | typeof MISSING_SIGNATURE_MODAL_CODE | typeof EXTERNAL_SCENE_RELOAD_MODAL_CODE | typeof PROJECT_DOWNGRADE_MODAL_CODE | typeof DUPLICATE_PROJECT_INSTANCE_MODAL_CODE | typeof SCOPED_REGISTRY_IMPORT_MODAL_CODE | typeof SCENE_BACKUP_RECOVERY_MODAL_CODE | typeof LOW_DISK_SPACE_MODAL_CODE;
type UnityBlockingNativeProgressCode = typeof DOMAIN_RELOAD_PROGRESS_CODE;

export type ExecFileSyncLike = (file: string, args: string[], options: { encoding: null; timeout: number; windowsHide: boolean; stdio: "pipe"; maxBuffer: number }) => string | Buffer;

export type UnityWindowDiagnosticsOptions = {
  beforeInteraction?: () => void;
  execFileSync?: ExecFileSyncLike;
  expectedExternalSceneReloadEvidence?: UnityExternalSceneReloadEvidence;
  expectedProjectDowngradeEvidence?: UnityProjectDowngradeEvidence;
  scanBudgetMs?: number;
  nowMonotonic?: () => number;
};

export type UnityWindowScanOptions = {
  scan_budget_ms?: number;
};

export type UnityWindowDiagnosticsRuntime = {
  status: (options?: UnityWindowScanOptions) => UnityWindowDiagnosticsStatus;
  statusAsync?: (options?: UnityWindowScanOptions) => Promise<UnityWindowDiagnosticsStatus>;
  cachedStatus?: () => UnityWindowDiagnosticsSnapshot | undefined;
  externalSceneReloadEvidence?: (pid: number) => UnityExternalSceneReloadEvidence | undefined;
  projectDowngradeEvidence?: (pid: number, canonicalProjectRoot: string) => UnityProjectDowngradeEvidence | undefined;
  invalidateProjectDowngradeEvidence?: (evidenceId: string) => void;
  planScopedRegistryImportClose?: (args: UnityScopedRegistryImportCloseArgs) => UnityScopedRegistryImportCloseResult;
  closeScopedRegistryImport?: (args: UnityScopedRegistryImportCloseArgs, beforeInteraction?: () => void) => UnityScopedRegistryImportCloseResult;
  planSafeModeEnter?: (args: UnitySafeModeEnterArgs) => UnitySafeModeEnterResult;
  enterSafeMode?: (args: UnitySafeModeEnterArgs, beforeInteraction?: () => void) => UnitySafeModeEnterResult;
  keepSceneBackups?: (args: UnitySceneBackupKeepArgs, beforeInteraction?: () => void) => UnitySceneBackupKeepResult;
  respond?: (args: UnityRecoveryPromptRespondArgs) => UnityRecoveryPromptRespondResult;
  respondExternalSceneReload?: (args: UnityExternalSceneReloadRespondArgs, beforeInteraction?: () => void) => UnityExternalSceneReloadRespondResult;
  continueProjectDowngrade?: (args: UnityProjectDowngradeContinueArgs, beforeInteraction?: () => void) => UnityProjectDowngradeContinueResult;
};

export type UnityWindowDiagnosticsSnapshot = {
  status: UnityWindowDiagnosticsStatus;
  captured_at: string;
};

export type UnityWindowDiagnosticsRuntimeOptions = {
  execFileSync?: ExecFileSyncLike;
  inspect?: () => UnityWindowDiagnosticsStatus;
  inspectAsync?: () => Promise<UnityWindowDiagnosticsStatus>;
  respond?: (args: UnityRecoveryPromptRespondArgs) => UnityRecoveryPromptRespondResult;
  respondExternalSceneReload?: (args: UnityExternalSceneReloadRespondArgs) => UnityExternalSceneReloadRespondResult;
  continueProjectDowngrade?: (args: UnityProjectDowngradeContinueArgs) => UnityProjectDowngradeContinueResult;
  closeScopedRegistryImport?: (evidence: UnityScopedRegistryImportCloseEvidence) => UnityScopedRegistryImportInteraction;
  enterSafeMode?: (evidence: UnitySafeModeEnterEvidence) => UnitySafeModeEnterInteraction;
  keepSceneBackups?: (evidence: UnitySceneBackupKeepEvidence) => UnitySceneBackupKeepInteraction;
  now?: () => Date;
};

export type UnityWindowInfo = {
  pid: number;
  image_name: string;
  window_title: string;
  source: "tasklist";
  responding?: boolean;
  likely_modal: boolean;
  likely_recovery_prompt: boolean;
  matched_keywords: string[];
};

export type UnityNativeModalInfo = {
  hwnd: string;
  pid: number;
  process_name: string;
  process_start_utc?: string;
  window_thread_id?: number;
  window_visible?: boolean;
  window_enabled?: boolean;
  class_name: string;
  window_title: string;
  child_texts: string[];
  source: "user32";
  likely_modal: boolean;
  likely_unity_licensing_client_crash: boolean;
  likely_corrupted_library: boolean;
  likely_safe_mode_prompt: boolean;
  likely_missing_signature_prompt: boolean;
  likely_external_scene_reload_prompt: boolean;
  external_scene_reload_evidence_scope?: "complete_body_and_scene_paths" | "sparse_control_topology";
  likely_project_downgrade_prompt?: boolean;
  likely_duplicate_project_instance?: boolean;
  likely_scoped_registry_import_prompt?: boolean;
  likely_scene_backup_recovery_prompt?: boolean;
  scene_backup_recovery_variant?: "recovering_scene_backups" | "scene_backup_detected";
  likely_low_disk_space_prompt?: boolean;
  safe_mode?: {
    evidence_scope: "complete_body" | "sparse_control_topology";
    body_sha256: string;
    control_topology_sha256: string;
    visible_control_count: number;
    button_count: 3;
    enter_safe_mode_button_count: 1;
    ignore_button_count: 1;
    quit_button_count: 1;
    enter_safe_mode_button_hwnd: string;
    ignore_button_hwnd: string;
    quit_button_hwnd: string;
  };
  scene_paths?: string[];
  scene_paths_count?: number;
  scene_paths_truncated?: boolean;
  reload_button_count?: number;
  ignore_button_count?: number;
  reload_button_hwnd?: string;
  ignore_button_hwnd?: string;
  project_downgrade?: {
    source_version: string;
    target_version: string;
    body_sha256: string;
    body_evidence: {
      source_newer_target_older: true;
      downgrade_unsupported: true;
      continue_downgrades: true;
      assets_reimport: true;
      packages_settings_also_downgraded: true;
    };
    button_count: 2;
    continue_button_hwnd: string;
    quit_button_hwnd: string;
  };
  duplicate_project_instance?: {
    body_sha256: string;
    project_identity_sha256: string;
    body_evidence: {
      another_instance_running: true;
      multiple_instances_forbidden: true;
      project_path_present: true;
    };
    button_count: 1;
    ok_button_count: 1;
    diagnostic_only: true;
    interaction_supported: false;
  };
  scoped_registry_import?: {
    title_sha256: string;
    body_sha256: string;
    body_evidence: {
      exact_single_registry_message: true;
      package_manager_named: true;
    };
    visible_control_count: number;
    text_control_count: 3;
    button_count: 2;
    learn_more_button_count: 1;
    close_button_count: 1;
    diagnostic_only: true;
    interaction_supported: false;
  };
  scene_backup_recovery?: {
    title_sha256: string;
    body_sha256: string;
    body_evidence: {
      previous_editor_session_detected: true;
      editor_may_not_have_closed_correctly: true;
      recovery_copy_destination_named: true;
    };
    visible_control_count: number;
    text_control_count: 3;
    button_count: 2;
    yes_button_count: 1;
    no_button_count: 1;
    diagnostic_only: true;
    interaction_supported: false;
  };
  scene_backup_detected?: {
    evidence_scope: "complete_body";
    title_sha256: string;
    body_sha256: string;
    control_topology_sha256: string;
    visible_control_count: 3 | 4;
    button_count: 2;
    keep_backups_button_count: 1;
    delete_button_count: 1;
    diagnostic_only: true;
    interaction_supported: false;
  };
  low_disk_space?: {
    title_sha256: string;
    body_sha256: string;
    body_evidence: {
      disk_space_below_one_gb: true;
      free_space_requested: true;
      unity_work_impacted: true;
    };
    visible_control_count: 3;
    static_control_count: 1;
    edit_control_count: 1;
    read_only_multiline_edit_count: 1;
    button_count: 1;
    ok_button_count: 1;
    diagnostic_only: true;
    interaction_supported: false;
    disk_cleanup_supported: false;
  };
  unclassified_modal?: {
    evidence_redacted: true;
    modal_like_reason: "dialog_class" | "owned_unity_window" | "dialog_modal_frame";
    window_title_sha256: string;
    visible_child_text_sha256: string;
    visible_child_control_count: number;
    inspection_complete: boolean;
  };
  exception_code?: string;
  block_code?: UnityBlockingNativeModalCode;
  matched_keywords: string[];
};

export type UnityNativeProgressWindowInfo = {
  hwnd: string;
  pid: number;
  process_name: string;
  class_name: string;
  window_title: string;
  child_texts: string[];
  source: "user32";
  likely_progress_window: true;
  progress_kind: "asset_import" | "domain_reload" | "compile" | "unknown";
  busy_text?: string;
  busy_seconds?: number;
  stall_threshold_seconds: number;
  is_stalled: boolean;
  diagnostic_only: true;
  automation_attempted: false;
  block_code?: UnityBlockingNativeProgressCode;
  matched_keywords: string[];
};

export type UnityWindowDiagnosticsStatus = {
  schema_version: 1;
  supported: boolean;
  active_unity_windows_count: number;
  active_unity_windows: UnityWindowInfo[];
  native_modal_candidates_count: number;
  native_modal_candidates: UnityNativeModalInfo[];
  top_level_window_scan_authoritative: boolean;
  native_modal_scan_authoritative: boolean;
  aggregate_scan_authoritative: boolean;
  scan_budget_ms: number;
  scan_elapsed_ms: number;
  helper_observations: {
    tasklist: UnityWindowHelperObservation;
    native_powershell: UnityWindowHelperObservation;
  };
  semantic_channels: {
    top_level_inventory: UnityWindowSemanticObservation;
    native_user32: UnityWindowSemanticObservation;
    native_uia_scoped_fallback: UnityWindowSemanticObservation;
  };
  licensing_client_crash_modal_candidates_count: number;
  licensing_client_crash_modal_candidates: UnityNativeModalInfo[];
  corrupted_library_modal_candidates_count: number;
  corrupted_library_modal_candidates: UnityNativeModalInfo[];
  safe_mode_modal_candidates_count: number;
  safe_mode_modal_candidates: UnityNativeModalInfo[];
  missing_signature_modal_candidates_count: number;
  missing_signature_modal_candidates: UnityNativeModalInfo[];
  external_scene_reload_modal_candidates_count: number;
  external_scene_reload_modal_candidates: UnityNativeModalInfo[];
  project_downgrade_modal_candidates_count?: number;
  project_downgrade_modal_candidates?: UnityNativeModalInfo[];
  duplicate_project_instance_modal_candidates_count?: number;
  duplicate_project_instance_modal_candidates?: UnityNativeModalInfo[];
  scoped_registry_import_modal_candidates_count?: number;
  scoped_registry_import_modal_candidates?: UnityNativeModalInfo[];
  scene_backup_recovery_modal_candidates_count?: number;
  scene_backup_recovery_modal_candidates?: UnityNativeModalInfo[];
  low_disk_space_modal_candidates_count?: number;
  low_disk_space_modal_candidates?: UnityNativeModalInfo[];
  native_progress_windows_count: number;
  native_progress_windows: UnityNativeProgressWindowInfo[];
  domain_reload_progress_windows_count: number;
  domain_reload_progress_windows: UnityNativeProgressWindowInfo[];
  domain_reload_progress_stall_count: number;
  recovery_prompt_candidates_count: number;
  recovery_prompt_candidates: UnityWindowInfo[];
  blocking_startup_possible: boolean;
  blocking_automation_possible: boolean;
  blocking_progress_possible: boolean;
  blocking_reload_possible: boolean;
  blocking_modal_code?: UnityBlockingNativeModalCode;
  blocking_progress_code?: UnityBlockingNativeProgressCode;
  errors: string[];
  next_valid_actions: string[];
};

export type UnityWindowHelperObservation = {
  status: "ok" | "error" | "timeout" | "skipped";
  authoritative: boolean;
  elapsed_ms: number;
  exit_code: number | null;
  timed_out: boolean;
  stdout_bytes: number;
  stderr_bytes: number;
  stdout_truncated: boolean;
  stderr_truncated: boolean;
  parsed_row_count: number;
  normalized_error_code: string | null;
  last_stage: string | null;
  stdout_encoding: "utf8" | "ibm866" | "unknown";
  cleanup_status: "not_required" | "owned_child_terminated" | "uncertain";
};

export type UnityWindowSemanticObservation = {
  status: "authoritative" | "partial" | "failed" | "unsupported";
  authoritative: boolean;
  coverage: string;
  parsed_count: number;
  partial_reason?: string;
  source_helper: "tasklist" | "native_powershell";
};

export type UnityRecoveryPromptAction = (typeof UNITY_RECOVERY_PROMPT_ACTIONS)[number];

export type UnityRecoveryPromptRespondArgs = {
  action?: string;
  pids?: number[];
  dry_run?: boolean;
};

export type UnityRecoveryPromptRespondResult = {
  schema_version: 1;
  action: "do_not_restore" | "close_missing_signature";
  action_alias?: UnityRecoveryPromptAction;
  dry_run: boolean;
  supported: boolean;
  button_click_method: "powershell_user32_bm_click" | "powershell_user32_exact_window_close";
  target_pids: number[];
  clicked_count: number;
  skipped: Array<{ pid: number; reason: string }>;
  results: UnityRecoveryPromptClickResult[];
  active_before: UnityWindowDiagnosticsStatus;
  active_after?: UnityWindowDiagnosticsStatus;
  errors: string[];
  next_valid_actions: string[];
};

export type UnityExternalSceneReloadAction = "reload" | "ignore";

export type UnityExternalSceneReloadEvidence = {
  schema_version: 1;
  evidence_id: string;
  captured_at: string;
  source: "user32";
  pid: number;
  hwnd: string;
  process_name: "Unity" | "Unity.exe";
  process_start_utc: string;
  window_thread_id: number;
  class_name: "#32770" | "UnityContainerWndClass";
  window_title: "The open scene(s) have been modified externally";
  evidence_scope: "complete_body_and_scene_paths" | "sparse_control_topology";
  scene_paths: string[];
  scene_paths_count: number;
  body_evidence?: {
    changed_on_disk_text: true;
    reload_question_text: true;
  };
  buttons: {
    reload_count: 1;
    ignore_count: 1;
    reload_hwnd: string;
    ignore_hwnd: string;
  };
};

export type UnityExternalSceneReloadRespondArgs = {
  action?: string;
  pid?: number;
  hwnd?: string;
  evidence_id?: string;
};

export type UnityExternalSceneReloadRespondResult = {
  schema_version: 1;
  action: UnityExternalSceneReloadAction | null;
  status: "message_sent" | "refused" | "error";
  interaction_attempted: boolean | "unknown";
  message_sent: boolean;
  delivery_state: "not_attempted" | "message_sent" | "unknown_after_send_attempt" | "unknown";
  popup_closed_verified: false;
  compile_state_verified: false;
  target_pid: number | null;
  target_hwnd: string | null;
  evidence?: UnityExternalSceneReloadEvidence;
  button_text?: "Reload" | "Ignore";
  matched_reload_button_count?: number;
  matched_ignore_button_count?: number;
  error?: string;
  next_valid_actions: string[];
};

export type UnityProjectDowngradeEvidence = {
  schema_version: 1;
  evidence_id: string;
  captured_at: string;
  source: "user32";
  project_identity_sha256: string;
  pid: number;
  process_name: "Unity" | "Unity.exe";
  process_start_utc: string;
  hwnd: string;
  window_thread_id: number;
  class_name: "#32770" | "UnityContainerWndClass";
  window_title: "Project Downgrade Required";
  source_version: string;
  target_version: string;
  body_sha256: string;
  body_evidence: {
    source_newer_target_older: true;
    downgrade_unsupported: true;
    continue_downgrades: true;
    assets_reimport: true;
    packages_settings_also_downgraded: true;
  };
  buttons: {
    count: 2;
    continue_count: 1;
    quit_count: 1;
    continue_hwnd: string;
    quit_hwnd: string;
  };
};

export type UnityProjectDowngradeContinueArgs = {
  evidence_id?: string;
  canonical_project_root?: string;
  confirm_continue?: boolean;
  pid?: number;
  process_name?: string;
  process_start_utc?: string;
  hwnd?: string;
  window_thread_id?: number;
  class_name?: string;
  window_title?: string;
  source_version?: string;
  target_version?: string;
  body_sha256?: string;
  button_count?: number;
  continue_button_hwnd?: string;
  quit_button_hwnd?: string;
};

export type UnityProjectDowngradeContinueResult = {
  schema_version: 1;
  status: "message_submitted" | "refused" | "error";
  success: false;
  interaction_attempted: boolean | "unknown";
  message_submitted: boolean;
  delivery_state: "not_attempted" | "message_submitted" | "unknown_after_send_attempt" | "unknown";
  popup_closed_verified: false;
  fresh_project_state_verified: false;
  target_pid: number | null;
  target_hwnd: string | null;
  evidence?: UnityProjectDowngradeEvidence;
  button_text?: "Continue";
  error?: string;
  next_valid_actions: string[];
};

export type UnityScopedRegistryImportCloseArgs = {
  pid?: number;
  process_start_utc?: string;
  project_identity_sha256?: string;
  dry_run?: boolean;
  confirm_close?: boolean;
  precondition_hash?: string;
};

export type UnityScopedRegistryImportCloseEvidence = {
  schema_version: 1;
  precondition_hash: string;
  captured_at: string;
  expires_at: string;
  source: "user32";
  project_identity_sha256: string;
  pid: number;
  process_name: "Unity" | "Unity.exe";
  process_start_utc: string;
  hwnd: string;
  window_thread_id: number;
  class_name: "#32770";
  window_title: "Importing a scoped registry";
  title_sha256: string;
  body_sha256: string;
  visible_control_count: number;
  button_count: 2;
  learn_more_button_count: 1;
  close_button_count: 1;
  learn_more_button_hwnd: string;
  close_button_hwnd: string;
};

export type UnityScopedRegistryImportInteraction = {
  status: "message_sent" | "refused" | "error";
  interaction_attempted: boolean | "unknown";
  delivery_state: "not_attempted" | "message_sent" | "unknown_after_send_attempt" | "unknown";
  error?: string;
};

export type UnityScopedRegistryImportCloseResult = {
  schema_version: 1;
  action: "close";
  dry_run: boolean;
  status: "ready" | "message_sent" | "refused" | "error";
  interaction_attempted: boolean | "unknown";
  native_dispatch_count: 0 | 1;
  delivery_state: "not_attempted" | "message_sent" | "unknown_after_send_attempt" | "unknown";
  popup_state: "not_checked" | "modal_absent" | "modal_still_present" | "replacement_present" | "unverified";
  popup_disappearance_verified: boolean;
  blocked_operation_replayed: false;
  target_pid: number | null;
  process_start_utc: string | null;
  precondition_hash?: string;
  expires_at?: string;
  binding?: {
    source: "user32";
    process_generation_verified: true;
    window_thread_id: number;
    class_name: "#32770";
    title_sha256: string;
    body_sha256: string;
    visible_control_count: number;
    button_count: 2;
    learn_more_button_count: 1;
    close_button_count: 1;
    button_identities_bound_internally: true;
  };
  error?: string;
  next_valid_actions: string[];
};

export type UnitySafeModeEnterArgs = {
  pid?: number;
  process_start_utc?: string;
  project_identity_sha256?: string;
  dry_run?: boolean;
  confirm_enter_safe_mode?: boolean;
  precondition_hash?: string;
};

export type UnitySafeModeEnterEvidence = {
  schema_version: 1;
  precondition_hash: string;
  captured_at: string;
  expires_at: string;
  source: "user32";
  project_identity_sha256: string;
  pid: number;
  process_name: "Unity" | "Unity.exe";
  process_start_utc: string;
  hwnd: string;
  window_thread_id: number;
  class_name: "#32770";
  window_title: "Enter Safe Mode?";
  evidence_scope: "complete_body" | "sparse_control_topology";
  body_sha256: string;
  control_topology_sha256: string;
  visible_control_count: number;
  buttons: {
    count: 3;
    enter_safe_mode_count: 1;
    ignore_count: 1;
    quit_count: 1;
    enter_safe_mode_hwnd: string;
    ignore_hwnd: string;
    quit_hwnd: string;
  };
};

export type UnitySafeModeEnterInteraction = {
  status: "message_submitted" | "refused" | "error";
  interaction_attempted: boolean | "unknown";
  delivery_state: "not_attempted" | "message_submitted" | "unknown_after_send_attempt" | "unknown";
  error?: string;
};

export type UnitySafeModeEnterResult = {
  schema_version: 1;
  action: "enter_safe_mode";
  dry_run: boolean;
  status: "ready" | "message_submitted" | "refused" | "error";
  interaction_attempted: boolean | "unknown";
  native_dispatch_count: 0 | 1;
  delivery_state: "not_attempted" | "message_submitted" | "unknown_after_send_attempt" | "unknown";
  safe_mode_entered_verified: false;
  popup_closed_verified: false;
  target_pid: number | null;
  process_start_utc: string | null;
  precondition_hash?: string;
  expires_at?: string;
  binding?: {
    source: "user32";
    process_generation_verified: true;
    window_thread_id: number;
    class_name: "#32770";
    window_title: "Enter Safe Mode?";
    evidence_scope: "complete_body" | "sparse_control_topology";
    body_sha256: string;
    control_topology_sha256: string;
    visible_control_count: number;
    button_count: 3;
    enter_safe_mode_button_count: 1;
    ignore_button_count: 1;
    quit_button_count: 1;
    exact_button_identities_bound_internally: true;
  };
  error?: string;
  next_valid_actions: string[];
};

export type UnitySceneBackupKeepArgs = Pick<UnitySafeModeEnterArgs, "pid" | "process_start_utc" | "dry_run" | "precondition_hash"> & {
  canonical_project_root?: string;
  confirm_keep_backups?: boolean;
};

export type UnitySceneBackupKeepEvidence = Omit<UnitySafeModeEnterEvidence, "window_title" | "evidence_scope" | "visible_control_count" | "buttons"> & {
  window_title: "Scene Backup Detected";
  evidence_scope: "complete_body";
  visible_control_count: 3 | 4;
  buttons: {
    count: 2;
    keep_backups_count: 1;
    delete_count: 1;
    keep_backups_hwnd: string;
    delete_hwnd: string;
  };
};

export type UnitySceneBackupKeepInteraction = UnitySafeModeEnterInteraction;

export type UnitySceneBackupKeepResult = Omit<UnitySafeModeEnterResult, "action" | "safe_mode_entered_verified" | "binding"> & {
  action: "keep_scene_backups";
  message_submitted: boolean;
  backups_kept_verified: false;
  binding?: {
    source: "user32";
    process_generation_verified: true;
    window_thread_id: number;
    class_name: "#32770";
    window_title: "Scene Backup Detected";
    evidence_scope: "complete_body";
    body_sha256: string;
    control_topology_sha256: string;
    visible_control_count: 3 | 4;
    button_count: 2;
    keep_backups_button_count: 1;
    delete_button_count: 1;
    exact_button_identities_bound_internally: true;
  };
};

type UnityRecoveryPromptClickResult = {
  pid: number;
  status: "would_click" | "clicked" | "not_found" | "ambiguous" | "error";
  executable?: string;
  window_title?: string;
  button_text?: string;
  close_method?: "button_click" | "window_close";
  matched_button_count?: number;
  matched_button_texts?: string[];
  error?: string;
};

type WindowsToolResult = {
  status: "ok" | "error" | "skipped";
  executable?: string;
  output: Buffer;
  observation: UnityWindowHelperObservation;
};

type UnityWindowScanBudget = {
  budgetMs: number;
  startedMs: number;
  deadlineMs: number;
  now: () => number;
};

type RawRecoveryPromptClickResult = {
  pid?: unknown;
  status?: unknown;
  window_title?: unknown;
  button_text?: unknown;
  close_method?: unknown;
  matched_button_count?: unknown;
  matched_button_texts?: unknown;
  error?: unknown;
};

type RawNativeModalResult = {
  hwnd?: unknown;
  pid?: unknown;
  process_name?: unknown;
  process_start_utc?: unknown;
  window_thread_id?: unknown;
  window_visible?: unknown;
  window_enabled?: unknown;
  class_name?: unknown;
  window_title?: unknown;
  child_texts?: unknown;
  child_controls?: unknown;
  modal_like_reason?: unknown;
  inspection_complete?: unknown;
};

type RawNativeChildControl = {
  hwnd?: unknown;
  parent_hwnd?: unknown;
  pid?: unknown;
  window_thread_id?: unknown;
  visible?: unknown;
  enabled?: unknown;
  control_id?: unknown;
  class_name?: unknown;
  text?: unknown;
  read_only?: unknown;
  multiline?: unknown;
};

type ParsedNativeChildControl = {
  hwnd: string;
  parentHwnd?: string;
  pid: number;
  windowThreadId: number;
  visible: boolean;
  visibleObserved: boolean;
  enabled: boolean;
  enabledObserved: boolean;
  controlId?: number;
  className: string;
  text: string;
  readOnly?: boolean;
  multiline?: boolean;
};

type NativeModalProcessIdentity = {
  pid: number;
  processStartUtc?: string;
  windowThreadId: number;
  windowVisible?: boolean;
  windowEnabled?: boolean;
  inspectionComplete?: boolean;
};

type ModalRespondSpec = {
  action: "do_not_restore" | "close_missing_signature";
  candidates: Array<{ pid: number }>;
  notCandidateReason: string;
  noCandidateError: string;
  noRequestedCandidateError: string;
  titlePattern: string;
  buttonPattern: string;
  requiredTextPattern: string;
  requiredClassPattern: string;
  closeMethod: "button" | "window_close";
};

const scopedRegistryButtonIdentities = new WeakMap<object, { learnMoreHwnd: string; closeHwnd: string }>();
const sceneBackupDetectedNativeBindings = new WeakMap<object, { keepHwnd: string; deleteHwnd: string; authoritative: boolean }>();

export function createUnityWindowDiagnosticsRuntime(options: UnityWindowDiagnosticsRuntimeOptions = {}): UnityWindowDiagnosticsRuntime {
  let cached: UnityWindowDiagnosticsSnapshot | undefined;
  let latestScanGeneration = 0;
  let inFlightAsyncStatus: Promise<UnityWindowDiagnosticsStatus> | undefined;
  const issuedExternalSceneEvidence = new Map<string, { evidence: UnityExternalSceneReloadEvidence; expiresAtMs: number }>();
  const issuedProjectDowngradeEvidence = new Map<string, { evidence: UnityProjectDowngradeEvidence; expiresAtMs: number }>();
  const issuedScopedRegistryClosePlans = new Map<string, { evidence: UnityScopedRegistryImportCloseEvidence; expiresAtMs: number }>();
  const issuedSafeModeEnterPlans = new Map<string, { evidence: UnitySafeModeEnterEvidence; expiresAtMs: number }>();
  const attemptedSafeModePopups = new Map<string, UnitySafeModeEnterEvidence>();
  const issuedSceneBackupKeepPlans = new Map<string, { evidence: UnitySceneBackupKeepEvidence; expiresAtMs: number }>();
  const attemptedSceneBackupKeepPopups = new Map<string, UnitySceneBackupKeepEvidence>();
  const cache = (status: UnityWindowDiagnosticsStatus, generation?: number): UnityWindowDiagnosticsStatus => {
    if (generation !== undefined && generation < latestScanGeneration) return cached?.status ?? status;
    if (generation === undefined) latestScanGeneration += 1;
    const capturedAt = (options.now?.() ?? new Date()).toISOString();
    const nowMs = options.now?.().getTime() ?? Date.now();
    for (const [evidenceId, issued] of issuedExternalSceneEvidence) {
      const current = externalSceneReloadEvidence(status, issued.evidence.pid, capturedAt);
      if (issued.expiresAtMs < nowMs || !current || !sameExternalSceneReloadIdentity(issued.evidence, current)) {
        issuedExternalSceneEvidence.delete(evidenceId);
      }
    }
    for (const [evidenceId, issued] of issuedProjectDowngradeEvidence) {
      const current = projectDowngradeEvidence(status, issued.evidence.pid, issued.evidence.captured_at, issued.evidence.project_identity_sha256);
      if (issued.expiresAtMs < nowMs || !current || !sameProjectDowngradeIdentity(issued.evidence, current)) {
        issuedProjectDowngradeEvidence.delete(evidenceId);
      }
    }
    for (const [preconditionHash, issued] of issuedScopedRegistryClosePlans) {
      const current = scopedRegistryImportCloseEvidence(status, issued.evidence.pid, issued.evidence.process_start_utc, issued.evidence.project_identity_sha256, issued.evidence.captured_at, issued.evidence.expires_at, preconditionHash);
      if (issued.expiresAtMs < nowMs || !current || !sameScopedRegistryImportCloseIdentity(issued.evidence, current)) {
        issuedScopedRegistryClosePlans.delete(preconditionHash);
      }
    }
    for (const [preconditionHash, issued] of issuedSafeModeEnterPlans) {
      const current = safeModeEnterEvidence(status, issued.evidence.pid, issued.evidence.process_start_utc, issued.evidence.project_identity_sha256, issued.evidence.captured_at, issued.evidence.expires_at, preconditionHash);
      if (issued.expiresAtMs < nowMs || !current || !sameSafeModeEnterIdentity(issued.evidence, current)) {
        issuedSafeModeEnterPlans.delete(preconditionHash);
      }
    }
    for (const [preconditionHash, issued] of issuedSceneBackupKeepPlans) {
      const current = sceneBackupKeepEvidence(status, issued.evidence.pid, issued.evidence.process_start_utc, issued.evidence.project_identity_sha256, issued.evidence.captured_at, issued.evidence.expires_at, preconditionHash);
      if (issued.expiresAtMs <= nowMs || !current || !sameSceneBackupKeepIdentity(issued.evidence, current)) issuedSceneBackupKeepPlans.delete(preconditionHash);
    }
    if (status.native_modal_scan_authoritative && status.semantic_channels.native_user32.authoritative) {
      for (const [identityKey, attempted] of attemptedSceneBackupKeepPopups) {
        const stillPresent = status.native_modal_candidates.some((candidate) => candidate.pid === attempted.pid
          && candidate.process_start_utc === attempted.process_start_utc
          && candidate.hwnd.toLowerCase() === attempted.hwnd.toLowerCase());
        if (!stillPresent) attemptedSceneBackupKeepPopups.delete(identityKey);
      }
    }
    if (status.native_modal_scan_authoritative && status.semantic_channels.native_user32.authoritative) {
      for (const [identityKey, attempted] of attemptedSafeModePopups) {
        const stillPresent = status.safe_mode_modal_candidates
          .filter((candidate) => candidate.pid === attempted.pid && candidate.process_start_utc === attempted.process_start_utc)
          .some((candidate) => {
            const current = safeModeEnterCandidateEvidence(candidate, attempted.pid, attempted.process_start_utc, attempted.project_identity_sha256, attempted.captured_at, attempted.expires_at, attempted.precondition_hash);
            return current !== undefined && sameSafeModeEnterIdentity(attempted, current);
          });
        if (!stillPresent) attemptedSafeModePopups.delete(identityKey);
      }
    }
    cached = { status, captured_at: capturedAt };
    return status;
  };
  return {
    status: (scanOptions = {}) => cache(options.inspect?.() ?? inspectUnityWindowDiagnostics({
      ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}),
      ...(scanOptions.scan_budget_ms !== undefined ? { scanBudgetMs: scanOptions.scan_budget_ms } : {}),
    })),
    statusAsync: (scanOptions = {}) => {
      if (inFlightAsyncStatus) return inFlightAsyncStatus;
      const generation = ++latestScanGeneration;
      const scan = async () => options.inspectAsync
        ? await options.inspectAsync()
        : options.inspect
          ? options.inspect()
          : options.execFileSync
            ? inspectUnityWindowDiagnostics({ execFileSync: options.execFileSync, ...(scanOptions.scan_budget_ms !== undefined ? { scanBudgetMs: scanOptions.scan_budget_ms } : {}) })
            : await inspectUnityWindowDiagnosticsAsync(scanOptions.scan_budget_ms);
      let pending!: Promise<UnityWindowDiagnosticsStatus>;
      pending = scan().then((status) => cache(status, generation)).finally(() => {
        if (inFlightAsyncStatus === pending) inFlightAsyncStatus = undefined;
      });
      inFlightAsyncStatus = pending;
      return pending;
    },
    cachedStatus: () => cached,
    externalSceneReloadEvidence: (pid) => {
      if (!cached) return undefined;
      const evidence = externalSceneReloadEvidence(cached.status, pid, cached.captured_at);
      if (!evidence) return undefined;
      const issued = {
        ...evidence,
        evidence_id: `sha256:${createHash("sha256").update(`${evidence.evidence_id}\n${evidence.captured_at}\n${randomUUID()}`).digest("hex")}`,
      };
      const nowMs = options.now?.().getTime() ?? Date.now();
      issuedExternalSceneEvidence.set(issued.evidence_id, { evidence: issued, expiresAtMs: nowMs + EXTERNAL_SCENE_EVIDENCE_TOKEN_TTL_MS });
      return issued;
    },
    projectDowngradeEvidence: (pid, canonicalProjectRoot) => {
      if (!cached || typeof canonicalProjectRoot !== "string" || canonicalProjectRoot.length === 0) return undefined;
      const projectIdentitySha256 = `sha256:${createHash("sha256").update(canonicalProjectRoot).digest("hex")}`;
      const evidence = projectDowngradeEvidence(cached.status, pid, cached.captured_at, projectIdentitySha256);
      if (!evidence) return undefined;
      const nowMs = options.now?.().getTime() ?? Date.now();
      for (const issued of issuedProjectDowngradeEvidence.values()) {
        if (issued.expiresAtMs >= nowMs && sameProjectDowngradePopupIdentity(issued.evidence, evidence)) return issued.evidence;
      }
      const issued = {
        ...evidence,
        evidence_id: `sha256:${createHash("sha256").update(`${evidence.evidence_id}\n${evidence.captured_at}\n${randomUUID()}`).digest("hex")}`,
      };
      issuedProjectDowngradeEvidence.set(issued.evidence_id, { evidence: issued, expiresAtMs: nowMs + PROJECT_DOWNGRADE_EVIDENCE_TOKEN_TTL_MS });
      return issued;
    },
    invalidateProjectDowngradeEvidence: (evidenceId) => {
      const issued = issuedProjectDowngradeEvidence.get(evidenceId);
      if (!issued) return;
      for (const [candidateId, candidate] of issuedProjectDowngradeEvidence) {
        if (sameProjectDowngradePopupIdentity(issued.evidence, candidate.evidence)) issuedProjectDowngradeEvidence.delete(candidateId);
      }
    },
    planScopedRegistryImportClose: (args) => {
      const status = cache(options.inspect?.() ?? inspectUnityWindowDiagnostics({ ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) }));
      const pid = numberOrZero(args.pid);
      const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
      const projectIdentitySha256 = typeof args.project_identity_sha256 === "string" ? args.project_identity_sha256 : "";
      if (!status.native_modal_scan_authoritative || !status.semantic_channels.native_user32.authoritative) {
        return scopedRegistryImportCloseResult({ dryRun: true, pid, processStartUtc, status: "refused", error: "authoritative_user32_scan_required" });
      }
      const capturedAt = cached?.captured_at ?? (options.now?.() ?? new Date()).toISOString();
      const nowMs = options.now?.().getTime() ?? Date.now();
      const expiresAt = new Date(nowMs + SCOPED_REGISTRY_CLOSE_PLAN_TTL_MS).toISOString();
      const provisional = scopedRegistryImportCloseEvidence(status, pid, processStartUtc, projectIdentitySha256, capturedAt, expiresAt, `sha256:${"0".repeat(64)}`);
      if (!provisional) return scopedRegistryImportCloseResult({ dryRun: true, pid, processStartUtc, status: "refused", error: "exact_scoped_registry_import_modal_not_found_or_ambiguous" });
      const preconditionHash = `sha256:${createHash("sha256").update(`${JSON.stringify(provisional)}\n${randomUUID()}`).digest("hex")}`;
      const evidence = { ...provisional, precondition_hash: preconditionHash };
      issuedScopedRegistryClosePlans.set(preconditionHash, { evidence, expiresAtMs: nowMs + SCOPED_REGISTRY_CLOSE_PLAN_TTL_MS });
      return scopedRegistryImportCloseResult({ dryRun: true, pid, processStartUtc, status: "ready", evidence });
    },
    closeScopedRegistryImport: (args, beforeInteraction) => {
      const pid = numberOrZero(args.pid);
      const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
      const preconditionHash = typeof args.precondition_hash === "string" ? args.precondition_hash : "";
      const issued = issuedScopedRegistryClosePlans.get(preconditionHash);
      issuedScopedRegistryClosePlans.delete(preconditionHash);
      const nowMs = options.now?.().getTime() ?? Date.now();
      if (args.dry_run !== false || args.confirm_close !== true || !issued || issued.expiresAtMs <= nowMs) {
        return scopedRegistryImportCloseResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "scoped_registry_close_plan_not_issued_expired_or_consumed" });
      }
      if (!scopedRegistryImportCloseArgsMatchEvidence(args, issued.evidence)) {
        return scopedRegistryImportCloseResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "scoped_registry_close_plan_binding_changed" });
      }
      const currentStatus = cache(options.inspect?.() ?? inspectUnityWindowDiagnostics({ ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) }));
      if (!currentStatus.native_modal_scan_authoritative || !currentStatus.semantic_channels.native_user32.authoritative) {
        return scopedRegistryImportCloseResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "authoritative_user32_rescan_required" });
      }
      const current = scopedRegistryImportCloseEvidence(currentStatus, pid, processStartUtc, issued.evidence.project_identity_sha256, issued.evidence.captured_at, issued.evidence.expires_at, preconditionHash);
      if (!current || !sameScopedRegistryImportCloseIdentity(issued.evidence, current)) {
        return scopedRegistryImportCloseResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "scoped_registry_import_modal_or_button_changed" });
      }
      const guard = createPopupInteractionGuard(issued.expiresAtMs, beforeInteraction, options.now);
      try { guard.check(); } catch (error) {
        if (guard.shouldRestore(error)) issuedScopedRegistryClosePlans.set(preconditionHash, issued);
        if (error instanceof PopupEvidenceExpired) return scopedRegistryImportCloseResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "scoped_registry_close_plan_not_issued_expired_or_consumed" });
        throw error;
      }
      const interaction = options.closeScopedRegistryImport?.(issued.evidence)
        ?? closeUnityScopedRegistryImport(issued.evidence, { ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) });
      if (interaction.status !== "message_sent") {
        return scopedRegistryImportCloseResult({ dryRun: false, pid, processStartUtc, status: interaction.status, interaction, nativeDispatchCount: interaction.interaction_attempted === false ? 0 : 1, ...(interaction.error ? { error: interaction.error } : {}) });
      }
      const after = cache(options.inspect?.() ?? inspectUnityWindowDiagnostics({ ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) }));
      const remaining = (after.scoped_registry_import_modal_candidates ?? []).filter((candidate) => candidate.pid === pid && candidate.process_start_utc === processStartUtc);
      const sameModalRemains = remaining.some((candidate) => candidate.hwnd.toLowerCase() === issued.evidence.hwnd.toLowerCase());
      const popupState = !after.native_modal_scan_authoritative || !after.semantic_channels.native_user32.authoritative
        ? "unverified" as const
        : sameModalRemains
          ? "modal_still_present" as const
          : remaining.length > 0
            ? "replacement_present" as const
            : "modal_absent" as const;
      return scopedRegistryImportCloseResult({ dryRun: false, pid, processStartUtc, status: "message_sent", interaction, nativeDispatchCount: 1, popupState });
    },
    planSafeModeEnter: (args) => {
      const status = cache(options.inspect?.() ?? inspectUnityWindowDiagnostics({ ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) }));
      const pid = numberOrZero(args.pid);
      const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
      const projectIdentitySha256 = typeof args.project_identity_sha256 === "string" ? args.project_identity_sha256 : "";
      if (!status.native_modal_scan_authoritative || !status.semantic_channels.native_user32.authoritative) {
        return safeModeEnterResult({ dryRun: true, pid, processStartUtc, status: "refused", error: "authoritative_user32_scan_required" });
      }
      const capturedAt = cached?.captured_at ?? (options.now?.() ?? new Date()).toISOString();
      const nowMs = options.now?.().getTime() ?? Date.now();
      const expiresAt = new Date(nowMs + SAFE_MODE_ENTER_PLAN_TTL_MS).toISOString();
      const provisional = safeModeEnterEvidence(status, pid, processStartUtc, projectIdentitySha256, capturedAt, expiresAt, `sha256:${"0".repeat(64)}`);
      if (!provisional) return safeModeEnterResult({ dryRun: true, pid, processStartUtc, status: "refused", error: "exact_safe_mode_prompt_not_found_or_ambiguous" });
      if (attemptedSafeModePopups.has(safeModeAttemptIdentityKey(provisional))) {
        return safeModeEnterResult({ dryRun: true, pid, processStartUtc, status: "refused", error: "safe_mode_enter_delivery_already_attempted_for_popup" });
      }
      if (attemptedSafeModePopups.size >= MAX_SAFE_MODE_ATTEMPTED_POPUPS) {
        return safeModeEnterResult({ dryRun: true, pid, processStartUtc, status: "refused", error: "safe_mode_attempt_tracking_capacity_reached" });
      }
      const preconditionHash = `sha256:${createHash("sha256").update(`${JSON.stringify(provisional)}\n${randomUUID()}`).digest("hex")}`;
      const evidence = { ...provisional, precondition_hash: preconditionHash };
      issuedSafeModeEnterPlans.set(preconditionHash, { evidence, expiresAtMs: nowMs + SAFE_MODE_ENTER_PLAN_TTL_MS });
      return safeModeEnterResult({ dryRun: true, pid, processStartUtc, status: "ready", evidence });
    },
    enterSafeMode: (args, beforeInteraction) => {
      const pid = numberOrZero(args.pid);
      const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
      const preconditionHash = typeof args.precondition_hash === "string" ? args.precondition_hash : "";
      const issued = issuedSafeModeEnterPlans.get(preconditionHash);
      issuedSafeModeEnterPlans.delete(preconditionHash);
      const nowMs = options.now?.().getTime() ?? Date.now();
      if (args.dry_run !== false || args.confirm_enter_safe_mode !== true || !issued || issued.expiresAtMs <= nowMs) {
        return safeModeEnterResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "safe_mode_enter_plan_not_issued_expired_or_consumed" });
      }
      if (!safeModeEnterArgsMatchEvidence(args, issued.evidence)) {
        return safeModeEnterResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "safe_mode_enter_plan_binding_changed" });
      }
      const currentStatus = cache(options.inspect?.() ?? inspectUnityWindowDiagnostics({ ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) }));
      if (!currentStatus.native_modal_scan_authoritative || !currentStatus.semantic_channels.native_user32.authoritative) {
        return safeModeEnterResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "authoritative_user32_rescan_required" });
      }
      const current = safeModeEnterEvidence(currentStatus, pid, processStartUtc, issued.evidence.project_identity_sha256, issued.evidence.captured_at, issued.evidence.expires_at, preconditionHash);
      if (!current || !sameSafeModeEnterIdentity(issued.evidence, current)) {
        return safeModeEnterResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "safe_mode_prompt_or_button_changed" });
      }
      const attemptedIdentityKey = safeModeAttemptIdentityKey(issued.evidence);
      for (const [issuedHash, candidate] of issuedSafeModeEnterPlans) {
        if (safeModeAttemptIdentityKey(candidate.evidence) === attemptedIdentityKey) issuedSafeModeEnterPlans.delete(issuedHash);
      }
      if (!attemptedSafeModePopups.has(attemptedIdentityKey) && attemptedSafeModePopups.size >= MAX_SAFE_MODE_ATTEMPTED_POPUPS) {
        return safeModeEnterResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "safe_mode_attempt_tracking_capacity_reached" });
      }
      const guard = createPopupInteractionGuard(issued.expiresAtMs, beforeInteraction, options.now);
      try { guard.check(); } catch (error) {
        if (guard.shouldRestore(error)) issuedSafeModeEnterPlans.set(preconditionHash, issued);
        if (error instanceof PopupEvidenceExpired) return safeModeEnterResult({ dryRun: false, pid, processStartUtc, status: "refused", error: "safe_mode_enter_plan_not_issued_expired_or_consumed" });
        throw error;
      }
      const interaction = options.enterSafeMode?.(issued.evidence)
        ?? enterUnitySafeMode(issued.evidence, { ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) });
      if (interaction.interaction_attempted !== false) {
        attemptedSafeModePopups.set(attemptedIdentityKey, issued.evidence);
      }
      return safeModeEnterResult({
        dryRun: false,
        pid,
        processStartUtc,
        status: interaction.status,
        interaction,
        nativeDispatchCount: interaction.interaction_attempted === false ? 0 : 1,
        evidence: issued.evidence,
        ...(interaction.error ? { error: interaction.error } : {}),
      });
    },
    keepSceneBackups: (args, beforeInteraction) => {
      const dryRun = args.dry_run !== false;
      const pid = numberOrZero(args.pid);
      const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
      const root = args.canonical_project_root;
      const refuse = (error: string) => sceneBackupKeepResult({ dryRun, pid, processStartUtc, status: "refused", error });
      const preconditionHash = typeof args.precondition_hash === "string" ? args.precondition_hash : "";
      const issued = dryRun ? undefined : issuedSceneBackupKeepPlans.get(preconditionHash);
      if (!dryRun) issuedSceneBackupKeepPlans.delete(preconditionHash);
      if (!Number.isInteger(pid) || pid <= 0 || Number.isNaN(Date.parse(processStartUtc))
        || typeof root !== "string" || root.length === 0 || root.length > 512 || root.trim() !== root || root.includes("\0")) return refuse("invalid_scene_backup_project_or_process_binding");
      // The dedicated Gateway handler verifies fresh process/root correlation; this engine binds that exact root.
      const projectIdentitySha256 = `sha256:${createHash("sha256").update(root).digest("hex")}`;
      const nowMs = options.now?.().getTime() ?? Date.now();
      if (!dryRun && (args.confirm_keep_backups !== true || !issued || issued.expiresAtMs <= nowMs)) return refuse("scene_backup_keep_plan_not_issued_expired_or_consumed");
      if (issued && (issued.evidence.pid !== pid || issued.evidence.process_start_utc !== processStartUtc || issued.evidence.project_identity_sha256 !== projectIdentitySha256)) return refuse("scene_backup_keep_plan_binding_changed");
      let status: UnityWindowDiagnosticsStatus;
      try {
        status = cache(options.inspect?.() ?? inspectUnityWindowDiagnostics({ ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) }));
      } catch {
        return refuse("authoritative_scene_backup_target_scan_required");
      }
      if (issued && issued.expiresAtMs <= (options.now?.().getTime() ?? Date.now())) return refuse("scene_backup_keep_plan_not_issued_expired_or_consumed");
      const capturedAt = issued?.evidence.captured_at ?? cached!.captured_at;
      const expiresAt = issued?.evidence.expires_at ?? new Date(nowMs + SCENE_BACKUP_KEEP_PLAN_TTL_MS).toISOString();
      const current = sceneBackupKeepEvidence(status, pid, processStartUtc, projectIdentitySha256, capturedAt, expiresAt, preconditionHash || `sha256:${"0".repeat(64)}`);
      if (!current) return refuse("exact_scene_backup_detected_modal_not_found_ambiguous_or_non_authoritative");
      if (issued && !sameSceneBackupKeepIdentity(issued.evidence, current)) return refuse("scene_backup_modal_or_control_binding_changed");
      const attemptedIdentityKey = sceneBackupKeepPopupKey(current);
      if (attemptedSceneBackupKeepPopups.has(attemptedIdentityKey)) return refuse("scene_backup_keep_delivery_already_attempted_for_popup");
      if (attemptedSceneBackupKeepPopups.size >= MAX_SCENE_BACKUP_KEEP_TRACKED_POPUPS) return refuse("scene_backup_keep_attempt_tracking_capacity_reached");
      if (dryRun) {
        if (issuedSceneBackupKeepPlans.size >= MAX_SCENE_BACKUP_KEEP_TRACKED_POPUPS) return refuse("scene_backup_keep_plan_capacity_reached");
        const hash = `sha256:${createHash("sha256").update(`${JSON.stringify(current)}\n${randomUUID()}`).digest("hex")}`;
        const evidence = { ...current, precondition_hash: hash };
        issuedSceneBackupKeepPlans.set(hash, { evidence, expiresAtMs: nowMs + SCENE_BACKUP_KEEP_PLAN_TTL_MS });
        return sceneBackupKeepResult({ dryRun: true, pid, processStartUtc, status: "ready", evidence });
      }
      if (!issued) return refuse("scene_backup_keep_plan_not_issued_expired_or_consumed");
      const guard = createPopupInteractionGuard(issued.expiresAtMs, beforeInteraction, options.now);
      try { guard.check(); } catch (error) {
        if (guard.shouldRestore(error)) issuedSceneBackupKeepPlans.set(preconditionHash, issued);
        if (error instanceof PopupEvidenceExpired) return refuse("scene_backup_keep_plan_not_issued_expired_or_consumed");
        throw error;
      }
      for (const [hash, plan] of issuedSceneBackupKeepPlans) {
        if (sceneBackupKeepPopupKey(plan.evidence) === attemptedIdentityKey) issuedSceneBackupKeepPlans.delete(hash);
      }
      // Record before invoking the helper: exceptions, timeouts and reentrant calls must not replay a send.
      attemptedSceneBackupKeepPopups.set(attemptedIdentityKey, issued.evidence);
      let interaction: UnitySceneBackupKeepInteraction;
      try {
        interaction = options.keepSceneBackups?.(issued.evidence)
          ?? keepUnitySceneBackups(issued.evidence, { ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) });
      } catch {
        interaction = { status: "error", interaction_attempted: "unknown", delivery_state: "unknown", error: "scene_backup_keep_helper_outcome_unknown" };
      }
      if (interaction.interaction_attempted === false) attemptedSceneBackupKeepPopups.delete(attemptedIdentityKey);
      return sceneBackupKeepResult({ dryRun: false, pid, processStartUtc, status: interaction.status, evidence: issued.evidence, interaction, nativeDispatchCount: interaction.interaction_attempted === false ? 0 : 1, ...(interaction.error ? { error: interaction.error } : {}) });
    },
    respond: (args) => {
      const result = options.respond?.(args) ?? respondToUnityRecoveryPrompt(args, { ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) });
      cache(result.active_after ?? result.active_before);
      return result;
    },
    respondExternalSceneReload: (args, beforeInteraction) => {
      const evidenceId = typeof args.evidence_id === "string" ? args.evidence_id : "";
      const issued = issuedExternalSceneEvidence.get(evidenceId);
      issuedExternalSceneEvidence.delete(evidenceId);
      const nowMs = options.now?.().getTime() ?? Date.now();
      if (!issued || issued.expiresAtMs <= nowMs) {
        return externalSceneReloadRespondResult({
          ...(args.action === "reload" || args.action === "ignore" ? { action: args.action } : {}),
          pid: numberOrZero(args.pid),
          ...(typeof args.hwnd === "string" ? { hwnd: args.hwnd } : {}),
          status: "refused",
          error: "external_scene_reload_evidence_not_issued_expired_or_consumed",
        });
      }
      const guard = createPopupInteractionGuard(issued.expiresAtMs, beforeInteraction, options.now);
      try {
        if (options.respondExternalSceneReload) {
          guard.check();
          return options.respondExternalSceneReload(args);
        }
        return respondToUnityExternalSceneReload(args, { expectedExternalSceneReloadEvidence: issued.evidence, beforeInteraction: guard.check, ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) });
      } catch (error) {
        if (guard.shouldRestore(error)) issuedExternalSceneEvidence.set(evidenceId, issued);
        if (error instanceof PopupEvidenceExpired) return externalSceneReloadRespondResult({ ...(args.action === "reload" || args.action === "ignore" ? { action: args.action } : {}), pid: numberOrZero(args.pid), ...(typeof args.hwnd === "string" ? { hwnd: args.hwnd } : {}), status: "refused", error: "external_scene_reload_evidence_not_issued_expired_or_consumed" });
        throw error;
      }
    },
    continueProjectDowngrade: (args, beforeInteraction) => {
      const evidenceId = typeof args.evidence_id === "string" ? args.evidence_id : "";
      const issued = issuedProjectDowngradeEvidence.get(evidenceId);
      if (issued) {
        for (const [candidateId, candidate] of issuedProjectDowngradeEvidence) {
          if (sameProjectDowngradePopupIdentity(issued.evidence, candidate.evidence)) issuedProjectDowngradeEvidence.delete(candidateId);
        }
      }
      const nowMs = options.now?.().getTime() ?? Date.now();
      if (!issued || issued.expiresAtMs <= nowMs) {
        return projectDowngradeContinueResult({ pid: numberOrZero(args.pid), ...(typeof args.hwnd === "string" ? { hwnd: args.hwnd } : {}), status: "refused", error: "project_downgrade_evidence_not_issued_expired_or_consumed" });
      }
      if (!projectDowngradeArgsMatchEvidence(args, issued.evidence)) {
        return projectDowngradeContinueResult({ pid: numberOrZero(args.pid), ...(typeof args.hwnd === "string" ? { hwnd: args.hwnd } : {}), status: "refused", error: "invalid_or_changed_project_downgrade_binding" });
      }
      const guard = createPopupInteractionGuard(issued.expiresAtMs, beforeInteraction, options.now);
      try {
        if (options.continueProjectDowngrade) {
          guard.check();
          return options.continueProjectDowngrade(args);
        }
        return continueUnityProjectDowngrade(args, { expectedProjectDowngradeEvidence: issued.evidence, beforeInteraction: guard.check, ...(options.execFileSync ? { execFileSync: options.execFileSync } : {}) });
      } catch (error) {
        if (guard.shouldRestore(error)) issuedProjectDowngradeEvidence.set(evidenceId, issued);
        if (error instanceof PopupEvidenceExpired) return projectDowngradeContinueResult({ pid: numberOrZero(args.pid), ...(typeof args.hwnd === "string" ? { hwnd: args.hwnd } : {}), status: "refused", error: "project_downgrade_evidence_not_issued_expired_or_consumed" });
        throw error;
      }
    },
  };
}

export function projectDowngradeEvidence(status: UnityWindowDiagnosticsStatus, pid: number, capturedAt: string, projectIdentitySha256: string): UnityProjectDowngradeEvidence | undefined {
  if (!Number.isInteger(pid) || pid <= 0 || Number.isNaN(Date.parse(capturedAt)) || !/^sha256:[a-f0-9]{64}$/.test(projectIdentitySha256)) return undefined;
  const candidates = (status.project_downgrade_modal_candidates ?? []).filter((candidate) => candidate.pid === pid);
  if (candidates.length !== 1) return undefined;
  const candidate = candidates[0];
  const downgrade = candidate?.project_downgrade;
  if (!candidate || !downgrade
    || candidate.block_code !== PROJECT_DOWNGRADE_MODAL_CODE
    || candidate.likely_project_downgrade_prompt !== true
    || (candidate.process_name !== "Unity" && candidate.process_name !== "Unity.exe")
    || typeof candidate.process_start_utc !== "string" || Number.isNaN(Date.parse(candidate.process_start_utc))
    || !Number.isInteger(candidate.window_thread_id) || (candidate.window_thread_id ?? 0) <= 0
    || !PROJECT_DOWNGRADE_CLASSES.includes(candidate.class_name as typeof PROJECT_DOWNGRADE_CLASSES[number])
    || candidate.window_title !== PROJECT_DOWNGRADE_TITLE
    || !/^0x[0-9A-Fa-f]+$/.test(candidate.hwnd)
    || downgrade.button_count !== 2
    || !/^0x[0-9A-Fa-f]+$/.test(downgrade.continue_button_hwnd)
    || !/^0x[0-9A-Fa-f]+$/.test(downgrade.quit_button_hwnd)
    || compareUnityVersions(downgrade.source_version, downgrade.target_version) !== 1) return undefined;
  const identity = {
    schema_version: 1 as const,
    source: "user32" as const,
    project_identity_sha256: projectIdentitySha256,
    pid,
    process_name: candidate.process_name as "Unity" | "Unity.exe",
    process_start_utc: candidate.process_start_utc,
    hwnd: candidate.hwnd,
    window_thread_id: candidate.window_thread_id as number,
    class_name: candidate.class_name as "#32770" | "UnityContainerWndClass",
    window_title: PROJECT_DOWNGRADE_TITLE as "Project Downgrade Required",
    source_version: downgrade.source_version,
    target_version: downgrade.target_version,
    body_sha256: downgrade.body_sha256,
    body_evidence: downgrade.body_evidence,
    buttons: {
      count: 2 as const,
      continue_count: 1 as const,
      quit_count: 1 as const,
      continue_hwnd: downgrade.continue_button_hwnd,
      quit_hwnd: downgrade.quit_button_hwnd,
    },
  };
  return { ...identity, evidence_id: `sha256:${createHash("sha256").update(JSON.stringify(identity)).digest("hex")}`, captured_at: capturedAt };
}

function scopedRegistryImportCloseEvidence(
  status: UnityWindowDiagnosticsStatus,
  pid: number,
  processStartUtc: string,
  projectIdentitySha256: string,
  capturedAt: string,
  expiresAt: string,
  preconditionHash: string,
): UnityScopedRegistryImportCloseEvidence | undefined {
  if (!Number.isInteger(pid) || pid <= 0
    || Number.isNaN(Date.parse(processStartUtc))
    || Number.isNaN(Date.parse(capturedAt))
    || Number.isNaN(Date.parse(expiresAt))
    || !/^sha256:[a-f0-9]{64}$/.test(projectIdentitySha256)
    || !/^sha256:[a-f0-9]{64}$/.test(preconditionHash)) return undefined;
  const candidates = (status.scoped_registry_import_modal_candidates ?? [])
    .filter((candidate) => candidate.pid === pid && candidate.process_start_utc === processStartUtc);
  if (candidates.length !== 1) return undefined;
  const candidate = candidates[0];
  const scoped = candidate?.scoped_registry_import;
  const buttonIdentities = scoped ? scopedRegistryButtonIdentities.get(scoped) : undefined;
  if (!candidate || !scoped
    || candidate.source !== "user32"
    || candidate.block_code !== SCOPED_REGISTRY_IMPORT_MODAL_CODE
    || candidate.likely_scoped_registry_import_prompt !== true
    || (candidate.process_name !== "Unity" && candidate.process_name !== "Unity.exe")
    || candidate.class_name !== "#32770"
    || candidate.window_title !== SCOPED_REGISTRY_IMPORT_TITLE
    || candidate.window_visible !== true
    || candidate.window_enabled !== true
    || !Number.isInteger(candidate.window_thread_id) || (candidate.window_thread_id ?? 0) <= 0
    || !/^0x[0-9A-Fa-f]+$/.test(candidate.hwnd)
    || scoped.button_count !== 2
    || scoped.learn_more_button_count !== 1
    || scoped.close_button_count !== 1
    || !buttonIdentities
    || !/^0x[0-9A-Fa-f]+$/.test(buttonIdentities.learnMoreHwnd)
    || !/^0x[0-9A-Fa-f]+$/.test(buttonIdentities.closeHwnd)) return undefined;
  return {
    schema_version: 1,
    precondition_hash: preconditionHash,
    captured_at: capturedAt,
    expires_at: expiresAt,
    source: "user32",
    project_identity_sha256: projectIdentitySha256,
    pid,
    process_name: candidate.process_name as "Unity" | "Unity.exe",
    process_start_utc: processStartUtc,
    hwnd: candidate.hwnd,
    window_thread_id: candidate.window_thread_id as number,
    class_name: "#32770",
    window_title: SCOPED_REGISTRY_IMPORT_TITLE,
    title_sha256: scoped.title_sha256,
    body_sha256: scoped.body_sha256,
    visible_control_count: scoped.visible_control_count,
    button_count: 2,
    learn_more_button_count: 1,
    close_button_count: 1,
    learn_more_button_hwnd: buttonIdentities.learnMoreHwnd,
    close_button_hwnd: buttonIdentities.closeHwnd,
  };
}

function scopedRegistryImportCloseArgsMatchEvidence(args: UnityScopedRegistryImportCloseArgs, evidence: UnityScopedRegistryImportCloseEvidence): boolean {
  return args.dry_run === false
    && args.confirm_close === true
    && args.precondition_hash === evidence.precondition_hash
    && args.pid === evidence.pid
    && args.process_start_utc === evidence.process_start_utc
    && args.project_identity_sha256 === evidence.project_identity_sha256;
}

function sameScopedRegistryImportCloseIdentity(left: UnityScopedRegistryImportCloseEvidence, right: UnityScopedRegistryImportCloseEvidence): boolean {
  return left.project_identity_sha256 === right.project_identity_sha256
    && left.pid === right.pid
    && left.process_name === right.process_name
    && left.process_start_utc === right.process_start_utc
    && left.hwnd.toLowerCase() === right.hwnd.toLowerCase()
    && left.window_thread_id === right.window_thread_id
    && left.class_name === right.class_name
    && left.window_title === right.window_title
    && left.title_sha256 === right.title_sha256
    && left.body_sha256 === right.body_sha256
    && left.visible_control_count === right.visible_control_count
    && left.button_count === right.button_count
    && left.learn_more_button_count === right.learn_more_button_count
    && left.close_button_count === right.close_button_count
    && left.learn_more_button_hwnd.toLowerCase() === right.learn_more_button_hwnd.toLowerCase()
    && left.close_button_hwnd.toLowerCase() === right.close_button_hwnd.toLowerCase();
}

function safeModeEnterEvidence(
  status: UnityWindowDiagnosticsStatus,
  pid: number,
  processStartUtc: string,
  projectIdentitySha256: string,
  capturedAt: string,
  expiresAt: string,
  preconditionHash: string,
): UnitySafeModeEnterEvidence | undefined {
  if (!Number.isInteger(pid) || pid <= 0
    || Number.isNaN(Date.parse(processStartUtc))
    || Number.isNaN(Date.parse(capturedAt))
    || Number.isNaN(Date.parse(expiresAt))
    || !/^sha256:[a-f0-9]{64}$/.test(projectIdentitySha256)
    || !/^sha256:[a-f0-9]{64}$/.test(preconditionHash)) return undefined;
  const candidates = status.safe_mode_modal_candidates.filter((candidate) => candidate.pid === pid && candidate.process_start_utc === processStartUtc);
  if (candidates.length !== 1) return undefined;
  return safeModeEnterCandidateEvidence(candidates[0]!, pid, processStartUtc, projectIdentitySha256, capturedAt, expiresAt, preconditionHash);
}

function safeModeEnterCandidateEvidence(
  candidate: UnityNativeModalInfo,
  pid: number,
  processStartUtc: string,
  projectIdentitySha256: string,
  capturedAt: string,
  expiresAt: string,
  preconditionHash: string,
): UnitySafeModeEnterEvidence | undefined {
  const safeMode = candidate?.safe_mode;
  if (!safeMode
    || candidate.source !== "user32"
    || candidate.block_code !== SAFE_MODE_MODAL_CODE
    || candidate.likely_safe_mode_prompt !== true
    || (candidate.process_name !== "Unity" && candidate.process_name !== "Unity.exe")
    || candidate.class_name !== "#32770"
    || candidate.window_title !== "Enter Safe Mode?"
    || candidate.window_visible !== true
    || candidate.window_enabled !== true
    || !Number.isInteger(candidate.window_thread_id) || (candidate.window_thread_id ?? 0) <= 0
    || !/^0x[0-9A-Fa-f]+$/.test(candidate.hwnd)
    || safeMode.button_count !== 3
    || safeMode.enter_safe_mode_button_count !== 1
    || safeMode.ignore_button_count !== 1
    || safeMode.quit_button_count !== 1
    || !/^sha256:[a-f0-9]{64}$/.test(safeMode.body_sha256)
    || !/^sha256:[a-f0-9]{64}$/.test(safeMode.control_topology_sha256)
    || !/^0x[0-9A-Fa-f]+$/.test(safeMode.enter_safe_mode_button_hwnd)
    || !/^0x[0-9A-Fa-f]+$/.test(safeMode.ignore_button_hwnd)
    || !/^0x[0-9A-Fa-f]+$/.test(safeMode.quit_button_hwnd)) return undefined;
  return {
    schema_version: 1,
    precondition_hash: preconditionHash,
    captured_at: capturedAt,
    expires_at: expiresAt,
    source: "user32",
    project_identity_sha256: projectIdentitySha256,
    pid,
    process_name: candidate.process_name as "Unity" | "Unity.exe",
    process_start_utc: processStartUtc,
    hwnd: candidate.hwnd,
    window_thread_id: candidate.window_thread_id as number,
    class_name: "#32770",
    window_title: "Enter Safe Mode?",
    evidence_scope: safeMode.evidence_scope,
    body_sha256: safeMode.body_sha256,
    control_topology_sha256: safeMode.control_topology_sha256,
    visible_control_count: safeMode.visible_control_count,
    buttons: {
      count: 3,
      enter_safe_mode_count: 1,
      ignore_count: 1,
      quit_count: 1,
      enter_safe_mode_hwnd: safeMode.enter_safe_mode_button_hwnd,
      ignore_hwnd: safeMode.ignore_button_hwnd,
      quit_hwnd: safeMode.quit_button_hwnd,
    },
  };
}

function safeModeEnterArgsMatchEvidence(args: UnitySafeModeEnterArgs, evidence: UnitySafeModeEnterEvidence): boolean {
  return args.dry_run === false
    && args.confirm_enter_safe_mode === true
    && args.precondition_hash === evidence.precondition_hash
    && args.pid === evidence.pid
    && args.process_start_utc === evidence.process_start_utc
    && args.project_identity_sha256 === evidence.project_identity_sha256;
}

function sameSafeModeEnterIdentity(left: UnitySafeModeEnterEvidence, right: UnitySafeModeEnterEvidence): boolean {
  return left.project_identity_sha256 === right.project_identity_sha256
    && left.pid === right.pid
    && left.process_name === right.process_name
    && left.process_start_utc === right.process_start_utc
    && left.hwnd.toLowerCase() === right.hwnd.toLowerCase()
    && left.window_thread_id === right.window_thread_id
    && left.class_name === right.class_name
    && left.window_title === right.window_title
    && left.evidence_scope === right.evidence_scope
    && left.body_sha256 === right.body_sha256
    && left.control_topology_sha256 === right.control_topology_sha256
    && left.visible_control_count === right.visible_control_count
    && left.buttons.enter_safe_mode_hwnd.toLowerCase() === right.buttons.enter_safe_mode_hwnd.toLowerCase()
    && left.buttons.ignore_hwnd.toLowerCase() === right.buttons.ignore_hwnd.toLowerCase()
    && left.buttons.quit_hwnd.toLowerCase() === right.buttons.quit_hwnd.toLowerCase();
}

function safeModeAttemptIdentityKey(evidence: UnitySafeModeEnterEvidence): string {
  return createHash("sha256").update(JSON.stringify({
    project_identity_sha256: evidence.project_identity_sha256,
    pid: evidence.pid,
    process_name: evidence.process_name,
    process_start_utc: evidence.process_start_utc,
    hwnd: evidence.hwnd.toLowerCase(),
    window_thread_id: evidence.window_thread_id,
    class_name: evidence.class_name,
    window_title: evidence.window_title,
    evidence_scope: evidence.evidence_scope,
    body_sha256: evidence.body_sha256,
    control_topology_sha256: evidence.control_topology_sha256,
    visible_control_count: evidence.visible_control_count,
    buttons: {
      enter_safe_mode_hwnd: evidence.buttons.enter_safe_mode_hwnd.toLowerCase(),
      ignore_hwnd: evidence.buttons.ignore_hwnd.toLowerCase(),
      quit_hwnd: evidence.buttons.quit_hwnd.toLowerCase(),
    },
  })).digest("hex");
}

function sceneBackupKeepEvidence(status: UnityWindowDiagnosticsStatus, pid: number, processStartUtc: string, projectIdentitySha256: string, capturedAt: string, expiresAt: string, preconditionHash: string): UnitySceneBackupKeepEvidence | undefined {
  const candidates = status.native_modal_candidates.filter((candidate) => candidate.pid === pid);
  if (candidates.length !== 1 || candidates[0]?.process_start_utc !== processStartUtc) return undefined;
  const candidate = candidates[0]!;
  const detected = candidate.scene_backup_detected;
  const native = detected ? sceneBackupDetectedNativeBindings.get(detected) : undefined;
  if (!detected || !native?.authoritative || candidate.source !== "user32"
    || candidate.block_code !== SCENE_BACKUP_RECOVERY_MODAL_CODE || candidate.scene_backup_recovery_variant !== "scene_backup_detected"
    || candidate.window_title !== SCENE_BACKUP_DETECTED_TITLE || candidate.class_name !== "#32770"
    || (candidate.process_name !== "Unity" && candidate.process_name !== "Unity.exe")
    || candidate.window_visible !== true || candidate.window_enabled !== true
    || !candidate.window_thread_id || candidate.window_thread_id <= 0) return undefined;
  return {
    schema_version: 1,
    precondition_hash: preconditionHash,
    captured_at: capturedAt,
    expires_at: expiresAt,
    source: "user32",
    project_identity_sha256: projectIdentitySha256,
    pid,
    process_name: candidate.process_name,
    process_start_utc: processStartUtc,
    hwnd: candidate.hwnd,
    window_thread_id: candidate.window_thread_id,
    class_name: "#32770",
    window_title: SCENE_BACKUP_DETECTED_TITLE,
    evidence_scope: "complete_body",
    body_sha256: detected.body_sha256,
    control_topology_sha256: detected.control_topology_sha256,
    visible_control_count: detected.visible_control_count,
    buttons: { count: 2, keep_backups_count: 1, delete_count: 1, keep_backups_hwnd: native.keepHwnd, delete_hwnd: native.deleteHwnd },
  };
}

function sameSceneBackupKeepIdentity(left: UnitySceneBackupKeepEvidence, right: UnitySceneBackupKeepEvidence): boolean {
  return left.project_identity_sha256 === right.project_identity_sha256
    && sceneBackupKeepPopupKey(left) === sceneBackupKeepPopupKey(right)
    && left.process_name === right.process_name
    && left.body_sha256 === right.body_sha256
    && left.control_topology_sha256 === right.control_topology_sha256
    && left.visible_control_count === right.visible_control_count
    && left.buttons.keep_backups_hwnd.toLowerCase() === right.buttons.keep_backups_hwnd.toLowerCase()
    && left.buttons.delete_hwnd.toLowerCase() === right.buttons.delete_hwnd.toLowerCase();
}

function sceneBackupKeepPopupKey(evidence: UnitySceneBackupKeepEvidence): string {
  return JSON.stringify([evidence.pid, evidence.process_start_utc, evidence.hwnd.toLowerCase(), evidence.window_thread_id]);
}

export function externalSceneReloadEvidence(status: UnityWindowDiagnosticsStatus, pid: number, capturedAt: string): UnityExternalSceneReloadEvidence | undefined {
  if (!Number.isInteger(pid) || pid <= 0 || Number.isNaN(Date.parse(capturedAt))) return undefined;
  const candidates = status.external_scene_reload_modal_candidates.filter((candidate) => candidate.pid === pid);
  if (candidates.length !== 1) return undefined;
  const candidate = candidates[0];
  if (!candidate
    || candidate.block_code !== EXTERNAL_SCENE_RELOAD_MODAL_CODE
    || candidate.likely_external_scene_reload_prompt !== true
    || (candidate.process_name !== "Unity" && candidate.process_name !== "Unity.exe")
    || typeof candidate.process_start_utc !== "string"
    || Number.isNaN(Date.parse(candidate.process_start_utc))
    || !Number.isInteger(candidate.window_thread_id)
    || (candidate.window_thread_id ?? 0) <= 0
    || (candidate.class_name !== "#32770" && candidate.class_name !== "UnityContainerWndClass")
    || candidate.window_title !== "The open scene(s) have been modified externally"
    || !/^0x[0-9A-Fa-f]+$/.test(candidate.hwnd)
    || !candidate.reload_button_hwnd
    || !/^0x[0-9A-Fa-f]+$/.test(candidate.reload_button_hwnd)
    || !candidate.ignore_button_hwnd
    || !/^0x[0-9A-Fa-f]+$/.test(candidate.ignore_button_hwnd)) return undefined;

  const scenePathCount = candidate.scene_paths_count ?? candidate.scene_paths?.length ?? 0;
  const scenePaths = safeExternalScenePaths(candidate.scene_paths ?? []);
  const completeBodyEvidence = candidate.child_texts.some((text) => text.includes("The following open scene(s) have been changed on disk:"))
    && candidate.child_texts.some((text) => text.includes("Do you want to reload the scene(s)?"));
  const evidenceScope = candidate.external_scene_reload_evidence_scope
    ?? (completeBodyEvidence && scenePathCount > 0 ? "complete_body_and_scene_paths" : undefined);
  if (!evidenceScope
    || candidate.reload_button_count !== 1
    || candidate.ignore_button_count !== 1
    || candidate.scene_paths_truncated === true) return undefined;
  if (evidenceScope === "complete_body_and_scene_paths"
    && (scenePathCount < 1
      || scenePathCount > MAX_EXTERNAL_SCENE_PATHS
      || scenePaths.length !== scenePathCount
      || !completeBodyEvidence)) return undefined;
  if (evidenceScope === "sparse_control_topology"
    && (candidate.class_name !== "#32770"
      || candidate.window_visible !== true
      || candidate.window_enabled !== true
      || scenePathCount !== 0
      || scenePaths.length !== 0
      || candidate.child_texts.length !== 2
      || candidate.child_texts[0] !== "Reload"
      || candidate.child_texts[1] !== "Ignore")) return undefined;

  const identity = {
    schema_version: 1 as const,
    source: "user32" as const,
    pid,
    hwnd: candidate.hwnd,
    process_name: candidate.process_name as "Unity" | "Unity.exe",
    process_start_utc: candidate.process_start_utc,
    window_thread_id: candidate.window_thread_id as number,
    class_name: candidate.class_name as "#32770" | "UnityContainerWndClass",
    window_title: candidate.window_title as "The open scene(s) have been modified externally",
    evidence_scope: evidenceScope,
    scene_paths: [...scenePaths].sort(),
    scene_paths_count: scenePathCount,
    ...(evidenceScope === "complete_body_and_scene_paths" ? { body_evidence: { changed_on_disk_text: true as const, reload_question_text: true as const } } : {}),
    buttons: {
      reload_count: 1 as const,
      ignore_count: 1 as const,
      reload_hwnd: candidate.reload_button_hwnd,
      ignore_hwnd: candidate.ignore_button_hwnd,
    },
  };
  return {
    ...identity,
    evidence_id: `sha256:${createHash("sha256").update(JSON.stringify(identity)).digest("hex")}`,
    captured_at: capturedAt,
  };
}

export function inspectUnityWindowDiagnostics(options: UnityWindowDiagnosticsOptions = {}): UnityWindowDiagnosticsStatus {
  const budget = createUnityWindowScanBudget(options.scanBudgetMs, options.nowMonotonic);
  const scan = scanUnityWindows(options, budget);
  const nativeScan = scanNativeUnityModals(options, budget);
  return buildUnityWindowDiagnosticsStatus(scan, nativeScan, budget);
}

function buildUnityWindowDiagnosticsStatus(
  scan: ReturnType<typeof scanUnityWindowsFromResult>,
  nativeScan: ReturnType<typeof scanNativeUnityModalsFromResult>,
  budget: UnityWindowScanBudget,
): UnityWindowDiagnosticsStatus {
  const recoveryPromptCandidates = scan.windows.filter((window) => window.likely_recovery_prompt);
  const nativeModalCandidates = nativeScan.modals;
  const nativeProgressWindows = nativeScan.progressWindows;
  const licensingClientCrashModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_unity_licensing_client_crash);
  const corruptedLibraryModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_corrupted_library);
  const safeModeModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_safe_mode_prompt);
  const missingSignatureModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_missing_signature_prompt);
  const externalSceneReloadModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_external_scene_reload_prompt);
  const projectDowngradeModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_project_downgrade_prompt);
  const duplicateProjectInstanceModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_duplicate_project_instance);
  const scopedRegistryImportModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_scoped_registry_import_prompt);
  const sceneBackupRecoveryModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_scene_backup_recovery_prompt);
  const lowDiskSpaceModalCandidates = nativeModalCandidates.filter((modal) => modal.likely_low_disk_space_prompt);
  const genericRecoveryPromptCandidates = recoveryPromptCandidates.filter((candidate) => !sceneBackupRecoveryModalCandidates.some((modal) => modal.pid === candidate.pid));
  const domainReloadProgressWindows = nativeProgressWindows.filter((window) => window.progress_kind === "domain_reload");
  const stalledDomainReloadProgressWindows = domainReloadProgressWindows.filter((window) => window.is_stalled);
  const blockingNativeModalCandidates = nativeModalCandidates.filter((modal) => modal.block_code);
  const unclassifiedNativeModalCandidates = nativeModalCandidates.filter((modal) => !modal.block_code);
  const blockingAutomationPossible = blockingNativeModalCandidates.length > 0;
  const blockingProgressPossible = stalledDomainReloadProgressWindows.length > 0;
  const blockingReloadPossible = domainReloadProgressWindows.length > 0;
  const blockingStartupPossible = recoveryPromptCandidates.length > 0 || blockingAutomationPossible || blockingProgressPossible;
  const blockingModalCode = blockingNativeModalCandidates[0]?.block_code;
  const blockingProgressCode = stalledDomainReloadProgressWindows[0]?.block_code;
  const nextValidActions = ["call_sgg_unity_projects", "call_diagnostics_status"];

  if (!nativeScan.supported) {
    nextValidActions.push("retry_native_unity_modal_scan", "inspect_visible_unity_windows_manually", "call_diagnostics.report_problem");
  }
  if (unclassifiedNativeModalCandidates.length > 0) {
    nextValidActions.push("operator_review_unclassified_native_unity_modal", "do_not_treat_zero_candidates_as_authoritative", "do_not_click_focus_close_or_restart_unity_from_gateway", "call_diagnostics.report_problem");
  }
  if (!scan.supported) {
    nextValidActions.push("retry_top_level_unity_window_scan", "inspect_visible_unity_windows_manually", "call_diagnostics.report_problem");
  }

  if (blockingAutomationPossible) {
    nextValidActions.unshift("inspect_native_unity_modal");
    nextValidActions.push("call_diagnostics.unity_modal.status", "call_diagnostics.report_problem");
    if (scopedRegistryImportModalCandidates.length === 0 && sceneBackupRecoveryModalCandidates.length === 0 && lowDiskSpaceModalCandidates.length === 0) nextValidActions.push("retry_after_modal_closed");
  }

  if (licensingClientCrashModalCandidates.length > 0) {
    nextValidActions.push("operator_close_unity_licensing_client_error_dialog");
  }

  if (corruptedLibraryModalCandidates.length > 0) {
    nextValidActions.push("operator_choose_dont_rebuild_library_or_close_dialog");
  }

  if (safeModeModalCandidates.length > 0) {
    nextValidActions.push("dry_run_diagnostics.unity_safe_mode.enter", "ask_gate_exact_enter_safe_mode_only", "do_not_choose_ignore_or_quit_from_gateway", "do_not_auto_click_safe_mode_prompt");
  }

  if (missingSignatureModalCandidates.length > 0) {
    nextValidActions.push("dry_run_diagnostics.unity_recovery_prompt.respond_close_missing_signature", "close_only_the_missing_signature_prompt_after_operator_review", "call_sgg_unity_projects_after_modal_resolution", "call_compile.status_after_heartbeat_recovers");
  }

  if (externalSceneReloadModalCandidates.length > 0) {
    nextValidActions.push("operator_choose_reload_or_ignore_external_scene_changes", "do_not_auto_respond_external_scene_reload_prompt", "do_not_retry_write_command", "call_sgg_unity_projects_after_modal_resolution", "call_compile.status_after_heartbeat_recovers", "call_diagnostics.report_problem");
  }

  if (projectDowngradeModalCandidates.length > 0) {
    nextValidActions.push("review_project_downgrade_warning", "ask_gate_diagnostics.unity_project_downgrade.continue", "do_not_auto_continue_or_quit_project_downgrade", "require_fresh_project_state_after_operator_resolution");
  }

  if (duplicateProjectInstanceModalCandidates.length > 0) {
    nextValidActions.push("inspect_unity_processes_and_project_roots", "operator_close_duplicate_project_dialog_after_review", "do_not_auto_click_duplicate_project_dialog", "do_not_launch_another_unity_instance", "call_sgg_unity_projects_after_modal_resolution", "wait_for_fresh_mcp_heartbeat", "call_diagnostics.report_problem");
  }

  if (scopedRegistryImportModalCandidates.length > 0) {
    nextValidActions.push("operator_review_scoped_registry_import_dialog", "do_not_click_learn_more_or_close_from_gateway", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution", "call_diagnostics.report_problem");
  }

  if (sceneBackupRecoveryModalCandidates.length > 0) {
    if (sceneBackupRecoveryModalCandidates.some((candidate) => candidate.scene_backup_recovery_variant === "scene_backup_detected")) {
      nextValidActions.push("operator_review_scene_backup_detected_dialog", "obtain_exact_keep_backups_dry_run_plan", "do_not_choose_delete_or_legacy_yes_no_from_gateway");
    }
    if (sceneBackupRecoveryModalCandidates.some((candidate) => candidate.scene_backup_recovery_variant !== "scene_backup_detected")) {
      nextValidActions.push("operator_review_scene_backup_recovery_dialog", "do_not_click_yes_or_no_from_gateway");
    }
    nextValidActions.push("do_not_focus_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution", "call_diagnostics.report_problem");
  }

  if (lowDiskSpaceModalCandidates.length > 0) {
    nextValidActions.push("operator_review_low_disk_space_dialog", "do_not_click_focus_or_close_low_disk_dialog_from_gateway", "do_not_free_disk_or_delete_library_or_cache_from_gateway", "do_not_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution", "call_diagnostics.report_problem");
  }

  if (blockingProgressPossible) {
    nextValidActions.unshift("inspect_visible_unity_progress_window");
    nextValidActions.push("call_diagnostics.unity_modal.status", "wait_briefly_then_recheck_heartbeat", "operator_inspect_visible_unity_progress_window", "do_not_click_cancel_from_gateway", "do_not_kill_unity_from_gateway", "do_not_delete_library_or_cache_files", "call_diagnostics.report_problem");
  }

  if (blockingStartupPossible) {
    if (genericRecoveryPromptCandidates.length > 0) {
      nextValidActions.unshift("inspect_unity_recovery_modal");
      nextValidActions.push("call_diagnostics.unity_recovery_prompt.status", "dry_run_diagnostics.unity_recovery_prompt.respond", "operator_close_recovery_prompt_discard_if_safe", "call_sgg_unity_projects_after_closing_modal");
    }
  }

  return {
    schema_version: 1,
    supported: scan.supported || nativeScan.supported,
    active_unity_windows_count: scan.windows.length,
    active_unity_windows: scan.windows,
    native_modal_candidates_count: nativeModalCandidates.length,
    native_modal_candidates: nativeModalCandidates,
    top_level_window_scan_authoritative: scan.supported,
    native_modal_scan_authoritative: nativeScan.supported,
    aggregate_scan_authoritative: scan.supported && nativeScan.user32Semantic.authoritative,
    scan_budget_ms: budget.budgetMs,
    scan_elapsed_ms: Math.max(0, Math.round(budget.now() - budget.startedMs)),
    helper_observations: {
      tasklist: scan.helper,
      native_powershell: nativeScan.helper,
    },
    semantic_channels: {
      top_level_inventory: scan.semantic,
      native_user32: nativeScan.user32Semantic,
      native_uia_scoped_fallback: nativeScan.uiaSemantic,
    },
    licensing_client_crash_modal_candidates_count: licensingClientCrashModalCandidates.length,
    licensing_client_crash_modal_candidates: licensingClientCrashModalCandidates,
    corrupted_library_modal_candidates_count: corruptedLibraryModalCandidates.length,
    corrupted_library_modal_candidates: corruptedLibraryModalCandidates,
    safe_mode_modal_candidates_count: safeModeModalCandidates.length,
    safe_mode_modal_candidates: safeModeModalCandidates,
    missing_signature_modal_candidates_count: missingSignatureModalCandidates.length,
    missing_signature_modal_candidates: missingSignatureModalCandidates,
    external_scene_reload_modal_candidates_count: externalSceneReloadModalCandidates.length,
    external_scene_reload_modal_candidates: externalSceneReloadModalCandidates,
    project_downgrade_modal_candidates_count: projectDowngradeModalCandidates.length,
    project_downgrade_modal_candidates: projectDowngradeModalCandidates,
    duplicate_project_instance_modal_candidates_count: duplicateProjectInstanceModalCandidates.length,
    duplicate_project_instance_modal_candidates: duplicateProjectInstanceModalCandidates,
    scoped_registry_import_modal_candidates_count: scopedRegistryImportModalCandidates.length,
    scoped_registry_import_modal_candidates: scopedRegistryImportModalCandidates,
    scene_backup_recovery_modal_candidates_count: sceneBackupRecoveryModalCandidates.length,
    scene_backup_recovery_modal_candidates: sceneBackupRecoveryModalCandidates,
    low_disk_space_modal_candidates_count: lowDiskSpaceModalCandidates.length,
    low_disk_space_modal_candidates: lowDiskSpaceModalCandidates,
    native_progress_windows_count: nativeProgressWindows.length,
    native_progress_windows: nativeProgressWindows,
    domain_reload_progress_windows_count: domainReloadProgressWindows.length,
    domain_reload_progress_windows: domainReloadProgressWindows,
    domain_reload_progress_stall_count: stalledDomainReloadProgressWindows.length,
    recovery_prompt_candidates_count: recoveryPromptCandidates.length,
    recovery_prompt_candidates: recoveryPromptCandidates,
    blocking_startup_possible: blockingStartupPossible,
    blocking_automation_possible: blockingAutomationPossible,
    blocking_progress_possible: blockingProgressPossible,
    blocking_reload_possible: blockingReloadPossible,
    ...(blockingModalCode ? { blocking_modal_code: blockingModalCode } : {}),
    ...(blockingProgressCode ? { blocking_progress_code: blockingProgressCode } : {}),
    errors: [...scan.errors, ...nativeScan.errors],
    next_valid_actions: uniqueStrings(nextValidActions),
  };
}

async function inspectUnityWindowDiagnosticsAsync(scanBudgetMs?: number): Promise<UnityWindowDiagnosticsStatus> {
  const budget = createUnityWindowScanBudget(scanBudgetMs);
  const tasklist = await runWindowsToolAsync("tasklist.exe", tasklistArgs(), budget);
  const native = await runWindowsToolAsync("powershell.exe", nativeUnityModalPowerShellArgs(), budget);
  return buildUnityWindowDiagnosticsStatus(scanUnityWindowsFromResult(tasklist), scanNativeUnityModalsFromResult(native), budget);
}

export function respondToUnityRecoveryPrompt(args: UnityRecoveryPromptRespondArgs, options: UnityWindowDiagnosticsOptions = {}): UnityRecoveryPromptRespondResult {
  const activeBefore = inspectUnityWindowDiagnostics(options);
  const action = normalizeRecoveryPromptAction(args.action);
  const dryRun = args.dry_run !== false;
  const errors: string[] = [];
  const skipped: Array<{ pid: number; reason: string }> = [];
  const results: UnityRecoveryPromptClickResult[] = [];
  const targetPids: number[] = [];

  if (!action) {
    errors.push("invalid_action");
    return recoveryPromptRespondResult({ activeBefore, actionAlias: undefined, dryRun, supported: activeBefore.supported, targetPids, skipped, results, errors });
  }

  const spec = modalRespondSpec(action, activeBefore);
  const candidatesByPid = new Map(spec.candidates.map((window) => [window.pid, window]));
  const requestedPids = normalizedRequestedPids(args.pids);
  if (requestedPids.invalid.length > 0) {
    skipped.push(...requestedPids.invalid.map((pid) => ({ pid, reason: "invalid_pid" })));
  }

  const candidatePids = requestedPids.valid.length > 0 ? requestedPids.valid : spec.candidates.map((window) => window.pid);
  for (const pid of candidatePids) {
    if (!candidatesByPid.has(pid)) {
      skipped.push({ pid, reason: spec.notCandidateReason });
    } else if (!targetPids.includes(pid)) {
      targetPids.push(pid);
    }
  }

  if (targetPids.length === 0) {
    errors.push(spec.candidates.length === 0 ? spec.noCandidateError : spec.noRequestedCandidateError);
    return recoveryPromptRespondResult({ activeBefore, actionAlias: action, dryRun, supported: activeBefore.supported, targetPids, skipped, results, errors });
  }

  if (!dryRun && targetPids.length !== 1) {
    for (const pid of targetPids) {
      skipped.push({ pid, reason: "explicit_single_pid_required" });
    }
    errors.push("explicit_single_pid_required");
    return recoveryPromptRespondResult({ activeBefore, actionAlias: action, dryRun, supported: activeBefore.supported, targetPids, skipped, results, errors });
  }

  const powershell = runWindowsTool("powershell.exe", recoveryPromptPowerShellArgs(targetPids, dryRun, spec), options);
  if (powershell.status !== "ok") {
    const errorCode = powershell.observation.normalized_error_code ?? "helper_failed";
    errors.push(`powershell_failed:${errorCode}`);
    for (const pid of targetPids) {
      results.push({ pid, status: "error", ...(powershell.executable ? { executable: powershell.executable } : {}), error: errorCode });
    }
    return recoveryPromptRespondResult({ activeBefore, actionAlias: action, dryRun, supported: activeBefore.supported, targetPids, skipped, results, errors });
  }

  results.push(...parseRecoveryPromptClickResults(powershell.output.toString("utf8"), powershell.executable ?? "powershell.exe", targetPids));
  return recoveryPromptRespondResult({
    activeBefore,
    ...(dryRun ? {} : { activeAfter: inspectUnityWindowDiagnostics(options) }),
    actionAlias: action,
    dryRun,
    supported: activeBefore.supported,
    targetPids,
    skipped,
    results,
    errors,
  });
}

export function respondToUnityExternalSceneReload(args: UnityExternalSceneReloadRespondArgs, options: UnityWindowDiagnosticsOptions = {}): UnityExternalSceneReloadRespondResult {
  const action = args.action === "reload" || args.action === "ignore" ? args.action : undefined;
  const pid = numberOrZero(args.pid);
  const hwnd = typeof args.hwnd === "string" && /^0x[0-9A-Fa-f]+$/.test(args.hwnd) ? args.hwnd : undefined;
  const evidenceId = typeof args.evidence_id === "string" && /^sha256:[a-f0-9]{64}$/.test(args.evidence_id) ? args.evidence_id : undefined;
  if (!action || pid === 0 || !hwnd || !evidenceId) {
    return externalSceneReloadRespondResult({ ...(action ? { action } : {}), pid, ...(hwnd ? { hwnd } : {}), status: "refused", error: "invalid_exact_popup_binding" });
  }

  const issuedEvidence = options.expectedExternalSceneReloadEvidence;
  if (!issuedEvidence
    || issuedEvidence.evidence_id !== evidenceId
    || issuedEvidence.pid !== pid
    || issuedEvidence.hwnd.toLowerCase() !== hwnd.toLowerCase()) {
    return externalSceneReloadRespondResult({ action, pid, hwnd, status: "refused", error: "external_scene_reload_evidence_not_issued_expired_or_consumed" });
  }

  const capturedAt = new Date().toISOString();
  const status = inspectUnityWindowDiagnostics(options);
  const currentEvidence = externalSceneReloadEvidence(status, pid, capturedAt);
  if (!currentEvidence) {
    return externalSceneReloadRespondResult({ action, pid, hwnd, status: "refused", error: "exact_external_scene_reload_popup_not_found" });
  }
  if (!sameExternalSceneReloadIdentity(issuedEvidence, currentEvidence)) {
    return externalSceneReloadRespondResult({ action, pid, hwnd, evidence: issuedEvidence, status: "refused", error: "external_scene_reload_popup_changed" });
  }

  options.beforeInteraction?.();
  const powershell = runWindowsTool("powershell.exe", externalSceneReloadPowerShellArgs(issuedEvidence, action), options);
  if (powershell.status !== "ok") {
    return externalSceneReloadRespondResult({ action, pid, hwnd, evidence: issuedEvidence, status: "error", interactionAttempted: "unknown", deliveryState: "unknown", error: `powershell_failed:${powershell.observation.normalized_error_code ?? "helper_failed"}` });
  }
  const interaction = parseExternalSceneReloadInteraction(powershell.output.toString("utf8"));
  if (!interaction) {
    return externalSceneReloadRespondResult({ action, pid, hwnd, evidence: issuedEvidence, status: "error", interactionAttempted: "unknown", deliveryState: "unknown", error: "invalid_powershell_response" });
  }
  return externalSceneReloadRespondResult({
    action,
    pid,
    hwnd,
    evidence: issuedEvidence,
    status: interaction.status,
    interactionAttempted: interaction.status === "message_sent" || interaction.error === "bm_click_failed_or_timed_out",
    deliveryState: interaction.status === "message_sent" ? "message_sent" : interaction.error === "bm_click_failed_or_timed_out" ? "unknown_after_send_attempt" : "not_attempted",
    ...(interaction.buttonText ? { buttonText: interaction.buttonText } : {}),
    ...(interaction.reloadCount !== undefined ? { reloadCount: interaction.reloadCount } : {}),
    ...(interaction.ignoreCount !== undefined ? { ignoreCount: interaction.ignoreCount } : {}),
    ...(interaction.error ? { error: interaction.error } : {}),
  });
}

export function continueUnityProjectDowngrade(args: UnityProjectDowngradeContinueArgs, options: UnityWindowDiagnosticsOptions = {}): UnityProjectDowngradeContinueResult {
  const evidence = options.expectedProjectDowngradeEvidence;
  const hwnd = typeof args.hwnd === "string" ? args.hwnd : undefined;
  const pid = numberOrZero(args.pid);
  if (!evidence || !projectDowngradeArgsMatchEvidence(args, evidence)) {
    return projectDowngradeContinueResult({ pid, ...(hwnd ? { hwnd } : {}), status: "refused", error: "invalid_or_changed_project_downgrade_binding" });
  }
  const current = projectDowngradeEvidence(inspectUnityWindowDiagnostics(options), pid, new Date().toISOString(), evidence.project_identity_sha256);
  if (!current) return projectDowngradeContinueResult({ pid, ...(hwnd ? { hwnd } : {}), evidence, status: "refused", error: "exact_project_downgrade_popup_not_found" });
  if (!sameProjectDowngradeIdentity(evidence, current)) return projectDowngradeContinueResult({ pid, ...(hwnd ? { hwnd } : {}), evidence, status: "refused", error: "project_downgrade_popup_changed" });
  options.beforeInteraction?.();
  const powershell = runWindowsTool("powershell.exe", projectDowngradePowerShellArgs(evidence), options);
  if (powershell.status !== "ok") return projectDowngradeContinueResult({ pid, ...(hwnd ? { hwnd } : {}), evidence, status: "error", interactionAttempted: "unknown", deliveryState: "unknown", error: "powershell_failed" });
  const interaction = parseProjectDowngradeInteraction(powershell.output.toString("utf8"));
  if (!interaction) return projectDowngradeContinueResult({ pid, ...(hwnd ? { hwnd } : {}), evidence, status: "error", interactionAttempted: "unknown", deliveryState: "unknown", error: "invalid_powershell_response" });
  return projectDowngradeContinueResult({
    pid,
    ...(hwnd ? { hwnd } : {}),
    evidence,
    status: interaction.status,
    interactionAttempted: interaction.status === "message_submitted" || interaction.error === "bm_click_failed_or_timed_out",
    deliveryState: interaction.status === "message_submitted" ? "message_submitted" : interaction.error === "bm_click_failed_or_timed_out" ? "unknown_after_send_attempt" : "not_attempted",
    ...(interaction.buttonText ? { buttonText: interaction.buttonText } : {}),
    ...(interaction.error ? { error: interaction.error } : {}),
  });
}

export function closeUnityScopedRegistryImport(evidence: UnityScopedRegistryImportCloseEvidence, options: UnityWindowDiagnosticsOptions = {}): UnityScopedRegistryImportInteraction {
  const powershell = runWindowsTool("powershell.exe", scopedRegistryImportClosePowerShellArgs(evidence), options);
  if (powershell.status !== "ok") {
    return { status: "error", interaction_attempted: "unknown", delivery_state: "unknown", error: `powershell_failed:${powershell.observation.normalized_error_code ?? "helper_failed"}` };
  }
  const interaction = parseScopedRegistryImportCloseInteraction(powershell.output.toString("utf8"));
  return interaction ?? { status: "error", interaction_attempted: "unknown", delivery_state: "unknown", error: "invalid_powershell_response" };
}

export function enterUnitySafeMode(evidence: UnitySafeModeEnterEvidence, options: UnityWindowDiagnosticsOptions = {}): UnitySafeModeEnterInteraction {
  const powershell = runWindowsTool("powershell.exe", safeModeEnterPowerShellArgs(evidence), options);
  if (powershell.status !== "ok") {
    return { status: "error", interaction_attempted: "unknown", delivery_state: "unknown", error: `powershell_failed:${powershell.observation.normalized_error_code ?? "helper_failed"}` };
  }
  const interaction = parseSafeModeEnterInteraction(powershell.output.toString("utf8"));
  return interaction ?? { status: "error", interaction_attempted: "unknown", delivery_state: "unknown", error: "invalid_powershell_response" };
}

function keepUnitySceneBackups(evidence: UnitySceneBackupKeepEvidence, options: UnityWindowDiagnosticsOptions): UnitySceneBackupKeepInteraction {
  const identity = Buffer.from(JSON.stringify(evidence), "utf8").toString("base64");
  const powershell = runWindowsTool("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", sceneBackupKeepPowerShellScript(evidence.hwnd.slice(2), evidence.pid, identity)], options);
  if (powershell.status !== "ok") return { status: "error", interaction_attempted: "unknown", delivery_state: "unknown", error: `powershell_failed:${powershell.observation.normalized_error_code ?? "helper_failed"}` };
  return parseSafeModeEnterInteraction(powershell.output.toString("utf8"))
    ?? { status: "error", interaction_attempted: "unknown", delivery_state: "unknown", error: "invalid_powershell_response" };
}

function projectDowngradeArgsMatchEvidence(args: UnityProjectDowngradeContinueArgs, evidence: UnityProjectDowngradeEvidence): boolean {
  const projectHash = typeof args.canonical_project_root === "string" ? `sha256:${createHash("sha256").update(args.canonical_project_root).digest("hex")}` : "";
  return args.confirm_continue === true
    && args.evidence_id === evidence.evidence_id
    && projectHash === evidence.project_identity_sha256
    && args.pid === evidence.pid
    && args.process_name === evidence.process_name
    && args.process_start_utc === evidence.process_start_utc
    && typeof args.hwnd === "string" && args.hwnd.toLowerCase() === evidence.hwnd.toLowerCase()
    && args.window_thread_id === evidence.window_thread_id
    && args.class_name === evidence.class_name
    && args.window_title === evidence.window_title
    && args.source_version === evidence.source_version
    && args.target_version === evidence.target_version
    && args.body_sha256 === evidence.body_sha256
    && args.button_count === 2
    && typeof args.continue_button_hwnd === "string" && args.continue_button_hwnd.toLowerCase() === evidence.buttons.continue_hwnd.toLowerCase()
    && typeof args.quit_button_hwnd === "string" && args.quit_button_hwnd.toLowerCase() === evidence.buttons.quit_hwnd.toLowerCase();
}

function projectDowngradeContinueResult(input: {
  pid: number;
  hwnd?: string;
  evidence?: UnityProjectDowngradeEvidence;
  status: UnityProjectDowngradeContinueResult["status"];
  interactionAttempted?: boolean | "unknown";
  deliveryState?: UnityProjectDowngradeContinueResult["delivery_state"];
  buttonText?: "Continue";
  error?: string;
}): UnityProjectDowngradeContinueResult {
  const submitted = input.status === "message_submitted";
  const attempted = input.interactionAttempted ?? submitted;
  return {
    schema_version: 1,
    status: input.status,
    success: false,
    interaction_attempted: attempted,
    message_submitted: submitted,
    delivery_state: input.deliveryState ?? (submitted ? "message_submitted" : "not_attempted"),
    popup_closed_verified: false,
    fresh_project_state_verified: false,
    target_pid: input.pid > 0 ? input.pid : null,
    target_hwnd: input.hwnd ?? null,
    ...(input.evidence ? { evidence: input.evidence } : {}),
    ...(input.buttonText ? { button_text: input.buttonText } : {}),
    ...(input.error ? { error: input.error } : {}),
    next_valid_actions: submitted || attempted !== false
      ? [
          "do_not_repeat_project_downgrade_continue",
          "call_diagnostics.unity_modal.status_for_fresh_popup_scan",
          "call_unity.process.list_for_project_process_rediscovery",
          "call_sgg_unity_projects_for_fresh_heartbeat",
          "call_package.info_and_package.operation.status_for_resolution_after_fresh_heartbeat",
          "call_compile.status_after_fresh_heartbeat",
          "do_not_claim_popup_closed_or_downgrade_complete_from_message_submission",
        ]
      : ["call_diagnostics.unity_modal.status", "obtain_fresh_project_downgrade_evidence", "ask_operator_before_any_new_continue_attempt"],
  };
}

function scopedRegistryImportCloseResult(input: {
  dryRun: boolean;
  pid: number;
  processStartUtc: string;
  status: UnityScopedRegistryImportCloseResult["status"];
  evidence?: UnityScopedRegistryImportCloseEvidence;
  interaction?: UnityScopedRegistryImportInteraction;
  nativeDispatchCount?: 0 | 1;
  popupState?: UnityScopedRegistryImportCloseResult["popup_state"];
  error?: string;
}): UnityScopedRegistryImportCloseResult {
  const interactionAttempted = input.interaction?.interaction_attempted ?? false;
  const popupState = input.popupState ?? "not_checked";
  return {
    schema_version: 1,
    action: "close",
    dry_run: input.dryRun,
    status: input.status,
    interaction_attempted: interactionAttempted,
    native_dispatch_count: input.nativeDispatchCount ?? 0,
    delivery_state: input.interaction?.delivery_state ?? "not_attempted",
    popup_state: popupState,
    popup_disappearance_verified: popupState === "modal_absent",
    blocked_operation_replayed: false,
    target_pid: input.pid > 0 ? input.pid : null,
    process_start_utc: Number.isNaN(Date.parse(input.processStartUtc)) ? null : input.processStartUtc,
    ...(input.evidence ? {
      precondition_hash: input.evidence.precondition_hash,
      expires_at: input.evidence.expires_at,
      binding: {
        source: "user32",
        process_generation_verified: true,
        window_thread_id: input.evidence.window_thread_id,
        class_name: "#32770",
        title_sha256: input.evidence.title_sha256,
        body_sha256: input.evidence.body_sha256,
        visible_control_count: input.evidence.visible_control_count,
        button_count: 2,
        learn_more_button_count: 1,
        close_button_count: 1,
        button_identities_bound_internally: true,
      },
    } : {}),
    ...(input.error ? { error: input.error } : {}),
    next_valid_actions: input.dryRun && input.status === "ready"
      ? ["obtain_authoritative_human_approval", "retry_same_exact_close_binding_with_dry_run_false", "do_not_use_chat_text_as_approval"]
      : input.interaction?.delivery_state === "unknown_after_send_attempt" || input.interaction?.delivery_state === "unknown"
        ? ["do_not_repeat_scoped_registry_close", "call_diagnostics.unity_scoped_registry_import.status", "call_sgg_unity_projects", "require_fresh_operation_status_without_replay"]
        : input.status === "message_sent"
          ? ["call_diagnostics.unity_scoped_registry_import.status", "call_sgg_unity_projects", "require_fresh_heartbeat_catalog_and_operation_status", "do_not_repeat_blocked_operation"]
          : ["call_diagnostics.unity_scoped_registry_import.status", "create_a_new_dry_run_plan", "do_not_click_learn_more", "do_not_repeat_blocked_operation"],
  };
}

function safeModeEnterResult(input: {
  dryRun: boolean;
  pid: number;
  processStartUtc: string;
  status: UnitySafeModeEnterResult["status"];
  evidence?: UnitySafeModeEnterEvidence;
  interaction?: UnitySafeModeEnterInteraction;
  nativeDispatchCount?: 0 | 1;
  error?: string;
}): UnitySafeModeEnterResult {
  const interactionAttempted = input.interaction?.interaction_attempted ?? false;
  return {
    schema_version: 1,
    action: "enter_safe_mode",
    dry_run: input.dryRun,
    status: input.status,
    interaction_attempted: interactionAttempted,
    native_dispatch_count: input.nativeDispatchCount ?? 0,
    delivery_state: input.interaction?.delivery_state ?? "not_attempted",
    safe_mode_entered_verified: false,
    popup_closed_verified: false,
    target_pid: input.pid > 0 ? input.pid : null,
    process_start_utc: Number.isNaN(Date.parse(input.processStartUtc)) ? null : input.processStartUtc,
    ...(input.evidence ? {
      precondition_hash: input.evidence.precondition_hash,
      expires_at: input.evidence.expires_at,
      binding: {
        source: "user32",
        process_generation_verified: true,
        window_thread_id: input.evidence.window_thread_id,
        class_name: "#32770",
        window_title: "Enter Safe Mode?",
        evidence_scope: input.evidence.evidence_scope,
        body_sha256: input.evidence.body_sha256,
        control_topology_sha256: input.evidence.control_topology_sha256,
        visible_control_count: input.evidence.visible_control_count,
        button_count: 3,
        enter_safe_mode_button_count: 1,
        ignore_button_count: 1,
        quit_button_count: 1,
        exact_button_identities_bound_internally: true,
      },
    } : {}),
    ...(input.error ? { error: input.error } : {}),
    next_valid_actions: input.dryRun && input.status === "ready"
      ? ["obtain_authoritative_human_approval", "retry_same_exact_enter_safe_mode_binding_with_dry_run_false", "do_not_use_chat_text_as_approval", "do_not_choose_ignore_or_quit_from_gateway"]
      : interactionAttempted !== false
        ? ["do_not_repeat_enter_safe_mode", "call_diagnostics.unity_modal.status_for_fresh_popup_scan", "call_unity.process.list_for_project_process_rediscovery", "call_sgg_unity_projects_for_fresh_heartbeat", "do_not_claim_safe_mode_entered_from_message_submission"]
        : ["call_diagnostics.unity_modal.status", "create_a_new_dry_run_plan", "do_not_choose_ignore_or_quit_from_gateway"],
  };
}

function sceneBackupKeepResult(input: {
  dryRun: boolean;
  pid: number;
  processStartUtc: string;
  status: UnitySceneBackupKeepResult["status"];
  evidence?: UnitySceneBackupKeepEvidence;
  interaction?: UnitySceneBackupKeepInteraction;
  nativeDispatchCount?: 0 | 1;
  error?: string;
}): UnitySceneBackupKeepResult {
  return {
    schema_version: 1,
    action: "keep_scene_backups",
    dry_run: input.dryRun,
    status: input.status,
    interaction_attempted: input.interaction?.interaction_attempted ?? false,
    native_dispatch_count: input.nativeDispatchCount ?? 0,
    message_submitted: input.interaction?.status === "message_submitted",
    delivery_state: input.interaction?.delivery_state ?? "not_attempted",
    popup_closed_verified: false,
    backups_kept_verified: false,
    target_pid: input.pid > 0 ? input.pid : null,
    process_start_utc: Number.isNaN(Date.parse(input.processStartUtc)) ? null : input.processStartUtc,
    ...(input.evidence ? {
      precondition_hash: input.evidence.precondition_hash,
      expires_at: input.evidence.expires_at,
      binding: {
        source: "user32" as const,
        process_generation_verified: true as const,
        window_thread_id: input.evidence.window_thread_id,
        class_name: "#32770" as const,
        window_title: SCENE_BACKUP_DETECTED_TITLE,
        evidence_scope: "complete_body" as const,
        body_sha256: input.evidence.body_sha256,
        control_topology_sha256: input.evidence.control_topology_sha256,
        visible_control_count: input.evidence.visible_control_count,
        button_count: 2 as const,
        keep_backups_button_count: 1 as const,
        delete_button_count: 1 as const,
        exact_button_identities_bound_internally: true as const,
      },
    } : {}),
    ...(input.error ? { error: input.error } : {}),
    next_valid_actions: input.dryRun && input.status === "ready"
      ? ["submit_same_exact_keep_backups_binding_after_confirmation_policy", "do_not_choose_delete_or_legacy_yes_no_from_gateway"]
      : input.interaction?.interaction_attempted !== undefined && input.interaction.interaction_attempted !== false
        ? ["do_not_repeat_keep_backups", "call_diagnostics.unity_modal.status", "call_sgg_unity_projects_for_fresh_heartbeat", "do_not_claim_backups_kept_from_message_submission"]
        : ["call_diagnostics.unity_modal.status", "create_a_new_dry_run_plan", "do_not_choose_delete_or_legacy_yes_no_from_gateway"],
  };
}

function sameProjectDowngradeIdentity(left: UnityProjectDowngradeEvidence, right: UnityProjectDowngradeEvidence): boolean {
  return left.project_identity_sha256 === right.project_identity_sha256 && sameProjectDowngradePopupIdentity(left, right);
}

function sameProjectDowngradePopupIdentity(left: UnityProjectDowngradeEvidence, right: UnityProjectDowngradeEvidence): boolean {
  return left.pid === right.pid
    && left.process_name === right.process_name
    && left.process_start_utc === right.process_start_utc
    && left.hwnd.toLowerCase() === right.hwnd.toLowerCase()
    && left.window_thread_id === right.window_thread_id
    && left.class_name === right.class_name
    && left.window_title === right.window_title
    && left.source_version === right.source_version
    && left.target_version === right.target_version
    && left.body_sha256 === right.body_sha256
    && left.buttons.count === right.buttons.count
    && left.buttons.continue_hwnd.toLowerCase() === right.buttons.continue_hwnd.toLowerCase()
    && left.buttons.quit_hwnd.toLowerCase() === right.buttons.quit_hwnd.toLowerCase();
}

function externalSceneReloadRespondResult(input: {
  action?: UnityExternalSceneReloadAction;
  pid: number;
  hwnd?: string;
  evidence?: UnityExternalSceneReloadEvidence;
  status: UnityExternalSceneReloadRespondResult["status"];
  interactionAttempted?: boolean | "unknown";
  deliveryState?: UnityExternalSceneReloadRespondResult["delivery_state"];
  buttonText?: "Reload" | "Ignore";
  reloadCount?: number;
  ignoreCount?: number;
  error?: string;
}): UnityExternalSceneReloadRespondResult {
  const messageSent = input.status === "message_sent";
  const interactionAttempted = input.interactionAttempted ?? messageSent;
  const deliveryUncertain = !messageSent && interactionAttempted !== false;
  return {
    schema_version: 1,
    action: input.action ?? null,
    status: input.status,
    interaction_attempted: interactionAttempted,
    message_sent: messageSent,
    delivery_state: input.deliveryState ?? (messageSent ? "message_sent" : "not_attempted"),
    popup_closed_verified: false,
    compile_state_verified: false,
    target_pid: input.pid > 0 ? input.pid : null,
    target_hwnd: input.hwnd ?? null,
    ...(input.evidence ? { evidence: input.evidence } : {}),
    ...(input.buttonText ? { button_text: input.buttonText } : {}),
    ...(input.reloadCount !== undefined ? { matched_reload_button_count: input.reloadCount } : {}),
    ...(input.ignoreCount !== undefined ? { matched_ignore_button_count: input.ignoreCount } : {}),
    ...(input.error ? { error: input.error } : {}),
    next_valid_actions: messageSent
      ? ["call_sgg_unity_projects", "call_compile.status_after_fresh_heartbeat", "do_not_claim_popup_closed_or_compile_ready_from_button_message"]
      : deliveryUncertain
        ? ["do_not_repeat_external_scene_reload_response", "call_diagnostics.unity_modal.status", "call_sgg_unity_projects", "require_fresh_popup_and_compile_evidence"]
        : ["call_diagnostics.unity_modal.status", "obtain_fresh_external_scene_reload_evidence", "ask_operator_before_any_new_response_attempt"],
  };
}

function sameExternalSceneReloadIdentity(left: UnityExternalSceneReloadEvidence, right: UnityExternalSceneReloadEvidence): boolean {
  return left.pid === right.pid
    && left.hwnd.toLowerCase() === right.hwnd.toLowerCase()
    && left.process_name === right.process_name
    && left.process_start_utc === right.process_start_utc
    && left.window_thread_id === right.window_thread_id
    && left.class_name === right.class_name
    && left.window_title === right.window_title
    && left.evidence_scope === right.evidence_scope
    && left.scene_paths_count === right.scene_paths_count
    && JSON.stringify(left.scene_paths) === JSON.stringify(right.scene_paths)
    && left.buttons.reload_count === right.buttons.reload_count
    && left.buttons.ignore_count === right.buttons.ignore_count
    && left.buttons.reload_hwnd.toLowerCase() === right.buttons.reload_hwnd.toLowerCase()
    && left.buttons.ignore_hwnd.toLowerCase() === right.buttons.ignore_hwnd.toLowerCase();
}

function safeExternalScenePaths(values: string[]): string[] {
  const result: string[] = [];
  for (const value of values) {
    const normalized = value.replaceAll("\\", "/");
    if (normalized.length < 1
      || normalized.length > MAX_EXTERNAL_SCENE_PATH_LENGTH
      || !/^Assets\/[^\r\n]*\.unity$/.test(normalized)
      || normalized.split("/").includes("..")
      || result.includes(normalized)) continue;
    result.push(normalized);
  }
  return result;
}

function tasklistArgs(): string[] {
  return ["/FI", `IMAGENAME eq ${UNITY_IMAGE_NAME}`, "/V", "/FO", "CSV", "/NH"];
}

function scanUnityWindows(options: UnityWindowDiagnosticsOptions, budget: UnityWindowScanBudget): ReturnType<typeof scanUnityWindowsFromResult> {
  return scanUnityWindowsFromResult(runWindowsTool("tasklist.exe", tasklistArgs(), options, budget));
}

function scanUnityWindowsFromResult(tasklist: WindowsToolResult): {
  supported: boolean;
  windows: UnityWindowInfo[];
  errors: string[];
  helper: UnityWindowHelperObservation;
  semantic: UnityWindowSemanticObservation;
} {
  if (tasklist.status !== "ok") {
    const errorCode = tasklist.observation.normalized_error_code ?? "tool_failed";
    return {
      supported: false,
      windows: [],
      errors: [`tasklist_failed:${UNITY_IMAGE_NAME}:${errorCode}`],
      helper: tasklist.observation,
      semantic: semanticObservation("failed", false, "unity_exe_verbose_top_level_inventory", 0, "tasklist", errorCode),
    };
  }

  const parsed = parseTasklistWindows(tasklist.output, UNITY_IMAGE_NAME);
  if (!parsed.valid) {
    return {
      supported: false,
      windows: [],
      errors: [`tasklist_invalid_output:${UNITY_IMAGE_NAME}`],
      helper: { ...tasklist.observation, authoritative: false, parsed_row_count: 0, normalized_error_code: "tasklist_invalid_output", stdout_encoding: parsed.encoding },
      semantic: semanticObservation("failed", false, "unity_exe_verbose_top_level_inventory", 0, "tasklist", "tasklist_invalid_output"),
    };
  }

  const windows = [...new Map(parsed.windows.map((window) => [window.pid, window])).values()].sort((left, right) => left.pid - right.pid);
  return {
    supported: true,
    windows,
    errors: [],
    helper: { ...tasklist.observation, authoritative: true, parsed_row_count: parsed.parsedRowCount, stdout_encoding: parsed.encoding },
    semantic: semanticObservation("authoritative", true, "unity_exe_verbose_top_level_inventory", windows.length, "tasklist"),
  };
}

function scanNativeUnityModals(options: UnityWindowDiagnosticsOptions, budget: UnityWindowScanBudget): ReturnType<typeof scanNativeUnityModalsFromResult> {
  return scanNativeUnityModalsFromResult(runWindowsTool("powershell.exe", nativeUnityModalPowerShellArgs(), options, budget));
}

function scanNativeUnityModalsFromResult(powershell: WindowsToolResult): {
  supported: boolean;
  modals: UnityNativeModalInfo[];
  progressWindows: UnityNativeProgressWindowInfo[];
  errors: string[];
  helper: UnityWindowHelperObservation;
  user32Semantic: UnityWindowSemanticObservation;
  uiaSemantic: UnityWindowSemanticObservation;
} {
  if (powershell.status !== "ok") {
    const errorCode = powershell.observation.normalized_error_code ?? "tool_failed";
    return {
      supported: false,
      modals: [],
      progressWindows: [],
      errors: [`native_modal_scan_failed:${errorCode}`],
      helper: powershell.observation,
      user32Semantic: semanticObservation("failed", false, "user32_top_level_and_child_windows", 0, "native_powershell", errorCode),
      uiaSemantic: semanticObservation("failed", false, "scoped_unity_dialog_fallback", 0, "native_powershell", errorCode),
    };
  }

  const parsed = parseNativeUnityWindowRows(powershell.output);
  const helper: UnityWindowHelperObservation = {
    ...powershell.observation,
    authoritative: parsed.valid && (parsed.user32Authoritative || parsed.uiaAuthoritative === true),
    parsed_row_count: parsed.rowCount,
    normalized_error_code: parsed.valid ? null : "native_modal_scan_invalid_output",
    last_stage: parsed.lastStage ?? powershell.observation.last_stage,
    stdout_encoding: "utf8",
  };
  if (!parsed.valid) {
    return {
      supported: false,
      modals: [],
      progressWindows: [],
      errors: ["native_modal_scan_invalid_output"],
      helper,
      user32Semantic: semanticObservation("failed", false, "user32_top_level_and_child_windows", 0, "native_powershell", "native_modal_scan_invalid_output"),
      uiaSemantic: semanticObservation("failed", false, "scoped_unity_dialog_fallback", 0, "native_powershell", "native_modal_scan_invalid_output"),
    };
  }
  const user32Semantic = parsed.user32Authoritative
    ? semanticObservation("authoritative", true, "user32_top_level_and_child_windows", parsed.rowCount, "native_powershell")
    : semanticObservation(parsed.uiaAuthoritative || parsed.user32ScanSucceeded ? "partial" : "failed", false, "user32_top_level_and_child_windows", parsed.rowCount, "native_powershell", parsed.user32PartialReason ?? "user32_scan_failed");
  const uiaSemantic = parsed.uiaStatus === "legacy"
    ? semanticObservation("unsupported", false, "scoped_unity_dialog_fallback", 0, "native_powershell", "legacy_fixture_without_uia_channel")
    : parsed.uiaStatus === "not_required"
      ? semanticObservation("unsupported", false, "scoped_unity_dialog_fallback", 0, "native_powershell", parsed.user32Authoritative ? "uia_not_required_user32_authoritative" : "uia_not_run_after_user32_partial_scan")
      : parsed.uiaAuthoritative
      ? semanticObservation("authoritative", true, "scoped_unity_dialog_fallback", parsed.rowCount, "native_powershell")
      : semanticObservation("failed", false, "scoped_unity_dialog_fallback", 0, "native_powershell", "uia_scan_failed");
  return {
    supported: parsed.user32Authoritative,
    modals: parsed.modals,
    progressWindows: parsed.progressWindows,
    errors: parsed.user32Authoritative ? [] : [`native_modal_scan_partial:${parsed.user32PartialReason === "user32_scan_failed" && parsed.uiaAuthoritative ? "user32_failed_uia_fallback" : parsed.user32PartialReason ?? "user32_failed_uia_fallback"}`],
    helper,
    user32Semantic,
    uiaSemantic,
  };
}

function semanticObservation(
  status: UnityWindowSemanticObservation["status"],
  authoritative: boolean,
  coverage: string,
  parsedCount: number,
  sourceHelper: UnityWindowSemanticObservation["source_helper"],
  partialReason?: string,
): UnityWindowSemanticObservation {
  return { status, authoritative, coverage, parsed_count: parsedCount, ...(partialReason ? { partial_reason: partialReason } : {}), source_helper: sourceHelper };
}

function parseTasklistWindows(output: Buffer, expectedImageName: string): { valid: boolean; windows: UnityWindowInfo[]; parsedRowCount: number; encoding: UnityWindowHelperObservation["stdout_encoding"] } {
  const decoded = decodeTasklistOutput(output);
  if (!decoded) return { valid: false, windows: [], parsedRowCount: 0, encoding: "unknown" };
  const trimmed = decoded.text.trim();
  if (/^INFO:\s+No tasks are running which match the specified criteria\.?$/i.test(trimmed)
    || trimmed === "Информация: Задачи, отвечающие заданным критериям, отсутствуют.") {
    return { valid: true, windows: [], parsedRowCount: 0, encoding: decoded.encoding };
  }
  if (trimmed.length === 0) return { valid: false, windows: [], parsedRowCount: 0, encoding: decoded.encoding };
  const rows = parseCsvRows(decoded.text);
  if (!rows || rows.length === 0 || rows.some((row) => row.length !== 9
    || (row[0] ?? "").toLowerCase() !== expectedImageName.toLowerCase()
    || !Number.isInteger(Number(row[1] ?? ""))
    || Number(row[1] ?? "") <= 0)) {
    return { valid: false, windows: [], parsedRowCount: 0, encoding: decoded.encoding };
  }

  return { valid: true, parsedRowCount: rows.length, encoding: decoded.encoding, windows: rows
    .map((row): UnityWindowInfo | undefined => {
      const imageName = row[0] ?? "";
      const pid = Number(row[1] ?? "");
      const responding = tasklistResponding(row[5]);
      const windowTitle = row[8] ?? "";
      if (!UNITY_IMAGE_NAMES.some((name) => imageName.toLowerCase() === name.toLowerCase()) || !Number.isInteger(pid) || pid <= 0) {
        return undefined;
      }

      if (!windowTitle || windowTitle === "N/A") {
        return undefined;
      }

      const matchedKeywords = modalKeywords(windowTitle);
      const likelyRecoveryPrompt = matchedKeywords.some((keyword) => keyword === "recovery" || keyword === "crash" || keyword === "restore") && !matchedKeywords.includes("safe_mode");
      return {
        pid,
        image_name: imageName,
        window_title: windowTitle,
        source: "tasklist",
        ...(responding !== undefined ? { responding } : {}),
        likely_modal: matchedKeywords.length > 0,
        likely_recovery_prompt: likelyRecoveryPrompt,
        matched_keywords: matchedKeywords,
      };
    })
    .filter((window): window is UnityWindowInfo => window !== undefined) };
}

function decodeTasklistOutput(output: Buffer): { text: string; encoding: "utf8" | "ibm866" } | undefined {
  const sha256 = createHash("sha256").update(output).digest("hex");
  if (sha256 === TASKLIST_RU_OEM866_NO_MATCH_SHA256) {
    return { text: new TextDecoder("ibm866").decode(output), encoding: "ibm866" };
  }
  try {
    return { text: new TextDecoder("utf-8", { fatal: true }).decode(output), encoding: "utf8" };
  } catch {
    try {
      return { text: new TextDecoder("ibm866", { fatal: true }).decode(output), encoding: "ibm866" };
    } catch {
      return undefined;
    }
  }
}

function extractNativeScanOutput(output: Buffer): { payload: string; lastStage: string | null } {
  const lines = output.toString("utf8").split(/\r?\n/);
  let lastStage: string | null = null;
  const payload: string[] = [];
  for (const line of lines) {
    const marker = /^__SGG_SCAN_STAGE__:(add_type|user32_top_level|user32_process|user32_title|user32_children|user32_child_text|uia_enumeration|serialization|trace_limit):(entered|completed|skipped)$/.exec(line.trim());
    if (marker) {
      lastStage = `${marker[1]}:${marker[2]}`;
    } else if (line.length > 0) {
      payload.push(line);
    }
  }
  return { payload: payload.join("\n"), lastStage };
}

function lastNativeScanStage(output: Buffer): string | null {
  return extractNativeScanOutput(output).lastStage;
}

function parseNativeUnityWindowRows(output: Buffer): { valid: boolean; user32ScanSucceeded: boolean; user32Authoritative: boolean; user32PartialReason?: string; uiaAuthoritative: boolean | undefined; uiaStatus: "succeeded" | "failed" | "not_required" | "legacy"; rowCount: number; lastStage: string | null; modals: UnityNativeModalInfo[]; progressWindows: UnityNativeProgressWindowInfo[] } {
  const extracted = extractNativeScanOutput(output);
  if (extracted.payload.trim().length === 0) {
    return { valid: false, user32ScanSucceeded: false, user32Authoritative: false, uiaAuthoritative: false, uiaStatus: "failed", rowCount: 0, lastStage: extracted.lastStage, modals: [], progressWindows: [] };
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(extracted.payload.trim());
  } catch {
    return { valid: false, user32ScanSucceeded: false, user32Authoritative: false, uiaAuthoritative: false, uiaStatus: "failed", rowCount: 0, lastStage: extracted.lastStage, modals: [], progressWindows: [] };
  }

  const envelope = typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed as Record<string, unknown> : undefined;
  if (!Array.isArray(parsed) && (!Array.isArray(envelope?.rows)
    || typeof envelope.user32_scan_succeeded !== "boolean"
    || typeof envelope.uia_scan_succeeded !== "boolean"
    || (envelope.user32_scan_complete !== undefined && typeof envelope.user32_scan_complete !== "boolean")
    || (envelope.user32_candidate_count !== undefined && (!Number.isInteger(envelope.user32_candidate_count) || (envelope.user32_candidate_count as number) < 0))
    || (envelope.uia_scan_status !== undefined && envelope.uia_scan_status !== "succeeded" && envelope.uia_scan_status !== "failed" && envelope.uia_scan_status !== "not_required"))) {
    return { valid: false, user32ScanSucceeded: false, user32Authoritative: false, uiaAuthoritative: false, uiaStatus: "failed", rowCount: 0, lastStage: extracted.lastStage, modals: [], progressWindows: [] };
  }
  const rows = Array.isArray(parsed) ? parsed : envelope!.rows as unknown[];
  const user32ScanSucceeded = Array.isArray(parsed) || envelope!.user32_scan_succeeded === true;
  const uiaAuthoritative = Array.isArray(parsed) ? undefined : envelope!.uia_scan_succeeded === true;
  const uiaStatus = Array.isArray(parsed)
    ? "legacy" as const
    : envelope!.uia_scan_status === "succeeded" || envelope!.uia_scan_status === "failed" || envelope!.uia_scan_status === "not_required"
      ? envelope!.uia_scan_status
      : uiaAuthoritative ? "succeeded" as const : "failed" as const;
  if ((uiaStatus === "succeeded") !== (uiaAuthoritative === true) || (uiaStatus === "not_required" && !user32ScanSucceeded)) {
    return { valid: false, user32ScanSucceeded: false, user32Authoritative: false, uiaAuthoritative: false, uiaStatus: "failed", rowCount: 0, lastStage: extracted.lastStage, modals: [], progressWindows: [] };
  }
  if (rows.some((row) => !isRawNativeWindowRow(row))) {
    return { valid: false, user32ScanSucceeded: false, user32Authoritative: false, uiaAuthoritative, uiaStatus, rowCount: 0, lastStage: extracted.lastStage, modals: [], progressWindows: [] };
  }
  const modals: UnityNativeModalInfo[] = [];
  const progressWindows: UnityNativeProgressWindowInfo[] = [];
  for (const row of rows) {
    if (typeof row !== "object" || row === null) {
      continue;
    }

    const raw = row as RawNativeModalResult;
    const hwnd = typeof raw.hwnd === "string" ? raw.hwnd : "";
    const pid = numberOrZero(raw.pid);
    const processName = typeof raw.process_name === "string" ? raw.process_name : "";
    const processStartUtc = typeof raw.process_start_utc === "string" && !Number.isNaN(Date.parse(raw.process_start_utc)) ? raw.process_start_utc : undefined;
    const windowThreadId = numberOrZero(raw.window_thread_id);
    const windowVisible = typeof raw.window_visible === "boolean" ? raw.window_visible : undefined;
    const windowEnabled = typeof raw.window_enabled === "boolean" ? raw.window_enabled : undefined;
    const className = typeof raw.class_name === "string" ? raw.class_name : "";
    const windowTitle = typeof raw.window_title === "string" ? raw.window_title : "";
    const childTexts = Array.isArray(raw.child_texts) ? raw.child_texts.filter((value): value is string => typeof value === "string") : [];
    const supportedProcessOwner = processName === "Unity" || processName === "Unity.exe" || processName === "Unity.Licensing.Client" || processName === "Unity.Licensing.Client.exe";
    const modalLikeReason = raw.modal_like_reason === "dialog_class" || raw.modal_like_reason === "owned_unity_window" || raw.modal_like_reason === "dialog_modal_frame"
      ? raw.modal_like_reason
      : (processName === "Unity" || processName === "Unity.exe") && className === "#32770" ? "dialog_class" as const : undefined;
    const inspectionComplete = raw.inspection_complete !== false;
    const childControls = Array.isArray(raw.child_controls) ? raw.child_controls.flatMap((value) => {
      if (typeof value !== "object" || value === null) return [];
      const control = value as RawNativeChildControl;
      const controlHwnd = typeof control.hwnd === "string" && /^0x[0-9A-Fa-f]+$/.test(control.hwnd) ? control.hwnd : undefined;
      const parentHwnd = typeof control.parent_hwnd === "string" && /^0x[0-9A-Fa-f]+$/.test(control.parent_hwnd) ? control.parent_hwnd : undefined;
      const controlPid = numberOrZero(control.pid);
      const controlThreadId = numberOrZero(control.window_thread_id);
      const visibleObserved = typeof control.visible === "boolean";
      const visible = visibleObserved ? control.visible as boolean : true;
      const enabledObserved = typeof control.enabled === "boolean";
      const enabled = enabledObserved ? control.enabled as boolean : true;
      const controlId = typeof control.control_id === "number" && Number.isInteger(control.control_id) && control.control_id >= 0 ? control.control_id : undefined;
      const controlClass = typeof control.class_name === "string" ? control.class_name : "";
      const text = typeof control.text === "string" ? control.text : "";
      const readOnly = typeof control.read_only === "boolean" ? control.read_only : undefined;
      const multiline = typeof control.multiline === "boolean" ? control.multiline : undefined;
      return controlHwnd ? [{ hwnd: controlHwnd, ...(parentHwnd ? { parentHwnd } : {}), pid: controlPid, windowThreadId: controlThreadId, visible, visibleObserved, enabled, enabledObserved, ...(controlId !== undefined ? { controlId } : {}), className: controlClass, text, ...(readOnly !== undefined ? { readOnly } : {}), ...(multiline !== undefined ? { multiline } : {}) }] : [];
    }) : [];
    if (!hwnd || pid <= 0 || (!windowTitle && childTexts.length === 0 && !modalLikeReason)) {
      continue;
    }

    let candidateChildTexts = childTexts;
    let candidateChildControls = childControls.filter((control) => control.visible);
    let candidateHwnd = hwnd;
    let candidateWindowThreadId = windowThreadId;
    let candidateWindowVisible = windowVisible;
    let candidateWindowEnabled = windowEnabled;
    let candidateClassName = className;
    let candidateWindowTitle = windowTitle;
    const processIdentity: NativeModalProcessIdentity = {
      pid,
      ...(processStartUtc ? { processStartUtc } : {}),
      windowThreadId,
      ...(windowVisible !== undefined ? { windowVisible } : {}),
      ...(windowEnabled !== undefined ? { windowEnabled } : {}),
      inspectionComplete: raw.inspection_complete === true,
    };
    const sceneBackupDetected = classifySceneBackupDetectedPrompt(processName, className, windowTitle, hwnd, childTexts, childControls, processIdentity);
    let classification = classifyNativeUnityModal(processName, className, windowTitle, hwnd, candidateChildTexts, candidateChildControls, processIdentity, childControls);
    if (sceneBackupDetected) {
      classification.sceneBackupRecoveryPrompt = true;
      classification.likelyModal = true;
      classification.blockCode = SCENE_BACKUP_RECOVERY_MODAL_CODE;
      classification.matchedKeywords.push("scene_backup_detected", "keep_backups", "diagnostic_only");
      const binding = sceneBackupDetectedNativeBindings.get(sceneBackupDetected)!;
      binding.authoritative = !Array.isArray(parsed) && user32ScanSucceeded
        && envelope!.user32_scan_complete === true
        && typeof envelope!.user32_candidate_count === "number" && envelope!.user32_candidate_count === rows.length;
    }
    if (!classification.externalSceneReloadPrompt) {
      const embeddedDialogs = candidateChildControls.filter((control) => (control.pid === 0 || control.pid === pid)
        && (control.className === "#32770" || control.className === "UnityContainerWndClass")
        && control.text.trim() === "The open scene(s) have been modified externally");
      if (embeddedDialogs.length === 1) {
        const embedded = embeddedDialogs[0]!;
        const embeddedControls = descendantControls(candidateChildControls, embedded.hwnd);
        const embeddedTexts = embeddedControls.map((control) => control.text).filter((text) => text.length > 0);
        const embeddedClassification = classifyNativeUnityModal(processName, embedded.className, embedded.text, embedded.hwnd, embeddedTexts, embeddedControls, {
          pid,
          ...(processStartUtc ? { processStartUtc } : {}),
          windowThreadId: embedded.windowThreadId > 0 ? embedded.windowThreadId : windowThreadId,
          ...(embedded.visibleObserved ? { windowVisible: embedded.visible } : {}),
          ...(embedded.enabledObserved ? { windowEnabled: embedded.enabled } : {}),
        });
        if (embeddedClassification.externalSceneReloadPrompt) {
          candidateChildTexts = embeddedTexts;
          candidateChildControls = embeddedControls;
          candidateHwnd = embedded.hwnd;
          candidateWindowThreadId = embedded.windowThreadId > 0 ? embedded.windowThreadId : windowThreadId;
          candidateWindowVisible = embedded.visibleObserved ? embedded.visible : undefined;
          candidateWindowEnabled = embedded.enabledObserved ? embedded.enabled : undefined;
          candidateClassName = embedded.className;
          candidateWindowTitle = embedded.text;
          classification = embeddedClassification;
        }
      }
    }
    const childTextPreview = candidateChildTexts.slice(0, 20);
    const reloadButtons = candidateChildControls.filter((control) => control.enabled && control.className === "Button" && control.text.trim() === "Reload");
    const ignoreButtons = candidateChildControls.filter((control) => control.enabled && control.className === "Button" && control.text.trim() === "Ignore");
    const progress = isUnityProgressWindowOwner(processName, className)
      ? classifyNativeUnityProgressWindow(windowTitle, childTexts)
      : undefined;
    const candidateProcessSupported = supportedProcessOwner || classification.licensingClientCrash;
    const unclassifiedModal = !classification.blockCode
      && (classification.likelyModal || (modalLikeReason !== undefined && progress?.likelyProgressWindow !== true));
    const redactUnclassifiedText = candidateProcessSupported && unclassifiedModal;
    if (candidateProcessSupported && (classification.likelyModal || (modalLikeReason !== undefined && progress?.likelyProgressWindow !== true))) {
      modals.push({
        hwnd: candidateHwnd,
        pid,
        process_name: processName,
        ...(processStartUtc ? { process_start_utc: processStartUtc } : {}),
        ...(candidateWindowThreadId > 0 ? { window_thread_id: candidateWindowThreadId } : {}),
        ...(candidateWindowVisible !== undefined ? { window_visible: candidateWindowVisible } : {}),
        ...(candidateWindowEnabled !== undefined ? { window_enabled: candidateWindowEnabled } : {}),
        class_name: candidateClassName,
        window_title: redactUnclassifiedText ? "<redacted>" : candidateWindowTitle,
        child_texts: redactUnclassifiedText
          ? []
          : classification.projectDowngradePrompt
          ? ["Continue", "Quit"]
          : classification.duplicateProjectInstancePrompt
            ? ["OK"]
            : classification.scopedRegistryImportPrompt
              ? ["Learn More", "Close"]
              : classification.sceneBackupRecoveryPrompt
                ? (sceneBackupDetected ? ["Keep Backups", "Delete"] : ["Yes", "No"])
                : classification.lowDiskSpacePrompt ? ["OK"] : childTextPreview,
        source: "user32",
        likely_modal: true,
        likely_unity_licensing_client_crash: classification.licensingClientCrash,
        likely_corrupted_library: classification.corruptedLibrary,
        likely_safe_mode_prompt: classification.safeModePrompt,
        likely_missing_signature_prompt: classification.missingSignaturePrompt,
        likely_external_scene_reload_prompt: classification.externalSceneReloadPrompt,
        likely_project_downgrade_prompt: classification.projectDowngradePrompt,
        likely_duplicate_project_instance: classification.duplicateProjectInstancePrompt,
        likely_scoped_registry_import_prompt: classification.scopedRegistryImportPrompt,
        likely_scene_backup_recovery_prompt: classification.sceneBackupRecoveryPrompt,
        ...(classification.sceneBackupRecoveryPrompt ? { scene_backup_recovery_variant: sceneBackupDetected ? "scene_backup_detected" as const : "recovering_scene_backups" as const } : {}),
        likely_low_disk_space_prompt: classification.lowDiskSpacePrompt,
        ...(classification.scenePaths.length > 0 ? {
          scene_paths: classification.scenePaths.slice(0, MAX_EXTERNAL_SCENE_PATHS),
          scene_paths_count: classification.scenePaths.length,
          scene_paths_truncated: classification.scenePaths.length > MAX_EXTERNAL_SCENE_PATHS,
        } : {}),
        ...(classification.externalSceneReloadPrompt ? {
          external_scene_reload_evidence_scope: classification.externalSceneReloadEvidenceScope,
          reload_button_count: reloadButtons.length,
          ignore_button_count: ignoreButtons.length,
        } : {}),
        ...(reloadButtons.length === 1 ? { reload_button_hwnd: reloadButtons[0]!.hwnd } : {}),
        ...(ignoreButtons.length === 1 ? { ignore_button_hwnd: ignoreButtons[0]!.hwnd } : {}),
        ...(classification.safeMode ? { safe_mode: classification.safeMode } : {}),
        ...(classification.projectDowngrade ? { project_downgrade: classification.projectDowngrade } : {}),
        ...(classification.duplicateProjectInstance ? { duplicate_project_instance: classification.duplicateProjectInstance } : {}),
        ...(classification.scopedRegistryImport ? { scoped_registry_import: classification.scopedRegistryImport } : {}),
        ...(classification.sceneBackupRecovery ? { scene_backup_recovery: classification.sceneBackupRecovery } : {}),
        ...(sceneBackupDetected ? { scene_backup_detected: sceneBackupDetected } : {}),
        ...(classification.lowDiskSpace ? { low_disk_space: classification.lowDiskSpace } : {}),
        ...(unclassifiedModal && modalLikeReason ? {
          unclassified_modal: {
            evidence_redacted: true as const,
            modal_like_reason: modalLikeReason,
            window_title_sha256: `sha256:${createHash("sha256").update(candidateWindowTitle).digest("hex")}`,
            visible_child_text_sha256: `sha256:${createHash("sha256").update(candidateChildTexts.join("\n")).digest("hex")}`,
            visible_child_control_count: candidateChildControls.length,
            inspection_complete: inspectionComplete,
          },
        } : {}),
        ...(classification.exceptionCode ? { exception_code: classification.exceptionCode } : {}),
        ...(classification.blockCode ? { block_code: classification.blockCode } : {}),
        matched_keywords: classification.matchedKeywords,
      });
    }

    if (progress?.likelyProgressWindow) {
        progressWindows.push({
          hwnd,
          pid,
          process_name: processName,
          class_name: className,
          window_title: windowTitle,
          child_texts: childTextPreview,
          source: "user32",
          likely_progress_window: true,
          progress_kind: progress.progressKind,
          ...(progress.busyText ? { busy_text: progress.busyText } : {}),
          ...(progress.busySeconds !== undefined ? { busy_seconds: progress.busySeconds } : {}),
          stall_threshold_seconds: DOMAIN_RELOAD_PROGRESS_STALL_SECONDS,
          is_stalled: progress.isStalled,
          diagnostic_only: true,
          automation_attempted: false,
          ...(progress.blockCode ? { block_code: progress.blockCode } : {}),
          matched_keywords: progress.matchedKeywords,
        });
    }
  }

  const coverageMetadataPresent = !Array.isArray(parsed)
    && typeof envelope!.user32_scan_complete === "boolean"
    && typeof envelope!.user32_candidate_count === "number";
  const reportedCandidateCount = coverageMetadataPresent ? envelope!.user32_candidate_count as number : rows.length;
  const projectionComplete = !user32ScanSucceeded || reportedCandidateCount === rows.length;
  const inspectionComplete = rows.every((row) => (row as RawNativeModalResult).inspection_complete !== false)
    && (!coverageMetadataPresent || envelope!.user32_scan_complete === true);
  const hasUnclassifiedModal = modals.some((modal) => modal.block_code === undefined);
  const user32PartialReason = !user32ScanSucceeded
    ? "user32_scan_failed"
    : !coverageMetadataPresent
      ? "coverage_metadata_missing"
      : !projectionComplete
      ? "candidate_projection_incomplete"
      : !inspectionComplete
        ? "candidate_inspection_incomplete"
        : hasUnclassifiedModal ? "unclassified_unity_modal_candidate" : undefined;
  const user32Authoritative = user32ScanSucceeded && user32PartialReason === undefined;

  return {
    valid: true,
    user32ScanSucceeded,
    user32Authoritative,
    ...(user32PartialReason ? { user32PartialReason } : {}),
    uiaAuthoritative,
    uiaStatus,
    rowCount: rows.length,
    lastStage: extracted.lastStage,
    modals: modals.sort((left, right) => left.pid - right.pid || left.hwnd.localeCompare(right.hwnd)),
    progressWindows: progressWindows.sort((left, right) => left.pid - right.pid || left.hwnd.localeCompare(right.hwnd)),
  };
}

function isRawNativeWindowRow(value: unknown): value is RawNativeModalResult {
  if (typeof value !== "object" || value === null) return false;
  const row = value as RawNativeModalResult;
  return typeof row.hwnd === "string" && /^0x[0-9A-Fa-f]+$/.test(row.hwnd)
    && typeof row.pid === "number" && Number.isInteger(row.pid) && row.pid > 0
    && typeof row.process_name === "string"
    && (row.process_start_utc === undefined || (typeof row.process_start_utc === "string"
      && (row.process_start_utc.length === 0 || !Number.isNaN(Date.parse(row.process_start_utc)))))
    && (row.window_thread_id === undefined || (typeof row.window_thread_id === "number"
      && Number.isInteger(row.window_thread_id) && row.window_thread_id >= 0))
    && (row.window_visible === undefined || typeof row.window_visible === "boolean")
    && (row.window_enabled === undefined || typeof row.window_enabled === "boolean")
    && typeof row.class_name === "string"
    && typeof row.window_title === "string"
    && Array.isArray(row.child_texts) && row.child_texts.every((text) => typeof text === "string")
    && (row.window_title.length > 0 || row.child_texts.some((text) => text.length > 0)
      || row.modal_like_reason === "dialog_class" || row.modal_like_reason === "owned_unity_window" || row.modal_like_reason === "dialog_modal_frame")
    && (row.modal_like_reason === undefined || row.modal_like_reason === "" || row.modal_like_reason === "dialog_class" || row.modal_like_reason === "owned_unity_window" || row.modal_like_reason === "dialog_modal_frame")
    && (row.inspection_complete === undefined || typeof row.inspection_complete === "boolean")
    && (row.child_controls === undefined || (Array.isArray(row.child_controls)
      && row.child_controls.every(isRawNativeChildControl)));
}

function isRawNativeChildControl(value: unknown): value is RawNativeChildControl {
  if (typeof value !== "object" || value === null) return false;
  const control = value as RawNativeChildControl;
  return typeof control.hwnd === "string" && /^0x[0-9A-Fa-f]+$/.test(control.hwnd)
    && (control.parent_hwnd === undefined || (typeof control.parent_hwnd === "string" && /^0x[0-9A-Fa-f]+$/.test(control.parent_hwnd)))
    && (control.pid === undefined || (typeof control.pid === "number" && Number.isInteger(control.pid) && control.pid >= 0))
    && (control.window_thread_id === undefined || (typeof control.window_thread_id === "number" && Number.isInteger(control.window_thread_id) && control.window_thread_id >= 0))
    && (control.visible === undefined || typeof control.visible === "boolean")
    && (control.enabled === undefined || typeof control.enabled === "boolean")
    && (control.control_id === undefined || (typeof control.control_id === "number" && Number.isInteger(control.control_id) && control.control_id >= 0))
    && typeof control.class_name === "string"
    && typeof control.text === "string"
    && (control.read_only === undefined || typeof control.read_only === "boolean")
    && (control.multiline === undefined || typeof control.multiline === "boolean");
}

function descendantControls<T extends { hwnd: string; parentHwnd?: string }>(controls: T[], rootHwnd: string): T[] {
  const descendants = new Set<string>();
  let changed = true;
  while (changed) {
    changed = false;
    for (const control of controls) {
      if (!control.parentHwnd || descendants.has(control.hwnd) || (control.parentHwnd !== rootHwnd && !descendants.has(control.parentHwnd))) continue;
      descendants.add(control.hwnd);
      changed = true;
    }
  }
  return controls.filter((control) => descendants.has(control.hwnd));
}

function isUnityProgressWindowOwner(processName: string, className: string): boolean {
  const normalizedProcess = processName.toLowerCase();
  return normalizedProcess === "unity" || normalizedProcess === "unity.exe" || className === "UnityContainerWndClass";
}

function classifyNativeUnityModal(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity, unfilteredChildControls: ParsedNativeChildControl[] = childControls): { likelyModal: boolean; licensingClientCrash: boolean; corruptedLibrary: boolean; safeModePrompt: boolean; safeMode?: NonNullable<UnityNativeModalInfo["safe_mode"]>; missingSignaturePrompt: boolean; externalSceneReloadPrompt: boolean; externalSceneReloadEvidenceScope?: "complete_body_and_scene_paths" | "sparse_control_topology"; projectDowngradePrompt: boolean; duplicateProjectInstancePrompt: boolean; scopedRegistryImportPrompt: boolean; sceneBackupRecoveryPrompt: boolean; lowDiskSpacePrompt: boolean; projectDowngrade?: NonNullable<UnityNativeModalInfo["project_downgrade"]>; duplicateProjectInstance?: NonNullable<UnityNativeModalInfo["duplicate_project_instance"]>; scopedRegistryImport?: NonNullable<UnityNativeModalInfo["scoped_registry_import"]>; sceneBackupRecovery?: NonNullable<UnityNativeModalInfo["scene_backup_recovery"]>; lowDiskSpace?: NonNullable<UnityNativeModalInfo["low_disk_space"]>; scenePaths: string[]; exceptionCode?: string; blockCode?: UnityBlockingNativeModalCode; matchedKeywords: string[] } {
  const matchedKeywords = modalKeywords(windowTitle);
  const allText = [windowTitle, ...childTexts].join("\n");
  const exceptionCode = /\b0xe0434352\b/i.exec(allText)?.[0]?.toLowerCase();
  const titleMatchesLicensingCrash = /^Unity\.Licensing\.Client\.exe\s+-\s+(?:Application Error|Ошибка приложения)$/iu.test(windowTitle.trim());
  const titleMatchesCorruptedLibrary = /\bcorrupted\s+library\b|\blibrary\b.*\bcorrupt/i.test(windowTitle.trim());
  const titleMatchesSafeMode = windowTitle.trim() === "Enter Safe Mode?";
  const titleMatchesMissingSignature = /^Missing Signature$/i.test(windowTitle.trim());
  const hasDialogClass = className === "#32770";
  const hasUnityModalWindowClass = className === "UnityContainerWndClass";
  const hasUnknownSoftwareException = /unknown software exception|Исключение неизвестное программное исключение/iu.test(allText);
  const hasDistinctFaultAddress = [...allText.matchAll(/\b0x[0-9a-f]{8,16}\b/gi)]
    .some((match) => match[0].toLowerCase() !== exceptionCode);
  const hasOkButton = childTexts.some((text) => /^(?:OK|ОК)$/u.test(text.trim()));
  const hasCancelButton = childTexts.some((text) => /^(?:Cancel|Отмена)$/u.test(text.trim()));
  const licensingClientCrash = hasDialogClass
    && titleMatchesLicensingCrash
    && hasUnknownSoftwareException
    && Boolean(exceptionCode)
    && hasDistinctFaultAddress
    && hasOkButton
    && hasCancelButton;
  const corruptedLibraryText = /Corrupted files in the Library prevented Unity to load your project|rebuild the Library|Don'?t rebuild Library|library[^\n]*(corrupt|rebuild)|corrupt[^\n]*library/i.test(allText);
  const corruptedLibrary = hasDialogClass && (titleMatchesCorruptedLibrary || corruptedLibraryText) && /library/i.test(allText) && /corrupt|rebuild/i.test(allText);
  const safeModeButtons = ["Enter Safe Mode", "Ignore", "Quit"];
  const safeModeButtonEvidence = safeModeButtons.every((button) => childTexts.filter((text) => text.trim() === button).length === 1);
  const safeModeBody = childTexts.filter((text) => !safeModeButtons.includes(text.trim())).join("\n");
  const safeModeBodyEvidence = /The project you are opening contains compilation errors/i.test(safeModeBody)
    && /Entering Safe Mode/i.test(safeModeBody)
    && /If you choose Ignore/i.test(safeModeBody);
  const safeModeControls = childControls.filter((control) => control.className === "Button" && safeModeButtons.includes(control.text.trim()));
  const safeModeControlEvidence = safeModeControls.length === 3
    && safeModeControls.every((control) => control.enabled
      && control.pid === processIdentity.pid
      && control.windowThreadId === processIdentity.windowThreadId);
  const completeSafeModePrompt = (processName === "Unity" || processName === "Unity.exe")
    && Boolean(processIdentity.processStartUtc)
    && processIdentity.windowThreadId > 0
    && hasDialogClass
    && titleMatchesSafeMode
    && safeModeBodyEvidence
    && safeModeButtonEvidence
    && safeModeControlEvidence;
  const sparseSafeModePrompt = !completeSafeModePrompt
    && isSparseSafeModePrompt(processName, className, windowTitle, windowHwnd, childTexts, unfilteredChildControls, processIdentity);
  const safeModePrompt = completeSafeModePrompt || sparseSafeModePrompt;
  const safeMode = safeModePrompt
    ? classifyActionableSafeModePrompt(windowHwnd, unfilteredChildControls, processIdentity, completeSafeModePrompt ? "complete_body" : "sparse_control_topology")
    : undefined;
  const missingSignatureText = /unsigned\s+packages?|Package Manager|open\s+the\s+Package\s+Manager|\bClose\b|UnityEditor\.PackageManager\.UI\.Internal\.ModalWindowContainer/i.test(allText);
  const missingSignaturePrompt = (hasDialogClass || hasUnityModalWindowClass) && titleMatchesMissingSignature && (missingSignatureText || childTexts.length === 0);
  const scenePaths = uniqueStrings([windowTitle, ...childTexts].flatMap((text) => text.match(/Assets[\\/][^\r\n]*?\.unity\b/g) ?? []));
  const completeExternalSceneReloadPrompt = (hasDialogClass || hasUnityModalWindowClass)
    && windowTitle.trim() === "The open scene(s) have been modified externally"
    && allText.includes("The following open scene(s) have been changed on disk:")
    && allText.includes("Do you want to reload the scene(s)?")
    && scenePaths.length > 0
    && childTexts.some((text) => text.trim() === "Reload")
    && childTexts.some((text) => text.trim() === "Ignore");
  const sparseExternalSceneReloadPrompt = !completeExternalSceneReloadPrompt
    && isSparseExternalSceneReloadPrompt(processName, className, windowTitle, windowHwnd, childTexts, unfilteredChildControls, processIdentity);
  const externalSceneReloadPrompt = completeExternalSceneReloadPrompt || sparseExternalSceneReloadPrompt;
  const externalSceneReloadEvidenceScope = completeExternalSceneReloadPrompt
    ? "complete_body_and_scene_paths" as const
    : sparseExternalSceneReloadPrompt ? "sparse_control_topology" as const : undefined;
  const projectDowngrade = classifyProjectDowngradePrompt(processName, className, windowTitle, childControls);
  const projectDowngradePrompt = projectDowngrade !== undefined;
  const duplicateProjectInstance = classifyDuplicateProjectInstancePrompt(processName, className, windowTitle, childTexts, childControls, processIdentity);
  const duplicateProjectInstancePrompt = duplicateProjectInstance !== undefined;
  const scopedRegistryImport = classifyScopedRegistryImportPrompt(processName, className, windowTitle, windowHwnd, childTexts, childControls, processIdentity);
  const scopedRegistryImportPrompt = scopedRegistryImport !== undefined;
  const sceneBackupRecovery = classifySceneBackupRecoveryPrompt(processName, className, windowTitle, windowHwnd, childTexts, childControls, processIdentity);
  const sceneBackupRecoveryPrompt = sceneBackupRecovery !== undefined;
  const lowDiskSpace = classifyLowDiskSpacePrompt(processName, className, windowTitle, windowHwnd, childTexts, childControls, processIdentity);
  const lowDiskSpacePrompt = lowDiskSpace !== undefined
    || isSparseLowDiskSpacePrompt(processName, className, windowTitle, windowHwnd, childTexts, unfilteredChildControls, processIdentity);
  const blockCode = licensingClientCrash
    ? UNITY_LICENSING_CLIENT_CRASH_CODE
    : corruptedLibrary ? CORRUPTED_LIBRARY_MODAL_CODE
    : safeModePrompt ? SAFE_MODE_MODAL_CODE
    : missingSignaturePrompt ? MISSING_SIGNATURE_MODAL_CODE
    : externalSceneReloadPrompt ? EXTERNAL_SCENE_RELOAD_MODAL_CODE
    : projectDowngradePrompt ? PROJECT_DOWNGRADE_MODAL_CODE
    : duplicateProjectInstancePrompt ? DUPLICATE_PROJECT_INSTANCE_MODAL_CODE
    : scopedRegistryImportPrompt ? SCOPED_REGISTRY_IMPORT_MODAL_CODE
    : sceneBackupRecoveryPrompt ? SCENE_BACKUP_RECOVERY_MODAL_CODE
    : lowDiskSpacePrompt ? LOW_DISK_SPACE_MODAL_CODE
    : undefined;

  if (titleMatchesLicensingCrash) {
    matchedKeywords.push("license", "crash");
  }
  if (titleMatchesCorruptedLibrary) {
    matchedKeywords.push("corrupted_library", "library");
  }
  if (titleMatchesSafeMode) {
    matchedKeywords.push("safe_mode");
  }
  if (titleMatchesMissingSignature || missingSignaturePrompt) {
    matchedKeywords.push("missing_signature", "package_signature");
  }
  if (externalSceneReloadPrompt) {
    matchedKeywords.push("external_scene_reload", "scene_changed_on_disk", "reload", "ignore");
  }
  if (projectDowngradePrompt) {
    matchedKeywords.push("project_downgrade", "unsupported_downgrade", "continue", "quit");
  }
  if (duplicateProjectInstancePrompt) {
    matchedKeywords.push("duplicate_project_instance", "project_already_open", "manual_ok_only");
  }
  if (scopedRegistryImportPrompt) {
    matchedKeywords.push("scoped_registry_import", "package_manager", "diagnostic_only");
  }
  if (sceneBackupRecoveryPrompt) {
    matchedKeywords.push("scene_backup_recovery", "previous_editor_session", "diagnostic_only");
  }
  if (lowDiskSpacePrompt) {
    matchedKeywords.push("low_disk_space", "diagnostic_only", "no_gateway_cleanup");
  }
  if (exceptionCode) {
    matchedKeywords.push(exceptionCode);
  }

  return {
    likelyModal: Boolean(blockCode) || matchedKeywords.length > 0,
    licensingClientCrash,
    corruptedLibrary,
    safeModePrompt,
    ...(safeMode ? { safeMode } : {}),
    missingSignaturePrompt,
    externalSceneReloadPrompt,
    ...(externalSceneReloadEvidenceScope ? { externalSceneReloadEvidenceScope } : {}),
    projectDowngradePrompt,
    duplicateProjectInstancePrompt,
    scopedRegistryImportPrompt,
    sceneBackupRecoveryPrompt,
    lowDiskSpacePrompt,
    ...(projectDowngrade ? { projectDowngrade } : {}),
    ...(duplicateProjectInstance ? { duplicateProjectInstance } : {}),
    ...(scopedRegistryImport ? { scopedRegistryImport } : {}),
    ...(sceneBackupRecovery ? { sceneBackupRecovery } : {}),
    ...(lowDiskSpace ? { lowDiskSpace } : {}),
    scenePaths,
    ...(exceptionCode ? { exceptionCode } : {}),
    ...(blockCode ? { blockCode } : {}),
    matchedKeywords: uniqueStrings(matchedKeywords),
  };
}

function isSparseSafeModePrompt(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity): boolean {
  const safeModeButtons = ["Enter Safe Mode", "Ignore", "Quit"];
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || className !== "#32770"
    || windowTitle !== "Enter Safe Mode?"
    || !processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || processIdentity.windowVisible !== true
    || processIdentity.windowEnabled !== true
    || childTexts.length !== safeModeButtons.length
    || childTexts.some((text, index) => text !== safeModeButtons[index])
    || childControls.length < safeModeButtons.length
    || childControls.length > 8) return false;

  const textControls = childControls.filter((control) => control.text.length > 0);
  const buttons = childControls.filter((control) => control.className === "Button");
  return textControls.length === safeModeButtons.length
    && buttons.length === safeModeButtons.length
    && safeModeButtons.every((button) => buttons.filter((control) => control.text === button).length === 1)
    && childControls.every((control) => control.visibleObserved && control.visible
      && control.parentHwnd?.toLowerCase() === windowHwnd.toLowerCase()
      && control.pid === processIdentity.pid
      && control.windowThreadId === processIdentity.windowThreadId)
    && buttons.every((control) => control.enabledObserved && control.enabled);
}

function classifyActionableSafeModePrompt(windowHwnd: string, childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity, evidenceScope: "complete_body" | "sparse_control_topology"): NonNullable<UnityNativeModalInfo["safe_mode"]> | undefined {
  if (!processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || processIdentity.windowVisible !== true
    || processIdentity.windowEnabled !== true
    || childControls.length < 3
    || childControls.length > 8) return undefined;
  const directControls = childControls.filter((control) => control.parentHwnd?.toLowerCase() === windowHwnd.toLowerCase());
  if (directControls.length !== childControls.length
    || directControls.some((control) => !control.visibleObserved || !control.visible
      || control.pid !== processIdentity.pid
      || control.windowThreadId !== processIdentity.windowThreadId)) return undefined;

  const buttons = directControls.filter((control) => control.className === "Button");
  const enter = buttons.filter((control) => control.text === "Enter Safe Mode");
  const ignore = buttons.filter((control) => control.text === "Ignore");
  const quit = buttons.filter((control) => control.text === "Quit");
  if (buttons.length !== 3 || enter.length !== 1 || ignore.length !== 1 || quit.length !== 1
    || buttons.some((control) => !control.enabledObserved || !control.enabled)) return undefined;

  const body = directControls.filter((control) => control.className !== "Button" && control.text.length > 0).map((control) => control.text).join("\n");
  if (evidenceScope === "complete_body" && (!/The project you are opening contains compilation errors/i.test(body)
    || !/Entering Safe Mode/i.test(body)
    || !/If you choose Ignore/i.test(body))) return undefined;
  if (evidenceScope === "sparse_control_topology" && body.length > 0) return undefined;

  const topology = [...directControls]
    .sort((left, right) => left.hwnd < right.hwnd ? -1 : left.hwnd > right.hwnd ? 1 : 0)
    .map((control) => `${control.hwnd.toLowerCase()}|${control.parentHwnd?.toLowerCase() ?? ""}|${control.pid}|${control.windowThreadId}|${control.visible ? 1 : 0}|${control.enabled ? 1 : 0}|${control.controlId ?? -1}|${control.className}|${control.text}`)
    .join("\n");
  return {
    evidence_scope: evidenceScope,
    body_sha256: `sha256:${createHash("sha256").update(body).digest("hex")}`,
    control_topology_sha256: `sha256:${createHash("sha256").update(topology).digest("hex")}`,
    visible_control_count: directControls.length,
    button_count: 3,
    enter_safe_mode_button_count: 1,
    ignore_button_count: 1,
    quit_button_count: 1,
    enter_safe_mode_button_hwnd: enter[0]!.hwnd,
    ignore_button_hwnd: ignore[0]!.hwnd,
    quit_button_hwnd: quit[0]!.hwnd,
  };
}

function isSparseExternalSceneReloadPrompt(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity): boolean {
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || className !== "#32770"
    || windowTitle !== "The open scene(s) have been modified externally"
    || !processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || processIdentity.windowVisible !== true
    || processIdentity.windowEnabled !== true
    || childTexts.length !== 2
    || childTexts[0] !== "Reload"
    || childTexts[1] !== "Ignore"
    || childControls.length !== 2) return false;

  const [reloadButton, ignoreButton] = childControls;
  return Boolean(reloadButton && ignoreButton
    && reloadButton.className === "Button" && reloadButton.text === "Reload"
    && ignoreButton.className === "Button" && ignoreButton.text === "Ignore"
    && childControls.every((control) => control.visibleObserved && control.visible
      && control.enabledObserved && control.enabled
      && control.parentHwnd?.toLowerCase() === windowHwnd.toLowerCase()
      && control.pid === processIdentity.pid
      && control.windowThreadId === processIdentity.windowThreadId));
}

function classifyLowDiskSpacePrompt(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity): NonNullable<UnityNativeModalInfo["low_disk_space"]> | undefined {
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || className !== "#32770"
    || windowTitle !== LOW_DISK_SPACE_TITLE
    || !processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || processIdentity.windowVisible !== true
    || processIdentity.windowEnabled !== true
    || childTexts.length !== 2
    || childControls.length !== 3) return undefined;

  const normalizedTexts = childTexts.map(normalizeNativeDialogText);
  if (normalizedTexts[0] !== LOW_DISK_SPACE_BODY || normalizedTexts[1] !== "OK") return undefined;
  const [staticControl, editControl, okButton] = childControls;
  if (!staticControl || !editControl || !okButton
    || staticControl.className !== "Static" || staticControl.controlId !== 1040 || staticControl.text !== ""
    || editControl.className !== "Edit" || editControl.controlId !== 1019 || normalizeNativeDialogText(editControl.text) !== LOW_DISK_SPACE_BODY || editControl.readOnly !== true || editControl.multiline !== true
    || okButton.className !== "Button" || okButton.controlId !== 1 || okButton.text.trim() !== "OK" || !okButton.enabled
    || childControls.some((control) => !control.visible
      || control.parentHwnd?.toLowerCase() !== windowHwnd.toLowerCase()
      || control.pid !== processIdentity.pid
      || control.windowThreadId !== processIdentity.windowThreadId)) return undefined;

  return {
    title_sha256: `sha256:${createHash("sha256").update(LOW_DISK_SPACE_TITLE).digest("hex")}`,
    body_sha256: `sha256:${createHash("sha256").update(LOW_DISK_SPACE_BODY).digest("hex")}`,
    body_evidence: { disk_space_below_one_gb: true, free_space_requested: true, unity_work_impacted: true },
    visible_control_count: 3,
    static_control_count: 1,
    edit_control_count: 1,
    read_only_multiline_edit_count: 1,
    button_count: 1,
    ok_button_count: 1,
    diagnostic_only: true,
    interaction_supported: false,
    disk_cleanup_supported: false,
  };
}

function isSparseLowDiskSpacePrompt(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity): boolean {
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || className !== "#32770"
    || windowTitle !== LOW_DISK_SPACE_TITLE
    || !processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || processIdentity.windowVisible !== true
    || processIdentity.windowEnabled !== true
    || childTexts.length !== 1
    || childTexts[0] !== "OK"
    || childControls.length !== 2) return false;

  const [staticControl, okButton] = childControls;
  return Boolean(staticControl && okButton
    && staticControl.className === "Static" && staticControl.controlId === 1040 && staticControl.text === ""
    && okButton.className === "Button" && okButton.controlId === 1 && okButton.text === "OK" && okButton.enabledObserved && okButton.enabled
    && childControls.every((control) => control.visibleObserved && control.visible
      && control.parentHwnd?.toLowerCase() === windowHwnd.toLowerCase()
      && control.pid === processIdentity.pid
      && control.windowThreadId === processIdentity.windowThreadId));
}

function classifySceneBackupRecoveryPrompt(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity): NonNullable<UnityNativeModalInfo["scene_backup_recovery"]> | undefined {
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || className !== "#32770"
    || windowTitle !== SCENE_BACKUP_RECOVERY_TITLE
    || !processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || processIdentity.windowVisible !== true
    || processIdentity.windowEnabled !== true
    || childTexts.length !== 3
    || childTexts.some((text) => text.length > 2000)
    || childControls.length < 3
    || childControls.length > 8) return undefined;

  const normalizedTexts = childTexts.map(normalizeNativeDialogText);
  const expectedTexts = [SCENE_BACKUP_RECOVERY_BODY, "Yes", "No"];
  if (!expectedTexts.every((expected) => normalizedTexts.filter((text) => text === expected).length === 1)
    || normalizedTexts.some((text) => !expectedTexts.includes(text))) return undefined;

  const textControls = childControls.filter((control) => control.text.trim().length > 0);
  const buttons = childControls.filter((control) => control.className === "Button");
  const bodyControls = textControls.filter((control) => control.className !== "Button" && normalizeNativeDialogText(control.text) === SCENE_BACKUP_RECOVERY_BODY);
  const yesButtons = buttons.filter((control) => control.text.trim() === "Yes");
  const noButtons = buttons.filter((control) => control.text.trim() === "No");
  if (textControls.length !== 3
    || buttons.length !== 2
    || bodyControls.length !== 1
    || yesButtons.length !== 1
    || noButtons.length !== 1
    || childControls.some((control) => !control.visible
      || control.parentHwnd?.toLowerCase() !== windowHwnd.toLowerCase()
      || control.pid !== processIdentity.pid
      || control.windowThreadId !== processIdentity.windowThreadId)
    || buttons.some((control) => !control.enabled)) return undefined;

  return {
    title_sha256: `sha256:${createHash("sha256").update(SCENE_BACKUP_RECOVERY_TITLE).digest("hex")}`,
    body_sha256: `sha256:${createHash("sha256").update(SCENE_BACKUP_RECOVERY_BODY).digest("hex")}`,
    body_evidence: {
      previous_editor_session_detected: true,
      editor_may_not_have_closed_correctly: true,
      recovery_copy_destination_named: true,
    },
    visible_control_count: childControls.length,
    text_control_count: 3,
    button_count: 2,
    yes_button_count: 1,
    no_button_count: 1,
    diagnostic_only: true,
    interaction_supported: false,
  };
}

function normalizeNativeDialogText(value: string): string {
  return value.replaceAll("\r\n", "\n").trimEnd();
}

function normalizeSceneBackupDetectedBody(value: string): string {
  // Screenshot line wrapping is layout evidence, not part of the canonical words/punctuation.
  return value.replace(/\s+/g, " ").trim();
}

function classifySceneBackupDetectedPrompt(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], identity: NativeModalProcessIdentity): NonNullable<UnityNativeModalInfo["scene_backup_detected"]> | undefined {
  if ((processName !== "Unity" && processName !== "Unity.exe") || className !== "#32770" || windowTitle !== SCENE_BACKUP_DETECTED_TITLE || /^0x0+$/i.test(windowHwnd)
    || !identity.processStartUtc || identity.windowThreadId <= 0 || identity.windowVisible !== true || identity.windowEnabled !== true
    || identity.inspectionComplete !== true || childTexts.length !== 3 || childTexts.some((text) => text.length >= 1023)
    || (childControls.length !== 3 && childControls.length !== 4)
    || new Set(childControls.map((control) => control.hwnd.toLowerCase())).size !== childControls.length
    || childControls.some((control) => !control.visibleObserved || !control.visible || !control.enabledObserved || !control.enabled
      || /^0x0+$/i.test(control.hwnd)
      || control.pid !== identity.pid || control.windowThreadId !== identity.windowThreadId
      || control.parentHwnd?.toLowerCase() !== windowHwnd.toLowerCase())) return undefined;
  const buttons = childControls.filter((control) => control.className === "Button");
  const keep = buttons.filter((control) => control.controlId === 1 && control.text === "Keep Backups");
  const remove = buttons.filter((control) => control.controlId === 2 && control.text === "Delete");
  const body = childControls.filter((control) => control.className === "Edit" && control.controlId === 1019 && control.readOnly === true && control.multiline === true);
  const icons = childControls.filter((control) => control.className === "Static" && control.controlId === 1040 && control.text === "");
  const canonicalBody = normalizeSceneBackupDetectedBody(SCENE_BACKUP_DETECTED_BODY);
  if (buttons.length !== 2 || keep.length !== 1 || remove.length !== 1 || body.length !== 1
    || icons.length !== childControls.length - 3
    || normalizeSceneBackupDetectedBody(body[0]!.text) !== canonicalBody
    || childTexts.filter((text) => normalizeSceneBackupDetectedBody(text) === canonicalBody).length !== 1
    || childTexts.filter((text) => text === "Keep Backups").length !== 1 || childTexts.filter((text) => text === "Delete").length !== 1) return undefined;
  const topology = [...childControls].sort((left, right) => left.hwnd.toLowerCase().localeCompare(right.hwnd.toLowerCase()))
    .map((control) => `${control.hwnd.toLowerCase()}|${control.parentHwnd!.toLowerCase()}|${control.pid}|${control.windowThreadId}|1|1|${control.controlId}|${control.className}|${control.readOnly === true ? 1 : 0}|${control.multiline === true ? 1 : 0}|${control.text}`)
    .join("\n");
  const result: NonNullable<UnityNativeModalInfo["scene_backup_detected"]> = {
    evidence_scope: "complete_body",
    title_sha256: `sha256:${createHash("sha256").update(SCENE_BACKUP_DETECTED_TITLE).digest("hex")}`,
    body_sha256: `sha256:${createHash("sha256").update(body[0]!.text).digest("hex")}`,
    control_topology_sha256: `sha256:${createHash("sha256").update(topology).digest("hex")}`,
    visible_control_count: childControls.length as 3 | 4,
    button_count: 2,
    keep_backups_button_count: 1,
    delete_button_count: 1,
    diagnostic_only: true,
    interaction_supported: false,
  };
  sceneBackupDetectedNativeBindings.set(result, { keepHwnd: keep[0]!.hwnd, deleteHwnd: remove[0]!.hwnd, authoritative: false });
  return result;
}

function classifyScopedRegistryImportPrompt(processName: string, className: string, windowTitle: string, windowHwnd: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity): NonNullable<UnityNativeModalInfo["scoped_registry_import"]> | undefined {
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || className !== "#32770"
    || windowTitle !== SCOPED_REGISTRY_IMPORT_TITLE
    || !processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || processIdentity.windowVisible !== true
    || processIdentity.windowEnabled !== true
    || childTexts.length !== 3
    || childTexts.some((text) => text.length > 2000)
    || childControls.length < 3
    || childControls.length > 8) return undefined;

  const expectedTexts = [SCOPED_REGISTRY_IMPORT_BODY, "Learn More", "Close"];
  if (!expectedTexts.every((expected) => childTexts.filter((text) => text.trim() === expected).length === 1)
    || childTexts.some((text) => !expectedTexts.includes(text.trim()))) return undefined;

  const textControls = childControls.filter((control) => control.text.trim().length > 0);
  const buttons = childControls.filter((control) => control.className === "Button");
  const bodyControls = textControls.filter((control) => control.className !== "Button" && control.text.trim() === SCOPED_REGISTRY_IMPORT_BODY);
  const learnMoreButtons = buttons.filter((control) => control.text.trim() === "Learn More");
  const closeButtons = buttons.filter((control) => control.text.trim() === "Close");
  if (textControls.length !== 3
    || buttons.length !== 2
    || bodyControls.length !== 1
    || learnMoreButtons.length !== 1
    || closeButtons.length !== 1
    || childControls.some((control) => !control.visible
      || control.parentHwnd?.toLowerCase() !== windowHwnd.toLowerCase()
      || control.pid !== processIdentity.pid
      || control.windowThreadId !== processIdentity.windowThreadId)
    || buttons.some((control) => !control.enabled)) return undefined;

  const result: NonNullable<UnityNativeModalInfo["scoped_registry_import"]> = {
    title_sha256: `sha256:${createHash("sha256").update(SCOPED_REGISTRY_IMPORT_TITLE).digest("hex")}`,
    body_sha256: `sha256:${createHash("sha256").update(SCOPED_REGISTRY_IMPORT_BODY).digest("hex")}`,
    body_evidence: { exact_single_registry_message: true, package_manager_named: true },
    visible_control_count: childControls.length,
    text_control_count: 3,
    button_count: 2,
    learn_more_button_count: 1,
    close_button_count: 1,
    diagnostic_only: true,
    interaction_supported: false,
  };
  scopedRegistryButtonIdentities.set(result, { learnMoreHwnd: learnMoreButtons[0]!.hwnd, closeHwnd: closeButtons[0]!.hwnd });
  return result;
}

function classifyDuplicateProjectInstancePrompt(processName: string, className: string, windowTitle: string, childTexts: string[], childControls: ParsedNativeChildControl[], processIdentity: NativeModalProcessIdentity): NonNullable<UnityNativeModalInfo["duplicate_project_instance"]> | undefined {
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || !DUPLICATE_PROJECT_INSTANCE_CLASSES.includes(className as typeof DUPLICATE_PROJECT_INSTANCE_CLASSES[number])
    || windowTitle !== DUPLICATE_PROJECT_INSTANCE_TITLE
    || !processIdentity.processStartUtc
    || processIdentity.windowThreadId <= 0
    || childTexts.length === 0
    || childTexts.length > 32
    || childTexts.some((text) => text.length > 2000)
    || childControls.length === 0
    || childControls.length > 64) return undefined;

  const bodyTexts = childTexts.filter((text) => text.trim() !== "OK");
  const body = bodyTexts.join("\n");
  if (body.length === 0 || body.length > 16_000
    || !body.includes("It looks like another Unity instance is running with this project open!")
    || !body.includes("Multiple Unity instances cannot open the same project.")) return undefined;

  const projectMatches = [...body.matchAll(/(?:^|\s)Project:\s*([A-Za-z]:[\\/][^\r\n]{1,512})$/gm)];
  const projectPath = projectMatches.length === 1 ? projectMatches[0]?.[1]?.trim() : undefined;
  if (!projectPath || projectPath.includes("<") || projectPath.includes(">")) return undefined;

  const buttons = childControls.filter((control) => control.className === "Button");
  const textControls = childControls.filter((control) => control.text.length > 0);
  const okButtons = buttons.filter((control) => control.enabled
    && control.text.trim() === "OK"
    && control.pid === processIdentity.pid
    && control.windowThreadId === processIdentity.windowThreadId);
  if (buttons.length !== 1 || okButtons.length !== 1
    || textControls.some((control) => control.pid !== processIdentity.pid || control.windowThreadId !== processIdentity.windowThreadId)) return undefined;

  const normalizedProjectIdentity = projectPath.replaceAll("\\", "/").replace(/\/+$/g, "").toLowerCase();
  return {
    body_sha256: `sha256:${createHash("sha256").update(body).digest("hex")}`,
    project_identity_sha256: `sha256:${createHash("sha256").update(normalizedProjectIdentity).digest("hex")}`,
    body_evidence: {
      another_instance_running: true,
      multiple_instances_forbidden: true,
      project_path_present: true,
    },
    button_count: 1,
    ok_button_count: 1,
    diagnostic_only: true,
    interaction_supported: false,
  };
}

function classifyProjectDowngradePrompt(processName: string, className: string, windowTitle: string, childControls: Array<{ hwnd: string; className: string; text: string }>): NonNullable<UnityNativeModalInfo["project_downgrade"]> | undefined {
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || !PROJECT_DOWNGRADE_CLASSES.includes(className as typeof PROJECT_DOWNGRADE_CLASSES[number])
    || windowTitle !== PROJECT_DOWNGRADE_TITLE
    || childControls.length > 64) return undefined;
  const buttons = childControls.filter((control) => control.className === "Button");
  const continueButtons = buttons.filter((control) => control.text.trim() === "Continue");
  const quitButtons = buttons.filter((control) => control.text.trim() === "Quit");
  if (buttons.length !== 2 || continueButtons.length !== 1 || quitButtons.length !== 1) return undefined;
  const bodyTexts = childControls
    .filter((control) => control.className !== "Button" && control.text.length > 0)
    .map((control) => control.text);
  if (bodyTexts.length === 0 || bodyTexts.length > 32 || bodyTexts.some((text) => text.length > 2000) || bodyTexts.join("\n").length > 16_000) return undefined;
  const body = bodyTexts.join("\n");
  const versions = uniqueStrings([...body.matchAll(/\b\d{1,4}\.\d{1,4}\.\d{1,4}(?:(?:a|b|f|p|rc)\d{1,4})?\b/g)].map((match) => match[0]));
  if (versions.length !== 2) return undefined;
  const [sourceVersion, targetVersion] = versions;
  if (!sourceVersion || !targetVersion || compareUnityVersions(sourceVersion, targetVersion) !== 1) return undefined;
  const sourcePattern = new RegExp(`\\b${escapeRegExp(sourceVersion)}\\b`, "i");
  const targetPattern = new RegExp(`\\b${escapeRegExp(targetVersion)}\\b`, "i");
  const sourceNewer = bodyTexts.some((text) => sourcePattern.test(text) && /(?:source|project|saved|created|last\s+used)/i.test(text) && /\bnewer\b/i.test(text));
  const targetOlder = bodyTexts.some((text) => targetPattern.test(text) && /(?:target|current|currently\s+using|opening)/i.test(text) && /\bolder\b/i.test(text));
  const sourceNewerTargetOlder = sourceNewer && targetOlder;
  const downgradeUnsupported = /\bdowngrad\w*\b[^\r\n.]{0,120}\b(?:is\s+not\s+supported|not\s+supported|unsupported)\b/i.test(body);
  const continueDowngrades = /\bcontinu(?:e|ing)\b[^\r\n.]{0,120}\bdowngrad\w*\b/i.test(body);
  const assetsReimport = /\bassets?\b[^\r\n.]{0,120}\bre-?import\w*\b/i.test(body);
  const packagesSettingsAlsoDowngraded = /\bpackages?\b[^\r\n.]{0,160}\bsettings?\b[^\r\n.]{0,160}\b(?:may|might|will)\b[^\r\n.]{0,80}\balso\b[^\r\n.]{0,80}\bdowngrad\w*\b/i.test(body)
    || /\bsettings?\b[^\r\n.]{0,160}\bpackages?\b[^\r\n.]{0,160}\b(?:may|might|will)\b[^\r\n.]{0,80}\balso\b[^\r\n.]{0,80}\bdowngrad\w*\b/i.test(body);
  if (!sourceNewerTargetOlder || !downgradeUnsupported || !continueDowngrades || !assetsReimport || !packagesSettingsAlsoDowngraded) return undefined;
  return {
    source_version: sourceVersion,
    target_version: targetVersion,
    body_sha256: `sha256:${createHash("sha256").update(body).digest("hex")}`,
    body_evidence: {
      source_newer_target_older: true,
      downgrade_unsupported: true,
      continue_downgrades: true,
      assets_reimport: true,
      packages_settings_also_downgraded: true,
    },
    button_count: 2,
    continue_button_hwnd: continueButtons[0]!.hwnd,
    quit_button_hwnd: quitButtons[0]!.hwnd,
  };
}

function compareUnityVersions(left: string, right: string): -1 | 0 | 1 | undefined {
  const parse = (value: string): number[] | undefined => {
    const match = /^(\d{1,4})\.(\d{1,4})\.(\d{1,4})(?:(a|b|rc|f|p)(\d{1,4}))?$/.exec(value);
    if (!match) return undefined;
    const stage = match[4] ?? "f";
    return [Number(match[1]), Number(match[2]), Number(match[3]), { a: 0, b: 1, rc: 2, f: 3, p: 4 }[stage] ?? -1, Number(match[5] ?? 0)];
  };
  const leftParts = parse(left);
  const rightParts = parse(right);
  if (!leftParts || !rightParts) return undefined;
  for (let index = 0; index < leftParts.length; index += 1) {
    if (leftParts[index]! > rightParts[index]!) return 1;
    if (leftParts[index]! < rightParts[index]!) return -1;
  }
  return 0;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

function classifyNativeUnityProgressWindow(windowTitle: string, childTexts: string[]): { likelyProgressWindow: boolean; progressKind: UnityNativeProgressWindowInfo["progress_kind"]; busyText?: string; busySeconds?: number; isStalled: boolean; blockCode?: UnityBlockingNativeProgressCode; matchedKeywords: string[] } {
  const allText = [windowTitle, ...childTexts].join("\n");
  const domainReload = /\b(reloading\s+domain|domain\s+reload)\b/i.test(allText);
  const compile = /\b(compiling\s+scripts?|running\s+backend)\b/i.test(allText);
  const assetImport = /\b(importing\s+(?:asset|assets|package|packages)|asset\s+import|refreshing\s+(?:asset|assets|asset\s+database)|updating\s+asset\s+database|initializing\s+asset\s+database)\b/i.test(allText);
  const busy = parseBusyDuration(allText);
  const hasBusyProgress = busy.busySeconds !== undefined && /\bbusy\s+for\b/i.test(allText);
  const progressKind: UnityNativeProgressWindowInfo["progress_kind"] = domainReload ? "domain_reload" : compile ? "compile" : assetImport ? "asset_import" : "unknown";
  const likelyProgressWindow = domainReload || compile || assetImport || hasBusyProgress;
  const isStalled = domainReload && busy.busySeconds !== undefined && busy.busySeconds >= DOMAIN_RELOAD_PROGRESS_STALL_SECONDS;
  const matchedKeywords: string[] = [];
  if (domainReload) {
    matchedKeywords.push("domain_reload");
  }
  if (compile) {
    matchedKeywords.push("compile_progress");
  }
  if (assetImport) {
    matchedKeywords.push("asset_import_progress");
  }
  if (busy.busySeconds !== undefined) {
    matchedKeywords.push("busy_for");
  }
  if (isStalled) {
    matchedKeywords.push("stalled_progress");
  }

  return {
    likelyProgressWindow,
    progressKind,
    ...(busy.busyText ? { busyText: busy.busyText } : {}),
    ...(busy.busySeconds !== undefined ? { busySeconds: busy.busySeconds } : {}),
    isStalled,
    ...(isStalled ? { blockCode: DOMAIN_RELOAD_PROGRESS_CODE } : {}),
    matchedKeywords: uniqueStrings(matchedKeywords),
  };
}

function tasklistResponding(value: string | undefined): boolean | undefined {
  const normalized = (value ?? "").trim().toLowerCase();
  if (normalized === "running") return true;
  if (normalized === "not responding") return false;
  return undefined;
}

function parseBusyDuration(text: string): { busyText?: string; busySeconds?: number } {
  const match = /\bbusy\s+for\s+((?:\d{1,2}:){1,2}\d{1,2}|\d+)\b/i.exec(text);
  if (!match?.[1]) {
    return {};
  }

  const busyText = match[1];
  const parts = busyText.split(":").map((part) => Number(part));
  if (parts.some((part) => !Number.isInteger(part) || part < 0)) {
    return { busyText };
  }

  if (parts.length === 1) {
    const seconds = parts[0];
    return seconds === undefined ? { busyText } : { busyText, busySeconds: seconds };
  }

  if (parts.length === 2) {
    const minutes = parts[0];
    const seconds = parts[1];
    return minutes === undefined || seconds === undefined ? { busyText } : { busyText, busySeconds: minutes * 60 + seconds };
  }

  const hours = parts[0];
  const minutes = parts[1];
  const seconds = parts[2];
  return hours === undefined || minutes === undefined || seconds === undefined ? { busyText } : { busyText, busySeconds: hours * 3600 + minutes * 60 + seconds };
}

function modalKeywords(title: string): string[] {
  const normalized = title.toLowerCase();
  const matches: string[] = [];
  const add = (keyword: string, pattern: RegExp) => {
    if (pattern.test(normalized)) {
      matches.push(keyword);
    }
  };

  add("recovery", /recover|recovery|восстанов/);
  add("restore", /restore|восстанов/);
  add("crash", /crash|crashed|сбой|авар/);
  add("safe_mode", /safe mode|безопасн/);
  add("project_upgrade", /^project upgrade required$/);
  add("missing_signature", /missing signature/);
  add("corrupted_library", /corrupted library|library detected|библиотек/);
  add("low_disk_space", /low disk space/);
  add("scene_modified", /scene\(s\) have been modified|save scene|unsaved|modified|сцен|измен/);
  add("license", /license|licensing|activation|лиценз|активац/);
  add("modal", /dialog|prompt|popup|диалог|окно/);
  return matches;
}

function recoveryPromptRespondResult(input: {
  activeBefore: UnityWindowDiagnosticsStatus;
  activeAfter?: UnityWindowDiagnosticsStatus;
  actionAlias: UnityRecoveryPromptAction | undefined;
  dryRun: boolean;
  supported: boolean;
  targetPids: number[];
  skipped: Array<{ pid: number; reason: string }>;
  results: UnityRecoveryPromptClickResult[];
  errors: string[];
}): UnityRecoveryPromptRespondResult {
  const clickedCount = input.results.filter((result) => result.status === "clicked").length;
  const canonicalAction = input.actionAlias === "close_missing_signature" ? "close_missing_signature" : "do_not_restore";
  const nextValidActions = ["call_diagnostics.unity_recovery_prompt.status", "call_sgg_unity_projects_after_modal_resolution"];
  if (canonicalAction === "close_missing_signature") {
    nextValidActions.push("call_compile.status_after_heartbeat_recovers", "optionally_call_package.info_after_heartbeat_recovers");
  }
  if (input.dryRun) {
    nextValidActions.unshift("review_dry_run_match_before_non_dry_run");
  }
  if (input.errors.includes("explicit_single_pid_required")) {
    nextValidActions.unshift("rerun_with_one_explicit_pid");
  }
  if (input.errors.includes("no_recovery_prompt") || input.errors.includes("no_requested_recovery_prompt") || input.errors.includes("no_missing_signature_prompt") || input.errors.includes("no_requested_missing_signature_prompt")) {
    nextValidActions.unshift("inspect_unity_window_manually");
  }

  return {
    schema_version: 1,
    action: canonicalAction,
    ...(input.actionAlias && input.actionAlias !== canonicalAction ? { action_alias: input.actionAlias } : {}),
    dry_run: input.dryRun,
    supported: input.supported,
    button_click_method: canonicalAction === "close_missing_signature" ? "powershell_user32_exact_window_close" : "powershell_user32_bm_click",
    target_pids: input.targetPids,
    clicked_count: clickedCount,
    skipped: input.skipped,
    results: input.results,
    active_before: input.activeBefore,
    ...(input.activeAfter ? { active_after: input.activeAfter } : {}),
    errors: input.errors,
    next_valid_actions: uniqueStrings(nextValidActions),
  };
}

function modalRespondSpec(action: UnityRecoveryPromptAction, activeBefore: UnityWindowDiagnosticsStatus): ModalRespondSpec {
  if (action === "close_missing_signature") {
    return {
      action,
      candidates: activeBefore.missing_signature_modal_candidates,
      notCandidateReason: "not_active_missing_signature_prompt",
      noCandidateError: "no_missing_signature_prompt",
      noRequestedCandidateError: "no_requested_missing_signature_prompt",
      titlePattern: "(?i)^Missing Signature$",
      buttonPattern: "(?i)^Close$",
      requiredTextPattern: "(?i)(unsigned\\s+packages?|Package Manager|open\\s+the\\s+Package\\s+Manager|UnityEditor\\.PackageManager\\.UI\\.Internal\\.ModalWindowContainer)",
      requiredClassPattern: "(?i)^(#32770|UnityContainerWndClass)$",
      closeMethod: "window_close",
    };
  }

  return {
    action: "do_not_restore",
    candidates: activeBefore.recovery_prompt_candidates.filter((candidate) => !(activeBefore.scene_backup_recovery_modal_candidates ?? []).some((modal) => modal.pid === candidate.pid)),
    notCandidateReason: "not_active_unity_recovery_prompt",
    noCandidateError: "no_recovery_prompt",
    noRequestedCandidateError: "no_requested_recovery_prompt",
    titlePattern: "(?i)(recover|recovery|restore|crash|crashed|safe mode|восстанов|сбой|авар|безопасн)",
    buttonPattern: "(?i)(don''?t\\s+restore|do\\s+not\\s+restore|dont\\s+restore|discard|skip|continue\\s+without\\s+restor|не\\s+восстанавливать|не\\s+восстанавл|пропустить)",
    requiredTextPattern: "",
    requiredClassPattern: "",
    closeMethod: "button",
  };
}

function normalizeRecoveryPromptAction(action: string | undefined): UnityRecoveryPromptAction | undefined {
  if (action === undefined) {
    return undefined;
  }
  return UNITY_RECOVERY_PROMPT_ACTIONS.includes(action as UnityRecoveryPromptAction) ? action as UnityRecoveryPromptAction : undefined;
}

function normalizedRequestedPids(pids: number[] | undefined): { valid: number[]; invalid: number[] } {
  const valid: number[] = [];
  const invalid: number[] = [];
  for (const pid of pids ?? []) {
    if (!Number.isInteger(pid) || pid <= 0) {
      invalid.push(pid);
    } else if (!valid.includes(pid)) {
      valid.push(pid);
    }
  }
  return { valid, invalid };
}

function nativeUnityModalPowerShellArgs(): string[] {
  return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", nativeUnityModalPowerShellScript()];
}

function nativeUnityModalPowerShellScript(): string {
  return `
$ErrorActionPreference = 'Stop'
$script:scanStageCount = 0
function Write-ScanStage([string]$stage, [string]$state) {
  if ($script:scanStageCount -gt 256) { return }
  if ($script:scanStageCount -eq 256) {
    # Bounded diagnostic tracing is not native coverage evidence. Do not leave a stale stage claiming localization.
    [Console]::Out.WriteLine('__SGG_SCAN_STAGE__:trace_limit:skipped')
    [Console]::Out.Flush()
    $script:scanStageCount++
    return
  }
  $script:scanStageCount++
  [Console]::Out.WriteLine(('__SGG_SCAN_STAGE__:{0}:{1}' -f $stage, $state))
  [Console]::Out.Flush()
}
Write-ScanStage 'add_type' 'entered'
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class SGGUnityNativeModalUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
  [DllImport("user32.dll", EntryPoint="GetWindowLong")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
  [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
"@
Add-Type -TypeDefinition $source
Write-ScanStage 'add_type' 'completed'

function Read-WindowTextValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 1024
  [IntPtr]$messageResult = [IntPtr]::Zero
  $sent = [SGGUnityNativeModalUser32]::SendMessageTimeoutText($handle, 0x000D, [IntPtr]$builder.Capacity, $builder, 0x0002, 100, [ref]$messageResult)
  return [pscustomobject]@{ text = $builder.ToString(); complete = ($sent -ne [IntPtr]::Zero -and $messageResult.ToInt64() -lt ($builder.Capacity - 1)) }
}

function Get-ClassNameValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 256
  [void][SGGUnityNativeModalUser32]::GetClassName($handle, $builder, $builder.Capacity)
  return $builder.ToString()
}

$windowPattern = '(?im)(^Unity\\.Licensing\\.Client\\.exe\\s+-\\s+.+\\r?$|^Corrupted Library Detected\\r?$|^Enter Safe Mode\\??\\r?$|^Missing Signature\\r?$|^The open scene\\(s\\) have been modified externally\\r?$|^Project Upgrade Required\\r?$|^Project Downgrade Required\\r?$|^Importing a scoped registry\\r?$|^Recovering Scene Backups\\r?$|^Low disk space\\r?$|^Error!\\r?$|Reloading\\s+Domain|Domain\\s+Reload|busy\\s+for|Compiling\\s+Scripts|running\\s+backend|Importing\\s+(?:Asset|Assets|Package|Packages)|Asset\\s+Import|Refreshing\\s+(?:Asset|Assets|Asset\\s+Database)|Updating\\s+Asset\\s+Database|Initializing\\s+Asset\\s+Database)'
$results = New-Object System.Collections.Generic.List[object]
$script:user32CandidateCount = 0
$script:user32IncompleteInspectionCount = 0

$user32ScanSucceeded = $false
Write-ScanStage 'user32_top_level' 'entered'
try {
$enumWindowsSucceeded = [SGGUnityNativeModalUser32]::EnumWindows([SGGUnityNativeModalUser32+EnumWindowsProc]{
  param([IntPtr]$handle, [IntPtr]$lparam)
  Write-ScanStage 'user32_top_level' 'entered'
  $windowVisible = [SGGUnityNativeModalUser32]::IsWindowVisible($handle)
  $windowEnabled = [SGGUnityNativeModalUser32]::IsWindowEnabled($handle)
  if (-not $windowVisible) { return $true }

  Write-ScanStage 'user32_process' 'entered'
  [uint32]$windowPid = 0
  $windowThreadId = [SGGUnityNativeModalUser32]::GetWindowThreadProcessId($handle, [ref]$windowPid)
  $processName = ''
  $processStartUtc = ''
  try {
    $process = Get-Process -Id ([int]$windowPid) -ErrorAction Stop
    $processName = $process.ProcessName
  } catch {}
  if ($processName) {
    try { $processStartUtc = $process.StartTime.ToUniversalTime().ToString('o') } catch { $processStartUtc = '' }
  }
  Write-ScanStage 'user32_process' 'completed'
  $isUnityProcess = $processName -ceq 'Unity'
  $isLicensingProcess = $processName -ceq 'Unity.Licensing.Client'
  $isPotentialLicensingDialogHost = $processName -ceq 'csrss'
  if (-not $isUnityProcess -and -not $isLicensingProcess -and -not $isPotentialLicensingDialogHost) { return $true }
  Write-ScanStage 'user32_title' 'entered'
  $titleRead = Read-WindowTextValue $handle
  Write-ScanStage 'user32_title' 'completed'
  $title = [string]$titleRead.text
  $isSupportedProcess = $isUnityProcess -or $isLicensingProcess -or ($isPotentialLicensingDialogHost -and $title -match '^Unity\.Licensing\.Client\.exe\s+-\s+.+$')
  if (-not $isSupportedProcess) { return $true }
  $script:currentWindowTextReadsComplete = [bool]$titleRead.complete

  $childTexts = New-Object System.Collections.Generic.List[string]
  $childControls = New-Object System.Collections.Generic.List[object]
  Write-ScanStage 'user32_children' 'entered'
  [void][SGGUnityNativeModalUser32]::EnumChildWindows($handle, [SGGUnityNativeModalUser32+EnumWindowsProc]{
    param([IntPtr]$childHandle, [IntPtr]$childParam)
    Write-ScanStage 'user32_child_text' 'entered'
    $textRead = Read-WindowTextValue $childHandle
    Write-ScanStage 'user32_child_text' 'completed'
    Write-ScanStage 'user32_children' 'entered'
    $text = [string]$textRead.text
    if (-not $textRead.complete) { $script:currentWindowTextReadsComplete = $false }
    [uint32]$childPid = 0
    $childThreadId = [SGGUnityNativeModalUser32]::GetWindowThreadProcessId($childHandle, [ref]$childPid)
    $childVisible = [SGGUnityNativeModalUser32]::IsWindowVisible($childHandle)
    $childEnabled = [SGGUnityNativeModalUser32]::IsWindowEnabled($childHandle)
    $childParent = [SGGUnityNativeModalUser32]::GetParent($childHandle)
    $childClassName = Get-ClassNameValue $childHandle
    $childStyle = [SGGUnityNativeModalUser32]::GetWindowLong($childHandle, -16)
    $childControls.Add([pscustomobject]@{ hwnd = ('0x{0:X}' -f $childHandle.ToInt64()); parent_hwnd = ('0x{0:X}' -f $childParent.ToInt64()); pid = [int]$childPid; window_thread_id = [int]$childThreadId; visible = $childVisible; enabled = $childEnabled; control_id = [SGGUnityNativeModalUser32]::GetDlgCtrlID($childHandle); class_name = $childClassName; text = $text; read_only = ($childClassName -ceq 'Edit' -and (($childStyle -band 0x0800) -ne 0)); multiline = ($childClassName -ceq 'Edit' -and (($childStyle -band 0x0004) -ne 0)) }) | Out-Null
    if ($childVisible -and $text) {
      $childTexts.Add($text) | Out-Null
    }
    return $true
  }, [IntPtr]::Zero)
  Write-ScanStage 'user32_children' 'completed'
  Write-ScanStage 'user32_top_level' 'entered'

  $dialogText = (@($title) + $childTexts.ToArray()) -join [Environment]::NewLine
  $className = Get-ClassNameValue $handle
  $owner = [SGGUnityNativeModalUser32]::GetWindow($handle, 4)
  $extendedStyle = [SGGUnityNativeModalUser32]::GetWindowLong($handle, -20)
  $modalLikeReason = ''
  if ($isUnityProcess -and $className -ceq '#32770') { $modalLikeReason = 'dialog_class' }
  elseif ($isUnityProcess -and $owner -ne [IntPtr]::Zero) { $modalLikeReason = 'owned_unity_window' }
  elseif ($isUnityProcess -and (($extendedStyle -band 0x00000001) -ne 0)) { $modalLikeReason = 'dialog_modal_frame' }
  $knownTextMatch = [bool]($isSupportedProcess -and $dialogText -and ($dialogText -match $windowPattern -or $title -ceq '${SCENE_BACKUP_DETECTED_TITLE}'))
  if ($isUnityProcess -and -not $script:currentWindowTextReadsComplete) { $script:user32IncompleteInspectionCount += 1 }
  if (-not $knownTextMatch -and -not $modalLikeReason) { return $true }

  $inspectionComplete = [bool]($script:currentWindowTextReadsComplete -and $windowThreadId -gt 0 -and $processName -and $processStartUtc)
  if (-not $inspectionComplete) { $script:user32IncompleteInspectionCount += 1 }
  $script:user32CandidateCount += 1

  $results.Add([pscustomobject]@{
    hwnd = ('0x{0:X}' -f $handle.ToInt64())
    pid = [int]$windowPid
    process_name = $processName
    process_start_utc = $processStartUtc
    window_thread_id = [int]$windowThreadId
    window_visible = $windowVisible
    window_enabled = $windowEnabled
    class_name = $className
    window_title = $title
    child_texts = $childTexts.ToArray()
    child_controls = $childControls.ToArray()
    modal_like_reason = $modalLikeReason
    inspection_complete = $inspectionComplete
  }) | Out-Null
  return $true
}, [IntPtr]::Zero)
  Write-ScanStage 'user32_top_level' 'completed'
  if (-not $enumWindowsSucceeded) { throw 'enum_windows_failed' }
  $user32ScanSucceeded = $true
} catch {
  $user32ScanSucceeded = $false
}

$uiAutomationScanSucceeded = $false
$uiAutomationScanStatus = 'not_required'
if (-not $user32ScanSucceeded) {
  $results.Clear()
  $uiAutomationScanStatus = 'failed'
  Write-ScanStage 'uia_enumeration' 'entered'
  try {
  Add-Type -AssemblyName UIAutomationClient
  Add-Type -AssemblyName UIAutomationTypes
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $unityProcesses = @(Get-Process -Name Unity -ErrorAction SilentlyContinue)
  foreach ($unityProcess in $unityProcesses) {
    $unityPid = [int]$unityProcess.Id
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
      $unityPid
    )
    $elements = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $processCondition)
    foreach ($dialog in $elements) {
      $dialogName = [string]$dialog.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
      if ($dialogName -cne 'The open scene(s) have been modified externally' -and $dialogName -cne 'Enter Safe Mode?' -and $dialogName -cne '${PROJECT_UPGRADE_TITLE}' -and $dialogName -cne 'Importing a scoped registry' -and $dialogName -cne 'Recovering Scene Backups') { continue }
      $dialogEnabled = [bool]$dialog.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsEnabledProperty)
      $dialogVisible = -not [bool]$dialog.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsOffscreenProperty)
      if (-not $dialogEnabled -or -not $dialogVisible) { continue }
    $dialogPid = [int]$dialog.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::ProcessIdProperty)
    $dialogHwndValue = [int]$dialog.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty)
    if ($dialogPid -ne $unityPid -or $dialogHwndValue -le 0) { continue }
    $dialogProcess = Get-Process -Id $dialogPid -ErrorAction Stop
    if ($dialogProcess.ProcessName -cne 'Unity' -or $dialogProcess.StartTime.ToUniversalTime().ToString('o') -cne $unityProcess.StartTime.ToUniversalTime().ToString('o')) { continue }

    $dialogHandle = [IntPtr]$dialogHwndValue
    $dialogHwnd = '0x{0:X}' -f $dialogHandle.ToInt64()
    [uint32]$verifiedDialogPid = 0
    $dialogThreadId = [SGGUnityNativeModalUser32]::GetWindowThreadProcessId($dialogHandle, [ref]$verifiedDialogPid)
    if ([int]$verifiedDialogPid -ne $dialogPid -or $dialogThreadId -le 0) { continue }

    $childTexts = New-Object System.Collections.Generic.List[string]
    $childControls = New-Object System.Collections.Generic.List[object]
    $children = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($child in $children) {
      $text = [string]$child.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NameProperty)
      $offscreen = [bool]$child.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsOffscreenProperty)
      $visible = -not $offscreen
      if ($visible -and $text) { $childTexts.Add($text) | Out-Null }

      $childHwndValue = [int]$child.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::NativeWindowHandleProperty)
      if ($childHwndValue -le 0) { continue }
      $childHandle = [IntPtr]$childHwndValue
      [uint32]$childPid = 0
      $childThreadId = [SGGUnityNativeModalUser32]::GetWindowThreadProcessId($childHandle, [ref]$childPid)
      $childParent = [SGGUnityNativeModalUser32]::GetParent($childHandle)
      $childEnabled = [bool]$child.GetCurrentPropertyValue([System.Windows.Automation.AutomationElement]::IsEnabledProperty)
      $childControls.Add([pscustomobject]@{
        hwnd = ('0x{0:X}' -f $childHandle.ToInt64())
        parent_hwnd = ('0x{0:X}' -f $childParent.ToInt64())
        pid = [int]$childPid
        window_thread_id = [int]$childThreadId
        visible = $visible
        enabled = $childEnabled
        class_name = Get-ClassNameValue $childHandle
        text = $text
      }) | Out-Null
    }

    $replacement = [pscustomobject]@{
      hwnd = $dialogHwnd
      pid = $dialogPid
      process_name = $dialogProcess.ProcessName
      process_start_utc = $dialogProcess.StartTime.ToUniversalTime().ToString('o')
      window_thread_id = [int]$dialogThreadId
      window_visible = $dialogVisible
      window_enabled = $dialogEnabled
      class_name = Get-ClassNameValue $dialogHandle
      window_title = $dialogName
      child_texts = $childTexts.ToArray()
      child_controls = $childControls.ToArray()
    }
    foreach ($existing in @($results | Where-Object { $_.hwnd -ceq $dialogHwnd })) {
      $results.Remove($existing) | Out-Null
    }
    $results.Add($replacement) | Out-Null
    }
  }
  $uiAutomationScanSucceeded = $true
  $uiAutomationScanStatus = 'succeeded'
  Write-ScanStage 'uia_enumeration' 'completed'
  } catch {
    $uiAutomationScanSucceeded = $false
    $uiAutomationScanStatus = 'failed'
  }
} else {
  Write-ScanStage 'uia_enumeration' 'skipped'
}

if (-not $user32ScanSucceeded -and -not $uiAutomationScanSucceeded) { throw 'user32_and_uia_scan_failed' }

Write-ScanStage 'serialization' 'entered'
$serialized = [pscustomobject]@{ rows = $results.ToArray(); user32_scan_succeeded = $user32ScanSucceeded; user32_scan_complete = ($user32ScanSucceeded -and $script:user32IncompleteInspectionCount -eq 0); user32_candidate_count = $script:user32CandidateCount; uia_scan_succeeded = $uiAutomationScanSucceeded; uia_scan_status = $uiAutomationScanStatus } | ConvertTo-Json -Compress -Depth 7
Write-ScanStage 'serialization' 'completed'
$serialized
`.trim();
}

function sceneBackupKeepPowerShellScript(hwndHex: string, pid: number, identityBase64: string): string {
  return `
$ErrorActionPreference = 'Stop'
$targetHwnd = [IntPtr]([Convert]::ToInt64('${hwndHex}', 16))
$targetPid = ${pid}
$expected = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${identityBase64}')))
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SGGUnitySceneBackupKeepUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll", EntryPoint="GetClassNameW", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
  [DllImport("user32.dll", EntryPoint="GetWindowLong")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
  [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
"@
Add-Type -TypeDefinition $source
$script:textReadsComplete=$true
function Get-Text([IntPtr]$handle) {
  $b=New-Object System.Text.StringBuilder 1024; [IntPtr]$r=[IntPtr]::Zero
  $sent=[SGGUnitySceneBackupKeepUser32]::SendMessageTimeoutText($handle,0x000D,[IntPtr]$b.Capacity,$b,0x0002,100,[ref]$r)
  if ($sent -eq [IntPtr]::Zero -or $r.ToInt64() -ge ($b.Capacity - 1)) { $script:textReadsComplete=$false; return '' }
  $b.ToString()
}
function Get-Class([IntPtr]$handle) { $b=New-Object System.Text.StringBuilder 256; [void][SGGUnitySceneBackupKeepUser32]::GetClassName($handle,$b,$b.Capacity); $b.ToString() }
function Get-Sha256([string]$value) { $sha=[Security.Cryptography.SHA256]::Create(); try { 'sha256:' + ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))).Replace('-','').ToLowerInvariant()) } finally { $sha.Dispose() } }
$result=$null
if (-not [SGGUnitySceneBackupKeepUser32]::IsWindow($targetHwnd) -or -not [SGGUnitySceneBackupKeepUser32]::IsWindowVisible($targetHwnd) -or -not [SGGUnitySceneBackupKeepUser32]::IsWindowEnabled($targetHwnd)) {
  $result=[pscustomobject]@{ status='refused'; error='target_window_not_found_or_disabled' }
} else {
  [uint32]$windowPid=0
  $threadId=[SGGUnitySceneBackupKeepUser32]::GetWindowThreadProcessId($targetHwnd,[ref]$windowPid)
  $processName=''; $processStartUtc=''
  try { $p=Get-Process -Id ([int]$windowPid) -ErrorAction Stop; $processName=$p.ProcessName; $processStartUtc=$p.StartTime.ToUniversalTime().ToString('o') } catch {}
  $title=Get-Text $targetHwnd
  $controls=New-Object System.Collections.Generic.List[object]
  [void][SGGUnitySceneBackupKeepUser32]::EnumChildWindows($targetHwnd,[SGGUnitySceneBackupKeepUser32+EnumWindowsProc]{
    param([IntPtr]$child,[IntPtr]$unused)
    [uint32]$childPid=0
    $childThreadId=[SGGUnitySceneBackupKeepUser32]::GetWindowThreadProcessId($child,[ref]$childPid)
    $className=Get-Class $child; $style=[SGGUnitySceneBackupKeepUser32]::GetWindowLong($child,-16)
    $controls.Add([pscustomobject]@{
      hwnd=('0x{0:X}' -f $child.ToInt64()); handle=$child; parent=('0x{0:X}' -f ([SGGUnitySceneBackupKeepUser32]::GetParent($child)).ToInt64()); root=[SGGUnitySceneBackupKeepUser32]::GetAncestor($child,2)
      pid=[int]$childPid; thread_id=[int]$childThreadId; visible=[SGGUnitySceneBackupKeepUser32]::IsWindowVisible($child); enabled=[SGGUnitySceneBackupKeepUser32]::IsWindowEnabled($child)
      control_id=[SGGUnitySceneBackupKeepUser32]::GetDlgCtrlID($child); class_name=$className; text=(Get-Text $child)
      read_only=($className -ceq 'Edit' -and (($style -band 0x0800) -ne 0)); multiline=($className -ceq 'Edit' -and (($style -band 0x0004) -ne 0))
    }) | Out-Null
    return $true
  },[IntPtr]::Zero)
  $buttons=@($controls | Where-Object { $_.class_name -ceq 'Button' })
  $keep=@($buttons | Where-Object { $_.control_id -eq 1 -and $_.text -ceq 'Keep Backups' })
  $delete=@($buttons | Where-Object { $_.control_id -eq 2 -and $_.text -ceq 'Delete' })
  $body=@($controls | Where-Object { $_.class_name -ceq 'Edit' -and $_.control_id -eq 1019 -and $_.read_only -and $_.multiline })
  $icons=@($controls | Where-Object { $_.class_name -ceq 'Static' -and $_.control_id -eq 1040 -and $_.text -ceq '' })
  $topologyLines=@($controls | Sort-Object { $_.hwnd.ToLowerInvariant() } | ForEach-Object {
    $visible=if ($_.visible) { 1 } else { 0 }; $enabled=if ($_.enabled) { 1 } else { 0 }; $readOnly=if ($_.read_only) { 1 } else { 0 }; $multiline=if ($_.multiline) { 1 } else { 0 }
    '{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}' -f $_.hwnd.ToLowerInvariant(),$_.parent.ToLowerInvariant(),$_.pid,$_.thread_id,$visible,$enabled,$_.control_id,$_.class_name,$readOnly,$multiline,$_.text
  })
  $expectedProcessName=([string]$expected.process_name).Replace('.exe','')
  $identityValid=[int]$windowPid -eq $targetPid -and $processName -ceq $expectedProcessName -and $processStartUtc -ceq ([string]$expected.process_start_utc) -and [int]$threadId -eq [int]$expected.window_thread_id -and (Get-Class $targetHwnd) -ceq '#32770' -and $title -ceq '${SCENE_BACKUP_DETECTED_TITLE}'
  $topologyValid=$script:textReadsComplete -and $controls.Count -eq [int]$expected.visible_control_count -and $buttons.Count -eq 2 -and $keep.Count -eq 1 -and $delete.Count -eq 1 -and $body.Count -eq 1 -and $icons.Count -eq ($controls.Count - 3) -and (Get-Sha256 $body[0].text) -ceq ([string]$expected.body_sha256) -and (Get-Sha256 ($topologyLines -join ([char]10))) -ceq ([string]$expected.control_topology_sha256)
  $boundaryValid=@($controls | Where-Object { -not $_.visible -or -not $_.enabled -or $_.pid -ne $targetPid -or $_.thread_id -ne [int]$threadId -or $_.parent -cne ('0x{0:X}' -f $targetHwnd.ToInt64()) -or $_.root -ne $targetHwnd }).Count -eq 0
  $handlesValid=$keep.Count -eq 1 -and $delete.Count -eq 1 -and $keep[0].hwnd -ceq ([string]$expected.buttons.keep_backups_hwnd) -and $delete[0].hwnd -ceq ([string]$expected.buttons.delete_hwnd)
  if (-not $identityValid) { $result=[pscustomobject]@{ status='refused'; error='popup_identity_changed' } }
  elseif (-not $topologyValid) { $result=[pscustomobject]@{ status='refused'; error='popup_body_or_control_topology_changed' } }
  elseif (-not $boundaryValid -or -not $handlesValid) { $result=[pscustomobject]@{ status='refused'; error='popup_button_boundary_changed' } }
  else {
    [uint32]$keepPid=0
    $keepThread=[SGGUnitySceneBackupKeepUser32]::GetWindowThreadProcessId($keep[0].handle,[ref]$keepPid)
    $freshStart=''; try { $freshStart=(Get-Process -Id $targetPid -ErrorAction Stop).StartTime.ToUniversalTime().ToString('o') } catch {}
    $finalBindingValid=$freshStart -ceq ([string]$expected.process_start_utc) -and [int]$keepPid -eq $targetPid -and [int]$keepThread -eq [int]$threadId -and [SGGUnitySceneBackupKeepUser32]::IsWindow($targetHwnd) -and [SGGUnitySceneBackupKeepUser32]::IsWindowVisible($targetHwnd) -and [SGGUnitySceneBackupKeepUser32]::IsWindowEnabled($targetHwnd) -and (Get-Class $targetHwnd) -ceq '#32770' -and (Get-Text $targetHwnd) -ceq '${SCENE_BACKUP_DETECTED_TITLE}' -and [SGGUnitySceneBackupKeepUser32]::IsWindow($keep[0].handle) -and [SGGUnitySceneBackupKeepUser32]::IsWindowVisible($keep[0].handle) -and [SGGUnitySceneBackupKeepUser32]::IsWindowEnabled($keep[0].handle) -and [SGGUnitySceneBackupKeepUser32]::GetParent($keep[0].handle) -eq $targetHwnd -and [SGGUnitySceneBackupKeepUser32]::GetAncestor($keep[0].handle,2) -eq $targetHwnd -and [SGGUnitySceneBackupKeepUser32]::GetDlgCtrlID($keep[0].handle) -eq 1 -and (Get-Text $keep[0].handle) -ceq 'Keep Backups' -and (Get-Sha256 (Get-Text $body[0].handle)) -ceq ([string]$expected.body_sha256) -and $script:textReadsComplete
    if (-not $finalBindingValid) { $result=[pscustomobject]@{ status='refused'; error='exact_keep_button_binding_changed' } }
    else {
      [IntPtr]$sendResult=[IntPtr]::Zero
      $sent=[SGGUnitySceneBackupKeepUser32]::SendMessageTimeout($keep[0].handle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero,0x0002,1000,[ref]$sendResult)
      if ($sent -eq [IntPtr]::Zero) { $result=[pscustomobject]@{ status='error'; error='bm_click_failed_or_timed_out' } }
      else { $result=[pscustomobject]@{ status='message_submitted' } }
    }
  }
}
$result | ConvertTo-Json -Compress -Depth 4
`.trim();
}

function safeModeEnterPowerShellArgs(evidence: UnitySafeModeEnterEvidence): string[] {
  const identity = Buffer.from(JSON.stringify({
    process_name: evidence.process_name,
    process_start_utc: evidence.process_start_utc,
    window_thread_id: evidence.window_thread_id,
    class_name: evidence.class_name,
    evidence_scope: evidence.evidence_scope,
    body_sha256: evidence.body_sha256,
    control_topology_sha256: evidence.control_topology_sha256,
    visible_control_count: evidence.visible_control_count,
    enter_safe_mode_hwnd: evidence.buttons.enter_safe_mode_hwnd,
    ignore_hwnd: evidence.buttons.ignore_hwnd,
    quit_hwnd: evidence.buttons.quit_hwnd,
  }), "utf8").toString("base64");
  return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", safeModeEnterPowerShellScript(evidence.hwnd.slice(2), evidence.pid, identity)];
}

function safeModeEnterPowerShellScript(hwndHex: string, pid: number, identityBase64: string): string {
  return `
$ErrorActionPreference = 'Stop'
$targetHwnd = [IntPtr]([Convert]::ToInt64('${hwndHex}', 16))
$targetPid = ${pid}
$expected = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${identityBase64}')))
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SGGUnitySafeModeEnterUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
  [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
"@
Add-Type -TypeDefinition $source
$script:textReadsComplete=$true
function Get-Text([IntPtr]$handle) {
  $b=New-Object System.Text.StringBuilder 1024; [IntPtr]$r=[IntPtr]::Zero
  $sent=[SGGUnitySafeModeEnterUser32]::SendMessageTimeoutText($handle,0x000D,[IntPtr]$b.Capacity,$b,0x0002,100,[ref]$r)
  if ($sent -eq [IntPtr]::Zero -or $r.ToInt64() -ge ($b.Capacity - 1)) { $script:textReadsComplete=$false; return '' }
  $b.ToString()
}
function Get-Class([IntPtr]$handle) { $b=New-Object System.Text.StringBuilder 256; [void][SGGUnitySafeModeEnterUser32]::GetClassName($handle,$b,$b.Capacity); $b.ToString() }
function Get-Sha256([string]$value) { $sha=[Security.Cryptography.SHA256]::Create(); try { 'sha256:' + ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))).Replace('-','').ToLowerInvariant()) } finally { $sha.Dispose() } }
$result=$null
if (-not [SGGUnitySafeModeEnterUser32]::IsWindow($targetHwnd) -or -not [SGGUnitySafeModeEnterUser32]::IsWindowVisible($targetHwnd) -or -not [SGGUnitySafeModeEnterUser32]::IsWindowEnabled($targetHwnd)) {
  $result=[pscustomobject]@{ status='refused'; error='target_window_not_found_or_disabled' }
} else {
  [uint32]$windowPid=0
  $threadId=[SGGUnitySafeModeEnterUser32]::GetWindowThreadProcessId($targetHwnd,[ref]$windowPid)
  $processName=''; $processStartUtc=''
  try { $p=Get-Process -Id ([int]$windowPid) -ErrorAction Stop; $processName=$p.ProcessName; $processStartUtc=$p.StartTime.ToUniversalTime().ToString('o') } catch {}
  $controls=New-Object System.Collections.Generic.List[object]
  [void][SGGUnitySafeModeEnterUser32]::EnumChildWindows($targetHwnd,[SGGUnitySafeModeEnterUser32+EnumWindowsProc]{
    param([IntPtr]$child,[IntPtr]$unused)
    [uint32]$childPid=0
    $childThreadId=[SGGUnitySafeModeEnterUser32]::GetWindowThreadProcessId($child,[ref]$childPid)
    $controls.Add([pscustomobject]@{
      hwnd=('0x{0:X}' -f $child.ToInt64()); handle=$child; parent=('0x{0:X}' -f ([SGGUnitySafeModeEnterUser32]::GetParent($child)).ToInt64()); root=[SGGUnitySafeModeEnterUser32]::GetAncestor($child,2)
      pid=[int]$childPid; thread_id=[int]$childThreadId; visible=[SGGUnitySafeModeEnterUser32]::IsWindowVisible($child); enabled=[SGGUnitySafeModeEnterUser32]::IsWindowEnabled($child)
      control_id=[SGGUnitySafeModeEnterUser32]::GetDlgCtrlID($child); class_name=(Get-Class $child); text=(Get-Text $child)
    }) | Out-Null
    return $true
  },[IntPtr]::Zero)
  $buttons=@($controls | Where-Object { $_.class_name -ceq 'Button' })
  $enter=@($buttons | Where-Object { $_.text -ceq 'Enter Safe Mode' })
  $ignore=@($buttons | Where-Object { $_.text -ceq 'Ignore' })
  $quit=@($buttons | Where-Object { $_.text -ceq 'Quit' })
  $bodyText=@($controls | Where-Object { $_.class_name -cne 'Button' -and $_.text } | ForEach-Object { $_.text }) -join ([char]10)
  $topologyLines=@($controls | Sort-Object { $_.hwnd.ToLowerInvariant() } | ForEach-Object {
    $visible=if ($_.visible) { 1 } else { 0 }; $enabled=if ($_.enabled) { 1 } else { 0 }
    '{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}' -f $_.hwnd.ToLowerInvariant(),$_.parent.ToLowerInvariant(),$_.pid,$_.thread_id,$visible,$enabled,$_.control_id,$_.class_name,$_.text
  })
  $topologyText=$topologyLines -join ([char]10)
  $expectedProcessName=([string]$expected.process_name).Replace('.exe','')
  $identityValid=[int]$windowPid -eq $targetPid -and $processName -ceq $expectedProcessName -and $processStartUtc -ceq ([string]$expected.process_start_utc) -and [int]$threadId -eq [int]$expected.window_thread_id -and (Get-Class $targetHwnd) -ceq '#32770' -and (Get-Text $targetHwnd) -ceq 'Enter Safe Mode?'
  $topologyValid=$controls.Count -eq [int]$expected.visible_control_count -and (Get-Sha256 $bodyText) -ceq ([string]$expected.body_sha256) -and (Get-Sha256 $topologyText) -ceq ([string]$expected.control_topology_sha256)
  $boundaryValid=@($controls | Where-Object { -not $_.visible -or $_.pid -ne $targetPid -or $_.thread_id -ne [int]$threadId -or $_.parent -cne ('0x{0:X}' -f $targetHwnd.ToInt64()) -or $_.root -ne $targetHwnd }).Count -eq 0
  $buttonsValid=$buttons.Count -eq 3 -and $enter.Count -eq 1 -and $ignore.Count -eq 1 -and $quit.Count -eq 1 -and @($buttons | Where-Object { -not $_.enabled }).Count -eq 0
  $handlesValid=$enter.Count -eq 1 -and $ignore.Count -eq 1 -and $quit.Count -eq 1 -and $enter[0].hwnd -ceq ([string]$expected.enter_safe_mode_hwnd) -and $ignore[0].hwnd -ceq ([string]$expected.ignore_hwnd) -and $quit[0].hwnd -ceq ([string]$expected.quit_hwnd)
  if (-not $script:textReadsComplete) { $result=[pscustomobject]@{ status='refused'; error='popup_text_read_incomplete' } }
  elseif (-not $identityValid) { $result=[pscustomobject]@{ status='refused'; error='popup_identity_changed' } }
  elseif (-not $topologyValid) { $result=[pscustomobject]@{ status='refused'; error='popup_body_or_control_topology_changed' } }
  elseif (-not $boundaryValid -or -not $buttonsValid -or -not $handlesValid) { $result=[pscustomobject]@{ status='refused'; error='popup_button_boundary_changed' } }
  else {
    $buttonText=Get-Text $enter[0].handle
    if (-not $script:textReadsComplete) { $result=[pscustomobject]@{ status='refused'; error='popup_text_read_incomplete' } }
    elseif ($buttonText -cne 'Enter Safe Mode') { $result=[pscustomobject]@{ status='refused'; error='popup_button_boundary_changed' } }
    else {
      [IntPtr]$sendResult=[IntPtr]::Zero
      $sent=[SGGUnitySafeModeEnterUser32]::SendMessageTimeout($enter[0].handle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero,0x0002,1000,[ref]$sendResult)
      if ($sent -eq [IntPtr]::Zero) { $result=[pscustomobject]@{ status='error'; error='bm_click_failed_or_timed_out' } }
      else { $result=[pscustomobject]@{ status='message_submitted' } }
    }
  }
}
$result | ConvertTo-Json -Compress -Depth 4
`.trim();
}

function parseSafeModeEnterInteraction(output: string): UnitySafeModeEnterInteraction | undefined {
  try {
    const raw = JSON.parse(output.trim()) as Record<string, unknown>;
    if (raw.status !== "message_submitted" && raw.status !== "refused" && raw.status !== "error") return undefined;
    const attempted = raw.status === "message_submitted" || raw.error === "bm_click_failed_or_timed_out";
    return {
      status: raw.status,
      interaction_attempted: attempted,
      delivery_state: raw.status === "message_submitted" ? "message_submitted" : attempted ? "unknown_after_send_attempt" : "not_attempted",
      ...(typeof raw.error === "string" ? { error: raw.error.slice(0, 200) } : {}),
    };
  } catch {
    return undefined;
  }
}

function scopedRegistryImportClosePowerShellArgs(evidence: UnityScopedRegistryImportCloseEvidence): string[] {
  const identity = Buffer.from(JSON.stringify({
    process_name: evidence.process_name,
    process_start_utc: evidence.process_start_utc,
    window_thread_id: evidence.window_thread_id,
    class_name: evidence.class_name,
    body_sha256: evidence.body_sha256,
    visible_control_count: evidence.visible_control_count,
    learn_more_button_hwnd: evidence.learn_more_button_hwnd,
    close_button_hwnd: evidence.close_button_hwnd,
  }), "utf8").toString("base64");
  return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", scopedRegistryImportClosePowerShellScript(evidence.hwnd.slice(2), evidence.pid, identity)];
}

function scopedRegistryImportClosePowerShellScript(hwndHex: string, pid: number, identityBase64: string): string {
  return `
$ErrorActionPreference = 'Stop'
$targetHwnd = [IntPtr]([Convert]::ToInt64('${hwndHex}', 16))
$targetPid = ${pid}
$expected = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${identityBase64}')))
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SGGUnityScopedRegistryCloseUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
  [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
"@
Add-Type -TypeDefinition $source
$script:textReadsComplete=$true
function Get-Text([IntPtr]$handle) {
  $b=New-Object System.Text.StringBuilder 1024; [IntPtr]$r=[IntPtr]::Zero
  $sent=[SGGUnityScopedRegistryCloseUser32]::SendMessageTimeoutText($handle,0x000D,[IntPtr]$b.Capacity,$b,0x0002,100,[ref]$r)
  if ($sent -eq [IntPtr]::Zero -or $r.ToInt64() -ge ($b.Capacity - 1)) { $script:textReadsComplete=$false; return '' }
  $b.ToString()
}
function Get-Class([IntPtr]$handle) { $b=New-Object System.Text.StringBuilder 256; [void][SGGUnityScopedRegistryCloseUser32]::GetClassName($handle,$b,$b.Capacity); $b.ToString() }
$result=$null
if (-not [SGGUnityScopedRegistryCloseUser32]::IsWindow($targetHwnd) -or -not [SGGUnityScopedRegistryCloseUser32]::IsWindowVisible($targetHwnd) -or -not [SGGUnityScopedRegistryCloseUser32]::IsWindowEnabled($targetHwnd)) {
  $result=[pscustomobject]@{ status='refused'; error='target_window_not_found_or_disabled' }
} else {
  [uint32]$windowPid=0
  $threadId=[SGGUnityScopedRegistryCloseUser32]::GetWindowThreadProcessId($targetHwnd,[ref]$windowPid)
  $processName=''; $processStartUtc=''
  try { $p=Get-Process -Id ([int]$windowPid) -ErrorAction Stop; $processName=$p.ProcessName; $processStartUtc=$p.StartTime.ToUniversalTime().ToString('o') } catch {}
  $controls=New-Object System.Collections.Generic.List[object]
  [void][SGGUnityScopedRegistryCloseUser32]::EnumChildWindows($targetHwnd,[SGGUnityScopedRegistryCloseUser32+EnumWindowsProc]{
    param([IntPtr]$child,[IntPtr]$unused)
    [uint32]$childPid=0
    $childThreadId=[SGGUnityScopedRegistryCloseUser32]::GetWindowThreadProcessId($child,[ref]$childPid)
    $controls.Add([pscustomobject]@{
      hwnd=('0x{0:X}' -f $child.ToInt64()); handle=$child; text=(Get-Text $child); class_name=(Get-Class $child)
      visible=[SGGUnityScopedRegistryCloseUser32]::IsWindowVisible($child); enabled=[SGGUnityScopedRegistryCloseUser32]::IsWindowEnabled($child)
      pid=[int]$childPid; thread_id=[int]$childThreadId; parent=[SGGUnityScopedRegistryCloseUser32]::GetParent($child); root=[SGGUnityScopedRegistryCloseUser32]::GetAncestor($child,2)
    }) | Out-Null
    return $true
  },[IntPtr]::Zero)
  $texts=@($controls | Where-Object { $_.text } | ForEach-Object { $_.text.Trim() })
  $body=@($controls | Where-Object { $_.class_name -cne 'Button' -and $_.text })
  $buttons=@($controls | Where-Object { $_.class_name -ceq 'Button' })
  $learnMore=@($buttons | Where-Object { $_.text.Trim() -ceq 'Learn More' })
  $close=@($buttons | Where-Object { $_.text.Trim() -ceq 'Close' })
  $sha=[Security.Cryptography.SHA256]::Create()
  try { $bodyHash=if ($body.Count -eq 1) { 'sha256:' + ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($body[0].text.Trim()))).Replace('-','').ToLowerInvariant()) } else { '' } } finally { $sha.Dispose() }
  $identityValid=[int]$windowPid -eq $targetPid -and $processName -ceq ([string]$expected.process_name) -and $processStartUtc -ceq ([string]$expected.process_start_utc) -and [int]$threadId -eq [int]$expected.window_thread_id -and (Get-Class $targetHwnd) -ceq ([string]$expected.class_name) -and (Get-Text $targetHwnd) -ceq 'Importing a scoped registry'
  $topologyValid=$controls.Count -eq [int]$expected.visible_control_count -and $texts.Count -eq 3 -and $bodyHash -ceq ([string]$expected.body_sha256) -and $buttons.Count -eq 2 -and $learnMore.Count -eq 1 -and $close.Count -eq 1
  $boundaryValid=@($controls | Where-Object { -not $_.visible -or $_.pid -ne $targetPid -or $_.thread_id -ne [int]$threadId -or $_.parent -ne $targetHwnd -or $_.root -ne $targetHwnd }).Count -eq 0 -and @($buttons | Where-Object { -not $_.enabled }).Count -eq 0
  $handlesValid=$learnMore.Count -eq 1 -and $close.Count -eq 1 -and $learnMore[0].hwnd -ceq ([string]$expected.learn_more_button_hwnd) -and $close[0].hwnd -ceq ([string]$expected.close_button_hwnd)
  if (-not $script:textReadsComplete) { $result=[pscustomobject]@{ status='refused'; error='popup_text_read_incomplete' } }
  elseif (-not $identityValid) { $result=[pscustomobject]@{ status='refused'; error='popup_identity_changed' } }
  elseif (-not $topologyValid) { $result=[pscustomobject]@{ status='refused'; error='popup_body_or_control_topology_changed' } }
  elseif (-not $boundaryValid -or -not $handlesValid) { $result=[pscustomobject]@{ status='refused'; error='popup_button_boundary_changed' } }
  else {
    $buttonText=Get-Text $close[0].handle
    if (-not $script:textReadsComplete) { $result=[pscustomobject]@{ status='refused'; error='popup_text_read_incomplete' } }
    elseif ($buttonText.Trim() -cne 'Close') { $result=[pscustomobject]@{ status='refused'; error='popup_button_boundary_changed' } }
    else {
      [IntPtr]$sendResult=[IntPtr]::Zero
      $sent=[SGGUnityScopedRegistryCloseUser32]::SendMessageTimeout($close[0].handle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero,0x0002,1000,[ref]$sendResult)
      if ($sent -eq [IntPtr]::Zero) { $result=[pscustomobject]@{ status='error'; error='bm_click_failed_or_timed_out' } }
      else { $result=[pscustomobject]@{ status='message_sent' } }
    }
  }
}
$result | ConvertTo-Json -Compress -Depth 4
`.trim();
}

function parseScopedRegistryImportCloseInteraction(output: string): UnityScopedRegistryImportInteraction | undefined {
  try {
    const raw = JSON.parse(output.trim()) as Record<string, unknown>;
    if (raw.status !== "message_sent" && raw.status !== "refused" && raw.status !== "error") return undefined;
    const interactionAttempted = raw.status === "message_sent" || raw.error === "bm_click_failed_or_timed_out";
    return {
      status: raw.status,
      interaction_attempted: interactionAttempted,
      delivery_state: raw.status === "message_sent" ? "message_sent" : interactionAttempted ? "unknown_after_send_attempt" : "not_attempted",
      ...(typeof raw.error === "string" ? { error: raw.error.slice(0, 200) } : {}),
    };
  } catch {
    return undefined;
  }
}

function externalSceneReloadPowerShellArgs(evidence: UnityExternalSceneReloadEvidence, action: UnityExternalSceneReloadAction): string[] {
  const scenePathsBase64 = Buffer.from(JSON.stringify(evidence.scene_paths), "utf8").toString("base64");
  const identityBase64 = Buffer.from(JSON.stringify({
    evidence_scope: evidence.evidence_scope,
    process_start_utc: evidence.process_start_utc,
    window_thread_id: evidence.window_thread_id,
    class_name: evidence.class_name,
    reload_button_hwnd: evidence.buttons.reload_hwnd,
    ignore_button_hwnd: evidence.buttons.ignore_hwnd,
  }), "utf8").toString("base64");
  return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", externalSceneReloadPowerShellScript(evidence.hwnd.slice(2), evidence.pid, scenePathsBase64, identityBase64, action)];
}

function projectDowngradePowerShellArgs(evidence: UnityProjectDowngradeEvidence): string[] {
  const identity = Buffer.from(JSON.stringify({
    process_name: evidence.process_name,
    process_start_utc: evidence.process_start_utc,
    window_thread_id: evidence.window_thread_id,
    class_name: evidence.class_name,
    source_version: evidence.source_version,
    target_version: evidence.target_version,
    body_sha256: evidence.body_sha256,
    continue_hwnd: evidence.buttons.continue_hwnd,
    quit_hwnd: evidence.buttons.quit_hwnd,
  }), "utf8").toString("base64");
  return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", projectDowngradePowerShellScript(evidence.hwnd.slice(2), evidence.pid, identity)];
}

function projectDowngradePowerShellScript(hwndHex: string, pid: number, identityBase64: string): string {
  return `
$ErrorActionPreference = 'Stop'
$targetHwnd = [IntPtr]([Convert]::ToInt64('${hwndHex}', 16))
$targetPid = ${pid}
$expected = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${identityBase64}')))
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class SGGUnityProjectDowngradeUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
  [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
"@
Add-Type -TypeDefinition $source
$script:textReadsComplete=$true
function Get-Text([IntPtr]$handle) {
  $b=New-Object System.Text.StringBuilder 1024; [IntPtr]$r=[IntPtr]::Zero
  $sent=[SGGUnityProjectDowngradeUser32]::SendMessageTimeoutText($handle,0x000D,[IntPtr]$b.Capacity,$b,0x0002,100,[ref]$r)
  if ($sent -eq [IntPtr]::Zero -or $r.ToInt64() -ge ($b.Capacity - 1)) { $script:textReadsComplete=$false; return '' }
  $b.ToString()
}
function Get-Class([IntPtr]$handle) { $b=New-Object System.Text.StringBuilder 256; [void][SGGUnityProjectDowngradeUser32]::GetClassName($handle,$b,$b.Capacity); $b.ToString() }
function Test-BoundButton([IntPtr]$handle,[string]$expectedHwnd,[string]$expectedText,[int]$expectedPid,[int]$expectedThreadId,[IntPtr]$expectedParent) {
  if (-not [SGGUnityProjectDowngradeUser32]::IsWindow($handle) -or -not [SGGUnityProjectDowngradeUser32]::IsWindowVisible($handle) -or -not [SGGUnityProjectDowngradeUser32]::IsWindowEnabled($handle)) { return $false }
  if (('0x{0:X}' -f $handle.ToInt64()) -cne $expectedHwnd -or (Get-Class $handle) -cne 'Button' -or (Get-Text $handle) -cne $expectedText) { return $false }
  if ([SGGUnityProjectDowngradeUser32]::GetParent($handle) -ne $expectedParent -or [SGGUnityProjectDowngradeUser32]::GetAncestor($handle,2) -ne $expectedParent) { return $false }
  [uint32]$childPid=0
  $childThreadId=[SGGUnityProjectDowngradeUser32]::GetWindowThreadProcessId($handle,[ref]$childPid)
  return [int]$childPid -eq $expectedPid -and [int]$childThreadId -eq $expectedThreadId
}
$result = $null
if (-not [SGGUnityProjectDowngradeUser32]::IsWindow($targetHwnd) -or -not [SGGUnityProjectDowngradeUser32]::IsWindowVisible($targetHwnd)) {
  $result = [pscustomobject]@{ status='refused'; error='target_window_not_found' }
} else {
  [uint32]$windowPid=0
  $threadId=[SGGUnityProjectDowngradeUser32]::GetWindowThreadProcessId($targetHwnd,[ref]$windowPid)
  $processName=''; $processStartUtc=''
  try { $p=Get-Process -Id ([int]$windowPid) -ErrorAction Stop; $processName=$p.ProcessName; $processStartUtc=$p.StartTime.ToUniversalTime().ToString('o') } catch {}
  $buttons=New-Object System.Collections.Generic.List[object]
  $body=New-Object System.Collections.Generic.List[string]
  [void][SGGUnityProjectDowngradeUser32]::EnumChildWindows($targetHwnd,[SGGUnityProjectDowngradeUser32+EnumWindowsProc]{
    param([IntPtr]$child,[IntPtr]$unused)
    $text=Get-Text $child; $class=Get-Class $child
    if ($class -ceq 'Button') { $buttons.Add([pscustomobject]@{ hwnd=('0x{0:X}' -f $child.ToInt64()); handle=$child; text=$text }) | Out-Null }
    elseif ($text) { $body.Add($text) | Out-Null }
    return $true
  },[IntPtr]::Zero)
  $bodyText=@($body) -join ([char]10)
  $sha=[Security.Cryptography.SHA256]::Create()
  try { $bodyHash='sha256:' + ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($bodyText))).Replace('-','').ToLowerInvariant()) } finally { $sha.Dispose() }
  $continue=@($buttons | Where-Object { $_.text -ceq 'Continue' })
  $quit=@($buttons | Where-Object { $_.text -ceq 'Quit' })
  $identityValid=[int]$windowPid -eq $targetPid -and $processName -ceq ([string]$expected.process_name) -and $processStartUtc -ceq ([string]$expected.process_start_utc) -and [int]$threadId -eq [int]$expected.window_thread_id -and (Get-Class $targetHwnd) -ceq ([string]$expected.class_name) -and (Get-Text $targetHwnd) -ceq 'Project Downgrade Required'
  $bodyValid=$bodyHash -ceq ([string]$expected.body_sha256) -and $bodyText.Contains([string]$expected.source_version) -and $bodyText.Contains([string]$expected.target_version)
  $buttonsValid=$buttons.Count -eq 2 -and $continue.Count -eq 1 -and $quit.Count -eq 1 -and $continue[0].hwnd -ceq ([string]$expected.continue_hwnd) -and $quit[0].hwnd -ceq ([string]$expected.quit_hwnd)
  if (-not $script:textReadsComplete) { $result=[pscustomobject]@{ status='refused'; error='popup_text_read_incomplete' } }
  elseif (-not $identityValid) { $result=[pscustomobject]@{ status='refused'; error='popup_identity_changed' } }
  elseif (-not $bodyValid) { $result=[pscustomobject]@{ status='refused'; error='popup_body_changed' } }
  elseif (-not $buttonsValid) { $result=[pscustomobject]@{ status='refused'; error='popup_buttons_changed' } }
  else {
    $buttonBoundaryValid=(Test-BoundButton $continue[0].handle ([string]$expected.continue_hwnd) 'Continue' $targetPid ([int]$threadId) $targetHwnd) -and (Test-BoundButton $quit[0].handle ([string]$expected.quit_hwnd) 'Quit' $targetPid ([int]$threadId) $targetHwnd)
    if (-not $script:textReadsComplete) { $result=[pscustomobject]@{ status='refused'; error='popup_text_read_incomplete' } }
    elseif (-not $buttonBoundaryValid) { $result=[pscustomobject]@{ status='refused'; error='popup_button_boundary_changed' } }
    else {
      [IntPtr]$sendResult=[IntPtr]::Zero
      $sent=[SGGUnityProjectDowngradeUser32]::SendMessageTimeout($continue[0].handle,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero,0x0002,1000,[ref]$sendResult)
      if ($sent -eq [IntPtr]::Zero) { $result=[pscustomobject]@{ status='error'; error='bm_click_failed_or_timed_out'; button_text='Continue' } }
      else { $result=[pscustomobject]@{ status='message_submitted'; button_text='Continue' } }
    }
  }
}
$result | ConvertTo-Json -Compress -Depth 4
`.trim();
}

function parseProjectDowngradeInteraction(output: string): { status: "message_submitted" | "refused" | "error"; buttonText?: "Continue"; error?: string } | undefined {
  try {
    const raw = JSON.parse(output.trim()) as Record<string, unknown>;
    if (raw.status !== "message_submitted" && raw.status !== "refused" && raw.status !== "error") return undefined;
    return { status: raw.status, ...(raw.button_text === "Continue" ? { buttonText: "Continue" as const } : {}), ...(typeof raw.error === "string" ? { error: raw.error.slice(0, 200) } : {}) };
  } catch {
    return undefined;
  }
}

function externalSceneReloadPowerShellScript(hwndHex: string, pid: number, scenePathsBase64: string, identityBase64: string, action: UnityExternalSceneReloadAction): string {
  return `
$ErrorActionPreference = 'Stop'
$targetHwnd = [IntPtr]([Convert]::ToInt64('${hwndHex}', 16))
$targetPid = ${pid}
$targetAction = '${action}'
$expectedScenePaths = @(ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${scenePathsBase64}'))))
$expectedIdentity = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${identityBase64}')))
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class SGGUnityExternalSceneReloadUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
  [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
"@
Add-Type -TypeDefinition $source

$script:textReadsComplete = $true
function Get-WindowTextValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 1024
  [IntPtr]$messageResult = [IntPtr]::Zero
  $sent = [SGGUnityExternalSceneReloadUser32]::SendMessageTimeoutText($handle, 0x000D, [IntPtr]$builder.Capacity, $builder, 0x0002, 100, [ref]$messageResult)
  if ($sent -eq [IntPtr]::Zero -or $messageResult.ToInt64() -ge ($builder.Capacity - 1)) { $script:textReadsComplete = $false; return '' }
  return $builder.ToString()
}

function Get-ClassNameValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 256
  [void][SGGUnityExternalSceneReloadUser32]::GetClassName($handle, $builder, $builder.Capacity)
  return $builder.ToString()
}

$result = $null
if (-not [SGGUnityExternalSceneReloadUser32]::IsWindow($targetHwnd) -or -not [SGGUnityExternalSceneReloadUser32]::IsWindowVisible($targetHwnd)) {
  $result = [pscustomobject]@{ status = 'refused'; error = 'target_window_not_found' }
} else {
  [uint32]$windowPid = 0
  $windowThreadId = [SGGUnityExternalSceneReloadUser32]::GetWindowThreadProcessId($targetHwnd, [ref]$windowPid)
  $processName = ''
  $processStartUtc = ''
  try {
    $process = Get-Process -Id ([int]$windowPid) -ErrorAction Stop
    $processName = $process.ProcessName
    $processStartUtc = $process.StartTime.ToUniversalTime().ToString('o')
  } catch {
    $processName = ''
    $processStartUtc = ''
  }
  $title = Get-WindowTextValue $targetHwnd
  $windowClass = Get-ClassNameValue $targetHwnd
  $childTexts = New-Object System.Collections.Generic.List[string]
  $childControls = New-Object System.Collections.Generic.List[object]
  $reloadButtons = New-Object System.Collections.Generic.List[IntPtr]
  $ignoreButtons = New-Object System.Collections.Generic.List[IntPtr]
  [void][SGGUnityExternalSceneReloadUser32]::EnumChildWindows($targetHwnd, [SGGUnityExternalSceneReloadUser32+EnumWindowsProc]{
    param([IntPtr]$childHandle, [IntPtr]$childParam)
    $text = Get-WindowTextValue $childHandle
    $className = Get-ClassNameValue $childHandle
    $visible = [SGGUnityExternalSceneReloadUser32]::IsWindowVisible($childHandle)
    $enabled = [SGGUnityExternalSceneReloadUser32]::IsWindowEnabled($childHandle)
    [uint32]$childPid = 0
    $childThreadId = [SGGUnityExternalSceneReloadUser32]::GetWindowThreadProcessId($childHandle, [ref]$childPid)
    $childControls.Add([pscustomobject]@{ hwnd = ('0x{0:X}' -f $childHandle.ToInt64()); parent = [SGGUnityExternalSceneReloadUser32]::GetParent($childHandle); pid = [int]$childPid; thread_id = [int]$childThreadId; class_name = $className; text = $text; visible = $visible; enabled = $enabled }) | Out-Null
    if ($visible -and $text) { $childTexts.Add($text) | Out-Null }
    if ($visible -and $enabled -and $className -ceq 'Button' -and $text -ceq 'Reload') { $reloadButtons.Add($childHandle) | Out-Null }
    if ($visible -and $enabled -and $className -ceq 'Button' -and $text -ceq 'Ignore') { $ignoreButtons.Add($childHandle) | Out-Null }
    return $true
  }, [IntPtr]::Zero)
  if (-not $script:textReadsComplete) {
    $result = [pscustomobject]@{ status = 'refused'; error = 'popup_text_read_incomplete'; reload_count = $reloadButtons.Count; ignore_count = $ignoreButtons.Count }
    $result | ConvertTo-Json -Compress -Depth 4
    return
  }
  $dialogText = (@($title) + @($childTexts)) -join [Environment]::NewLine
  $currentScenePaths = @([regex]::Matches($dialogText, 'Assets[\\/][^\r\n]*?\.unity\b') | ForEach-Object { $_.Value.Replace('\\', '/') } | Sort-Object -Unique)
  $expectedSorted = @($expectedScenePaths | ForEach-Object { [string]$_ } | Sort-Object -Unique)
  $sceneDifference = @(Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $currentScenePaths)
  $sparseEvidence = ([string]$expectedIdentity.evidence_scope) -ceq 'sparse_control_topology'
  $bindingValid = [int]$windowPid -eq $targetPid -and ($processName -eq 'Unity' -or $processName -eq 'Unity.exe') -and $processStartUtc -ceq ([string]$expectedIdentity.process_start_utc) -and [int]$windowThreadId -eq [int]$expectedIdentity.window_thread_id -and $windowClass -ceq ([string]$expectedIdentity.class_name) -and $title -ceq 'The open scene(s) have been modified externally' -and [SGGUnityExternalSceneReloadUser32]::IsWindowEnabled($targetHwnd)
  $bodyValid = $dialogText.Contains('The following open scene(s) have been changed on disk:') -and $dialogText.Contains('Do you want to reload the scene(s)?')
  $sparseControlsValid = $childControls.Count -eq 2 -and $reloadButtons.Count -eq 1 -and $ignoreButtons.Count -eq 1 -and @($childControls | Where-Object { -not $_.visible -or -not $_.enabled -or $_.class_name -cne 'Button' -or $_.parent -ne $targetHwnd -or $_.pid -ne $targetPid -or $_.thread_id -ne [int]$windowThreadId }).Count -eq 0
  if (-not $bindingValid) {
    $result = [pscustomobject]@{ status = 'refused'; error = 'popup_identity_changed'; reload_count = $reloadButtons.Count; ignore_count = $ignoreButtons.Count }
  } elseif ($sparseEvidence -and -not $sparseControlsValid) {
    $result = [pscustomobject]@{ status = 'refused'; error = 'sparse_popup_topology_changed'; reload_count = $reloadButtons.Count; ignore_count = $ignoreButtons.Count }
  } elseif (-not $sparseEvidence -and (-not $bodyValid -or $sceneDifference.Count -ne 0 -or $expectedSorted.Count -eq 0)) {
    $result = [pscustomobject]@{ status = 'refused'; error = 'popup_body_or_scene_paths_changed'; reload_count = $reloadButtons.Count; ignore_count = $ignoreButtons.Count }
  } elseif ($reloadButtons.Count -ne 1 -or $ignoreButtons.Count -ne 1) {
    $result = [pscustomobject]@{ status = 'refused'; error = 'missing_or_ambiguous_exact_buttons'; reload_count = $reloadButtons.Count; ignore_count = $ignoreButtons.Count }
  } elseif (('0x{0:X}' -f $reloadButtons[0].ToInt64()) -cne ([string]$expectedIdentity.reload_button_hwnd) -or ('0x{0:X}' -f $ignoreButtons[0].ToInt64()) -cne ([string]$expectedIdentity.ignore_button_hwnd)) {
    $result = [pscustomobject]@{ status = 'refused'; error = 'exact_button_identity_changed'; reload_count = 1; ignore_count = 1 }
  } else {
    $button = if ($targetAction -eq 'reload') { $reloadButtons[0] } else { $ignoreButtons[0] }
    $buttonText = if ($targetAction -eq 'reload') { 'Reload' } else { 'Ignore' }
    [uint32]$buttonPid = 0
    $buttonThreadId = [SGGUnityExternalSceneReloadUser32]::GetWindowThreadProcessId($button, [ref]$buttonPid)
    $buttonBindingValid = [int]$buttonPid -eq $targetPid -and [int]$buttonThreadId -eq [int]$windowThreadId -and [SGGUnityExternalSceneReloadUser32]::IsChild($targetHwnd, $button) -and [SGGUnityExternalSceneReloadUser32]::IsWindow($button) -and [SGGUnityExternalSceneReloadUser32]::IsWindowVisible($button) -and [SGGUnityExternalSceneReloadUser32]::IsWindowEnabled($button)
    $currentButtonText = Get-WindowTextValue $button
    if (-not $script:textReadsComplete) {
      $result = [pscustomobject]@{ status = 'refused'; error = 'popup_text_read_incomplete'; button_text = $buttonText; reload_count = 1; ignore_count = 1 }
    } elseif ($currentButtonText -cne $buttonText) {
      $result = [pscustomobject]@{ status = 'refused'; error = 'exact_button_identity_changed'; button_text = $buttonText; reload_count = 1; ignore_count = 1 }
    } elseif (-not $buttonBindingValid) {
      $result = [pscustomobject]@{ status = 'refused'; error = 'exact_button_process_thread_or_ancestry_changed'; button_text = $buttonText; reload_count = 1; ignore_count = 1 }
    } else {
      [IntPtr]$sendResult = [IntPtr]::Zero
      $sent = [SGGUnityExternalSceneReloadUser32]::SendMessageTimeout($button, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 1000, [ref]$sendResult)
      if ($sent -eq [IntPtr]::Zero) {
        $result = [pscustomobject]@{ status = 'error'; error = 'bm_click_failed_or_timed_out'; button_text = $buttonText; reload_count = 1; ignore_count = 1 }
      } else {
        $result = [pscustomobject]@{ status = 'message_sent'; button_text = $buttonText; reload_count = 1; ignore_count = 1 }
      }
    }
  }
}
$result | ConvertTo-Json -Compress -Depth 4
`.trim();
}

function parseExternalSceneReloadInteraction(output: string): { status: "message_sent" | "refused" | "error"; buttonText?: "Reload" | "Ignore"; reloadCount?: number; ignoreCount?: number; error?: string } | undefined {
  let parsed: unknown;
  try {
    parsed = JSON.parse(output.trim());
  } catch {
    return undefined;
  }
  if (typeof parsed !== "object" || parsed === null) return undefined;
  const raw = parsed as Record<string, unknown>;
  const status = raw.status === "message_sent" || raw.status === "refused" || raw.status === "error" ? raw.status : undefined;
  if (!status) return undefined;
  return {
    status,
    ...(raw.button_text === "Reload" || raw.button_text === "Ignore" ? { buttonText: raw.button_text } : {}),
    ...(typeof raw.reload_count === "number" && Number.isInteger(raw.reload_count) && raw.reload_count >= 0 ? { reloadCount: raw.reload_count } : {}),
    ...(typeof raw.ignore_count === "number" && Number.isInteger(raw.ignore_count) && raw.ignore_count >= 0 ? { ignoreCount: raw.ignore_count } : {}),
    ...(typeof raw.error === "string" ? { error: raw.error.slice(0, 200) } : {}),
  };
}

function recoveryPromptPowerShellArgs(pids: number[], dryRun: boolean, spec: ModalRespondSpec): string[] {
  const pidLiteral = pids.join(", ");
  const dryRunLiteral = dryRun ? "$true" : "$false";
  return ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", recoveryPromptPowerShellScript(pidLiteral, dryRunLiteral, spec)];
}

function recoveryPromptPowerShellScript(pidLiteral: string, dryRunLiteral: "$true" | "$false", spec: ModalRespondSpec): string {
  return `
$ErrorActionPreference = 'Stop'
$targetPids = @(${pidLiteral})
$dryRun = ${dryRunLiteral}
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class SGGUnityRecoveryPromptUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
"@
Add-Type -TypeDefinition $source

function Get-WindowTextValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 512
  [void][SGGUnityRecoveryPromptUser32]::GetWindowText($handle, $builder, $builder.Capacity)
  return $builder.ToString()
}

function Get-ClassNameValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 256
  [void][SGGUnityRecoveryPromptUser32]::GetClassName($handle, $builder, $builder.Capacity)
  return $builder.ToString()
}

$titlePattern = '${spec.titlePattern}'
$buttonPattern = "${spec.buttonPattern}"
$requiredTextPattern = '${spec.requiredTextPattern}'
$requiredClassPattern = '${spec.requiredClassPattern}'
$closeMethod = '${spec.closeMethod}'
$targets = @{}
foreach ($targetPid in $targetPids) { $targets[[int]$targetPid] = $true }
$seen = @{}
$results = New-Object System.Collections.Generic.List[object]

[void][SGGUnityRecoveryPromptUser32]::EnumWindows([SGGUnityRecoveryPromptUser32+EnumWindowsProc]{
  param([IntPtr]$handle, [IntPtr]$lparam)
  if (-not [SGGUnityRecoveryPromptUser32]::IsWindowVisible($handle)) { return $true }
  [uint32]$windowPid = 0
  [void][SGGUnityRecoveryPromptUser32]::GetWindowThreadProcessId($handle, [ref]$windowPid)
  if (-not $targets.ContainsKey([int]$windowPid)) { return $true }
  $title = Get-WindowTextValue $handle
  if (-not $title -or $title -notmatch $titlePattern) { return $true }
  $windowClass = Get-ClassNameValue $handle
  $seen[[int]$windowPid] = $true
  $buttons = New-Object System.Collections.Generic.List[object]
  $childTexts = New-Object System.Collections.Generic.List[string]

  [void][SGGUnityRecoveryPromptUser32]::EnumChildWindows($handle, [SGGUnityRecoveryPromptUser32+EnumWindowsProc]{
    param([IntPtr]$childHandle, [IntPtr]$childParam)
    $className = Get-ClassNameValue $childHandle
    $text = Get-WindowTextValue $childHandle
    if ($text) {
      $childTexts.Add($text) | Out-Null
    }
    if ($className -match '(?i)button' -and $text -and $text -match $buttonPattern) {
      $buttons.Add([pscustomobject]@{ hwnd = $childHandle; text = $text; class_name = $className }) | Out-Null
    }
    return $true
  }, [IntPtr]::Zero)

  $dialogText = (@($title) + @($childTexts)) -join [Environment]::NewLine
  if ($requiredClassPattern -and $windowClass -notmatch $requiredClassPattern) {
    $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'not_found'; window_title = $title; matched_button_count = $buttons.Count; matched_button_texts = @($buttons | ForEach-Object { $_.text }); error = 'required_window_class_not_found' }) | Out-Null
  } elseif ($requiredTextPattern -and $dialogText -notmatch $requiredTextPattern) {
    $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'not_found'; window_title = $title; matched_button_count = $buttons.Count; matched_button_texts = @($buttons | ForEach-Object { $_.text }); error = 'required_dialog_text_not_found' }) | Out-Null
  } elseif ($closeMethod -eq 'window_close') {
    if ($dryRun) {
      $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'would_click'; window_title = $title; button_text = 'Close'; close_method = 'window_close'; matched_button_count = $buttons.Count; matched_button_texts = @($buttons | ForEach-Object { $_.text }) }) | Out-Null
    } else {
      [IntPtr]$sendResult = [IntPtr]::Zero
      $sent = [SGGUnityRecoveryPromptUser32]::SendMessageTimeout($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 1000, [ref]$sendResult)
      if ($sent -eq [IntPtr]::Zero) {
        $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'error'; window_title = $title; button_text = 'Close'; close_method = 'window_close'; matched_button_count = $buttons.Count; error = 'wm_close_failed_or_timed_out' }) | Out-Null
      } else {
        $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'clicked'; window_title = $title; button_text = 'Close'; close_method = 'window_close'; matched_button_count = $buttons.Count }) | Out-Null
      }
    }
  } elseif ($buttons.Count -eq 0) {
    $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'not_found'; window_title = $title; matched_button_count = 0 }) | Out-Null
  } elseif ($buttons.Count -gt 1) {
    $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'ambiguous'; window_title = $title; matched_button_count = $buttons.Count; matched_button_texts = @($buttons | ForEach-Object { $_.text }) }) | Out-Null
  } elseif ($dryRun) {
    $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'would_click'; window_title = $title; button_text = $buttons[0].text; matched_button_count = 1 }) | Out-Null
  } else {
    [IntPtr]$sendResult = [IntPtr]::Zero
    $sent = [SGGUnityRecoveryPromptUser32]::SendMessageTimeout($buttons[0].hwnd, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero, 0x0002, 1000, [ref]$sendResult)
    if ($sent -eq [IntPtr]::Zero) {
      $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'error'; window_title = $title; button_text = $buttons[0].text; matched_button_count = 1; error = 'bm_click_failed_or_timed_out' }) | Out-Null
    } else {
      $results.Add([pscustomobject]@{ pid = [int]$windowPid; status = 'clicked'; window_title = $title; button_text = $buttons[0].text; matched_button_count = 1 }) | Out-Null
    }
  }
  return $true
}, [IntPtr]::Zero)

foreach ($targetPid in $targetPids) {
  if (-not $seen.ContainsKey([int]$targetPid)) {
    $results.Add([pscustomobject]@{ pid = [int]$targetPid; status = 'not_found'; error = 'recovery_window_not_found' }) | Out-Null
  }
}

$results | ConvertTo-Json -Compress -Depth 6
`.trim();
}

function parseRecoveryPromptClickResults(output: string, executable: string, targetPids: number[]): UnityRecoveryPromptClickResult[] {
  let parsed: unknown;
  try {
    parsed = JSON.parse(output.trim() || "[]");
  } catch (error) {
    const message = safeErrorMessage(error);
    return targetPids.map((pid) => ({ pid, status: "error", executable, error: `parse_failed: ${message}` }));
  }

  const rawResults = Array.isArray(parsed) ? parsed : [parsed];
  const results: UnityRecoveryPromptClickResult[] = [];
  for (const raw of rawResults) {
    if (typeof raw !== "object" || raw === null) {
      continue;
    }
    results.push(normalizeRecoveryPromptClickResult(raw as RawRecoveryPromptClickResult, executable));
  }

  const seen = new Set(results.map((result) => result.pid));
  for (const pid of targetPids) {
    if (!seen.has(pid)) {
      results.push({ pid, status: "not_found", executable, error: "no_result_from_powershell" });
    }
  }
  return results;
}

function normalizeRecoveryPromptClickResult(raw: RawRecoveryPromptClickResult, executable: string): UnityRecoveryPromptClickResult {
  const pid = numberOrZero(raw.pid);
  const status = recoveryPromptClickStatus(raw.status);
  const matchedButtonTexts = Array.isArray(raw.matched_button_texts)
    ? raw.matched_button_texts.filter((value): value is string => typeof value === "string").slice(0, 10)
    : undefined;
  return {
    pid,
    status,
    executable,
    ...(typeof raw.window_title === "string" ? { window_title: raw.window_title } : {}),
    ...(typeof raw.button_text === "string" ? { button_text: raw.button_text } : {}),
    ...(raw.close_method === "button_click" || raw.close_method === "window_close" ? { close_method: raw.close_method } : {}),
    ...(typeof raw.matched_button_count === "number" ? { matched_button_count: raw.matched_button_count } : {}),
    ...(matchedButtonTexts && matchedButtonTexts.length > 0 ? { matched_button_texts: matchedButtonTexts } : {}),
    ...(typeof raw.error === "string" ? { error: raw.error } : {}),
  };
}

function recoveryPromptClickStatus(value: unknown): UnityRecoveryPromptClickResult["status"] {
  return value === "would_click" || value === "clicked" || value === "not_found" || value === "ambiguous" || value === "error" ? value : "error";
}

function numberOrZero(value: unknown): number {
  return typeof value === "number" && Number.isInteger(value) && value > 0 ? value : 0;
}

function uniqueStrings(values: string[]): string[] {
  return [...new Set(values)];
}

function createUnityWindowScanBudget(scanBudgetMs = WINDOWS_TOOL_TIMEOUT_MS, now: () => number = () => performance.now()): UnityWindowScanBudget {
  const budgetMs = Math.max(1, Math.min(WINDOWS_TOOL_TIMEOUT_MS, Math.floor(scanBudgetMs)));
  const startedMs = now();
  return { budgetMs, startedMs, deadlineMs: startedMs + budgetMs, now };
}

function runWindowsTool(toolName: "tasklist.exe" | "powershell.exe", args: string[], options: UnityWindowDiagnosticsOptions, sharedBudget?: UnityWindowScanBudget): WindowsToolResult {
  const budget = sharedBudget ?? createUnityWindowScanBudget(options.scanBudgetMs, options.nowMonotonic);
  const execFileSync = options.execFileSync ?? defaultExecFileSync;
  const startedMs = budget.now();
  for (const executable of windowsToolCandidates(toolName)) {
    const timeout = helperExecutionTimeout(budget);
    if (timeout === undefined) return skippedWindowsToolResult(startedMs, budget, "scan_budget_exhausted");
    try {
      const rawOutput = execFileSync(executable, args, { encoding: null, timeout, windowsHide: true, stdio: "pipe", maxBuffer: WINDOWS_TOOL_MAX_OUTPUT_BYTES });
      const output = boundedToolBuffer(rawOutput);
      if (output.truncated) return failedWindowsToolResult(executable, output.buffer, Buffer.alloc(0), startedMs, budget, "helper_stdout_limit_exceeded", false, null, true);
      return successfulWindowsToolResult(executable, output.buffer, startedMs, budget);
    } catch (error) {
      if (isExecutableMissing(error)) {
        continue;
      }
      const captured = childProcessBuffers(error);
      const timedOut = childProcessTimedOut(error);
      return failedWindowsToolResult(
        executable,
        captured.stdout.buffer,
        captured.stderr.buffer,
        startedMs,
        budget,
        timedOut ? "helper_timeout" : captured.stdout.truncated || captured.stderr.truncated ? "helper_output_limit_exceeded" : "helper_failed",
        timedOut,
        childProcessExitCode(error),
        captured.stdout.truncated,
        captured.stderr.truncated,
      );
    }
  }
  return failedWindowsToolResult(undefined, Buffer.alloc(0), Buffer.alloc(0), startedMs, budget, `tool_not_found:${toolName}`, false, null);
}

async function runWindowsToolAsync(toolName: "tasklist.exe" | "powershell.exe", args: string[], budget: UnityWindowScanBudget): Promise<WindowsToolResult> {
  const startedMs = budget.now();
  for (const executable of windowsToolCandidates(toolName)) {
    const timeout = helperExecutionTimeout(budget);
    if (timeout === undefined) return skippedWindowsToolResult(startedMs, budget, "scan_budget_exhausted");
    const result = await new Promise<{ error: Error | null; stdout: string | Buffer; stderr: string | Buffer }>((resolve) => {
      nodeExecFile(executable, args, { encoding: null, timeout, windowsHide: true, maxBuffer: WINDOWS_TOOL_MAX_OUTPUT_BYTES }, (error, stdout, stderr) => {
          resolve({ error, stdout, stderr });
        });
      });
    if (!result.error) {
      const output = boundedToolBuffer(result.stdout);
      if (output.truncated) return failedWindowsToolResult(executable, output.buffer, Buffer.alloc(0), startedMs, budget, "helper_stdout_limit_exceeded", false, null, true);
      return successfulWindowsToolResult(executable, output.buffer, startedMs, budget);
    }
    if (isExecutableMissing(result.error)) continue;
    const stdout = boundedToolBuffer(result.stdout);
    const stderr = boundedToolBuffer(result.stderr);
    const timedOut = childProcessTimedOut(result.error);
    return failedWindowsToolResult(
      executable,
      stdout.buffer,
      stderr.buffer,
      startedMs,
      budget,
      timedOut ? "helper_timeout" : stdout.truncated || stderr.truncated ? "helper_output_limit_exceeded" : "helper_failed",
      timedOut,
      childProcessExitCode(result.error),
      stdout.truncated,
      stderr.truncated,
    );
  }
  return failedWindowsToolResult(undefined, Buffer.alloc(0), Buffer.alloc(0), startedMs, budget, `tool_not_found:${toolName}`, false, null);
}

function defaultExecFileSync(file: string, args: string[], options: { encoding: null; timeout: number; windowsHide: boolean; stdio: "pipe"; maxBuffer: number }): Buffer {
  return nodeExecFileSync(file, args, options);
}

function helperExecutionTimeout(budget: UnityWindowScanBudget): number | undefined {
  const remainingMs = Math.floor(budget.deadlineMs - budget.now() - WINDOWS_TOOL_CLEANUP_GRACE_MS);
  return remainingMs >= WINDOWS_TOOL_MINIMUM_BUDGET_MS ? remainingMs : undefined;
}

function successfulWindowsToolResult(executable: string, output: Buffer, startedMs: number, budget: UnityWindowScanBudget): WindowsToolResult {
  return {
    status: "ok",
    executable,
    output,
    observation: helperObservation({ status: "ok", elapsedMs: budget.now() - startedMs, stdout: output, cleanupStatus: "not_required" }),
  };
}

function failedWindowsToolResult(
  executable: string | undefined,
  stdout: Buffer,
  stderr: Buffer,
  startedMs: number,
  budget: UnityWindowScanBudget,
  errorCode: string,
  timedOut: boolean,
  exitCode: number | null,
  stdoutTruncated = false,
  stderrTruncated = false,
): WindowsToolResult {
  const cleanupStatus = timedOut ? process.platform === "win32" ? "owned_child_terminated" : "uncertain" : "not_required";
  return {
    status: "error",
    ...(executable ? { executable } : {}),
    output: stdout,
    observation: helperObservation({
      status: timedOut ? "timeout" : "error",
      elapsedMs: budget.now() - startedMs,
      stdout,
      stderr,
      timedOut,
      exitCode,
      errorCode,
      stdoutTruncated,
      stderrTruncated,
      cleanupStatus,
    }),
  };
}

function skippedWindowsToolResult(startedMs: number, budget: UnityWindowScanBudget, errorCode: string): WindowsToolResult {
  return {
    status: "skipped",
    output: Buffer.alloc(0),
    observation: helperObservation({ status: "skipped", elapsedMs: budget.now() - startedMs, stdout: Buffer.alloc(0), errorCode, cleanupStatus: "not_required" }),
  };
}

function helperObservation(input: {
  status: UnityWindowHelperObservation["status"];
  elapsedMs: number;
  stdout: Buffer;
  stderr?: Buffer;
  timedOut?: boolean;
  exitCode?: number | null;
  errorCode?: string;
  stdoutTruncated?: boolean;
  stderrTruncated?: boolean;
  cleanupStatus: UnityWindowHelperObservation["cleanup_status"];
}): UnityWindowHelperObservation {
  const stderr = input.stderr ?? Buffer.alloc(0);
  return {
    status: input.status,
    authoritative: false,
    elapsed_ms: Math.max(0, Math.round(input.elapsedMs)),
    exit_code: input.exitCode ?? (input.status === "ok" ? 0 : null),
    timed_out: input.timedOut === true,
    stdout_bytes: input.stdout.length,
    stderr_bytes: stderr.length,
    stdout_truncated: input.stdoutTruncated === true,
    stderr_truncated: input.stderrTruncated === true,
    parsed_row_count: 0,
    normalized_error_code: input.errorCode ?? null,
    last_stage: lastNativeScanStage(input.stdout),
    stdout_encoding: "unknown",
    cleanup_status: input.cleanupStatus,
  };
}

function boundedToolBuffer(value: unknown): { buffer: Buffer; truncated: boolean } {
  const buffer = Buffer.isBuffer(value) ? value : typeof value === "string" ? Buffer.from(value, "utf8") : Buffer.alloc(0);
  return buffer.length <= WINDOWS_TOOL_MAX_OUTPUT_BYTES
    ? { buffer, truncated: false }
    : { buffer: buffer.subarray(0, WINDOWS_TOOL_MAX_OUTPUT_BYTES), truncated: true };
}

function childProcessBuffers(error: unknown): { stdout: ReturnType<typeof boundedToolBuffer>; stderr: ReturnType<typeof boundedToolBuffer> } {
  const record = typeof error === "object" && error !== null ? error as { stdout?: unknown; stderr?: unknown } : {};
  return { stdout: boundedToolBuffer(record.stdout), stderr: boundedToolBuffer(record.stderr) };
}

function childProcessTimedOut(error: unknown): boolean {
  return typeof error === "object" && error !== null && (error as { code?: unknown }).code === "ETIMEDOUT";
}

function childProcessExitCode(error: unknown): number | null {
  const status = typeof error === "object" && error !== null ? (error as { status?: unknown; code?: unknown }).status : undefined;
  return typeof status === "number" && Number.isInteger(status) ? status : null;
}

function windowsToolCandidates(toolName: "tasklist.exe" | "powershell.exe"): string[] {
  if (process.platform === "win32") {
    if (toolName === "powershell.exe") {
      return [toolName, "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"];
    }
    return [toolName];
  }
  if (toolName === "powershell.exe") {
    return [toolName, "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"];
  }
  return [toolName, `/mnt/c/Windows/System32/${toolName}`];
}

function parseCsvRows(text: string): string[][] | undefined {
  const rows = text.split(/\r?\n/).map((line) => line.trim()).filter(Boolean).map(parseCsvRow);
  return rows.every((row): row is string[] => row !== undefined) ? rows : undefined;
}

function parseCsvRow(line: string): string[] | undefined {
  const values: string[] = [];
  let current = "";
  let quoted = false;
  let quoteClosed = false;

  for (let index = 0; index < line.length; index += 1) {
    const char = line[index];
    if (char === '"') {
      if (quoted && line[index + 1] === '"') {
        current += '"';
        index += 1;
      } else if (quoted) {
        quoted = false;
        quoteClosed = true;
      } else if (current.length === 0 && !quoteClosed) {
        quoted = true;
      } else {
        return undefined;
      }
    } else if (char === "," && !quoted) {
      if (!quoteClosed) return undefined;
      values.push(current);
      current = "";
      quoteClosed = false;
    } else {
      if (!quoted) return undefined;
      current += char;
    }
  }

  if (quoted || !quoteClosed) return undefined;
  values.push(current);
  return values;
}

function isExecutableMissing(error: unknown): boolean {
  return typeof error === "object" && error !== null && (error as { code?: unknown }).code === "ENOENT";
}

function safeErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
