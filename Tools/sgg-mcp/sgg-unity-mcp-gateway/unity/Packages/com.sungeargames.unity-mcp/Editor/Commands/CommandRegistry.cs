using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor.PackageManager;

namespace SGG.UnityMCP.Editor.Commands
{
    public sealed class CommandRegistry
    {
        readonly BridgeRuntimeState m_state;
        const string BuiltInPackageId = "com.sungeargames.unity-mcp";
        const int MaxManifestDiagnostics = 50;
        const int MaxProjectManifestCandidates = 100;
        const int MaxAssemblyTimingRows = 40;
        readonly Dictionary<string, RegisteredCommand> m_commands = new Dictionary<string, RegisteredCommand>(StringComparer.Ordinal);
        readonly List<CommandDescriptor> m_descriptors = new List<CommandDescriptor>();
        readonly List<ManifestScanDiagnostic> m_manifestDiagnostics = new List<ManifestScanDiagnostic>();
        readonly List<ProjectManifestCandidate> m_projectManifestCandidates = new List<ProjectManifestCandidate>();
        readonly object m_rebuildGate = new object();
        CatalogSnapshot m_snapshot = CatalogSnapshot.Empty;
        string m_buildCommandsHash = string.Empty;
        ManifestHandlerResolver m_manifestHandlers;

        CommandRegistry(BridgeRuntimeState state)
        {
            m_state = state;
        }

        public IReadOnlyList<CommandDescriptor> Descriptors => CaptureSnapshot().Descriptors;
        public IReadOnlyList<ManifestScanDiagnostic> ManifestDiagnostics => CaptureSnapshot().ManifestDiagnostics;
        public IReadOnlyList<ProjectManifestCandidate> ProjectManifestCandidates => CaptureSnapshot().ProjectManifestCandidates;
        public string CommandsHash => CaptureSnapshot().CommandsHash;
        public long CatalogGeneration => CaptureSnapshot().CatalogGeneration;

        public static CommandRegistry Build(BridgeRuntimeState state)
        {
            var registry = new CommandRegistry(state);
            registry.Rebuild();
            return registry;
        }

        public void Rebuild()
        {
            lock (m_rebuildGate)
            {
                var candidate = new CommandRegistry(m_state);
                candidate.BuildCandidate();
                var previous = CaptureSnapshot();
                var snapshot = candidate.CreateSnapshot(Math.Max(1, previous.CatalogGeneration + 1));
                Volatile.Write(ref m_snapshot, snapshot);
                BridgeReloadTrace.Log("command_registry.publish", "catalog_generation=" + snapshot.CatalogGeneration + ";commands_hash=" + snapshot.CommandsHash + ";command_count=" + snapshot.PublishedDescriptors.Count);
            }
        }

        void BuildCandidate()
        {
            var total = Stopwatch.StartNew();
            BridgeReloadTrace.Log("command_registry.rebuild.start");
            try
            {
                m_commands.Clear();
                m_descriptors.Clear();
                m_manifestDiagnostics.Clear();
                m_projectManifestCandidates.Clear();

                // Unity 6.6 discovery includes TypeCache/CompilationPipeline work. Capture it
                // once for this candidate, rather than rediscovering it for every manifest handler.
                var assemblies = EditorAssemblyDiscovery.LoadedAssemblies();
                m_manifestHandlers = new ManifestHandlerResolver(assemblies);

                var step = Stopwatch.StartNew();
                BridgeReloadTrace.Log("command_registry.attrib_scan.start");
                RegisterAttributedCommands(assemblies);
                BridgeReloadTrace.Log("command_registry.attrib_scan.end", step.ElapsedMilliseconds);

                step.Restart();
                BridgeReloadTrace.Log("command_registry.manifest_scan.start");
                CommandManifestScanner.ScanInto(this);
                BridgeReloadTrace.Log("command_registry.manifest_scan.end", step.ElapsedMilliseconds);

                RebuildDescriptorCache();
                BridgeReloadTrace.Log("command_registry.rebuild.end", total.ElapsedMilliseconds);
            }
            catch (Exception exception)
            {
                BridgeReloadTrace.LogError("command_registry.rebuild.error", exception);
                throw;
            }
        }

