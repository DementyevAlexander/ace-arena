import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const builtInCommandsPath = path.join(packageRoot, "Editor", "Commands", "BuiltInCommands.cs");
const consoleLogBufferPath = path.join(packageRoot, "Editor", "Commands", "ConsoleLogBuffer.cs");
const compileProgressWindowDetectorPath = path.join(packageRoot, "Editor", "Commands", "CompileProgressWindowDetector.cs");
const backgroundCompilerProgressPath = path.join(packageRoot, "Editor", "Commands", "BackgroundCompilerProgress.cs");
const bridgeRuntimeStatePath = path.join(packageRoot, "Editor", "Bridge", "BridgeRuntimeState.cs");
const bridgeRuntimePath = path.join(packageRoot, "Editor", "Bridge", "UnityBridgeRuntime.cs");
const bridgeServerPath = path.join(packageRoot, "Editor", "Bridge", "BridgeServer.cs");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");

describe("compile.wait_ready command source-contract", () => {
  it("declares a read-only bounded wait command in the Unity catalog", () => {
    const source = fs.readFileSync(builtInCommandsPath, "utf8");

    expect(source).toContain('"compile.wait_ready"');
    expect(source).toContain('"compile.refresh_and_wait"');
    expect(source).toContain('const string CompileWaitReadySchemaJson');
    expect(source).toContain('const string CompileRefreshAndWaitSchemaJson');
    expect(source).toContain('const string CompileErrorsRecentSchemaJson');
    expect(source).toContain('const string CompileWarningsRecentSchemaJson');
    expect(source).toContain('\\"max_results\\":{\\"type\\":\\"integer\\",\\"minimum\\":1,\\"maximum\\":100');
    expect(source).toContain('\\"max_results\\":{\\"type\\":\\"integer\\",\\"minimum\\":1,\\"maximum\\":200');
    expect(source).toContain('"compile.errors.recent"');
    expect(source).toContain('"compile.warnings.recent"');
    expect(source).toContain('"validation.preflight"');
    expect(source).toContain('"validation.after_edit"');
    expect(source).toContain('\\"timeout_ms\\"');
    expect(source).toContain('\\"poll_interval_ms\\"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    const waitDescriptor = source.slice(source.indexOf('"compile.wait_ready"'), source.indexOf('"compile.refresh_and_wait"'));
    expect(waitDescriptor).toContain('TimeoutMs = 300000');
    expect(source).toContain('TimeoutMs = 295000');
    expect(source).toContain('CompileRefreshAndWaitSchemaJson = "{\\"type\\":\\"object\\",\\"properties\\":{\\"timeout_ms\\":{\\"type\\":\\"integer\\",\\"minimum\\":1,\\"maximum\\":295000}');
    expect(source).toContain('\\"expected_script_paths\\":{\\"type\\":\\"array\\",\\"maxItems\\":64,\\"uniqueItems\\":true');
    expect(source).toContain('Local or Embedded package scripts use package.local_script.import.');
    expect(source).toContain('\\"pattern\\":\\"^Assets/.+[.][cC][sS]$\\"');
    expect(source).toContain('Tags = new string[] { "compile", "status" }');
  });

  it("requires exact GUID, MonoScript main-asset, and active compiler-source evidence", () => {
    const source = fs.readFileSync(builtInCommandsPath, "utf8");

    expect(source).toContain("AssetDatabase.LoadMainAssetAtPath(path) is MonoScript");
    expect(source).toContain("CompilationPipeline.GetAssemblies()");
    expect(source).toContain("IsExactUnityGuid(guid)");
    expect(source).toContain('causes.Add("invalid_guid")');
    expect(source).toContain('causes.Add("mono_script_missing")');
    expect(source).toContain('causes.Add("compiler_source_missing")');
    expect(source).toContain('builder.Append(",\\"items\\":[")');
  });

  it("tracks Unity updating state and reports ready status", () => {
    const stateSource = fs.readFileSync(bridgeRuntimeStatePath, "utf8");
    const commandSource = fs.readFileSync(builtInCommandsPath, "utf8");
    const bridgeSource = fs.readFileSync(bridgeServerPath, "utf8");

    expect(stateSource).toContain("public bool IsUpdating");
    expect(stateSource).toContain("EditorApplication.isUpdating");
    expect(stateSource).toContain('return "updating"');
    expect(stateSource).toContain("Status = ResolveLifecycleStatus(IsCompiling, IsUpdating, IsPlaying, PackageRefreshPending)");
    expect(stateSource).toContain('public bool IsReady => string.Equals(Status, "ready", StringComparison.Ordinal);');
    expect(stateSource).toContain("public bool HasUnsavedSceneChanges");
    expect(stateSource).toContain("public DirtySceneInfo[] DirtyScenes");
    expect(stateSource).toContain("public int LastEditorUpdateAgeMs");
    expect(stateSource).toContain("public int StatusDurationMs");
    expect(stateSource).toContain("m_lastEditorUpdateUtc = now");
    expect(stateSource).toContain("m_statusChangedUtc = now");
    expect(stateSource).toContain("SceneManager.GetSceneAt(index)");
    expect(commandSource).toContain('\\"is_updating\\"');
    expect(commandSource).toContain('\\"ready\\"');
    expect(commandSource).toContain('\\"editor_ready\\"');
    expect(commandSource).toContain('\\"compilable\\"');
    expect(commandSource).toContain('\\"last_editor_update_age_ms\\"');
    expect(commandSource).toContain('\\"status_duration_ms\\"');
    expect(commandSource).toContain('\\"has_compile_errors\\"');
    expect(commandSource).toContain('\\"compile_error_count\\"');
    expect(commandSource).toContain('\\"compile_diagnostics\\"');
    expect(commandSource).toContain('\\"compile_progress_window\\"');
    expect(commandSource).toContain('\\"background_compiler_progress\\"');
    expect(commandSource).toContain('\\"has_unsaved_scene_changes\\"');
    expect(commandSource).toContain('\\"dirty_scene_count\\"');
    expect(bridgeSource).toContain('\\"is_updating\\"');
    expect(bridgeSource).toContain('\\"ready\\"');
    expect(bridgeSource).toContain('\\"background_compiler_progress\\"');
    expect(bridgeSource).toContain('\\"has_unsaved_scene_changes\\"');
    expect(bridgeSource).toContain('\\"dirty_scenes\\"');
  });

  it("runs wait polling on the bridge request thread rather than Unity main-thread command invocation", () => {
    const source = fs.readFileSync(bridgeServerPath, "utf8");
    const runtimeSource = fs.readFileSync(bridgeRuntimePath, "utf8");

    expect(source).toContain('if (builtInCommand && command == "compile.wait_ready")');
    expect(source).toContain('if (builtInCommand && command == "compile.refresh_and_wait")');
    expect(source).toContain('if (builtInCommand && command == "validation.preflight")');
    expect(source).toContain('if (builtInCommand && command == "validation.after_edit")');
    expect(source).toContain("CompileWaitReadyJson(requestId, argsJson)");
    expect(source).toContain("CompileRefreshAndWaitJson(requestId, project, argsJson, settingsJson)");
    expect(source).toContain("ValidationAfterEditJson(requestId, project, argsJson, settingsJson)");
    expect(source).toContain("BridgeProtocol.TryParseInvoke(body, out var request, out var errorCode)");
    expect(source).toContain("InvokeJsonSafely(request, catalog)");
    expect(source).toContain("InvokeExceptionResponseErrorJson(request, exception)");
    expect(source).toContain('protocol_failure\\\":\\\"invoke_handler_exception');
    expect(source).toContain("AssetDatabaseRefreshTimeoutMs");
    expect(source).toContain('ValidationRefreshTimeoutDetailsJson(argsJson, exception.WorkState, exception.LateExecutionPossible, exception.QueueDepthAtTimeout, "asset_database_refresh_timeout")');
    expect(source).toContain("catch (MainThreadDispatchTimeoutException exception)");
    expect(source).toContain('string.Equals(workState, "cancelled_before_start", StringComparison.Ordinal) && !lateExecutionPossible');
    expect(source).toContain('\\\"timed_out_work_state\\\":');
    expect(source).toContain('\\\"late_execution_possible\\\":');
    expect(source).toContain('\\\"refresh_started\\\":false');
    expect(source).toContain('\\\"compile_barrier_created\\\":false');
    expect(source).toContain('\\\"script_compilation_requested\\\":false');
    expect(source).toContain('\\\"write_command_repeated\\\":false');
    expect(source).toContain('\\\"write_replay_allowed\\\":false');
    expect(source).toContain('cancelledBeforeStart ? string.Empty : expectedBarrierOperationId, !cancelledBeforeStart');
    expect(source).toContain("ValidationWaitTimeoutDetailsJson(waitedMs, timeoutMs, lifecycle)");
    expect(source).toContain('"main_thread_dispatch_timeout"');
    expect(source).toContain('"asset_database_refresh_handler_timeout"');
    expect(source).toContain('\\\"timeout_kind\\\":\\\"validation_wait_ready_timeout');
    expect(source).toContain('\\\"compile_ready_is_not_validation_success\\\":true');
    expect(source).toContain('BuiltInCommands.ValidationStatusJson(m_state, "preflight"');
    expect(source).toContain("ValidationWaitReadyResult(timeoutMs, pollIntervalMs, operationId[0], true)");
    expect(source).toContain('BuiltInCommands.CompileRefreshAndRequestBarrier(new McpCommandContext(m_state, requestId, project, argsJson, settingsJson), out operationId[0], out _, "validation.after_edit")');
    expect(source).toContain("MainThreadDispatcher.Run(() => BuiltInCommands.CompileRefreshAndRequestBarrier");
    expect(source).toContain("CompileWaitReadyResult(timeoutMs, pollIntervalMs, true, operationId[0], true, expectedScriptPaths[0])");
    expect(source).toContain("if (lifecycle.IsReady && barrier.Stable)");
    expect(source).toContain("Compilation or its correlated domain reload did not settle before the deadline");
    expect(source).toContain('\\"refreshed\\":true');
    expect(source).toContain("readonly ManualResetEventSlim m_shutdown = new ManualResetEventSlim(false)");
    expect(source).toContain("volatile bool m_shutdownStarted");
    expect(source).toContain("m_shutdown.Set()");
    expect(source.match(/m_shutdown\.Wait\(Math\.Min\(pollIntervalMs, timeoutMs - waitedMs\)\)/g)).toHaveLength(2);
    expect(source).toContain('CommandExecutionResult.Error("unity_reloading"');
    expect(source).toContain(',\\"write_replay_allowed\\":false');
    expect(source).not.toContain("Thread.Sleep(Math.Min(pollIntervalMs, timeoutMs - waitedMs))");
    expect(source).toContain("CompileStatusJsonWithRefresh(refreshed, true, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)");
    expect(source).toContain('CommandExecutionResult.Error("compilation_failed"');
    expect(source).toContain("ConsoleLogBuffer.HasCompileErrors()");
    expect(source).toContain('CommandExecutionResult.Error("asset_import_failed"');
    expect(source).toContain("if (localPackageCodeInventoryUnavailable && !allowPendingInventoryConvergence)");
    expect(source).toContain("if (localPackageCodeInventoryStale && !allowPendingInventoryConvergence)");
    expect(source).toContain("ShouldReturnPackageRefreshRequired(refreshed, lifecycle.PackageRefreshRequired, lifecycle.PackageRefreshWaitCanAdvance)");
    expect(source).toContain('CommandExecutionResult.Error("package_refresh_required"');
    expect(source).toContain('\\"run_compile.refresh_and_wait_only_as_a_new_explicit_action\\"');
    expect(source).not.toContain("localPackageCodeInventoryUnavailable && !(requireExplicitBarrier && allowPendingInventoryConvergence)");
    expect(source).toContain("prefab_import_diagnostics");
    expect(source).toContain("PrefabImportDiagnosticsJson(refreshed, barrier, expectedBarrierOperationId)");
    expect(source).toContain("operation.RequestIssuedTicks");
    expect(source).toContain("if (!ExplicitCompilationBarrierState.IsValidOperationId(expectedOperationId))");
    expect(source).toContain("operation.Finished && operation.FinishedTicks > 0");
    const compileWaitSource = source.slice(source.indexOf("CommandExecutionResult CompileWaitReadyResult"), source.indexOf("static CommandExecutionResult LifecycleShutdownResult"));
    expect(compileWaitSource.indexOf("ConsoleLogBuffer.HasCompileErrors()")).toBeLessThan(compileWaitSource.indexOf("PrefabImportDiagnosticsJson(refreshed, barrier, expectedBarrierOperationId)"));
    expect(compileWaitSource.indexOf("ShouldReturnPackageRefreshRequired")).toBeLessThan(compileWaitSource.indexOf("if (waitedMs >= timeoutMs)"));
    expect(source).toContain("WithForcedHeartbeat(CommandExecutionResult.Ok(CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle, prefabImportDiagnostics, expectedScriptImports))");
    expect(source).toContain("WithForcedHeartbeat(CommandExecutionResult.Error(\"compilation_failed\"");
    expect(source).toContain("WithForcedHeartbeat(CommandExecutionResult.Ok(BuiltInCommands.ValidationStatusJson");
    expect(source).toContain("WithForcedHeartbeat(CommandExecutionResult.Error(\"command_timeout\", \"Timed out waiting for Unity validation readiness after AssetDatabase.Refresh\"");
    expect(source).toContain("UnityBridgeRuntime.ForceHeartbeat(reason)");
    expect(runtimeSource).toContain("public static bool ForceHeartbeat(string reason)");
    expect(runtimeSource).toContain("static volatile bool s_lifecycleShutdown");
    expect(runtimeSource).toContain("if (s_lifecycleShutdown || State == null || HeartbeatWriter == null)");
    expect(runtimeSource).toContain("s_lifecycleShutdown = true");
    expect(runtimeSource).toContain("MainThreadDispatcher.Run(() =>");
    expect(runtimeSource).toContain("State.UpdateFromEditor()");
    expect(runtimeSource).toContain("PackageUpdateMonitor?.Update(State, EditorApplication.timeSinceStartup)");
    expect(runtimeSource).toContain("return WriteHeartbeatAndNotify()");
    expect(runtimeSource).toContain("catch (TimeoutException)");
    expect(source).toContain('if (builtInCommand && command == "compile.status")');
    expect(source.indexOf('if (builtInCommand && command == "compile.status")')).toBeLessThan(source.indexOf('if (builtInCommand && command == "compile.wait_ready")'));
  });

  it("drains admitted reload responses before closing the listener without delaying editor quit", () => {
    const source = fs.readFileSync(bridgeServerPath, "utf8");
    const runtimeSource = fs.readFileSync(bridgeRuntimePath, "utf8");

    expect(source).toContain("readonly ManualResetEventSlim m_handlersDrained = new ManualResetEventSlim(true)");
    expect(source.indexOf("TryAdmitHandler()")).toBeLessThan(source.indexOf("ThreadPool.QueueUserWorkItem"));
    expect(source).toContain("if (!ThreadPool.QueueUserWorkItem(_ => HandleSafely(context)))");
    expect(source).toContain("if (!TryClaimCommandDispatch())");
    expect(source.indexOf("if (!TryClaimCommandDispatch())")).toBeLessThan(source.indexOf("InvokeJsonSafely(request, catalog)"));
    expect(source).toContain("WriteShutdownResponse(context, request.RequestId)");
    expect(source).toContain("definitive non-dispatch response keeps request correlation");
    expect(source).toContain('{\\"command_dispatched\\":false}');
    expect(source).toContain("m_listenerAdmissionsSealed = true");
    expect(source).toContain("finally\n            {\n                CompleteHandler();");
    expect(source).toContain("public void BeginShutdown()");
    expect(source).toContain("public bool DisposeAfterDrain(int totalBudgetMs)");
    expect(source).toContain("var drainStartedTimestamp = Stopwatch.GetTimestamp()");
    expect(source).toContain("var remainingMs = Math.Max(0, totalBudgetMs - ElapsedMs(drainStartedTimestamp))");
    expect(source.indexOf("var drainStartedTimestamp = Stopwatch.GetTimestamp()")).toBeGreaterThan(source.indexOf("BeginShutdown();"));
    expect(source).toContain("BridgeProtocol.ErrorResponse(\"unity_reloading\"");
    expect(source).toContain("bridge.invoke.response.write.start");
    expect(source).toContain("bridge.invoke.response.write.end");
    expect(source).toContain("bridge.invoke.response.write.error");
    expect(source).toContain("response_bytes=");
    expect(source).toContain("response.Close(bytes, true)");
    expect(source).not.toContain("response.OutputStream.Write(bytes");
    expect(source).toContain("finally\n            {\n                CompleteHandler();");
    expect(source).not.toContain("response_json=");
    expect(source).not.toContain('";operation_id="');
    expect(runtimeSource).toContain("const int ReloadResponseDrainBudgetMs = 500");
    expect(runtimeSource).toContain("Server?.BeginShutdown()");
    expect(runtimeSource).toContain("Server.DisposeAfterDrain(ReloadResponseDrainBudgetMs)");
    expect(runtimeSource).toContain('if (reason == "assembly_reload")');
    expect(runtimeSource).toContain("else\n            {\n                Server.Dispose();");
    expect(runtimeSource.indexOf("Server?.BeginShutdown()")).toBeLessThan(runtimeSource.indexOf("State.MarkReloading()"));
    expect(runtimeSource.indexOf("Server?.BeginShutdown()")).toBeLessThan(runtimeSource.indexOf("HeartbeatWriter.Write()"));
  });

  it("returns direct validation no-replay and Gateway-only problem-report guidance", () => {
    const source = fs.readFileSync(bridgeServerPath, "utf8");
    const commandSource = fs.readFileSync(builtInCommandsPath, "utf8");

    expect(source).toContain("ValidationDirectSessionFields(command, true)");
    expect(source).toContain('\\"problem_report_tool\\":\\"sgg_unity_call\\"');
    expect(source).toContain('\\"problem_report_command\\":\\"diagnostics.report_problem\\"');
    expect(source).toContain('\\"problem_report_availability\\":\\"gateway_feature_and_policy_dependent\\"');
    expect(source).toContain('\\"offline_problem_report_cli\\":\\"sgg-unity-mcp-gateway report-problem --project <project-id> --summary <bounded-summary> --include-doctor\\"');
    expect(source).toContain('\\"problem_report_requires_gateway_session\\":true');
    expect(source).toContain('\\"direct_bridge_report_problem_available\\":false');
    expect(source).toContain('\\"write_command_repeated\\":false');
    expect(source).toContain('\\"write_replay_allowed\\":false');
    expect(source).toContain('\\"call_gateway_sgg_unity_call_diagnostics.report_problem_if_available\\"');
    expect(commandSource).toContain('Add("call_gateway_sgg_unity_call_diagnostics.report_problem_if_available")');
    expect(commandSource).not.toContain('retry_validation.after_edit_after_unity_recovers');
    expect(source).toContain('\\"timed_out_work_state\\":');
    expect(source).toContain('\\"late_execution_possible\\":');
    expect(source).toContain('\\"lifecycle_status_recheck_allowed\\":true');
    expect(source).not.toContain('[McpCommand(\n            "diagnostics.report_problem"');
  });

  it("tracks compiler errors from CompilationPipeline and exposes bounded diagnostics", () => {
    const source = fs.readFileSync(consoleLogBufferPath, "utf8");
    const commandSource = fs.readFileSync(builtInCommandsPath, "utf8");

    expect(commandSource).toContain('JsonUtil.HasProperty(context.ArgsJson, "limit") && JsonUtil.HasProperty(context.ArgsJson, "max_results")');
    expect(commandSource).toContain('JsonUtil.HasProperty(context.ArgsJson, "max_results") ? "max_results" : "limit", 20');
    expect(commandSource).toContain('CommandExecutionResult.Error("schema_validation_failed", "Provide only one of limit or max_results")');

    expect(source).toContain("CompilationPipeline.compilationStarted += OnCompilationStarted");
    expect(source).toContain("CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished");
    expect(source).toContain("CompilationPipeline.compilationFinished += OnCompilationFinished");
    expect(source).toContain("CompilerMessageType.Error");
    expect(source).toContain("IsCompilerErrorLog(condition, type");
    expect(source).toContain("internal static string PrefabImportDiagnosticsJson(DateTime sinceUtc, DateTime untilUtc, string operationId)");
    expect(source).toContain("entry.TimestampUtc < sinceUtc || entry.TimestampUtc > untilUtc");
    expect(source).toContain("persistedTimestampTicks <= untilUtc.Ticks");
    expect(source).toContain("PrefabYamlIntegrity.PrefabImportObservationPayload(messages)");
    expect(source).toContain("PrefabYamlIntegrity.PrefabImportDiagnosticsJsonFromObservationPayload(currentPayload)");
    expect(source).toContain("PrefabImportObservationKey");
    expect(source).toContain("Application.logMessageReceived += OnMainThreadLogMessageReceived");
    expect(source).toContain("SessionState.SetString(PrefabImportObservationKey, blob)");
    expect(source).toContain("LoadPrefabImportObservation()");
    expect(commandSource).toContain("ConsoleLogBuffer.BeginPrefabImportObservation(operationId)");
    expect(commandSource).not.toContain("ConsoleLogBuffer.PrefabImportDiagnosticsJson(");
    expect(source).toContain('condition.IndexOf("error CS"');
    expect(source).toContain('Source = "console_log"');
    expect(source).toContain('Source = "compilation_pipeline"');
    expect(source).toContain('Severity = "error"');
    expect(source).toContain('Code = ExtractCompilerCode(message.message, "error CS")');
    expect(source).toContain("AppendCompileDiagnosticEntryJson(builder, entry, maxMessageChars, maxStackChars, false)");
    expect(source).toContain('builder.Append(",\\"severity\\":").Append(JsonUtil.String(severity))');
    expect(source).toContain('builder.Append(",\\"code\\":").Append(JsonUtil.String(code))');
    expect(source).toContain("static string SafeCompilerCode(string code)");
    expect(source).toContain("code[index] >= 'A' && code[index] <= 'Z'");
    expect(source).toContain("code[index] >= '0' && code[index] <= '9'");
    expect(source).toContain('builder.Append(",\\"source\\":")');
    expect(source).toContain("MaxCompileErrors = 100");
    expect(source).toContain("CompileDiagnosticsJson");
    expect(source).toContain('builder.Append("{\\"has_compile_errors\\":")');
  });

  it("tracks compiler warnings separately from errors with bounded metadata", () => {
    const commandSource = fs.readFileSync(builtInCommandsPath, "utf8");
    const bufferSource = fs.readFileSync(consoleLogBufferPath, "utf8");

    expect(commandSource).toContain("public static CommandExecutionResult CompileWarningsRecent");
    expect(commandSource).toContain('JsonUtil.HasProperty(context.ArgsJson, "max_results") ? "max_results" : "limit", 50');
    expect(commandSource).toContain("ConsoleLogBuffer.CompileWarningsJson(limit, maxMessageChars, maxStackChars)");
    expect(bufferSource).toContain("MaxCompileWarnings = 200");
    expect(bufferSource).toContain("static readonly List<CompileDiagnosticEntry> CompileWarnings");
    expect(bufferSource).toContain("public static int CompileWarningCount()");
    expect(bufferSource).toContain("public static string CompileWarningsJson");
    expect(bufferSource).toContain("CompileWarnings.Clear()");
    expect(bufferSource).toContain("CompilerMessageType.Warning");
    expect(bufferSource).toContain("IsCompilerWarningLog(condition, type");
    expect(bufferSource).toContain('condition.IndexOf("warning CS"');
    expect(bufferSource).toContain("ExtractCompilerCode(message.message, \"warning CS\")");
    expect(bufferSource).toContain("AssemblyNameFromPath(assemblyPath)");
    expect(bufferSource).toContain("AppendCompileDiagnosticEntryJson(builder, entry, maxMessageChars, maxStackChars, true)");
    expect(bufferSource).toContain('builder.Append(",\\"severity\\":")');
    expect(bufferSource).toContain('builder.Append(",\\"code\\":")');
    expect(bufferSource).toContain('builder.Append(",\\"assembly_name\\":")');
    expect(bufferSource).toContain('builder.Append(",\\"read_only\\":true")');
  });

  it("declares validation preflight and after-edit status payloads", () => {
    const source = fs.readFileSync(builtInCommandsPath, "utf8");

    expect(source).toContain("ValidationStatusJson");
    expect(source).toContain('\\"validation_ready\\"');
    expect(source).toContain('\\"compile_status\\"');
    expect(source).toContain('\\"next_valid_actions\\"');
    expect(source).toContain("ValidationActionsJson");
    expect(source).toContain("compile.status");
    expect(source).toContain("compile.wait_ready");
    expect(source).toContain("fix_compile_errors_then_retry_validation.after_edit");
    expect(source).toContain("perform_edit_then_validation.after_edit");
  });

  it("detects the Windows compile progress window without Unity main-thread APIs", () => {
    const source = fs.readFileSync(compileProgressWindowDetectorPath, "utf8");

    expect(source).toContain("CompileProgressWindowDetector");
    expect(source).toContain("#if UNITY_EDITOR_WIN");
    expect(source).toContain("EnumWindows");
    expect(source).toContain("EnumChildWindows");
    expect(source).toContain('EntryPoint = "SendMessageTimeoutW"');
    expect(source).toContain("const uint WindowTextTimeoutMs = 100");
    expect(source).toContain("SmtoBlock | SmtoAbortIfHung | SmtoErrorOnExit");
    expect(source).toContain("if (!TryReadWindowText(window, out var title))");
    expect(source).toContain("if (!TryReadWindowText(child, out var text))");
    expect(source).not.toMatch(/static extern int GetWindowText/);
    expect(source).not.toContain("SmtoNotimeoutIfNotHung");
    expect(source).toContain("GetWindowThreadProcessId(window, out var windowProcessId)");
    expect(source).toContain("Process.GetCurrentProcess().Id");
    expect(source).toContain('key.Contains("compilingscripts")');
    expect(source).toContain('key.Contains("runningbackend")');
    expect(source).toContain('key.Contains("reloadingdomain")');
    expect(source).toContain('return "domain_reload"');
    expect(source).toContain('const string marker = "busy for "');
    expect(source).toContain('parts[0] * 60 + parts[1]');
    expect(source).toContain('window.BusySeconds >= 0 ? window.BusySeconds.ToString() : "null"');
    expect(source).toContain('\\"progress_kind\\"');
    expect(source).toContain('\\"is_stalled\\"');
    expect(source).toContain('\\"diagnostic_only\\"');
    expect(source).not.toContain("EditorApplication");
    expect(source).not.toContain("EditorUtility.DisplayDialog");
  });

  it("uses bounded authoritative Unity progress to gate background compute compilation", () => {
    const progressSource = fs.readFileSync(backgroundCompilerProgressPath, "utf8");
    const stateSource = fs.readFileSync(bridgeRuntimeStatePath, "utf8");
    const bridgeSource = fs.readFileSync(bridgeServerPath, "utf8");
    const testSource = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "BuildTestAutomationCommands.cs"), "utf8");
    const heartbeatSource = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "HeartbeatWriter.cs"), "utf8");

    expect(progressSource).toContain('GetType("UnityEditor.Progress"');
    expect(progressSource).toContain('GetMethod("EnumerateItems"');
    expect(progressSource).not.toContain('GetMethod("GetItems"');
    expect(progressSource).toContain("const int MaxProgressItems = 256");
    expect(progressSource).toContain("const int ClearSettleMilliseconds = 500");
    expect(progressSource).toContain('return "compute_variant_compile"');
    expect(progressSource).toContain('return "shader_variant_compile"');
    expect(progressSource).toContain('return "native_compiler"');
    expect(progressSource).toContain('cancellation_attempted\\\":false');
    expect(progressSource).toContain('work_replayed\\\":false');
    expect(stateSource).toContain("BackgroundCompilerProgress.BlocksReadiness");
    expect(stateSource).toContain('Status = "busy"');
    expect(testSource).toContain('"background_compilation_unavailable"');
    expect(testSource).toContain('"background_compilation_failed"');
    expect(testSource).toContain('test_runner_execute_invoked\\\":false');
    expect(bridgeSource).toContain("backgroundCompilerProgress.BlocksReadiness");
    expect(bridgeSource).toContain("background_compilation_timed_out");
    expect(heartbeatSource).toContain("background_compiler_progress_state");
    expect(progressSource).not.toContain("Progress.Cancel");
    expect(progressSource).not.toContain("Process.Kill");
  });

  it("allows compile.wait_ready in readonly policy examples", () => {
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");

    expect(policyExample).toContain('"compile.wait_ready"');
    expect(policyExample).toContain('"compile.warnings.recent"');
    expect(policyProfiles).toContain('"compile.wait_ready"');
    expect(policyProfiles).toContain('"compile.warnings.recent"');
  });
});
