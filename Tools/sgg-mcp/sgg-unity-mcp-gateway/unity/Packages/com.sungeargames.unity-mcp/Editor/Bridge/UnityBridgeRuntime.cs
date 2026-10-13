using System;
using System.IO;
using System.Threading;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SGG.UnityMCP.Editor.Bridge
{
    [InitializeOnLoad]
    public static class UnityBridgeRuntime
    {
        const double HeartbeatIntervalSeconds = 5.0;
        const double StartupWaitingTraceIntervalSeconds = 2.0;
        const int ReloadResponseDrainBudgetMs = 500;
        const double StartupIdleQuietSeconds = 2.0;
        const int StartupIdleQuietFrames = 2;
        const int MaxBootstrapInitializationAttempts = 3;
        const int MaxBridgeStartupAttempts = 3;
        const double BridgeStartupRetryDelaySeconds = 2.0;
        const int MaxListenerRecoveryAttempts = 3;
        const double ListenerRecoveryRetryDelaySeconds = 2.0;
        const double ListenerRecoveryStableSeconds = 10.0;
        const string BootstrapPendingCommandsHash = "bootstrap-pending";
        public const string PackageVersion = "2026.10.12-2";
        static readonly string Token = Guid.NewGuid().ToString("N");
        static BridgeRuntimeState State;
        static CommandRegistry Registry;
        static BridgeServer Server;
        static HeartbeatWriter HeartbeatWriter;
        static PackageUpdateMonitor PackageUpdateMonitor;
        static bool s_bootstrapped;
        static bool s_startupScheduled;
        static bool s_starting;
        static bool s_started;
        static bool s_registrationPublished;
        static volatile bool s_lifecycleShutdown;
        static double s_idleStartedAt = -1.0;
        static int s_idleFrameCount;
        static double s_nextStartupWaitingTraceTime;
        static int s_bootstrapInitializationAttempts;
        static int s_bridgeStartupAttempts;
        static double s_nextBridgeStartupAttemptTime;
        static bool s_bridgeStartupExhausted;
        static int s_listenerRecoveryAttempts;
        static double s_nextListenerRecoveryAttemptTime;
        static double s_listenerRecoveryStableSince = -1.0;
        static long s_assetRefreshGeneration;
        static string s_lastAssetRefreshUtc = string.Empty;

        public static string InstanceId => State == null ? string.Empty : State.InstanceId;
        public static string Project => State == null ? ProjectPaths.ProjectId : State.Project;
        public static string Status => State == null ? "disabled" : State.Status;
        public static string CommandsHash => State == null ? string.Empty : State.CommandsHash;
        public static long CatalogGeneration => State == null ? 0 : State.CatalogGeneration;
        public static System.Collections.Generic.IReadOnlyList<CommandDescriptor> CommandDescriptors => Registry == null ? System.Array.Empty<CommandDescriptor>() : Registry.Descriptors;
        public static bool HasBridgeToken => !string.IsNullOrEmpty(Token);
        public static int BridgePort => Server == null || !Server.IsRunning ? 0 : Server.Port;
        public static string InstancesDirectory => DefaultInstancesDirectory();
        public static string HeartbeatFilePath => Path.Combine(InstancesDirectory, InstanceId + ".json");
        public static event Action HeartbeatWritten;
        public static long AssetRefreshGeneration => Interlocked.Read(ref s_assetRefreshGeneration);
        public static string LastAssetRefreshUtc => s_lastAssetRefreshUtc;

        internal static bool PublishQuitLifecycleHeartbeat()
        {
            if (s_lifecycleShutdown || State == null || HeartbeatWriter == null)
            {
                return false;
            }

            State.UpdateFromEditor();
            return WriteHeartbeatAndNotify();
        }

        public static bool ForceHeartbeat(string reason)
        {
            if (s_lifecycleShutdown || State == null || HeartbeatWriter == null)
            {
                return false;
            }

            try
            {
                return MainThreadDispatcher.Run(() =>
                {
                    if (s_lifecycleShutdown)
                    {
                        return false;
                    }
                    State.SetBridgeListening(Server != null && Server.IsRunning);
                    State.UpdateFromEditor();
                    PackageUpdateMonitor?.Update(State, EditorApplication.timeSinceStartup);
                    return WriteHeartbeatAndNotify();
                }, 1000);
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("SGG Unity MCP could not force bridge heartbeat after " + (reason ?? "runtime event") + ": " + exception.Message);
                return false;
            }
        }

        public static bool RefreshCommandCatalog(string reason)
        {
            if (State == null || Registry == null || HeartbeatWriter == null)
            {
                return false;
            }

            try
            {
                return MainThreadDispatcher.Run(() =>
                {
                    TryStartBridge("refresh_command_catalog");
                    if (Registry == null || HeartbeatWriter == null)
                    {
                        return false;
                    }

                    Registry.Rebuild();
                    var catalog = Registry.CaptureSnapshot();
                    State.PublishCatalog(catalog.CommandsHash, catalog.CatalogGeneration);
                    State.SetBridgeListening(Server != null && Server.IsRunning);
                    State.UpdateFromEditor();
                    return WriteHeartbeatAndNotify();
                }, 5000);
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("SGG Unity MCP could not refresh command catalog after " + (reason ?? "runtime event") + ": " + exception.Message);
                return false;
            }
        }

        public static bool MarkPackageRefreshObserved(string reason)
        {
            if (State == null || PackageUpdateMonitor == null)
            {
                return false;
            }

            try
            {
                PackageUpdateMonitor.MarkRefreshObserved(State, reason);
                State.UpdateFromEditor();
                return HeartbeatWriter != null && WriteHeartbeatAndNotify();
            }
            catch (Exception exception)
            {
                BridgeReloadTrace.LogError("bridge.package_refresh_observed.error", exception);
                Debug.LogWarning("SGG Unity MCP could not update package refresh monitor after " + (reason ?? "explicit refresh") + ": " + exception.Message);
                return false;
            }
        }

        internal static void InvalidatePackageUpdateMonitor(string reason)
        {
            PackageUpdateMonitor?.InvalidateFromMainThread(reason);
        }

        internal static AssetRefreshObservation BeginAssetRefresh()
        {
            return new AssetRefreshObservation(
                Interlocked.Read(ref s_assetRefreshGeneration) + 1,
                DateTime.UtcNow,
                ConsoleLogBuffer.CompilationLifecycleSnapshot());
        }

        internal static string CompleteAssetRefresh(AssetRefreshObservation observation)
        {
            if (observation == null)
            {
                throw new ArgumentNullException(nameof(observation));
            }

            Interlocked.Exchange(ref s_assetRefreshGeneration, observation.Generation);
            s_lastAssetRefreshUtc = DateTime.UtcNow.ToString("O");
            var heartbeatWritten = MarkPackageRefreshObserved("assets.refresh");
            var compilation = ConsoleLogBuffer.CompilationLifecycleSnapshot();
            var compilationAdvanced = compilation.Generation > observation.CompilationBefore.Generation;
            var completionProven = compilationAdvanced
                && compilation.CompletedGeneration == compilation.Generation
                && State != null
                && !State.IsCompiling;
            var evidence = completionProven ? "finished_observed" : compilationAdvanced ? "active_or_transitioning" : "unproven";
            return "{\"refresh_generation\":" + observation.Generation
                + ",\"refresh_started_utc\":" + JsonUtil.String(observation.StartedUtc.ToString("O"))
                + ",\"refresh_completed_utc\":" + JsonUtil.String(s_lastAssetRefreshUtc)
                + ",\"heartbeat_written\":" + JsonUtil.Bool(heartbeatWritten)
                + ",\"editor_status_at_return\":" + JsonUtil.String(State == null ? "unknown" : State.Status)
                + ",\"compilation_generation_before\":" + observation.CompilationBefore.Generation
                + ",\"compilation\":" + compilation.ToJson()
                + ",\"compile_completion_proven\":" + JsonUtil.Bool(completionProven)
                + ",\"evidence\":" + JsonUtil.String(evidence)
                + ",\"write_replayed\":false}";
        }

        internal static void PublishCompilationFinishedHeartbeat()
        {
            var written = ForceHeartbeat("compilation_pipeline_finished");
            var compilation = ConsoleLogBuffer.CompilationLifecycleSnapshot();
            BridgeReloadTrace.Log(
                "bridge.compilation.finished_heartbeat",
                "generation=" + compilation.Generation
                    + ";completed_generation=" + compilation.CompletedGeneration
                    + ";written=" + (written ? "true" : "false"));
        }

        static UnityBridgeRuntime()
        {
            if (IsAssetImportWorkerProcess())
            {
                return;
            }

            BridgeBootstrapLifecycleObserver.MarkRuntimeInitializerObserved();
            EditorApplication.update += BootstrapUpdate;
        }

        static void BootstrapUpdate()
        {
            BridgeBootstrapLifecycleObserver.MarkEditorUpdateObserved();
            if (s_bootstrapped)
            {
                EditorApplication.update -= BootstrapUpdate;
                return;
            }

            BridgeBootstrapLifecycleObserver.ObserveIdleGate(EditorApplication.isCompiling, EditorApplication.isUpdating);
            WriteBootstrapWaitingHeartbeat("bootstrap_idle_gate");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                s_idleStartedAt = -1.0;
                s_idleFrameCount = 0;
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (s_idleStartedAt < 0)
            {
                s_idleStartedAt = now;
                s_idleFrameCount = 1;
                return;
            }

            s_idleFrameCount++;
            if (s_idleFrameCount < StartupIdleQuietFrames || now - s_idleStartedAt < StartupIdleQuietSeconds)
            {
                return;
            }

            EditorApplication.update -= BootstrapUpdate;
            BootstrapRuntime("stable_idle");
        }

        static void BootstrapRuntime(string reason)
        {
            if (s_bootstrapped)
            {
                return;
            }

            s_bootstrapInitializationAttempts++;
            BridgeBootstrapLifecycleObserver.MarkBootstrapStarted();
            try
            {
                var step = Stopwatch.StartNew();
                BridgeReloadTrace.Log("bridge.bootstrap.start", reason ?? "unknown");
                BridgeReloadTrace.Log("bridge.reload.after");

                BridgeReloadTrace.Log("bridge.dispatcher.init.start");
                MainThreadDispatcher.Initialize();
                ExplicitCompilationBarrier.EnsureInitializedOnMainThread();
                BridgeReloadTrace.Log("bridge.dispatcher.init.end", step.ElapsedMilliseconds);

                step.Restart();
                BridgeReloadTrace.Log("bridge.console_buffer.init.start");
                ConsoleLogBuffer.Initialize();
                BridgeReloadTrace.Log("bridge.console_buffer.init.end", step.ElapsedMilliseconds);

                EnsureBootstrapState();
                ExplicitCompilationBarrier.RecordReplacementObserved(InstanceId);
                State.UpdateFromEditor();

                EditorApplication.update -= Update;
                EditorApplication.update += Update;
                EditorApplication.quitting -= WriteStoppingHeartbeat;
                EditorApplication.quitting += WriteStoppingHeartbeat;
                AssemblyReloadEvents.beforeAssemblyReload -= WriteReloadingHeartbeat;
                AssemblyReloadEvents.beforeAssemblyReload += WriteReloadingHeartbeat;
                ScheduleDeferredStartup("bootstrap");
                s_bootstrapped = true;
                BridgeReloadTrace.Log("bridge.bootstrap.end");
            }
            catch (Exception exception)
            {
                BridgeBootstrapLifecycleObserver.MarkBootstrapFailed(exception);
                BridgeReloadTrace.LogError("bridge.bootstrap.error", exception);
                EditorApplication.update -= Update;
                EditorApplication.quitting -= WriteStoppingHeartbeat;
                AssemblyReloadEvents.beforeAssemblyReload -= WriteReloadingHeartbeat;
                EditorApplication.delayCall -= DeferredStartup;
                s_startupScheduled = false;
                if (s_bootstrapInitializationAttempts < MaxBootstrapInitializationAttempts)
                {
                    BridgeBootstrapLifecycleObserver.MarkBootstrapRecoveryScheduled(s_bootstrapInitializationAttempts);
                    EnsureBootstrapScheduled("bootstrap_failure_retry");
                }
                else
                {
                    BridgeBootstrapLifecycleObserver.MarkBootstrapRecoveryExhausted();
                }
            }
        }

        static void Update()
        {
            if (s_bridgeStartupExhausted)
            {
                EditorApplication.update -= Update;
                return;
            }
            if (!s_started)
            {
                TryStartBridge("update");
                if (!s_started && !s_bridgeStartupExhausted)
                {
                    WriteStartupWaitingHeartbeat("startup_retry_pending");
                }
                return;
            }

            if (State == null || HeartbeatWriter == null)
            {
                return;
            }

            var listenerRecoveryAttempted = false;
            if (Server == null || !Server.IsRunning)
            {
                listenerRecoveryAttempted = TryRecoverBridgeListener();
            }
            else if (s_listenerRecoveryAttempts > 0 && s_listenerRecoveryStableSince >= 0
                && EditorApplication.timeSinceStartup - s_listenerRecoveryStableSince >= ListenerRecoveryStableSeconds)
            {
                s_listenerRecoveryAttempts = 0;
                s_listenerRecoveryStableSince = -1.0;
                BridgeReloadTrace.Log("bridge.listener.recovery.stable", "automatic_command_replay_attempted=false;automatic_process_restart_attempted=false");
            }
            State.SetBridgeListening(Server != null && Server.IsRunning);
            State.UpdateFromEditor();
            PackageUpdateMonitor?.Update(State, EditorApplication.timeSinceStartup);
            if ((listenerRecoveryAttempted && WriteHeartbeatAndNotify())
                || (!listenerRecoveryAttempted && HeartbeatWriter.WriteIfDue(EditorApplication.timeSinceStartup, HeartbeatIntervalSeconds)))
            {
                MarkRegistrationPublishedAfterRetry();
                if (!listenerRecoveryAttempted)
                {
                    NotifyHeartbeatWritten();
                }
            }
        }

        static bool TryRecoverBridgeListener()
        {
            if (s_lifecycleShutdown || State == null || Registry == null
                || s_listenerRecoveryAttempts >= MaxListenerRecoveryAttempts
                || EditorApplication.timeSinceStartup < s_nextListenerRecoveryAttemptTime)
            {
                return false;
            }

            s_listenerRecoveryAttempts++;
            var failedServer = Server;
            BridgeServer replacement = null;
            Server = null;
            try
            {
                failedServer?.Dispose();
                replacement = new BridgeServer(Token, State, Registry);
                replacement.Start();
                Server = replacement;
                s_nextListenerRecoveryAttemptTime = 0;
                s_listenerRecoveryStableSince = EditorApplication.timeSinceStartup;
                BridgeReloadTrace.Log("bridge.listener.recovery.succeeded", "automatic_command_replay_attempted=false;automatic_process_restart_attempted=false");
            }
            catch (Exception exception)
            {
                try
                {
                    replacement?.Dispose();
                }
                catch
                {
                    // Preserve the original listener recovery failure.
                }
                s_nextListenerRecoveryAttemptTime = EditorApplication.timeSinceStartup + ListenerRecoveryRetryDelaySeconds;
                s_listenerRecoveryStableSince = -1.0;
                BridgeReloadTrace.LogError("bridge.listener.recovery.failed", exception);
            }
            return true;
        }

        static void ScheduleDeferredStartup(string reason)
        {
            if (s_startupScheduled || s_started)
            {
                return;
            }

            s_startupScheduled = true;
            BridgeReloadTrace.Log("bridge.startup.deferred", reason ?? "unknown");
            EditorApplication.delayCall += DeferredStartup;
        }

        static void DeferredStartup()
        {
            s_startupScheduled = false;
            TryStartBridge("delay_call");
        }

        static void TryStartBridge(string reason)
        {
            if (s_started || s_starting || s_bridgeStartupExhausted || State == null)
            {
                return;
            }

            if (s_bridgeStartupAttempts >= MaxBridgeStartupAttempts
                || EditorApplication.timeSinceStartup < s_nextBridgeStartupAttemptTime)
            {
                State.MarkWaitingForIdle();
                return;
            }

            var previousStatus = State.Status;
            State.UpdateFromEditor();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                State.MarkWaitingForIdle();
                WriteStartupWaitingHeartbeat(reason);
                if (EditorApplication.timeSinceStartup >= s_nextStartupWaitingTraceTime)
                {
                    s_nextStartupWaitingTraceTime = EditorApplication.timeSinceStartup + StartupWaitingTraceIntervalSeconds;
                    BridgeReloadTrace.Log("bridge.startup.waiting_for_idle", "reason=" + (reason ?? "unknown"));
                }

                ScheduleDeferredStartup("waiting_for_idle");
                return;
            }

            if (previousStatus == "waiting_for_idle" || previousStatus == "reloading")
            {
                BridgeReloadTrace.Log("bridge.startup.resume", "reason=" + (reason ?? "unknown"));
            }

            s_starting = true;
            s_bridgeStartupAttempts++;
            try
            {
                StartBridge(reason);
                s_started = true;
            }
            catch (Exception exception)
            {
                ResetFailedBridgeStartup();
                BridgeReloadTrace.LogError("bridge.startup.error", exception);
                Debug.LogError("SGG Unity MCP bridge startup failed: " + exception.Message);
                if (s_bridgeStartupAttempts < MaxBridgeStartupAttempts)
                {
                    s_nextBridgeStartupAttemptTime = EditorApplication.timeSinceStartup + BridgeStartupRetryDelaySeconds;
                    BridgeBootstrapLifecycleObserver.MarkBootstrapRecoveryScheduled(s_bridgeStartupAttempts);
                }
                else
                {
                    BridgeBootstrapLifecycleObserver.MarkBootstrapRecoveryExhausted();
                    s_bridgeStartupExhausted = true;
                    State.MarkWaitingForIdle();
                    if (HeartbeatWriter != null)
                    {
                        WriteHeartbeatAndNotify();
                    }
                    EditorApplication.update -= Update;
                }
            }
            finally
            {
                s_starting = false;
            }
        }

        static void StartBridge(string reason)
        {
            var total = Stopwatch.StartNew();
            BridgeReloadTrace.Log("bridge.startup.start", reason ?? "unknown");

            var step = Stopwatch.StartNew();
            BridgeReloadTrace.Log("bridge.registry.build.start");
            Registry = CommandRegistry.Build(State);
            BridgeReloadTrace.Log("bridge.registry.build.end", step.ElapsedMilliseconds);

            var catalog = Registry.CaptureSnapshot();
            State.PublishCatalog(catalog.CommandsHash, catalog.CatalogGeneration);
            step.Restart();
            BridgeReloadTrace.Log("bridge.state.update.start");
            State.UpdateFromEditor();
            BridgeReloadTrace.Log("bridge.state.update.end", step.ElapsedMilliseconds);

            step.Restart();
            BridgeReloadTrace.Log("bridge.package_path.resolve.start");
            var packageRoot = UnityMcpPackagePaths.PackageResolvedPath;
            BridgeReloadTrace.Log("bridge.package_path.resolve.end", string.IsNullOrEmpty(packageRoot) ? "empty" : "resolved", step.ElapsedMilliseconds);

            step.Restart();
            BridgeReloadTrace.Log("package_update_monitor.ctor.start");
            PackageUpdateMonitor = new PackageUpdateMonitor(packageRoot);
            BridgeReloadTrace.Log("package_update_monitor.ctor.end", step.ElapsedMilliseconds);

            Server = new BridgeServer(Token, State, Registry);
            step.Restart();
            BridgeReloadTrace.Log("bridge.server.start");
            Server.Start();
            BridgeReloadTrace.Log("bridge.server.end", step.ElapsedMilliseconds);
            State.SetBridgeListening(Server.IsRunning);
            State.UpdateFromEditor();

            EnsureHeartbeatWriter();
            step.Restart();
            BridgeReloadTrace.Log("bridge.heartbeat.write.start");
            var heartbeatWritten = WriteHeartbeatAndNotify();
            s_registrationPublished = heartbeatWritten;
            BridgeBootstrapLifecycleObserver.MarkHeartbeatResult(heartbeatWritten, HeartbeatWriter.LastFailureKind);
            BridgeReloadTrace.Log("bridge.heartbeat.write.end", step.ElapsedMilliseconds);
            BridgeReloadTrace.Log("bridge.startup.end", total.ElapsedMilliseconds);
        }

        static void WriteStoppingHeartbeat()
        {
            EditorQuitLifecycle.MarkAccepted();
            WriteLifecycleHeartbeat("stopping", "editor_quitting");
        }

        static void WriteReloadingHeartbeat()
        {
            BridgeReloadTrace.Log("bridge.reload.before");
            ExplicitCompilationBarrier.RecordReloadStarted(InstanceId);
            WriteLifecycleHeartbeat("reloading", "assembly_reload");
        }

        static void WriteLifecycleHeartbeat(string status, string reason)
        {
            s_lifecycleShutdown = true;
            try
            {
                DisposePackageUpdateMonitor();
                Server?.BeginShutdown();

                if (State != null)
                {
                    State.SetBridgeListening(Server != null && Server.IsRunning);
                    if (status == "reloading")
                    {
                        State.MarkReloading();
                    }
                    else
                    {
                        State.MarkStopping();
                    }
                }

                EnsureHeartbeatWriter();

                if (HeartbeatWriter != null)
                {
                    BridgeReloadTrace.Log("bridge.heartbeat.lifecycle.write.start", reason ?? status);
                    HeartbeatWriter.Write();
                    BridgeReloadTrace.Log("bridge.heartbeat.lifecycle.write.end", reason ?? status);
                }
            }
            finally
            {
                StopServer(reason);
            }
        }

        static void StopServer(string reason)
        {
            if (Server == null)
            {
                return;
            }

            BridgeReloadTrace.Log("bridge.server.dispose.start", reason ?? "lifecycle");
            if (reason == "assembly_reload")
            {
                var drained = Server.DisposeAfterDrain(ReloadResponseDrainBudgetMs);
                BridgeReloadTrace.Log("bridge.server.response_drain", "drained=" + (drained ? "true" : "false") + ";budget_ms=" + ReloadResponseDrainBudgetMs);
            }
            else
            {
                Server.Dispose();
            }
            BridgeReloadTrace.Log("bridge.server.dispose.end", reason ?? "lifecycle");
            State?.SetBridgeListening(false);
        }

        static void WriteStartupWaitingHeartbeat(string reason)
        {
            EnsureBootstrapState();
            State?.MarkWaitingForIdle();
            EnsureHeartbeatWriter();
            if (HeartbeatWriter != null && HeartbeatWriter.WriteIfDue(EditorApplication.timeSinceStartup, HeartbeatIntervalSeconds))
            {
                MarkRegistrationPublishedAfterRetry();
                BridgeReloadTrace.Log("bridge.heartbeat.waiting_for_idle.write", reason ?? "unknown");
                NotifyHeartbeatWritten();
            }
        }

        static void WriteBootstrapWaitingHeartbeat(string reason)
        {
            EnsureBootstrapState();
            if (State == null)
            {
                return;
            }

            State.UpdateFromEditor();
            State.MarkWaitingForIdle();
            EnsureHeartbeatWriter();
            if (HeartbeatWriter == null)
            {
                return;
            }

            var written = HeartbeatWriter.WriteIfDue(EditorApplication.timeSinceStartup, HeartbeatIntervalSeconds);
            if (written)
            {
                s_registrationPublished = true;
                NotifyHeartbeatWritten();
            }
            BridgeBootstrapLifecycleObserver.MarkWaitingHeartbeatResult(written, HeartbeatWriter.LastFailureKind);
            if (written)
            {
                BridgeReloadTrace.Log("bridge.heartbeat.bootstrap_waiting.write", reason ?? "unknown");
            }
        }

        static void EnsureBootstrapState()
        {
            if (State != null)
            {
                return;
            }

            State = new BridgeRuntimeState
            {
                InstanceId = ProjectPaths.ProjectId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Project = ProjectPaths.ProjectId,
                UnityVersion = Application.unityVersion,
                CommandsHash = BootstrapPendingCommandsHash,
            };
        }

        internal static bool EnsureBootstrapScheduled(string reason)
        {
            if (IsAssetImportWorkerProcess() || s_bootstrapped)
            {
                return false;
            }

            s_idleStartedAt = -1.0;
            s_idleFrameCount = 0;
            EditorApplication.update -= BootstrapUpdate;
            EditorApplication.update += BootstrapUpdate;
            BridgeReloadTrace.Log("bridge.bootstrap.recovery_scheduled", reason ?? "unknown");
            return true;
        }

        static void MarkRegistrationPublishedAfterRetry()
        {
            if (s_registrationPublished)
            {
                return;
            }

            s_registrationPublished = true;
            BridgeBootstrapLifecycleObserver.MarkHeartbeatRetryPublished();
        }

        static void EnsureHeartbeatWriter()
        {
            if (HeartbeatWriter != null || State == null)
            {
                return;
            }

            HeartbeatWriter = new HeartbeatWriter(DefaultInstancesDirectory(), Token, State, () => Server == null || !Server.IsRunning ? 0 : Server.Port);
        }

        static void ResetFailedBridgeStartup()
        {
            var failedServer = Server;
            Server = null;
            try
            {
                failedServer?.Dispose();
            }
            catch (Exception exception)
            {
                BridgeReloadTrace.LogError("bridge.startup.cleanup.error", exception);
            }

            Registry = null;
            DisposePackageUpdateMonitor();
            State?.SetBridgeListening(false);
        }

        static void DisposePackageUpdateMonitor()
        {
            var monitor = PackageUpdateMonitor;
            PackageUpdateMonitor = null;
            try { monitor?.Dispose(); }
            catch (Exception exception) { BridgeReloadTrace.LogError("package_update_monitor.dispose.error", exception); }
        }

        static bool WriteHeartbeatAndNotify()
        {
            if (HeartbeatWriter.Write())
            {
                NotifyHeartbeatWritten();
                return true;
            }

            return false;
        }

        static void NotifyHeartbeatWritten()
        {
            var handler = HeartbeatWritten;
            if (handler != null)
            {
                handler();
            }
        }

        static bool IsAssetImportWorkerProcess()
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];
                if (arg != null && arg.IndexOf("AssetImportWorker", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        static string DefaultInstancesDirectory()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = Application.temporaryCachePath;
            }

            return Path.Combine(localAppData, "SGG", "UnityMcpGateway", "instances");
        }
    }

    internal sealed class AssetRefreshObservation
    {
        public AssetRefreshObservation(long generation, DateTime startedUtc, CompilationLifecycleSnapshot compilationBefore)
        {
            Generation = Math.Max(1, generation);
            StartedUtc = startedUtc;
            CompilationBefore = compilationBefore ?? new CompilationLifecycleSnapshot(0, 0, DateTime.MinValue, DateTime.MinValue);
        }

        public long Generation { get; }
        public DateTime StartedUtc { get; }
        public CompilationLifecycleSnapshot CompilationBefore { get; }
    }
}