        public CommandExecutionResult Invoke(string requestId, string project, string commandId, string argsJson, string settingsJson)
        {
            return Invoke(CaptureSnapshot(), requestId, project, commandId, argsJson, settingsJson);
        }

        internal CommandExecutionResult Invoke(CatalogSnapshot snapshot, string requestId, string project, string commandId, string argsJson, string settingsJson)
        {
            if (string.IsNullOrEmpty(commandId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Invoke request must include command");
            }

            snapshot = snapshot ?? CaptureSnapshot();
            if (!snapshot.TryGetCommand(commandId, out var command))
            {
                return CommandExecutionResult.Error("command_not_found", "Command not found");
            }

            var context = new McpCommandContext(m_state, requestId, project, argsJson, settingsJson);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = MainThreadDispatcher.Run(() => command.Invoke(context), command.Descriptor.TimeoutMs);
                result.DurationMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
                return result;
            }
            catch (MainThreadDispatchTimeoutException exception)
            {
                return CommandExecutionResult.Error("command_timeout", "Unity command timed out", CommandTimeoutDetailsJson(command.Descriptor, exception, m_state));
            }
            catch (TargetInvocationException exception)
            {
                return UnityException(exception.InnerException ?? exception);
            }
            catch (Exception exception)
            {
                return UnityException(exception);
            }
            finally
            {
                stopwatch.Stop();
            }
        }

        static string CommandTimeoutDetailsJson(CommandDescriptor descriptor, MainThreadDispatchTimeoutException timeout, BridgeRuntimeState state)
        {
            var isDynamicCSharp = descriptor != null && (descriptor.Id == "editor.execute_csharp" || descriptor.Id == "editor.execute_csharp.readonly");
            var isEditorWindowsList = descriptor != null && descriptor.Id == "editor.windows.list";
            return "{\"command\":" + JsonUtil.String(descriptor == null ? string.Empty : descriptor.Id)
                + ",\"timeout_ms\":" + (descriptor == null ? 0 : descriptor.TimeoutMs)
                + ",\"timeout_context\":" + JsonUtil.String(isDynamicCSharp ? "dynamic_csharp_main_thread_execution" : "unity_main_thread_execution")
                + ",\"bounded_timeout_details\":true"
                + ",\"main_thread_dispatch\":{\"queue_depth_at_timeout\":" + (timeout == null ? MainThreadDispatcher.QueueDepth : timeout.QueueDepthAtTimeout)
                + ",\"timed_out_work_state\":" + JsonUtil.String(timeout == null ? "unknown" : timeout.WorkState)
                + ",\"late_execution_possible\":" + JsonUtil.Bool(timeout != null && timeout.LateExecutionPossible)
                + ",\"main_thread_interrupted\":false}"
                + ",\"main_thread_snapshot\":{\"last_editor_update_age_ms\":" + (state == null ? 0 : state.LastEditorUpdateAgeMs)
                + ",\"status_duration_ms\":" + (state == null ? 0 : state.StatusDurationMs)
                + ",\"editor_status\":" + JsonUtil.String(state == null ? "unknown" : state.Status)
                + ",\"is_compiling\":" + JsonUtil.Bool(state != null && state.IsCompiling)
                + ",\"is_updating\":" + JsonUtil.Bool(state != null && state.IsUpdating)
                + ",\"is_playing\":" + JsonUtil.Bool(state != null && state.IsPlaying) + "}"
                + (isEditorWindowsList ? ",\"compile_progress_window\":" + CompileProgressWindowDetector.StatusJson() : string.Empty)
                + ",\"auto_retry_attempted\":false"
                + ",\"command_replayed\":false"
                + ",\"suggested_recovery_actions\":" + (isDynamicCSharp
                    ? "[\"call_compile.status\",\"retry_after_unity_idle\",\"reduce_snippet_work\",\"prefer_regular_catalog_command_or_job\"]"
                    : isEditorWindowsList
                        ? "[\"inspect_timeout_evidence\",\"wait_for_fresh_heartbeat\",\"operator_focus_existing_unity_window_if_needed\",\"retry_editor.windows.list_explicitly_once\"]"
                        : "[\"call_compile.status\",\"retry_after_unity_idle\"]")
                + ",\"recovery_hint\":" + JsonUtil.String(isDynamicCSharp
                    ? "Dynamic C# ran on the Unity main thread and exceeded its command timeout; retry after Unity is idle, reduce snippet work, or prefer a regular command/job workflow when available."
                    : isEditorWindowsList
                        ? "Editor window inspection exceeded its main-thread timeout. Inspect the cached lifecycle and progress evidence, wait for a fresh heartbeat, optionally ask the operator to focus the already-open Unity window, then retry explicitly once without opening or closing windows."
                        : "The Unity command exceeded its command timeout; retry after Unity is idle or use a job-capable command when available.") + "}";
        }

