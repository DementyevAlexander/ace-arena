export type BridgeBootstrapTraceEvent = {
  event: string;
};

export type BridgeBootstrapProcessIdentity = {
  unity_pid?: number | undefined;
  unity_process_start_utc?: string | undefined;
  status?: string | undefined;
};

export type BridgeBootstrapObservation = {
  contract: "sgg.unity_mcp.bridge_bootstrap_observation.v1";
  state: string;
  diagnostic_only: true;
  authoritative: false;
  reload_before_observed: boolean;
  did_reload_scripts_observed: boolean;
  runtime_initializer_observed: boolean;
  editor_update_observed: boolean;
  bootstrap_started: boolean;
  first_heartbeat_published: boolean;
  waiting_heartbeat_published: boolean;
  bootstrap_recovery_scheduled: boolean;
  heartbeat_retry_published: boolean;
  current_candidate_count: number;
  unregistered_process_count: number;
  fresh_process_without_registration: boolean;
  fresh_process_pid: number | null;
  fresh_process_start_utc: string | null;
  same_process_replacement_ready: boolean;
  operator_restart_recovery_observed: boolean;
  observed_events: string[];
  automatic_command_replay_attempted: false;
  automatic_process_restart_attempted: false;
  next_valid_actions: string[];
};

export function classifyBridgeBootstrapObservation(
  traceReadable: boolean,
  events: BridgeBootstrapTraceEvent[],
  failedProcess: BridgeBootstrapProcessIdentity,
  currentCandidates: BridgeBootstrapProcessIdentity[],
  unregisteredProcesses: BridgeBootstrapProcessIdentity[] = [],
): BridgeBootstrapObservation {
  const observedEvents = events.map((entry) => normalizeBridgeLifecycleEvent(entry.event)).filter((event): event is string => event !== undefined).slice(-24);
  const reloadBeforeObserved = observedEvents.includes("bridge.reload.before");
  const afterReload = reloadBeforeObserved
    ? observedEvents.slice(observedEvents.lastIndexOf("bridge.reload.before") + 1)
    : observedEvents;
  const didReloadScriptsObserved = afterReload.some((event) => event.startsWith("bridge.bootstrap.did_reload_scripts."));
  const runtimeInitializerObserved = afterReload.includes("bridge.bootstrap.runtime_initializer_observed")
    || afterReload.includes("bridge.bootstrap.did_reload_scripts.runtime_initializer_observed");
  const editorUpdateObserved = afterReload.includes("bridge.bootstrap.editor_update_observed");
  const bootstrapStarted = afterReload.includes("bridge.bootstrap.lifecycle_started")
    || afterReload.includes("bridge.bootstrap.start")
    || afterReload.includes("bridge.startup.start");
  const firstHeartbeatPublished = afterReload.includes("bridge.bootstrap.first_heartbeat_published");
  const waitingHeartbeatPublished = afterReload.includes("bridge.bootstrap.waiting_heartbeat_published");
  const bootstrapRecoveryScheduled = afterReload.includes("bridge.bootstrap.did_reload_scripts.recovery_scheduled")
    || afterReload.includes("bridge.bootstrap.recovery_scheduled")
    || afterReload.some((event) => event.startsWith("bridge.bootstrap.lifecycle_recovery_scheduled."));
  const heartbeatRetryPublished = afterReload.includes("bridge.bootstrap.heartbeat_retry_published");
  const sameProcessReplacementReady = currentCandidates.some((candidate) => isExactSameProcess(failedProcess, candidate) && candidate.status === "ready");
  const operatorRestartRecoveryObserved = currentCandidates.some((candidate) => isDifferentProcess(failedProcess, candidate) && candidate.status === "ready");
  const freshProcess = latestFreshProcess(failedProcess, unregisteredProcesses);

  let state = "unclassified_missing_replacement";
  if (sameProcessReplacementReady) {
    state = "same_process_replacement_ready";
  } else if (operatorRestartRecoveryObserved) {
    state = "operator_restart_recovery";
  } else if (freshProcess) {
    state = "fresh_process_registration_missing";
  } else if (!traceReadable) {
    state = "bootstrap_trace_unavailable";
  } else if (afterReload.some((event) => event.startsWith("bridge.bootstrap.first_heartbeat_failed."))) {
    state = "heartbeat_publication_failed";
  } else if (afterReload.some((event) => event.startsWith("bridge.bootstrap.lifecycle_failed."))
    || afterReload.includes("bridge.bootstrap.error")
    || afterReload.includes("bridge.startup.error")) {
    state = "bootstrap_failed";
  } else if (firstHeartbeatPublished) {
    state = "heartbeat_publication_registration_unverified";
  } else if (afterReload.includes("bridge.bootstrap.idle_gate.compiling_and_updating")) {
    state = "bootstrap_delayed_compiling_and_updating";
  } else if (afterReload.includes("bridge.bootstrap.idle_gate.compiling")) {
    state = "bootstrap_delayed_compiling";
  } else if (afterReload.includes("bridge.bootstrap.idle_gate.updating")) {
    state = "bootstrap_delayed_updating";
  } else if (bootstrapStarted) {
    state = "bootstrap_startup_in_progress";
  } else if (editorUpdateObserved) {
    state = "bootstrap_idle_gate_pending";
  } else if (runtimeInitializerObserved) {
    state = "editor_update_not_observed";
  } else if (didReloadScriptsObserved) {
    state = "initialize_on_load_not_observed";
  } else if (reloadBeforeObserved) {
    state = "post_reload_callback_not_observed";
  }

  return {
    contract: "sgg.unity_mcp.bridge_bootstrap_observation.v1",
    state,
    diagnostic_only: true,
    authoritative: false,
    reload_before_observed: reloadBeforeObserved,
    did_reload_scripts_observed: didReloadScriptsObserved,
    runtime_initializer_observed: runtimeInitializerObserved,
    editor_update_observed: editorUpdateObserved,
    bootstrap_started: bootstrapStarted,
    first_heartbeat_published: firstHeartbeatPublished,
    waiting_heartbeat_published: waitingHeartbeatPublished,
    bootstrap_recovery_scheduled: bootstrapRecoveryScheduled,
    heartbeat_retry_published: heartbeatRetryPublished,
    current_candidate_count: currentCandidates.length,
    unregistered_process_count: unregisteredProcesses.length,
    fresh_process_without_registration: freshProcess !== undefined,
    fresh_process_pid: freshProcess?.unity_pid ?? null,
    fresh_process_start_utc: freshProcess?.unity_process_start_utc ?? null,
    same_process_replacement_ready: sameProcessReplacementReady,
    operator_restart_recovery_observed: operatorRestartRecoveryObserved,
    observed_events: afterReload.slice(-24),
    automatic_command_replay_attempted: false,
    automatic_process_restart_attempted: false,
    next_valid_actions: nextActions(state),
  };
}

