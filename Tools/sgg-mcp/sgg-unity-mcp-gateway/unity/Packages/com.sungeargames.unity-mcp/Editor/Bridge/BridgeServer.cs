using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Stopwatch = System.Diagnostics.Stopwatch;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Paths;

namespace SGG.UnityMCP.Editor.Bridge
{
    public sealed class BridgeServer : IDisposable
    {
        const int AssetDatabaseRefreshTimeoutMs = 30000;

        readonly string m_token;
        readonly BridgeRuntimeState m_state;
        readonly CommandRegistry m_registry;
        readonly ManualResetEventSlim m_shutdown = new ManualResetEventSlim(false);
        readonly ManualResetEventSlim m_handlersDrained = new ManualResetEventSlim(true);
        readonly object m_handlerGate = new object();
        readonly object m_listenerCloseGate = new object();
        HttpListener m_listener;
        Thread m_thread;
        volatile bool m_shutdownStarted;
        bool m_listenerAdmissionsSealed;
        bool m_listenerClosed;
        volatile bool m_listenerLoopRunning;
        int m_activeHandlers;

        public BridgeServer(string token, BridgeRuntimeState state, CommandRegistry registry)
        {
            m_token = token;
            m_state = state;
            m_registry = registry;
        }

        public int Port { get; private set; }