        internal void RegisterManifestCommand(CommandDescriptor descriptor, string handlerName)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Id))
            {
                return;
            }

            if (string.IsNullOrEmpty(handlerName))
            {
                if (m_commands.TryGetValue(descriptor.Id, out var existing))
                {
                    if (string.Equals(existing.Descriptor.PackageId, descriptor.PackageId, StringComparison.Ordinal))
                    {
                        existing.Descriptor = descriptor;
                    }
                    else
                    {
                        AddManifestDiagnostic("duplicate_command_id", descriptor.PackageId, descriptor.PackageVersion, descriptor.ManifestPath, "Manifest cannot replace a command owned by another package");
                    }
                }
                return;
            }

            var method = ResolveMethod(handlerName);
            if (method == null)
            {
                RegisterUnavailableManifestCommand(descriptor, handlerName);
                return;
            }

            RegisterCommand(descriptor, method);
        }

        internal void AddManifestDiagnostic(string code, string packageId, string packageVersion, string manifestPath, string message)
        {
            if (m_manifestDiagnostics.Count >= MaxManifestDiagnostics)
            {
                return;
            }

            m_manifestDiagnostics.Add(new ManifestScanDiagnostic
            {
                Code = string.IsNullOrEmpty(code) ? "manifest_scan_failed" : code,
                PackageId = packageId ?? string.Empty,
                PackageVersion = packageVersion ?? string.Empty,
                ManifestPath = manifestPath ?? string.Empty,
                Message = Bounded(FirstLine(message), 300),
            });
        }

        internal void AddProjectManifestCandidate(string manifestPath, string reason)
        {
            if (m_projectManifestCandidates.Count >= MaxProjectManifestCandidates)
            {
                return;
            }

            m_projectManifestCandidates.Add(new ProjectManifestCandidate
            {
                ManifestPath = manifestPath ?? string.Empty,
                Loadable = false,
                Reason = string.IsNullOrEmpty(reason) ? "project_asset_manifests_are_not_registered" : reason,
            });
        }

        void RegisterUnavailableManifestCommand(CommandDescriptor descriptor, string handlerName)
        {
            if (m_commands.ContainsKey(descriptor.Id))
            {
                return;
            }

            descriptor.ResultShape = "handler_unavailable";
            descriptor.Description = string.IsNullOrEmpty(descriptor.Description)
                ? "Manifest command handler is not available in the loaded editor assemblies."
                : descriptor.Description + " Handler is not available in the loaded editor assemblies.";
            m_commands[descriptor.Id] = new RegisteredCommand(descriptor, _ => CommandExecutionResult.Error("command_handler_unavailable", "Manifest handler not found: " + handlerName));
        }

        void RegisterAttributedCommands(Assembly[] assemblies)
        {
            var candidateTypes = AttributedCommandDiscovery.CandidateTypes<McpCommandAttribute>();
            var candidateAssemblies = new HashSet<Assembly>();
            foreach (var type in candidateTypes) candidateAssemblies.Add(type.Assembly);
            var timings = new List<AssemblyScanTiming>();
            foreach (var assembly in assemblies)
            {
                var timing = new AssemblyScanTiming { AssemblyName = SafeAssemblyName(assembly) };
                var stopwatch = Stopwatch.StartNew();
                timings.Add(timing);
                if (assembly != null && assembly.IsDynamic)
                {
                    timing.Status = "skipped_dynamic";
                    stopwatch.Stop();
                    timing.DurationMs = stopwatch.ElapsedMilliseconds;
                    continue;
                }

                if (!candidateAssemblies.Contains(assembly))
                {
                    timing.Status = "ok";
                    stopwatch.Stop();
                    timing.DurationMs = stopwatch.ElapsedMilliseconds;
                    continue;
                }

                var types = SafeGetTypes(assembly, out timing.Status, out timing.ErrorType);
                foreach (var type in types)
                {
                    timing.TypeCount++;
                    if (type == null || !candidateTypes.Contains(type))
                    {
                        continue;
                    }

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                    {
                        var attribute = method.GetCustomAttribute<McpCommandAttribute>();
                        if (attribute == null)
                        {
                            continue;
                        }

                        timing.AttributedMethodCount++;
                        RegisterCommand(DescriptorFromAttribute(attribute, method), method);
                    }
                }

                stopwatch.Stop();
                timing.DurationMs = stopwatch.ElapsedMilliseconds;
            }

            LogAssemblyScanTimings(timings);
        }

        void RegisterCommand(CommandDescriptor descriptor, MethodInfo method)
        {
            var handler = CreateHandler(method);
            if (handler == null)
            {
                UnityEngine.Debug.LogWarning("SGG Unity MCP command has unsupported handler signature: " + descriptor.Id);
                return;
            }

            if (m_commands.ContainsKey(descriptor.Id))
            {
                var existing = m_commands[descriptor.Id];
                if (IsBuiltInDescriptor(descriptor) && !IsBuiltInDescriptor(existing.Descriptor))
                {
                    m_commands[descriptor.Id] = new RegisteredCommand(descriptor, handler);
                    return;
                }
                UnityEngine.Debug.LogWarning("SGG Unity MCP duplicate command ignored: " + descriptor.Id);
                return;
            }

            m_commands[descriptor.Id] = new RegisteredCommand(descriptor, handler);
        }

        static bool IsBuiltInDescriptor(CommandDescriptor descriptor)
        {
            return descriptor != null
                && string.Equals(descriptor.PackageId, BuiltInPackageId, StringComparison.Ordinal)
                && string.Equals(descriptor.Trust, "builtin", StringComparison.Ordinal);
        }

        void RebuildDescriptorCache()
        {
            m_descriptors.Clear();
            m_descriptors.AddRange(DescriptorsSorted());
            m_buildCommandsHash = ComputeCommandsHash(m_descriptors);
        }

        CatalogSnapshot CreateSnapshot(long generation)
        {
            var descriptors = m_descriptors.Select(CloneDescriptor).ToArray();
            var descriptorsById = descriptors.ToDictionary(descriptor => descriptor.Id, StringComparer.Ordinal);
            var commands = new Dictionary<string, RegisteredCommand>(StringComparer.Ordinal);
            foreach (var pair in m_commands)
            {
                commands[pair.Key] = pair.Value.CloneWithDescriptor(descriptorsById[pair.Key]);
            }
            return new CatalogSnapshot(
                generation,
                m_buildCommandsHash,
                commands,
                descriptors,
                m_manifestDiagnostics.Select(CloneManifestDiagnostic).ToArray(),
                m_projectManifestCandidates.Select(CloneProjectManifestCandidate).ToArray());
        }

        public CatalogSnapshot CaptureSnapshot()
        {
            return Volatile.Read(ref m_snapshot);
        }

        static CommandDescriptor CloneDescriptor(CommandDescriptor descriptor)
        {
            return new CommandDescriptor
            {
                Id = descriptor.Id,
                Title = descriptor.Title,
                Description = descriptor.Description,
                Package = descriptor.Package,
                Risk = descriptor.Risk,
                Idempotency = descriptor.Idempotency,
                TimeoutMs = descriptor.TimeoutMs,
                SchemaJson = descriptor.SchemaJson,
                Tags = descriptor.Tags == null ? Array.Empty<string>() : (string[])descriptor.Tags.Clone(),
                Capability = descriptor.Capability,
                RequiresEditorStatus = descriptor.RequiresEditorStatus == null ? Array.Empty<string>() : (string[])descriptor.RequiresEditorStatus.Clone(),
                ResultShape = descriptor.ResultShape,
                OutputLimitJson = descriptor.OutputLimitJson,
                SupportsJob = descriptor.SupportsJob,
                SupportsCancel = descriptor.SupportsCancel,
                WrapperWaitJson = descriptor.WrapperWaitJson,
                FeatureFlag = descriptor.FeatureFlag,
                Source = descriptor.Source,
                PackageId = descriptor.PackageId,
                PackageVersion = descriptor.PackageVersion,
                ManifestPath = descriptor.ManifestPath,
                Trust = descriptor.Trust,
            };
        }

        static ManifestScanDiagnostic CloneManifestDiagnostic(ManifestScanDiagnostic diagnostic)
        {
            return new ManifestScanDiagnostic { Code = diagnostic.Code, PackageId = diagnostic.PackageId, PackageVersion = diagnostic.PackageVersion, ManifestPath = diagnostic.ManifestPath, Message = diagnostic.Message };
        }

        static ProjectManifestCandidate CloneProjectManifestCandidate(ProjectManifestCandidate candidate)
        {
            return new ProjectManifestCandidate { ManifestPath = candidate.ManifestPath, Loadable = candidate.Loadable, Reason = candidate.Reason };
        }

        IEnumerable<CommandDescriptor> DescriptorsSorted()
        {
            var descriptors = new List<CommandDescriptor>();
            foreach (var command in m_commands.Values)
            {
                descriptors.Add(command.Descriptor);
            }

            descriptors.Sort((left, right) => string.Compare(left.Id, right.Id, StringComparison.Ordinal));
            return descriptors;
        }

        static CommandDescriptor DescriptorFromAttribute(McpCommandAttribute attribute, MethodInfo method)
        {
            var assembly = method.DeclaringType == null ? null : method.DeclaringType.Assembly;
            var packageInfo = assembly == null ? null : PackageInfo.FindForAssembly(assembly);
            var builtIn = assembly == typeof(CommandRegistry).Assembly;
            var packageId = builtIn ? BuiltInPackageId : packageInfo?.name;
            if (string.IsNullOrEmpty(packageId))
            {
                packageId = "project:" + SafeAssemblyName(assembly);
            }

            var displayPackage = string.IsNullOrEmpty(attribute.Package) ? packageId : attribute.Package;

            return new CommandDescriptor
            {
                Id = attribute.Id,
                Title = string.IsNullOrEmpty(attribute.Title) ? attribute.Id : attribute.Title,
                Description = attribute.Description ?? string.Empty,
                Package = displayPackage,
                Risk = string.IsNullOrEmpty(attribute.Risk) ? "read" : attribute.Risk,
                Idempotency = string.IsNullOrEmpty(attribute.Idempotency) ? "safe" : attribute.Idempotency,
                TimeoutMs = attribute.TimeoutMs <= 0 ? 10000 : attribute.TimeoutMs,
                SchemaJson = string.IsNullOrEmpty(attribute.SchemaJson) ? "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" : attribute.SchemaJson,
                Tags = attribute.Tags ?? new string[0],
                Capability = attribute.Capability ?? string.Empty,
                RequiresEditorStatus = attribute.RequiresEditorStatus ?? new string[0],
                ResultShape = attribute.ResultShape ?? string.Empty,
                OutputLimitJson = attribute.OutputLimitJson ?? string.Empty,
                SupportsJob = attribute.SupportsJob,
                SupportsCancel = attribute.SupportsCancel,
                WrapperWaitJson = attribute.WrapperWaitJson ?? string.Empty,
                FeatureFlag = attribute.FeatureFlag ?? string.Empty,
                Source = builtIn ? "builtin" : "attribute",
                PackageId = packageId,
                PackageVersion = packageInfo?.version ?? string.Empty,
                Trust = builtIn ? "builtin" : "untrusted_package",
            };
        }

        static string SafeAssemblyName(Assembly assembly)
        {
            var name = assembly == null ? string.Empty : assembly.GetName().Name;
            if (string.IsNullOrEmpty(name))
            {
                return "unknown";
            }

            var builder = new StringBuilder(Math.Min(name.Length, 80));
            for (var index = 0; index < name.Length && builder.Length < 80; index++)
            {
                var character = name[index];
                builder.Append(char.IsLetterOrDigit(character) || character == '.' || character == '_' || character == '-' ? character : '-');
            }

            return builder.Length == 0 ? "unknown" : builder.ToString();
        }

        static Func<McpCommandContext, CommandExecutionResult> CreateHandler(MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (method.ReturnType == typeof(CommandExecutionResult))
            {
                if (parameters.Length == 0)
                {
                    return _ => (CommandExecutionResult)method.Invoke(null, Array.Empty<object>());
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(McpCommandContext))
                {
                    return context => (CommandExecutionResult)method.Invoke(null, new object[] { context });
                }
            }

            if (method.ReturnType == typeof(string))
            {
                if (parameters.Length == 0)
                {
                    return _ => RawJsonOk((string)method.Invoke(null, Array.Empty<object>()));
                }

                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    return context => RawJsonOk((string)method.Invoke(null, new object[] { context.ArgsJson }));
                }

                if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string))
                {
                    return context => RawJsonOk((string)method.Invoke(null, new object[] { context.ArgsJson, context.SettingsJson }));
                }
            }

            return null;
        }

        static CommandExecutionResult RawJsonOk(string dataJson)
        {
            return CommandExecutionResult.Ok(string.IsNullOrEmpty(dataJson) ? "{}" : dataJson);
        }

        MethodInfo ResolveMethod(string handlerName)
        {
            return m_manifestHandlers.Resolve(handlerName);
        }

        static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            string status;
            string errorType;
            return SafeGetTypes(assembly, out status, out errorType);
        }

        static Type[] SafeGetTypes(Assembly assembly, out string status, out string errorType)
        {
            status = "ok";
            errorType = string.Empty;
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                status = "partial_type_load";
                errorType = exception.GetType().Name;
                return exception.Types;
            }
        }

        static void LogAssemblyScanTimings(List<AssemblyScanTiming> timings)
        {
            var selected = new List<AssemblyScanTiming>();
            foreach (var timing in timings)
            {
                if (timing.AttributedMethodCount > 0 || timing.Status != "ok")
                {
                    AddSelectedTiming(selected, timing);
                }
            }

            var byDuration = new List<AssemblyScanTiming>(timings);
            byDuration.Sort((left, right) => right.DurationMs.CompareTo(left.DurationMs));
            foreach (var timing in byDuration)
            {
                AddSelectedTiming(selected, timing);
            }

            foreach (var timing in selected)
            {
                BridgeReloadTrace.Log("command_registry.attrib_scan.assembly", AssemblyScanDetail(timing), timing.DurationMs);
            }

            if (selected.Count < timings.Count)
            {
                BridgeReloadTrace.Log("command_registry.attrib_scan.assembly_rows_truncated", "assemblies_total=" + timings.Count
                    + ";rows_logged=" + selected.Count
                    + ";rows_omitted=" + (timings.Count - selected.Count)
                    + ";selection=commands_errors_then_slowest");
            }
        }

        static void AddSelectedTiming(List<AssemblyScanTiming> selected, AssemblyScanTiming timing)
        {
            if (selected.Count >= MaxAssemblyTimingRows || selected.Contains(timing))
            {
                return;
            }

            selected.Add(timing);
        }

        static string AssemblyScanDetail(AssemblyScanTiming timing)
        {
            var detail = "assembly=" + timing.AssemblyName
                + ";type_count=" + timing.TypeCount
                + ";attributed_method_count=" + timing.AttributedMethodCount
                + ";status=" + timing.Status;
            if (!string.IsNullOrEmpty(timing.ErrorType))
            {
                detail += ";error_type=" + timing.ErrorType;
            }

            return detail;
        }

        static string ComputeCommandsHash(IReadOnlyList<CommandDescriptor> descriptors)
        {
            var builder = new StringBuilder();
            foreach (var descriptor in descriptors)
            {
                AppendHashField(builder, descriptor.Id);
                AppendHashField(builder, descriptor.Title);
                AppendHashField(builder, descriptor.Description);
                AppendHashField(builder, descriptor.Package);
                AppendHashField(builder, descriptor.Risk);
                AppendHashField(builder, descriptor.Idempotency);
                AppendHashField(builder, descriptor.TimeoutMs.ToString());
                AppendHashField(builder, descriptor.SchemaJson);
                AppendHashFields(builder, descriptor.Tags);
                AppendHashField(builder, descriptor.Capability);
                AppendHashFields(builder, descriptor.RequiresEditorStatus);
                AppendHashField(builder, descriptor.ResultShape);
                AppendHashField(builder, descriptor.OutputLimitJson);
                AppendHashField(builder, descriptor.SupportsJob ? "true" : "false");
                AppendHashField(builder, descriptor.SupportsCancel ? "true" : "false");
                AppendHashField(builder, descriptor.WrapperWaitJson);
                AppendHashField(builder, descriptor.FeatureFlag);
                AppendHashField(builder, descriptor.Source);
                AppendHashField(builder, descriptor.PackageId);
                AppendHashField(builder, descriptor.PackageVersion);
                AppendHashField(builder, descriptor.ManifestPath);
                AppendHashField(builder, descriptor.Trust);
                builder.Append('\n');
            }

            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var hash = new StringBuilder(16);
            for (var index = 0; index < 8; index++)
            {
                hash.Append(bytes[index].ToString("x2"));
            }

            return hash.ToString();
        }

        static void AppendHashFields(StringBuilder builder, string[] values)
        {
            var fields = values ?? Array.Empty<string>();
            AppendHashField(builder, fields.Length.ToString());
            foreach (var field in fields)
            {
                AppendHashField(builder, field);
            }
        }

        static void AppendHashField(StringBuilder builder, string value)
        {
            var field = value ?? string.Empty;
            builder.Append(field.Length).Append(':').Append(field).Append('|');
        }

        static CommandExecutionResult UnityException(Exception exception)
        {
            var errorCode = FirstLine(exception.Message);
            if (IsKnownGatewayError(errorCode))
            {
                return CommandExecutionResult.Error(errorCode, errorCode);
            }

            return CommandExecutionResult.Error("unity_exception", exception.Message);
        }

        static string FirstLine(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var lineEnd = value.IndexOf('\n');
            return (lineEnd >= 0 ? value.Substring(0, lineEnd) : value).Trim();
        }

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxChars);
        }

        static bool IsKnownGatewayError(string value)
        {
            switch (value)
            {
                case "schema_validation_failed":
                case "invalid_field_value":
                case "path_not_allowed":
                case "path_traversal_rejected":
                case "absolute_path_rejected":
                case "filename_too_long":
                case "command_handler_unavailable":
                    return true;
                default:
                    return false;
            }
        }

        sealed class AssemblyScanTiming
        {
            public string AssemblyName = string.Empty;
            public int TypeCount;
            public int AttributedMethodCount;
            public long DurationMs;
            public string Status = "ok";
            public string ErrorType = string.Empty;
        }

        public sealed class CatalogSnapshot
        {
            internal static readonly CatalogSnapshot Empty = new CatalogSnapshot(
                0,
                string.Empty,
                new Dictionary<string, RegisteredCommand>(StringComparer.Ordinal),
                Array.Empty<CommandDescriptor>(),
                Array.Empty<ManifestScanDiagnostic>(),
                Array.Empty<ProjectManifestCandidate>());

            readonly Dictionary<string, RegisteredCommand> m_commands;
            readonly CommandDescriptor[] m_descriptors;
            readonly ManifestScanDiagnostic[] m_manifestDiagnostics;
            readonly ProjectManifestCandidate[] m_projectManifestCandidates;

            internal CatalogSnapshot(long catalogGeneration, string commandsHash, Dictionary<string, RegisteredCommand> commands, CommandDescriptor[] descriptors, ManifestScanDiagnostic[] manifestDiagnostics, ProjectManifestCandidate[] projectManifestCandidates)
            {
                CatalogGeneration = Math.Max(0, catalogGeneration);
                CommandsHash = commandsHash ?? string.Empty;
                m_commands = commands ?? new Dictionary<string, RegisteredCommand>(StringComparer.Ordinal);
                m_descriptors = descriptors ?? Array.Empty<CommandDescriptor>();
                m_manifestDiagnostics = manifestDiagnostics ?? Array.Empty<ManifestScanDiagnostic>();
                m_projectManifestCandidates = projectManifestCandidates ?? Array.Empty<ProjectManifestCandidate>();
            }

            public long CatalogGeneration { get; }
            public string CommandsHash { get; }
            public IReadOnlyList<CommandDescriptor> Descriptors => m_descriptors.Select(CloneDescriptor).ToArray();
            public IReadOnlyList<ManifestScanDiagnostic> ManifestDiagnostics => m_manifestDiagnostics.Select(CloneManifestDiagnostic).ToArray();
            public IReadOnlyList<ProjectManifestCandidate> ProjectManifestCandidates => m_projectManifestCandidates.Select(CloneProjectManifestCandidate).ToArray();
            internal IReadOnlyList<CommandDescriptor> PublishedDescriptors => m_descriptors;
            internal IReadOnlyList<ManifestScanDiagnostic> PublishedManifestDiagnostics => m_manifestDiagnostics;
            internal IReadOnlyList<ProjectManifestCandidate> PublishedProjectManifestCandidates => m_projectManifestCandidates;
            internal bool TryGetCommand(string commandId, out RegisteredCommand command) => m_commands.TryGetValue(commandId, out command);
            internal bool ContainsCommand(string commandId) => !string.IsNullOrEmpty(commandId) && m_commands.ContainsKey(commandId);
            internal bool IsBuiltInCommand(string commandId) => !string.IsNullOrEmpty(commandId)
                && m_commands.TryGetValue(commandId, out var command)
                && IsBuiltInDescriptor(command.Descriptor);
        }

        internal sealed class RegisteredCommand
        {
            readonly Func<McpCommandContext, CommandExecutionResult> m_handler;

            public RegisteredCommand(CommandDescriptor descriptor, Func<McpCommandContext, CommandExecutionResult> handler)
            {
                Descriptor = descriptor;
                m_handler = handler;
            }

            public CommandDescriptor Descriptor { get; set; }

            public CommandExecutionResult Invoke(McpCommandContext context)
            {
                return m_handler(context);
            }

            public RegisteredCommand CloneWithDescriptor(CommandDescriptor descriptor)
            {
                return new RegisteredCommand(descriptor, m_handler);
            }
        }
    }
}