function normalizeBridgeLifecycleEvent(event: string): string | undefined {
  const fixedEvents = new Set([
    "bridge.reload.before",
    "bridge.reload.after",
    "bridge.bootstrap.did_reload_scripts.runtime_initializer_observed",
    "bridge.bootstrap.did_reload_scripts.runtime_initializer_not_observed",
    "bridge.bootstrap.did_reload_scripts.recovery_scheduled",
    "bridge.bootstrap.runtime_initializer_observed",
    "bridge.bootstrap.editor_update_observed",
    "bridge.bootstrap.idle_gate.compiling",
    "bridge.bootstrap.idle_gate.updating",
    "bridge.bootstrap.idle_gate.compiling_and_updating",
    "bridge.bootstrap.idle_gate.idle",
    "bridge.bootstrap.lifecycle_started",
    "bridge.bootstrap.start",
    "bridge.bootstrap.end",
    "bridge.bootstrap.error",
    "bridge.bootstrap.first_heartbeat_published",
    "bridge.bootstrap.waiting_heartbeat_published",
    "bridge.bootstrap.heartbeat_retry_published",
    "bridge.bootstrap.recovery_scheduled",
    "bridge.bootstrap.lifecycle_recovery_exhausted",
    "bridge.startup.deferred",
    "bridge.startup.waiting_for_idle",
    "bridge.startup.resume",
    "bridge.startup.start",
    "bridge.startup.end",
    "bridge.startup.error",
  ]);
  if (fixedEvents.has(event)) return event;
  if (/^bridge\.bootstrap\.lifecycle_failed\.(io|unauthorized|unexpected)$/.test(event)) return event;
  if (/^bridge\.bootstrap\.first_heartbeat_failed\.(io|unauthorized|canonical_path_dependency_missing|unexpected)$/.test(event)) return event;
  if (/^bridge\.bootstrap\.waiting_heartbeat_failed\.(io|unauthorized|canonical_path_dependency_missing|unexpected)$/.test(event)) return event;
  if (/^bridge\.bootstrap\.lifecycle_recovery_scheduled\.[1-3]$/.test(event)) return event;
  return undefined;
}