        public bool IsRunning => m_listenerLoopRunning && m_listener != null && m_listener.IsListening;

        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            Port = PickFreePort();
            m_listener = new HttpListener();
            m_listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            m_listener.Start();
            m_listenerLoopRunning = true;
            m_thread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "SGG Unity MCP Bridge",
            };
            m_thread.Start();
        }

        public void Dispose()
        {
            BeginShutdown();
            CloseListener();
        }

        public void BeginShutdown()
        {
            lock (m_handlerGate)
            {
                if (m_shutdownStarted)
                {
                    return;
                }

                m_shutdown.Set();
                m_shutdownStarted = true;
            }
        }

        public bool DisposeAfterDrain(int totalBudgetMs)
        {
            BeginShutdown();
            var drainStartedTimestamp = Stopwatch.GetTimestamp();
            var drained = false;
            while (true)
            {
                lock (m_handlerGate)
                {
                    if (m_activeHandlers == 0)
                    {
                        m_listenerAdmissionsSealed = true;
                        drained = true;
                        break;
                    }
                }

                var remainingMs = Math.Max(0, totalBudgetMs - ElapsedMs(drainStartedTimestamp));
                if (remainingMs == 0)
                {
                    lock (m_handlerGate)
                    {
                        m_listenerAdmissionsSealed = true;
                        drained = m_activeHandlers == 0;
                    }
                    break;
                }

                m_handlersDrained.Wait(remainingMs);
            }

            CloseListener();
            return drained;
        }

        static int ElapsedMs(long startedTimestamp)
        {
            return startedTimestamp <= 0
                ? 0
                : (int)Math.Min(int.MaxValue, (Stopwatch.GetTimestamp() - startedTimestamp) * 1000L / Stopwatch.Frequency);
        }

        void CloseListener()
        {
            lock (m_listenerCloseGate)
            {
                lock (m_handlerGate)
                {
                    m_listenerAdmissionsSealed = true;
                }

                if (m_listenerClosed)
                {
                    return;
                }

                try
                {
                    m_listener?.Stop();
                    m_listenerLoopRunning = false;
                }
                catch
                {
                    // Listener shutdown should never block Unity Editor quit.
                }

                try
                {
                    m_listener?.Close();
                    m_listenerClosed = true;
                }
                catch
                {
                    // A later dispose call may retry a partially failed close.
                }
            }
        }

        void ListenLoop()
        {
            try
            {
                while (!m_shutdownStarted && m_listener != null && m_listener.IsListening)
                {
                    try
                    {
                        var context = m_listener.GetContext();
                        if (!TryAdmitHandler())
                        {
                            CloseRejectedContext(context);
                            return;
                        }

                        try
                        {
                            if (!ThreadPool.QueueUserWorkItem(_ => HandleSafely(context)))
                            {
                                CompleteHandler();
                                CloseRejectedContext(context);
                                return;
                            }
                        }
                        catch
                        {
                            CompleteHandler();
                            throw;
                        }
                    }
                    catch (HttpListenerException)
                    {
                        return;
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                BridgeReloadTrace.LogError("bridge.listener.loop.failed", exception);
            }
            finally
            {
                m_listenerLoopRunning = false;
            }
        }

        bool TryAdmitHandler()
        {
            lock (m_handlerGate)
            {
                if (m_listenerAdmissionsSealed)
                {
                    return false;
                }

                if (m_activeHandlers == 0)
                {
                    m_handlersDrained.Reset();
                }

                m_activeHandlers++;
                return true;
            }
        }

        bool TryClaimCommandDispatch()
        {
            lock (m_handlerGate)
            {
                return !m_shutdownStarted;
            }
        }

        void CompleteHandler()
        {
            lock (m_handlerGate)
            {
                m_activeHandlers = Math.Max(0, m_activeHandlers - 1);
                if (m_activeHandlers == 0)
                {
                    m_handlersDrained.Set();
                }
            }
        }

        void Handle(HttpListenerContext context)
        {
            var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
            if (!IsAuthorized(context.Request))
            {
                var json = path == "/invoke"
                    ? BridgeProtocol.ErrorResponse("invalid_token", "Invalid bridge token", string.Empty, 0)
                    : "{\"status\":\"error\",\"error\":{\"code\":\"invalid_token\"}}";
                WriteJson(context.Response, 401, json);
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/health")
            {
                WriteJson(context.Response, 200, HealthJson());
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/commands")
            {
                WriteJson(context.Response, 200, CommandsJson());
                return;
            }

            if (context.Request.HttpMethod == "GET" && path == "/logs")
            {
                WriteJson(context.Response, 200, LogsJson(context.Request));
                return;
            }

            if (context.Request.HttpMethod == "POST" && path == "/invoke")
            {
                var timer = Stopwatch.StartNew();
                string body;
                try
                {
                    body = ReadBodyBounded(context.Request);
                }
                catch (BridgeRequestTooLargeException)
                {
                    WriteJson(context.Response, 200, BridgeProtocol.ErrorResponse("protocol_validation_error", "Invoke request exceeds the 4 MiB limit", string.Empty, timer.ElapsedMilliseconds));
                    return;
                }
                catch
                {
                    WriteJson(context.Response, 200, BridgeProtocol.ErrorResponse("protocol_validation_error", "Invalid bridge invoke request body", string.Empty, timer.ElapsedMilliseconds));
                    return;
                }

                if (!BridgeProtocol.TryParseInvoke(body, out var request, out var errorCode))
                {
                    var message = errorCode == "protocol_version_mismatch" ? "Unsupported or missing bridge protocol schema version" : "Invalid bridge invoke request";
                    WriteJson(context.Response, 200, BridgeProtocol.ErrorResponse(errorCode, message, BridgeProtocol.SafeRequestId(body), timer.ElapsedMilliseconds));
                    return;
                }

                if (!BridgeProtocol.TryValidateProject(request, m_state.Project, timer.ElapsedMilliseconds, out var projectErrorJson))
                {
                    WriteJson(context.Response, 200, projectErrorJson);
                    return;
                }

                var catalog = m_registry.CaptureSnapshot();
                if (!TryValidateCatalogBinding(request, catalog, timer.ElapsedMilliseconds, out var catalogErrorJson))
                {
                    WriteJson(context.Response, 200, catalogErrorJson);
                    return;
                }
                if (!catalog.ContainsCommand(request.Command))
                {
                    WriteJson(context.Response, 200, BridgeProtocol.ErrorResponse("command_not_found", "Command is not present in the captured Unity catalog", request.RequestId, timer.ElapsedMilliseconds, "{\"command_dispatched\":false,\"catalog_generation\":" + catalog.CatalogGeneration + ",\"command_replayed\":false}"));
                    return;
                }

                if (!TryClaimCommandDispatch())
                {
                    WriteShutdownResponse(context, request.RequestId);
                    return;
                }

                var responseJson = InvokeJsonSafely(request, catalog);
                if (string.IsNullOrEmpty(responseJson))
                {
                    WriteInvokeJson(context.Response, request, EmptyInvokeResponseErrorJson(request));
                    return;
                }

                WriteInvokeJson(context.Response, request, responseJson);
                return;
            }

            WriteJson(context.Response, 404, "{\"status\":\"error\",\"error\":{\"code\":\"not_found\"}}");
        }

        void HandleSafely(HttpListenerContext context)
        {
            try
            {
                if (m_shutdownStarted)
                {
                    var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
                    if (context.Request.HttpMethod == "POST" && path == "/invoke")
                    {
                        // Parse the bounded invoke body so the definitive non-dispatch response keeps request correlation.
                        Handle(context);
                        return;
                    }
                    WriteShutdownResponse(context);
                    return;
                }

                Handle(context);
            }
            catch (Exception exception)
            {
                try
                {
                    var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
                    var json = path == "/invoke"
                        ? BridgeProtocol.ErrorResponse("bridge_protocol_error", "Unity bridge invoke handler failed", string.Empty, 0)
                        : "{\"status\":\"error\",\"error\":{\"code\":\"bridge_protocol_error\",\"message\":" + JsonString(exception.Message) + "}}";
                    WriteJson(context.Response, 500, json);
                }
                catch
                {
                    // The client may already be gone; keep the listener alive for future rescue requests.
                }
            }
            finally
            {
                CompleteHandler();
            }
        }

        static void CloseRejectedContext(HttpListenerContext context)
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // Listener close may already have disposed this context.
            }
        }

        static void WriteShutdownResponse(HttpListenerContext context, string requestId = "")
        {
            try
            {
                var path = context.Request.Url == null ? "/" : context.Request.Url.AbsolutePath;
                if (context.Request.HttpMethod == "POST" && path == "/invoke")
                {
                    WriteJson(context.Response, 200, BridgeProtocol.ErrorResponse("unity_reloading", "Unity bridge lifecycle shutdown began before this queued request was dispatched", requestId, 0, "{\"command_dispatched\":false}"));
                    return;
                }

                WriteJson(context.Response, 503, "{\"status\":\"error\",\"error\":{\"code\":\"unity_reloading\",\"message\":\"Unity bridge lifecycle shutdown is in progress\"}}");
            }
            catch
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // Listener shutdown may win this race.
                }
            }
        }

        bool IsAuthorized(HttpListenerRequest request)
        {
            if (string.IsNullOrEmpty(m_token))
            {
                return true;
            }

            return request.Headers["X-SGG-Unity-MCP-Bridge-Token"] == m_token;
        }

        string HealthJson()
        {
            var catalog = m_registry.CaptureSnapshot();
            var lifecycle = m_state.CapturePackageLifecycleSnapshot();
            return "{\"schema_version\":1"
                + ",\"instance_id\":" + JsonString(m_state.InstanceId)
                + ",\"project\":" + JsonString(m_state.Project)
                + ",\"status\":" + JsonString(lifecycle.Status)
                + ",\"unity_version\":" + JsonString(m_state.UnityVersion)
                + ",\"commands_hash\":" + JsonString(catalog.CommandsHash)
                + ",\"catalog_status\":" + JsonString(lifecycle.CatalogStatus)
                + ",\"catalog_generation\":" + catalog.CatalogGeneration
                + ",\"catalog_ready\":" + JsonUtil.Bool(catalog.CatalogGeneration > 0 && !string.IsNullOrEmpty(catalog.CommandsHash))
                + ",\"listener_ready\":" + JsonUtil.Bool(IsRunning)
                + ",\"is_compiling\":" + JsonUtil.Bool(lifecycle.IsCompiling)
                + ",\"is_updating\":" + JsonUtil.Bool(lifecycle.IsUpdating)
                + ",\"is_playing\":" + JsonUtil.Bool(lifecycle.IsPlaying)
                + ",\"ready\":" + JsonUtil.Bool(lifecycle.IsReady)
                + ",\"background_compiler_progress\":" + (lifecycle.BackgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved).ToJson()
                + ",\"package_update\":" + lifecycle.PackageUpdateJson
                + ",\"has_unsaved_scene_changes\":" + JsonUtil.Bool(m_state.HasUnsavedSceneChanges)
                + ",\"dirty_scene_count\":" + m_state.DirtySceneCount
                + ",\"dirty_scenes\":" + DirtyScenesJson()
                + ",\"queue_depth\":" + m_state.QueueDepth
                + "}";
        }

        string DirtyScenesJson()
        {
            var scenes = m_state.DirtyScenes ?? Array.Empty<DirtySceneInfo>();
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < scenes.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var scene = scenes[index];
                builder.Append("{\"scene_path\":").Append(JsonString(scene == null ? string.Empty : scene.scene_path));
                builder.Append(",\"scene_name\":").Append(JsonString(scene == null ? string.Empty : scene.scene_name));
                builder.Append(",\"is_active\":").Append(JsonUtil.Bool(scene != null && scene.is_active));
                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        string CommandsJson()
        {
            var catalog = m_registry.CaptureSnapshot();
            var builder = new StringBuilder();
            builder.Append("{\"schema_version\":1");
            builder.Append(",\"instance_id\":").Append(JsonString(m_state.InstanceId));
            builder.Append(",\"project\":").Append(JsonString(m_state.Project));
            builder.Append(",\"commands_hash\":").Append(JsonString(catalog.CommandsHash));
            builder.Append(",\"catalog_generation\":").Append(catalog.CatalogGeneration);
            builder.Append(",\"commands\":[");
            for (var index = 0; index < catalog.PublishedDescriptors.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var descriptor = catalog.PublishedDescriptors[index];
                builder.Append("{\"id\":").Append(JsonString(descriptor.Id));
                builder.Append(",\"title\":").Append(JsonString(descriptor.Title));
                builder.Append(",\"description\":").Append(JsonString(descriptor.Description));
                builder.Append(",\"package\":").Append(JsonString(descriptor.Package));
                builder.Append(",\"risk\":").Append(JsonString(descriptor.Risk));
                builder.Append(",\"idempotency\":").Append(JsonString(descriptor.Idempotency));
                builder.Append(",\"timeout_ms\":").Append(descriptor.TimeoutMs);
                builder.Append(",\"schema\":").Append(string.IsNullOrEmpty(descriptor.SchemaJson) ? "{}" : descriptor.SchemaJson);
                AppendOptionalStringArray(builder, "tags", descriptor.Tags);
                AppendOptionalString(builder, "capability", descriptor.Capability);
                AppendOptionalStringArray(builder, "requires_editor_status", descriptor.RequiresEditorStatus);
                AppendOptionalString(builder, "result_shape", descriptor.ResultShape);
                AppendOptionalObject(builder, "output_limit", descriptor.OutputLimitJson);
                AppendOptionalBool(builder, "supports_job", descriptor.SupportsJob);
                AppendOptionalBool(builder, "supports_cancel", descriptor.SupportsCancel);
                AppendOptionalObject(builder, "wrapper_wait", descriptor.WrapperWaitJson);
                AppendOptionalString(builder, "feature_flag", descriptor.FeatureFlag);
                AppendOptionalString(builder, "source", descriptor.Source);
                AppendOptionalString(builder, "package_id", descriptor.PackageId);
                AppendOptionalString(builder, "package_version", descriptor.PackageVersion);
                AppendOptionalString(builder, "manifest_path", descriptor.ManifestPath);
                AppendOptionalString(builder, "trust", descriptor.Trust);
                builder.Append('}');
            }

            builder.Append(']');
            AppendManifestDiagnostics(builder, catalog);
            AppendProjectManifestCandidates(builder, catalog);
            builder.Append('}');
            return builder.ToString();
        }

        static void AppendManifestDiagnostics(StringBuilder builder, CommandRegistry.CatalogSnapshot catalog)
        {
            if (catalog.PublishedManifestDiagnostics.Count == 0)
            {
                return;
            }

            builder.Append(",\"manifest_diagnostics\":[");
            for (var index = 0; index < catalog.PublishedManifestDiagnostics.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var diagnostic = catalog.PublishedManifestDiagnostics[index];
                builder.Append("{\"code\":").Append(JsonString(diagnostic.Code));
                AppendOptionalString(builder, "package_id", diagnostic.PackageId);
                AppendOptionalString(builder, "package_version", diagnostic.PackageVersion);
                AppendOptionalString(builder, "manifest_path", diagnostic.ManifestPath);
                builder.Append(",\"message\":").Append(JsonString(diagnostic.Message));
                builder.Append('}');
            }

            builder.Append(']');
        }

        static void AppendProjectManifestCandidates(StringBuilder builder, CommandRegistry.CatalogSnapshot catalog)
        {
            if (catalog.PublishedProjectManifestCandidates.Count == 0)
            {
                return;
            }

            builder.Append(",\"project_manifest_candidates\":[");
            for (var index = 0; index < catalog.PublishedProjectManifestCandidates.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var candidate = catalog.PublishedProjectManifestCandidates[index];
                builder.Append("{\"manifest_path\":").Append(JsonString(candidate.ManifestPath));
                builder.Append(",\"loadable\":").Append(JsonUtil.Bool(candidate.Loadable));
                builder.Append(",\"reason\":").Append(JsonString(candidate.Reason));
                builder.Append('}');
            }

            builder.Append(']');
        }

        string InvokeJson(BridgeInvokeRequest request, CommandRegistry.CatalogSnapshot catalog)
        {
            var requestId = request.RequestId;
            var project = request.Project;
            var command = request.Command;
            var argsJson = request.ArgsJson;
            var settingsJson = request.SettingsJson;
            var builtInCommand = catalog.IsBuiltInCommand(command);
            if (builtInCommand && command == "compile.status")
            {
                var response = CommandExecutionResult.Ok(BuiltInCommands.CompileStatusJson(m_state, false, 0, 0)).ToJson(requestId);
                McpFtueSmokeTracker.RecordSuccessfulCompileStatus();
                return response;
            }

            if (builtInCommand && EditorSavePromptCommands.IsBackgroundCommand(command))
            {
                return EditorSavePromptCommands.InvokeBackground(command, argsJson).ToJson(requestId);
            }

            if (builtInCommand && command == "compile.wait_ready")
            {
                return CompileWaitReadyJson(requestId, argsJson);
            }

            if (builtInCommand && command == "compile.refresh_and_wait")
            {
                return CompileRefreshAndWaitJson(requestId, project, argsJson, settingsJson);
            }

            if (builtInCommand && command == "validation.preflight")
            {
                return CommandExecutionResult.Ok(BuiltInCommands.ValidationStatusJson(m_state, "preflight", false, false, 0, 0)).ToJson(requestId);
            }

            if (builtInCommand && command == "validation.after_edit")
            {
                return ValidationAfterEditJson(requestId, project, argsJson, settingsJson);
            }

            if (builtInCommand && command == "test.run.status")
            {
                return BuildTestAutomationCommands.TestRunStatus(new McpCommandContext(m_state, requestId, project, argsJson, settingsJson)).ToJson(requestId);
            }

            if (builtInCommand && command == "build.run.status")
            {
                return BuildTestAutomationCommands.BuildRunStatus(new McpCommandContext(m_state, requestId, project, argsJson, settingsJson)).ToJson(requestId);
            }

            if (builtInCommand && command == "test.run.batchmode_fallback")
            {
                return BuildTestAutomationCommands.TestRunBatchmodeFallback(new McpCommandContext(m_state, requestId, project, argsJson, settingsJson)).ToJson(requestId);
            }

            if (builtInCommand && command == "test.run.cancel")
            {
                return BuildTestAutomationCommands.TestRunCancel(new McpCommandContext(m_state, requestId, project, argsJson, settingsJson)).ToJson(requestId);
            }

            return m_registry.Invoke(catalog, requestId, project, command, argsJson, settingsJson).ToJson(requestId);
        }

        string InvokeJsonSafely(BridgeInvokeRequest request, CommandRegistry.CatalogSnapshot catalog)
        {
            try
            {
                return InvokeJson(request, catalog);
            }
            catch (Exception exception)
            {
                return InvokeExceptionResponseErrorJson(request, exception);
            }
        }

        bool TryValidateCatalogBinding(BridgeInvokeRequest request, CommandRegistry.CatalogSnapshot catalog, long durationMs, out string errorJson)
        {
            if (request == null || !request.HasCatalogBinding)
            {
                errorJson = string.Empty;
                return true;
            }

            if (string.Equals(request.ExpectedInstanceId, m_state.InstanceId, StringComparison.Ordinal)
                && request.ExpectedCatalogGeneration == catalog.CatalogGeneration
                && string.Equals(request.ExpectedCommandsHash, catalog.CommandsHash, StringComparison.Ordinal))
            {
                errorJson = string.Empty;
                return true;
            }

            var details = "{\"command_dispatched\":false"
                + ",\"catalog_binding_provided\":true"
                + ",\"instance_match\":" + JsonUtil.Bool(string.Equals(request.ExpectedInstanceId, m_state.InstanceId, StringComparison.Ordinal))
                + ",\"expected_catalog_generation\":" + Math.Max(0, request.ExpectedCatalogGeneration)
                + ",\"actual_catalog_generation\":" + Math.Max(0, catalog.CatalogGeneration)
                + ",\"commands_hash_match\":" + JsonUtil.Bool(string.Equals(request.ExpectedCommandsHash, catalog.CommandsHash, StringComparison.Ordinal))
                + ",\"retry_requires_fresh_explicit_call\":true"
                + ",\"command_replayed\":false}";
            errorJson = BridgeProtocol.ErrorResponse("catalog_stale", "Command catalog changed before Unity dispatch", request.RequestId, durationMs, details);
            return false;
        }

        static string EmptyInvokeResponseErrorJson(BridgeInvokeRequest request)
        {
            var requestId = request.RequestId;
            var command = request.Command;
            var details = "{\"protocol_failure\":\"empty_invoke_response_body\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"endpoint\":\"/invoke\""
                + ValidationDirectSessionFields(command, true)
                + "}";
            return CommandExecutionResult.Error("bridge_protocol_error", "Unity bridge command produced an empty JSON response", details).ToJson(requestId);
        }

        static string InvokeExceptionResponseErrorJson(BridgeInvokeRequest request, Exception exception)
        {
            var requestId = request.RequestId;
            var command = request.Command;
            var details = "{\"protocol_failure\":\"invoke_handler_exception\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"endpoint\":\"/invoke\""
                + ",\"exception_type\":" + JsonUtil.String(exception == null ? string.Empty : exception.GetType().Name)
                + ",\"exception_message\":" + JsonUtil.String(exception == null ? string.Empty : exception.Message)
                + ValidationDirectSessionFields(command, true)
                + "}";
            if (exception is CanonicalPathDependencyMissingException)
            {
                return CommandExecutionResult.Error("dependency_missing", exception.Message, details).ToJson(requestId);
            }

            return CommandExecutionResult.Error("bridge_protocol_error", "Unity bridge invoke handler failed before producing JSON", details).ToJson(requestId);
        }

        string CompileWaitReadyJson(string requestId, string argsJson)
        {
            var timeoutMs = Clamp(JsonUtil.ExtractInt(argsJson, "timeout_ms", 120000), 1, 300000);
            var pollIntervalMs = Clamp(JsonUtil.ExtractInt(argsJson, "poll_interval_ms", 250), 50, 5000);
            var expectedBarrierOperationId = JsonUtil.ExtractString(argsJson, "_compile_barrier_expectation_id");
            var requireExplicitBarrier = JsonUtil.ExtractBool(argsJson, "_require_explicit_compile_barrier", false);
            var expectedPathsResult = BuiltInCommands.ReadExpectedScriptPaths(argsJson, "_expected_script_paths", out var expectedScriptPaths);
            if (expectedPathsResult != null)
            {
                return expectedPathsResult.ToJson(requestId);
            }
            return CompileWaitReadyResult(timeoutMs, pollIntervalMs, false, expectedBarrierOperationId, requireExplicitBarrier, expectedScriptPaths).ToJson(requestId);
        }

        string CompileRefreshAndWaitJson(string requestId, string project, string argsJson, string settingsJson)
        {
            var operationId = new string[1];
            var expectedScriptPaths = new string[1][];
            try
            {
                var refreshResult = MainThreadDispatcher.Run(() => BuiltInCommands.CompileRefreshAndRequestBarrier(new McpCommandContext(m_state, requestId, project, argsJson, settingsJson), out operationId[0], out expectedScriptPaths[0]), AssetDatabaseRefreshTimeoutMs);
                if (refreshResult.Status != "ok")
                {
                    return refreshResult.ToJson(requestId);
                }
            }
            catch (MainThreadDispatchTimeoutException exception)
            {
                var expected = string.IsNullOrEmpty(operationId[0]) ? JsonUtil.ExtractString(argsJson, "_compile_barrier_expectation_id") : operationId[0];
                return CommandExecutionResult.Error("command_timeout", "Timed out waiting for Unity main thread to request explicit script compilation", CompileRefreshTimeoutDetailsJson(expected, exception.WorkState, exception.LateExecutionPossible, exception.QueueDepthAtTimeout, "main_thread_dispatch_timeout")).ToJson(requestId);
            }
            catch (TimeoutException)
            {
                var expected = string.IsNullOrEmpty(operationId[0]) ? JsonUtil.ExtractString(argsJson, "_compile_barrier_expectation_id") : operationId[0];
                return CommandExecutionResult.Error("command_timeout", "Explicit script compilation refresh handler reported a timeout", CompileRefreshTimeoutDetailsJson(expected, "handler_timeout", false, MainThreadDispatcher.QueueDepth, "asset_database_refresh_handler_timeout")).ToJson(requestId);
            }

            var timeoutMs = Clamp(JsonUtil.ExtractInt(argsJson, "timeout_ms", 120000), 1, 295000);
            var pollIntervalMs = Clamp(JsonUtil.ExtractInt(argsJson, "poll_interval_ms", 250), 50, 5000);
            return CompileWaitReadyResult(timeoutMs, pollIntervalMs, true, operationId[0], true, expectedScriptPaths[0]).ToJson(requestId);
        }

        string ValidationAfterEditJson(string requestId, string project, string argsJson, string settingsJson)
        {
            var operationId = new string[1];
            try
            {
                var refreshResult = MainThreadDispatcher.Run(() => BuiltInCommands.CompileRefreshAndRequestBarrier(new McpCommandContext(m_state, requestId, project, argsJson, settingsJson), out operationId[0], out _, "validation.after_edit"), AssetDatabaseRefreshTimeoutMs);
                if (refreshResult.Status != "ok")
                {
                    return refreshResult.ToJson(requestId);
                }
            }
            catch (MainThreadDispatchTimeoutException exception)
            {
                return CommandExecutionResult.Error("command_timeout", "Timed out waiting for Unity main thread to run AssetDatabase.Refresh", ValidationRefreshTimeoutDetailsJson(argsJson, exception.WorkState, exception.LateExecutionPossible, exception.QueueDepthAtTimeout, "asset_database_refresh_timeout")).ToJson(requestId);
            }
            catch (TimeoutException)
            {
                return CommandExecutionResult.Error("command_timeout", "AssetDatabase.Refresh reported a timeout", ValidationRefreshTimeoutDetailsJson(argsJson, "handler_timeout", false, MainThreadDispatcher.QueueDepth, "asset_database_refresh_handler_timeout")).ToJson(requestId);
            }

            var timeoutMs = Clamp(JsonUtil.ExtractInt(argsJson, "timeout_ms", 120000), 1, 300000);
            var pollIntervalMs = Clamp(JsonUtil.ExtractInt(argsJson, "poll_interval_ms", 250), 50, 5000);
            return ValidationWaitReadyResult(timeoutMs, pollIntervalMs, operationId[0], true).ToJson(requestId);
        }

        CommandExecutionResult ValidationWaitReadyResult(int timeoutMs, int pollIntervalMs, string expectedBarrierOperationId, bool requireExplicitBarrier)
        {
            var startedAt = DateTime.UtcNow;

            while (true)
            {
                var waitedMs = (int)Math.Min(int.MaxValue, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                if (m_shutdownStarted)
                {
                    return LifecycleShutdownResult("validation.after_edit", waitedMs, timeoutMs, expectedBarrierOperationId, true);
                }
                var barrier = ExplicitCompilationBarrier.Snapshot(expectedBarrierOperationId, requireExplicitBarrier);
                var hasCompileErrors = ConsoleLogBuffer.HasCompileErrors();
                var terminalCompileFailureWithoutReload = barrier.TerminalCompileFailureWithoutReload;
                var lifecycle = m_state.CapturePackageLifecycleSnapshot();
                var priorityErrorCode = CompileWaitPriorityErrorCode(
                    lifecycle.IsReady,
                    requireExplicitBarrier,
                    barrier.Stable,
                    terminalCompileFailureWithoutReload,
                    hasCompileErrors,
                    lifecycle.PackageMetadataUnavailable,
                    lifecycle.LocalPackageCodeInventoryUnavailable,
                    lifecycle.LocalPackageCodeInventoryStale,
                    lifecycle.PackageRefreshWaitCanAdvance && waitedMs < timeoutMs);
                if (priorityErrorCode == "compilation_failed")
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("compilation_failed", "Unity compilation finished with compiler errors", BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), "validation.after_edit compilation_failed");
                }
                if (priorityErrorCode == "package_metadata_unavailable")
                {
                    return PackageMetadataUnavailableResult(
                        "validation.after_edit",
                        BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle),
                        true);
                }
                if (priorityErrorCode == "local_package_inventory_unavailable" || priorityErrorCode == "local_package_inventory_stale")
                {
                    return LocalPackageCodeInventoryResult(
                        "validation.after_edit",
                        BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle),
                        true,
                        priorityErrorCode == "local_package_inventory_stale");
                }
                var backgroundCompilerProgress = lifecycle.BackgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved;
                if (backgroundCompilerProgress.Failed)
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("background_compilation_failed", "Unity background shader or compute compilation reported a correlated failure", BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), "validation.after_edit background_compilation_failed");
                }
                if (barrier.Unavailable)
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unavailable", "Compilation lifecycle evidence is unavailable after AssetDatabase.Refresh", BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), "validation.after_edit barrier_unavailable");
                }
                if (requireExplicitBarrier && (barrier.State == "not_required" || barrier.State == "unknown"))
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unverified", "The expected validation compilation barrier does not exist", BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, false, waitedMs, timeoutMs, expectedBarrierOperationId, true, lifecycle)), "validation.after_edit barrier_missing");
                }
                if (lifecycle.IsReady && barrier.Stable)
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Ok(BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), "validation.after_edit ready");
                }

                if (waitedMs >= timeoutMs)
                {
                    if (backgroundCompilerProgress.BlocksReadiness)
                    {
                        return WithForcedHeartbeat(CommandExecutionResult.Error("command_timeout", "Timed out waiting for authoritative Unity background shader or compute compilation completion", BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, true, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), "validation.after_edit background_compilation_timed_out");
                    }
                    if (!barrier.Stable)
                    {
                        return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unverified", "Validation compilation or its correlated domain reload did not settle before the deadline", BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, true, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), "validation.after_edit barrier_unverified");
                    }
                    return WithForcedHeartbeat(CommandExecutionResult.Error("command_timeout", "Timed out waiting for Unity validation readiness after AssetDatabase.Refresh", ValidationWaitTimeoutDetailsJson(waitedMs, timeoutMs, lifecycle)), "validation.after_edit timed_out");
                }

                if (m_shutdown.Wait(Math.Min(pollIntervalMs, timeoutMs - waitedMs)))
                {
                    return LifecycleShutdownResult("validation.after_edit", waitedMs, timeoutMs, expectedBarrierOperationId, true);
                }
            }
        }

        internal static bool ShouldReturnPackageRefreshRequired(bool refreshed, bool packageRefreshRequired, bool packageRefreshWaitCanAdvance)
        {
            return !refreshed && packageRefreshRequired && !packageRefreshWaitCanAdvance;
        }

        CommandExecutionResult CompileWaitReadyResult(int timeoutMs, int pollIntervalMs, bool refreshed, string expectedBarrierOperationId, bool requireExplicitBarrier, string[] expectedScriptPaths)
        {
            var startedAt = DateTime.UtcNow;

            while (true)
            {
                var waitedMs = (int)Math.Min(int.MaxValue, (DateTime.UtcNow - startedAt).TotalMilliseconds);
                if (m_shutdownStarted)
                {
                    return LifecycleShutdownResult(refreshed ? "compile.refresh_and_wait" : "compile.wait_ready", waitedMs, timeoutMs, expectedBarrierOperationId, refreshed);
                }
                var barrier = ExplicitCompilationBarrier.Snapshot(expectedBarrierOperationId, requireExplicitBarrier);
                var hasCompileErrors = ConsoleLogBuffer.HasCompileErrors();
                var terminalCompileFailureWithoutReload = barrier.TerminalCompileFailureWithoutReload;
                var lifecycle = m_state.CapturePackageLifecycleSnapshot();
                var priorityErrorCode = CompileWaitPriorityErrorCode(
                    lifecycle.IsReady,
                    requireExplicitBarrier,
                    barrier.Stable,
                    terminalCompileFailureWithoutReload,
                    hasCompileErrors,
                    lifecycle.PackageMetadataUnavailable,
                    lifecycle.LocalPackageCodeInventoryUnavailable,
                    lifecycle.LocalPackageCodeInventoryStale,
                    lifecycle.PackageRefreshWaitCanAdvance && waitedMs < timeoutMs);
                if (priorityErrorCode == "compilation_failed")
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("compilation_failed", "Unity compilation finished with compiler errors", CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), refreshed ? "compile.refresh_and_wait compilation_failed" : "compile.wait_ready compilation_failed");
                }
                if (priorityErrorCode == "package_metadata_unavailable")
                {
                    return PackageMetadataUnavailableResult(
                        refreshed ? "compile.refresh_and_wait" : "compile.wait_ready",
                        CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle),
                        false);
                }
                if (priorityErrorCode == "local_package_inventory_unavailable" || priorityErrorCode == "local_package_inventory_stale")
                {
                    return LocalPackageCodeInventoryResult(
                        refreshed ? "compile.refresh_and_wait" : "compile.wait_ready",
                        CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle),
                        false,
                        priorityErrorCode == "local_package_inventory_stale");
                }
                if (ShouldReturnPackageRefreshRequired(refreshed, lifecycle.PackageRefreshRequired, lifecycle.PackageRefreshWaitCanAdvance))
                {
                    return PackageRefreshRequiredResult(
                        "compile.wait_ready",
                        CompileStatusJsonWithRefresh(false, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle));
                }
                var backgroundCompilerProgress = lifecycle.BackgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved;
                if (backgroundCompilerProgress.Failed)
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("background_compilation_failed", "Unity background shader or compute compilation reported a correlated failure", CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), refreshed ? "compile.refresh_and_wait background_compilation_failed" : "compile.wait_ready background_compilation_failed");
                }
                if (barrier.Unavailable)
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unavailable", "Explicit compilation barrier state is unavailable", CompileBarrierFailureDetailsJson(barrier, refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId)), refreshed ? "compile.refresh_and_wait barrier_unavailable" : "compile.wait_ready barrier_unavailable");
                }
                if (requireExplicitBarrier && (barrier.State == "not_required" || barrier.State == "unknown"))
                {
                    return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unverified", "The expected explicit compilation barrier does not exist", CompileBarrierFailureDetailsJson(barrier, refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId)), refreshed ? "compile.refresh_and_wait barrier_missing" : "compile.wait_ready barrier_missing");
                }
                if (lifecycle.IsReady && barrier.Stable)
                {
                    var prefabImportDiagnostics = PrefabImportDiagnosticsJson(refreshed, barrier, expectedBarrierOperationId);
                    if (!string.IsNullOrEmpty(prefabImportDiagnostics))
                    {
                        return WithForcedHeartbeat(CommandExecutionResult.Error("asset_import_failed", "Unity asset import reported a stale Transform child reference", CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle, prefabImportDiagnostics)), "compile.refresh_and_wait asset_import_failed");
                    }

                    BuiltInCommands.ExpectedScriptImportInspection expectedScriptImports = null;
                    if (expectedScriptPaths != null && expectedScriptPaths.Length > 0)
                    {
                        try
                        {
                            expectedScriptImports = MainThreadDispatcher.Run(() => BuiltInCommands.InspectExpectedScriptImports(expectedScriptPaths), AssetDatabaseRefreshTimeoutMs);
                        }
                        catch (Exception)
                        {
                            var unavailableDetails = AppendJsonObjectFields(
                                CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle),
                                "\"reason\":\"expected_script_inspection_unavailable\",\"write_command_repeated\":false,\"write_replay_allowed\":false");
                            return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unavailable", "Expected script import state could not be inspected on the Unity main thread", unavailableDetails), "compile.refresh_and_wait expected_script_inspection_unavailable");
                        }
                        if (!expectedScriptImports.Verified)
                        {
                            var missingDetails = AppendJsonObjectFields(
                                CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle, prefabImportDiagnostics, expectedScriptImports),
                                "\"reason\":\"expected_script_not_imported\",\"write_command_repeated\":false,\"write_replay_allowed\":false");
                            return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unverified", "One or more expected project scripts were not imported by Unity", missingDetails), "compile.refresh_and_wait expected_script_not_imported");
                        }
                    }

                    return WithForcedHeartbeat(CommandExecutionResult.Ok(CompileStatusJsonWithRefresh(refreshed, false, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle, prefabImportDiagnostics, expectedScriptImports)), refreshed ? "compile.refresh_and_wait ready" : "compile.wait_ready ready");
                }

                if (waitedMs >= timeoutMs)
                {
                    if (backgroundCompilerProgress.BlocksReadiness)
                    {
                        return WithForcedHeartbeat(CommandExecutionResult.Error("command_timeout", "Timed out waiting for authoritative Unity background shader or compute compilation completion", CompileStatusJsonWithRefresh(refreshed, true, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), refreshed ? "compile.refresh_and_wait background_compilation_timed_out" : "compile.wait_ready background_compilation_timed_out");
                    }
                    if (!barrier.Stable)
                    {
                        return WithForcedHeartbeat(CommandExecutionResult.Error("compile_freshness_unverified", "Compilation or its correlated domain reload did not settle before the deadline", CompileBarrierFailureDetailsJson(barrier, refreshed, true, waitedMs, timeoutMs, expectedBarrierOperationId)), refreshed ? "compile.refresh_and_wait barrier_unverified" : "compile.wait_ready barrier_unverified");
                    }
                    return WithForcedHeartbeat(CommandExecutionResult.Ok(CompileStatusJsonWithRefresh(refreshed, true, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)), refreshed ? "compile.refresh_and_wait timed_out" : "compile.wait_ready timed_out");
                }

                if (m_shutdown.Wait(Math.Min(pollIntervalMs, timeoutMs - waitedMs)))
                {
                    return LifecycleShutdownResult(refreshed ? "compile.refresh_and_wait" : "compile.wait_ready", waitedMs, timeoutMs, expectedBarrierOperationId, refreshed);
                }
            }
        }

        internal static string CompileWaitPriorityErrorCode(
            bool isReady,
            bool requireExplicitBarrier,
            bool barrierStable,
            bool terminalCompileFailureWithoutReload,
            bool hasCompileErrors,
            bool packageMetadataUnavailable,
            bool localPackageCodeInventoryUnavailable,
            bool localPackageCodeInventoryStale,
            bool allowPendingInventoryConvergence)
        {
            if (terminalCompileFailureWithoutReload
                || (barrierStable && hasCompileErrors && (isReady || requireExplicitBarrier)))
            {
                return "compilation_failed";
            }
            if (requireExplicitBarrier && !barrierStable)
            {
                return string.Empty;
            }
            if (packageMetadataUnavailable)
            {
                return "package_metadata_unavailable";
            }
            if (localPackageCodeInventoryUnavailable && !allowPendingInventoryConvergence)
            {
                return "local_package_inventory_unavailable";
            }
            if (localPackageCodeInventoryStale && !allowPendingInventoryConvergence)
            {
                return "local_package_inventory_stale";
            }
            return string.Empty;
        }

        static CommandExecutionResult LifecycleShutdownResult(string command, int waitedMs, int timeoutMs, string expectedBarrierOperationId, bool refreshed)
        {
            var validationCommand = string.Equals(command, "validation.after_edit", StringComparison.Ordinal);
            var compileRefreshCommand = string.Equals(command, "compile.refresh_and_wait", StringComparison.Ordinal);
            var barrier = compileRefreshCommand ? ExplicitCompilationBarrier.Snapshot(expectedBarrierOperationId, true) : null;
            var prefabImportDiagnostics = compileRefreshCommand && refreshed ? PrefabImportDiagnosticsJson(true, barrier, expectedBarrierOperationId) : string.Empty;
            var details = "{\"reason\":\"bridge_lifecycle_shutdown\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"waited_ms\":" + Math.Max(0, waitedMs)
                + ",\"timeout_ms\":" + Math.Max(0, timeoutMs)
                + ",\"retryable\":" + JsonUtil.Bool(!validationCommand)
                + (validationCommand ? ValidationDirectSessionFields(command, false) : ",\"write_replay_allowed\":false")
                + (validationCommand ? ",\"lifecycle_status_recheck_allowed\":true" : string.Empty)
                + (string.IsNullOrEmpty(prefabImportDiagnostics) ? string.Empty : ",\"prefab_import_diagnostics\":" + prefabImportDiagnostics)
                + (validationCommand
                    ? ",\"next_valid_actions\":[\"wait_for_bridge_lifecycle_status_to_become_ready\",\"use_compile.status_for_read_only_verification\",\"use_compile.wait_ready_for_read_only_verification\",\"call_gateway_sgg_unity_call_diagnostics.report_problem_if_available\",\"use_cli_report-problem_with_project_summary_and_include-doctor_if_gateway_mcp_unavailable\"]}"
                    : ",\"next_valid_actions\":[\"call_sgg_unity_projects\",\"wait_for_bridge_lifecycle_status_to_become_ready\",\"use_compile.wait_ready_for_read_only_verification\"]}");
            return CommandExecutionResult.Error("unity_reloading", "Unity bridge stopped the active wait for an Editor lifecycle transition", details);
        }

        static CommandExecutionResult PackageMetadataUnavailableResult(string command, string statusJson, bool statusHasNextActions)
        {
            var details = AppendJsonObjectFields(statusJson,
                "\"reason\":\"local_package_metadata_unavailable\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"retryable\":false"
                + (string.Equals(command, "validation.after_edit", StringComparison.Ordinal)
                    ? ValidationDirectSessionFields(command, false)
                    : ",\"write_replay_allowed\":false")
                + (statusHasNextActions ? string.Empty : ",\"next_valid_actions\":[\"package.info\",\"package.list\",\"inspect_or_fix_local_package_manifest_or_upm_resolution\"]"));
            return CommandExecutionResult.Error("package_metadata_unavailable", "Registered local package metadata could not be verified against Unity Package Manager resolution", details);
        }

        static CommandExecutionResult PackageRefreshRequiredResult(string command, string statusJson)
        {
            var details = AppendJsonObjectFields(statusJson,
                "\"reason\":\"explicit_package_refresh_required\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"retryable\":false"
                + ",\"write_replay_allowed\":false"
                + ",\"next_valid_actions\":[\"compile.status\",\"run_compile.refresh_and_wait_only_as_a_new_explicit_action\",\"do_not_auto_repeat_compile.wait_ready\"]");
            return CommandExecutionResult.Error("package_refresh_required", "Package/runtime changes require a new explicit refresh before compile readiness can be trusted", details);
        }

        static CommandExecutionResult LocalPackageCodeInventoryResult(string command, string statusJson, bool statusHasNextActions, bool stale)
        {
            var reason = stale ? "local_package_code_inventory_stale" : "local_package_code_inventory_unavailable";
            var details = AppendJsonObjectFields(statusJson,
                "\"reason\":" + JsonUtil.String(reason)
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"retryable\":false"
                + (string.Equals(command, "validation.after_edit", StringComparison.Ordinal)
                    ? ValidationDirectSessionFields(command, false)
                    : ",\"write_replay_allowed\":false")
                + (statusHasNextActions ? string.Empty : ",\"next_valid_actions\":[\"compile.status\",\"package.info\",\"inspect_local_package_code_inventory\",\"run_compile.refresh_and_wait_only_as_a_new_explicit_action\"]"));
            return CommandExecutionResult.Error(
                stale ? "local_package_inventory_stale" : "local_package_inventory_unavailable",
                stale
                    ? "Local package code inventory differs from Unity AssetDatabase after the observed refresh"
                    : "Local package code inventory could not be compared with Unity AssetDatabase",
                details);
        }

        static CommandExecutionResult WithForcedHeartbeat(CommandExecutionResult result, string reason)
        {
            UnityBridgeRuntime.ForceHeartbeat(reason);
            return result;
        }

        string CompileStatusJsonWithRefresh(bool refreshed, bool timedOut, int waitedMs, int timeoutMs, string expectedBarrierOperationId = "", bool requireExplicitBarrier = false, PackageLifecycleSnapshot lifecycle = null, string prefabImportDiagnostics = null, BuiltInCommands.ExpectedScriptImportInspection expectedScriptImports = null)
        {
            lifecycle = lifecycle ?? m_state.CapturePackageLifecycleSnapshot();
            var json = BuiltInCommands.CompileStatusJson(m_state, timedOut, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle, expectedScriptImports == null || expectedScriptImports.Verified);
            if ((!refreshed && expectedScriptImports == null) || string.IsNullOrEmpty(json) || json[json.Length - 1] != '}')
            {
                return json;
            }

            if (refreshed && prefabImportDiagnostics == null)
            {
                prefabImportDiagnostics = PrefabImportDiagnosticsJson(true, ExplicitCompilationBarrier.Snapshot(expectedBarrierOperationId, requireExplicitBarrier), expectedBarrierOperationId);
            }
            return json.Substring(0, json.Length - 1)
                + (refreshed ? ",\"refreshed\":true" : string.Empty)
                + (string.IsNullOrEmpty(prefabImportDiagnostics) ? string.Empty : ",\"prefab_import_diagnostics\":" + prefabImportDiagnostics)
                + (expectedScriptImports == null ? string.Empty : ",\"expected_script_imports\":" + expectedScriptImports.ToJson())
                + "}";
        }

        static string PrefabImportDiagnosticsJson(bool refreshed, ExplicitCompilationBarrierSnapshot barrier, string expectedOperationId)
        {
            var operation = barrier == null ? null : barrier.Operation;
            if (!refreshed)
            {
                return string.Empty;
            }

            if (!ExplicitCompilationBarrierState.IsValidOperationId(expectedOperationId))
            {
                return string.Empty;
            }
            var operationId = expectedOperationId;
            var matchingOperation = operation != null && operation.RequestIssued
                && string.Equals(operation.OperationId, operationId, StringComparison.Ordinal)
                && operation.RequestIssuedTicks > 0;
            if (!matchingOperation)
            {
                return string.Empty;
            }
            try
            {
                var untilTicks = operation.Finished && operation.FinishedTicks > 0
                    ? operation.FinishedTicks
                    : DateTime.UtcNow.Ticks;
                return ConsoleLogBuffer.PrefabImportDiagnosticsJson(
                    new DateTime(operation.RequestIssuedTicks, DateTimeKind.Utc),
                    new DateTime(untilTicks, DateTimeKind.Utc),
                    operationId);
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        }

        static string CompileBarrierFailureDetailsJson(ExplicitCompilationBarrierSnapshot barrier, bool refreshed, bool timedOut, int waitedMs, int timeoutMs, string expectedOperationId)
        {
            var prefabImportDiagnostics = PrefabImportDiagnosticsJson(refreshed, barrier, expectedOperationId);
            return "{\"ready\":false,\"compilable\":false"
                + ",\"refreshed\":" + JsonUtil.Bool(refreshed)
                + ",\"timed_out\":" + JsonUtil.Bool(timedOut)
                + ",\"waited_ms\":" + Math.Max(0, waitedMs)
                + ",\"timeout_ms\":" + Math.Max(0, timeoutMs)
                + ",\"compile_barrier\":" + barrier.ToJson()
                + (string.IsNullOrEmpty(prefabImportDiagnostics) ? string.Empty : ",\"prefab_import_diagnostics\":" + prefabImportDiagnostics)
                + ",\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\"]}";
        }

        string CompileRefreshTimeoutDetailsJson(string expectedBarrierOperationId, string workState, bool lateExecutionPossible, int queueDepthAtTimeout, string timeoutKind)
        {
            var cancelledBeforeStart = string.Equals(workState, "cancelled_before_start", StringComparison.Ordinal) && !lateExecutionPossible;
            var statusJson = BuiltInCommands.CompileStatusJson(m_state, true, AssetDatabaseRefreshTimeoutMs, AssetDatabaseRefreshTimeoutMs, cancelledBeforeStart ? string.Empty : expectedBarrierOperationId, !cancelledBeforeStart);
            var barrier = cancelledBeforeStart ? null : ExplicitCompilationBarrier.Snapshot(expectedBarrierOperationId, true);
            var prefabImportDiagnostics = cancelledBeforeStart ? string.Empty : PrefabImportDiagnosticsJson(true, barrier, expectedBarrierOperationId);
            var definitiveNonDispatchJson = cancelledBeforeStart
                ? ",\"refresh_started\":false"
                    + ",\"compile_barrier_created\":false"
                    + ",\"script_compilation_requested\":false"
                    + ",\"prior_compile_barrier_is_conflict\":false"
                : string.Empty;
            return AppendJsonObjectFields(statusJson,
                "\"timeout_kind\":" + JsonUtil.String(timeoutKind)
                + ",\"failure_phase\":" + JsonUtil.String(cancelledBeforeStart ? "main_thread_dispatch" : "asset_database_refresh")
                + ",\"refresh_completed\":false"
                + ",\"compile_status_checked_after_timeout\":true"
                + ",\"timed_out_work_state\":" + JsonUtil.String(workState)
                + ",\"late_execution_possible\":" + JsonUtil.Bool(lateExecutionPossible)
                + ",\"main_thread_dispatcher_queue_depth\":" + Math.Max(0, queueDepthAtTimeout)
                + ",\"asset_database_refresh_timeout_ms\":" + AssetDatabaseRefreshTimeoutMs
                + ",\"refresh_may_still_run_after_timeout\":" + JsonUtil.Bool(lateExecutionPossible)
                + (string.IsNullOrEmpty(prefabImportDiagnostics) ? string.Empty : ",\"prefab_import_diagnostics\":" + prefabImportDiagnostics)
                + definitiveNonDispatchJson
                + ",\"write_command_repeated\":false"
                + ",\"write_replay_allowed\":false"
                + ",\"recovery_contract\":\"sgg.unity_mcp.compile_refresh_dispatch_timeout.v1\"");
        }

        string ValidationRefreshTimeoutDetailsJson(string argsJson, string workState, bool lateExecutionPossible, int queueDepthAtTimeout, string timeoutKind)
        {
            var requestedTimeoutMs = Clamp(JsonUtil.ExtractInt(argsJson, "timeout_ms", 120000), 1, 300000);
            var requestedPollIntervalMs = Clamp(JsonUtil.ExtractInt(argsJson, "poll_interval_ms", 250), 50, 5000);
            return AppendJsonObjectFields(BuiltInCommands.ValidationStatusJson(m_state, "after_edit", false, true, AssetDatabaseRefreshTimeoutMs, AssetDatabaseRefreshTimeoutMs),
                "\"timeout_kind\":" + JsonUtil.String(timeoutKind)
                + ",\"failure_phase\":\"asset_database_refresh\""
                + ",\"refresh_completed\":false"
                + ",\"validation_completed\":false"
                + ",\"compile_ready_is_not_validation_success\":true"
                + ",\"direct_bridge_report_problem_available\":false"
                + ValidationProblemReportFields()
                + ",\"write_command_repeated\":false"
                + ",\"write_replay_allowed\":false"
                + ",\"timed_out_work_state\":" + JsonUtil.String(workState)
                + ",\"late_execution_possible\":" + JsonUtil.Bool(lateExecutionPossible)
                + ",\"main_thread_dispatcher_queue_depth\":" + Math.Max(0, queueDepthAtTimeout)
                + ",\"asset_database_refresh_timeout_ms\":" + AssetDatabaseRefreshTimeoutMs
                + ",\"requested_timeout_ms\":" + requestedTimeoutMs
                + ",\"requested_poll_interval_ms\":" + requestedPollIntervalMs
                + ",\"refresh_may_still_run_after_timeout\":" + JsonUtil.Bool(lateExecutionPossible)
                + ",\"recovery_contract\":\"sgg.unity_mcp.validation_after_edit_timeout.v1\"");
        }

        string ValidationWaitTimeoutDetailsJson(int waitedMs, int timeoutMs, PackageLifecycleSnapshot lifecycle)
        {
            return AppendJsonObjectFields(BuiltInCommands.ValidationStatusJson(m_state, "after_edit", true, true, waitedMs, timeoutMs, string.Empty, false, lifecycle),
                "\"timeout_kind\":\"validation_wait_ready_timeout\""
                + ",\"failure_phase\":\"validation_wait_ready\""
                + ",\"refresh_completed\":true"
                + ",\"validation_completed\":false"
                + ",\"compile_ready_is_not_validation_success\":true"
                + ",\"direct_bridge_report_problem_available\":false"
                + ValidationProblemReportFields()
                + ",\"write_command_repeated\":false"
                + ",\"write_replay_allowed\":false"
                + ",\"requested_timeout_ms\":" + timeoutMs
                + ",\"waited_ms\":" + waitedMs
                + ",\"recovery_contract\":\"sgg.unity_mcp.validation_after_edit_timeout.v1\"");
        }

        static string AppendJsonObjectFields(string json, string fields)
        {
            if (string.IsNullOrEmpty(json) || json[json.Length - 1] != '}' || string.IsNullOrEmpty(fields))
            {
                return json;
            }

            return json.Substring(0, json.Length - 1) + "," + fields + "}";
        }

        static string ValidationDirectSessionFields(string command, bool includeNextActions)
        {
            if (!string.Equals(command, "validation.after_edit", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return ",\"write_command_repeated\":false"
                + ",\"write_replay_allowed\":false"
                + ",\"direct_bridge_report_problem_available\":false"
                + ValidationProblemReportFields()
                + (includeNextActions
                    ? ",\"next_valid_actions\":[\"compile.status\",\"compile.wait_ready\",\"call_gateway_sgg_unity_call_diagnostics.report_problem_if_available\",\"use_cli_report-problem_with_project_summary_and_include-doctor_if_gateway_mcp_unavailable\"]"
                    : string.Empty);
        }

        static string ValidationProblemReportFields()
        {
            return ",\"problem_report_tool\":\"sgg_unity_call\""
                + ",\"problem_report_command\":\"diagnostics.report_problem\""
                + ",\"problem_report_availability\":\"gateway_feature_and_policy_dependent\""
                + ",\"problem_report_requires_gateway_session\":true"
                + ",\"offline_problem_report_cli\":\"sgg-unity-mcp-gateway report-problem --project <project-id> --summary <bounded-summary> --include-doctor\"";
        }

        string LogsJson(HttpListenerRequest request)
        {
            var kind = request.QueryString["kind"] ?? "console";
            if (kind != "console")
            {
                return "{\"schema_version\":1,\"entries\":[]}";
            }

            var limitValue = request.QueryString["limit"];
            var limit = int.TryParse(limitValue, out var parsedLimit) ? parsedLimit : 200;
            return "{\"schema_version\":1,\"entries\":" + BuiltInCommands.ConsoleLogEntriesArrayJson(limit)
                + ",\"buffer\":" + BuiltInCommands.ConsoleLogBufferMetadataJson() + "}";
        }

        static string ReadBodyBounded(HttpListenerRequest request)
        {
            if (request.ContentLength64 > BridgeProtocol.MaxInvokeBytes)
            {
                throw new BridgeRequestTooLargeException();
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            while (true)
            {
                var read = request.InputStream.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                if (buffer.Length + read > BridgeProtocol.MaxInvokeBytes) throw new BridgeRequestTooLargeException();
                buffer.Write(chunk, 0, read);
            }
            return new UTF8Encoding(false, true).GetString(buffer.ToArray());
        }

        sealed class BridgeRequestTooLargeException : Exception { }

        static void WriteInvokeJson(HttpListenerResponse response, BridgeInvokeRequest request, string json)
        {
            var dynamicCSharpReadonly = request != null && request.Command == "editor.execute_csharp.readonly";
            var traceResponse = request != null && (request.Command == "compile.wait_ready" || request.Command == "compile.refresh_and_wait" || request.Command == "validation.after_edit" || request.Command == "test.run" || dynamicCSharpReadonly);
            var timer = traceResponse ? Stopwatch.StartNew() : null;
            var detail = traceResponse ? InvokeResponseTraceDetail(request, json) : string.Empty;
            if (dynamicCSharpReadonly)
            {
                DynamicCSharpReadonlyResultStore.MarkResponseWrite(request.ArgsJson, "running", 0);
            }
            if (traceResponse)
            {
                BridgeReloadTrace.Log("bridge.invoke.response.write.start", detail);
            }

            try
            {
                WriteJson(response, 200, json);
                if (traceResponse)
                {
                    BridgeReloadTrace.Log("bridge.invoke.response.write.end", detail, timer.ElapsedMilliseconds);
                }
                if (dynamicCSharpReadonly)
                {
                    DynamicCSharpReadonlyResultStore.MarkResponseWrite(request.ArgsJson, "completed", timer.ElapsedMilliseconds);
                }
            }
            catch (Exception exception)
            {
                if (traceResponse)
                {
                    BridgeReloadTrace.Log("bridge.invoke.response.write.error", detail + ";error_type=" + exception.GetType().Name, timer.ElapsedMilliseconds);
                }
                if (dynamicCSharpReadonly)
                {
                    DynamicCSharpReadonlyResultStore.MarkResponseWrite(request.ArgsJson, "failed", timer.ElapsedMilliseconds);
                }

                throw;
            }
        }

        static string InvokeResponseTraceDetail(BridgeInvokeRequest request, string json)
        {
            var command = request == null ? string.Empty : request.Command;
            var operationId = command == "compile.refresh_and_wait"
                ? SafeBarrierOperationId(JsonUtil.ExtractString(request.ArgsJson, "_compile_barrier_expectation_id"))
                : string.Empty;
            return "command=" + command
                + ";barrier_operation_id=" + (command == "compile.refresh_and_wait" ? string.IsNullOrEmpty(operationId) ? "unavailable" : "present" : "not_applicable")
                + ";response_bytes=" + Encoding.UTF8.GetByteCount(json ?? string.Empty);
        }

        static string SafeBarrierOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
            {
                return string.Empty;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return string.Empty;
                }
            }

            return value;
        }

        static void WriteJson(HttpListenerResponse response, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;
            response.Close(bytes, true);
        }

        static string JsonString(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        static void AppendOptionalString(StringBuilder builder, string name, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                builder.Append(',').Append(JsonString(name)).Append(':').Append(JsonString(value));
            }
        }

        static void AppendOptionalStringArray(StringBuilder builder, string name, string[] values)
        {
            if (values == null || values.Length == 0)
            {
                return;
            }

            builder.Append(',').Append(JsonString(name)).Append(':').Append('[');
            for (var index = 0; index < values.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonString(values[index]));
            }

            builder.Append(']');
        }

        static void AppendOptionalBool(StringBuilder builder, string name, bool value)
        {
            if (value)
            {
                builder.Append(',').Append(JsonString(name)).Append(':').Append("true");
            }
        }

        static void AppendOptionalObject(StringBuilder builder, string name, string json)
        {
            if (!string.IsNullOrEmpty(json))
            {
                builder.Append(',').Append(JsonString(name)).Append(':').Append(json);
            }
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        static int PickFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            var port = endpoint.Port;
            listener.Stop();
            return port;
        }
    }
}