function latestFreshProcess(failedProcess: BridgeBootstrapProcessIdentity, candidates: BridgeBootstrapProcessIdentity[]): BridgeBootstrapProcessIdentity | undefined {
  if (!validProcessStart(failedProcess.unity_process_start_utc)) {
    return undefined;
  }
  const failedStart = Date.parse(failedProcess.unity_process_start_utc);
  return candidates
    .filter((candidate) => validPid(candidate.unity_pid)
      && validProcessStart(candidate.unity_process_start_utc)
      && isDifferentProcess(failedProcess, candidate)
      && Date.parse(candidate.unity_process_start_utc) > failedStart)
    .sort((left, right) => Date.parse(right.unity_process_start_utc!) - Date.parse(left.unity_process_start_utc!))[0];
}

function isExactSameProcess(left: BridgeBootstrapProcessIdentity, right: BridgeBootstrapProcessIdentity): boolean {
  return validPid(left.unity_pid)
    && left.unity_pid === right.unity_pid
    && validProcessStart(left.unity_process_start_utc)
    && validProcessStart(right.unity_process_start_utc)
    && Date.parse(left.unity_process_start_utc) === Date.parse(right.unity_process_start_utc);
}

function isDifferentProcess(left: BridgeBootstrapProcessIdentity, right: BridgeBootstrapProcessIdentity): boolean {
  if (validPid(left.unity_pid) && validPid(right.unity_pid) && left.unity_pid !== right.unity_pid) return true;
  return validProcessStart(left.unity_process_start_utc)
    && validProcessStart(right.unity_process_start_utc)
    && Date.parse(left.unity_process_start_utc) !== Date.parse(right.unity_process_start_utc);
}

function validPid(value: number | undefined): value is number {
  return typeof value === "number" && Number.isInteger(value) && value > 0;
}

function validProcessStart(value: string | undefined): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= 40 && Number.isFinite(Date.parse(value));
}

function nextActions(state: string): string[] {
  if (state === "operator_restart_recovery" || state === "same_process_replacement_ready") {
    return ["use_fresh_ready_instance_for_read_status", "verify_compile_barrier_before_claiming_completion", "do_not_replay_compile_or_test_write"];
  }
  if (state === "bootstrap_delayed_compiling" || state === "bootstrap_delayed_compiling_and_updating") {
    return ["wait_for_compilation_to_finish", "inspect_native_progress_and_modals", "retry_read_status_after_fresh_heartbeat", "do_not_repeat_compile_refresh"];
  }
  if (state === "bootstrap_delayed_updating") {
    return ["wait_for_asset_import_or_update_to_finish", "inspect_native_progress_and_modals", "retry_read_status_after_fresh_heartbeat", "do_not_repeat_refresh_or_import_work"];
  }
  if (state === "heartbeat_publication_failed") {
    return ["inspect_gateway_instance_directory_permissions", "call_diagnostics.report_problem", "do_not_restart_or_kill_unity_automatically"];
  }
  if (state === "fresh_process_registration_missing") {
    return ["inspect_fresh_process_bootstrap_trace_and_startup_log", "wait_for_fresh_ready_heartbeat", "call_diagnostics.report_problem", "do_not_restart_or_kill_unity_automatically", "do_not_replay_compile_or_test_write"];
  }
  return ["inspect_native_progress_modals_and_save_prompt", "call_diagnostics.report_problem", "wait_for_fresh_ready_heartbeat", "do_not_replay_compile_or_test_write", "do_not_restart_or_kill_unity_automatically"];
}
