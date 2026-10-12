using System;
using SGG.UnityMCP.Editor.Commands;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;

namespace SGG.UnityMCP.Editor.Bridge
{
    [InitializeOnLoad]
    public static class ExplicitCompilationBarrier
    {
        const string SessionStateKey = "SGG.UnityMCP.ExplicitCompilationBarrier.state";
        public const long MaxOperationAgeTicks = TimeSpan.TicksPerHour;
        public const long MaxReloadEdgeAgeTicks = 5 * TimeSpan.TicksPerMinute;
        static readonly object Gate = new object();
        static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;
        static ExplicitCompilationRuntimeEpoch s_runtimeEpoch;
        static ExplicitCompilationBarrierParseResult s_cachedState;
        static bool s_initialized;

        static ExplicitCompilationBarrier()
        {
            EnsureInitializedOnMainThread();
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        public static bool EnsureInitializedOnMainThread()
        {
            lock (Gate)
            {
                if (s_initialized)
                {
                    return true;
                }
            }
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                return false;
            }

            var blob = SessionState.GetString(SessionStateKey, string.Empty);
            var parsed = string.IsNullOrEmpty(blob)
                ? ExplicitCompilationBarrierParseResult.NotRequired()
                : ExplicitCompilationBarrierState.Parse(blob);
            var recovered = RecoverPersistedState(parsed);
            if (!ReferenceEquals(recovered, parsed) && recovered.State != null)
            {
                parsed = recovered;
                PersistOnMainThread(recovered.State);
            }
            lock (Gate)
            {
                if (!s_initialized)
                {
                    s_cachedState = parsed;
                    s_initialized = true;
                }
                return s_initialized;
            }
        }

        public static ExplicitCompilationBarrierResult Begin(string expectedOperationId)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                return ExplicitCompilationBarrierResult.Error("unity_api_thread_error", "explicit_compilation_begin_requires_main_thread", null);
            }
            if (!EnsureInitializedOnMainThread())
            {
                return ExplicitCompilationBarrierResult.Error("compile_freshness_unavailable", "barrier_cache_not_initialized", null);
            }
            var operationId = ExplicitCompilationBarrierState.IsValidOperationId(expectedOperationId)
                ? expectedOperationId
                : Guid.NewGuid().ToString("N");
            ExplicitCompilationBarrierParseResult current;
            bool terminalPostReloadObserved;
            bool terminalCompileFailedWithoutReload;
            lock (Gate)
            {
                current = CachedState();
                terminalPostReloadObserved = current.State == null
                    || current.State.Status != "verified"
                    || current.State.PostReloadObserved;
                terminalCompileFailedWithoutReload = current.State != null && current.State.TerminalCompileFailedWithoutReload;
            }
            var compilationActive = EditorApplication.isCompiling;
            var decision = ExplicitCompilationBarrierState.BeginNew(current, operationId, UnityBridgeRuntime.InstanceId, DateTime.UtcNow.Ticks, MaxOperationAgeTicks, compilationActive, terminalPostReloadObserved, terminalCompileFailedWithoutReload);
            if (!decision.Success)
            {
                return ExplicitCompilationBarrierResult.Error(decision.ErrorCode, decision.Failure, decision.State);
            }
            lock (Gate)
            {
                s_runtimeEpoch = null;
            }
            PersistOnMainThread(decision.State);
            return ExplicitCompilationBarrierResult.Ok(decision.State);
        }

        public static ExplicitCompilationBarrierResult MarkRequestIssued(string operationId)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                return ExplicitCompilationBarrierResult.Error("unity_api_thread_error", "explicit_compilation_request_requires_main_thread", null);
            }
            ExplicitCompilationBarrierParseResult current;
            lock (Gate)
            {
                current = CachedState();
            }
            if (!current.Available)
            {
                return ExplicitCompilationBarrierResult.Error("compile_freshness_unavailable", current.Failure, null);
            }
            if (current.State == null || current.State.Status != "pending" || current.State.RequestIssued
                || !string.Equals(current.State.OperationId, operationId, StringComparison.Ordinal))
            {
                return ExplicitCompilationBarrierResult.Error("compile_freshness_unverified", "explicit_compilation_operation_mismatch", current.State);
            }

            var next = current.State.WithRequestIssued(DateTime.UtcNow.Ticks);
            lock (Gate)
            {
                if (next.Status == "failed")
                {
                    s_runtimeEpoch = null;
                }
                else
                {
                    s_runtimeEpoch = new ExplicitCompilationRuntimeEpoch(next.OperationId, next.RequestGeneration);
                }
            }
            PersistOnMainThread(next);
            return next.Status == "failed"
                ? ExplicitCompilationBarrierResult.Error("compile_freshness_unavailable", next.Failure, next)
                : ExplicitCompilationBarrierResult.Ok(next);
        }

        public static ExplicitCompilationBarrierSnapshot Snapshot(string expectedOperationId, bool requireExplicitBarrier)
        {
            lock (Gate)
            {
                if (!s_initialized || s_cachedState == null)
                {
                    return new ExplicitCompilationBarrierSnapshot("unavailable", null, "barrier_cache_not_initialized", requireExplicitBarrier, false, 0);
                }
                var current = s_cachedState.Clone();
                if (!current.Available)
                {
                    return new ExplicitCompilationBarrierSnapshot("unavailable", null, current.Failure, requireExplicitBarrier, false, 0);
                }
                if (current.State == null)
                {
                    return new ExplicitCompilationBarrierSnapshot("not_required", null, string.Empty, requireExplicitBarrier, false, 0);
                }
                if (requireExplicitBarrier && (!ExplicitCompilationBarrierState.IsValidOperationId(expectedOperationId)
                    || !string.Equals(current.State.OperationId, expectedOperationId, StringComparison.Ordinal)))
                {
                    return new ExplicitCompilationBarrierSnapshot("unknown", current.State, "explicit_compilation_operation_mismatch", true, false, 0);
                }
                var postReloadObserved = current.State.PostReloadObserved;
                return new ExplicitCompilationBarrierSnapshot(current.State.Status, current.State, current.State.Failure, requireExplicitBarrier, postReloadObserved, postReloadObserved ? current.State.PostReloadObservedTicks : 0);
            }
        }

        public static string StatusJson(string expectedOperationId = "", bool requireExplicitBarrier = false)
        {
            return Snapshot(expectedOperationId, requireExplicitBarrier).ToJson();
        }

        public static ExplicitCompilationBarrierParseResult ParseForTests(string blob)
        {
            return ExplicitCompilationBarrierState.Parse(blob);
        }

        internal static ExplicitCompilationBarrierParseResult RecoverPersistedStateForTests(string blob)
        {
            return RecoverPersistedState(ExplicitCompilationBarrierState.Parse(blob));
        }

        public static ExplicitCompilationBarrierBeginDecision BeginForTests(string blob, string operationId, long nowTicks, bool compilationActive = false, bool terminalPostReloadObserved = true, bool terminalCompileFailedWithoutReload = false)
        {
            var current = string.IsNullOrEmpty(blob)
                ? ExplicitCompilationBarrierParseResult.NotRequired()
                : ExplicitCompilationBarrierState.Parse(blob);
            return ExplicitCompilationBarrierState.BeginNew(current, operationId, "test-instance-a", nowTicks, MaxOperationAgeTicks, compilationActive, terminalPostReloadObserved, terminalCompileFailedWithoutReload);
        }

        internal static bool CompileFailureMatchesBarrier(ExplicitCompilationBarrierState state, CompilationLifecycleSnapshot compilation, bool hasCompileErrors)
        {
            return hasCompileErrors && state != null && state.TerminalCompileFailedWithoutReload;
        }

        internal static bool RecordTerminalCompileFailureWithoutReload(ExplicitCompilationBarrierSnapshot barrier, CompilationLifecycleSnapshot compilation, bool hasCompileErrors)
        {
            if (barrier == null || barrier.PostReloadObserved || !hasCompileErrors
                || barrier.Operation == null || !barrier.Operation.TerminalCompileFailedWithoutReload)
            {
                return false;
            }

            lock (Gate)
            {
                var current = CachedState();
                if (!current.Available || current.State == null || !current.State.TerminalCompileFailedWithoutReload
                    || !MatchesExactVerifiedOperation(current.State, barrier.Operation.OperationId))
                {
                    return false;
                }
                return true;
            }
        }

        internal static bool RecordReloadStarted(string sourceInstanceId)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId || !EnsureInitializedOnMainThread())
            {
                return false;
            }
            ExplicitCompilationBarrierState current;
            lock (Gate)
            {
                var parsed = CachedState();
                current = parsed.Available ? parsed.State : null;
            }
            if (current == null)
            {
                return false;
            }
            var next = current.WithReloadStarted(sourceInstanceId, DateTime.UtcNow.Ticks);
            if (ReferenceEquals(next, current))
            {
                return current.ReloadStartedTicks > 0
                    && string.Equals(current.ReloadSourceInstanceId, sourceInstanceId, StringComparison.Ordinal);
            }
            PersistOnMainThread(next);
            return next.ReloadStartedTicks > 0;
        }

        internal static bool RecordReplacementObserved(string replacementInstanceId)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId || !EnsureInitializedOnMainThread())
            {
                return false;
            }
            ExplicitCompilationBarrierState current;
            lock (Gate)
            {
                var parsed = CachedState();
                current = parsed.Available ? parsed.State : null;
            }
            if (current == null)
            {
                return false;
            }
            var next = current.WithReplacementObserved(replacementInstanceId, DateTime.UtcNow.Ticks);
            if (ReferenceEquals(next, current))
            {
                return current.PostReloadObserved
                    && string.Equals(current.ReplacementInstanceId, replacementInstanceId, StringComparison.Ordinal);
            }
            PersistOnMainThread(next);
            return next.PostReloadObserved;
        }

        internal static bool MatchesExactVerifiedOperation(ExplicitCompilationBarrierState state, string operationId)
        {
            return state != null
                && state.Status == "verified"
                && state.RequestIssued
                && state.Started
                && state.Finished
                && ExplicitCompilationBarrierState.IsValidOperationId(operationId)
                && string.Equals(state.OperationId, operationId, StringComparison.Ordinal);
        }

        static ExplicitCompilationBarrierParseResult RecoverPersistedState(ExplicitCompilationBarrierParseResult parsed)
        {
            if (parsed == null || !parsed.Available || parsed.State == null)
            {
                return parsed;
            }
            var recovered = parsed.State.RecoverSupersededReplacementMarker();
            return ReferenceEquals(recovered, parsed.State)
                ? parsed
                : ExplicitCompilationBarrierParseResult.Ok(recovered);
        }

        static void OnCompilationStarted(object context)
        {
            ExplicitCompilationBarrierParseResult current;
            ExplicitCompilationRuntimeEpoch epoch;
            lock (Gate)
            {
                current = CachedState();
                epoch = s_runtimeEpoch;
            }
            if (!current.Available || current.State == null || current.State.Status != "pending")
            {
                return;
            }
            var handled = ExplicitCompilationBarrierCallbacks.HandleStarted(current.State, epoch, context, DateTime.UtcNow.Ticks);
            lock (Gate)
            {
                s_runtimeEpoch = handled.Epoch;
            }
            PersistOnMainThread(handled.State);
        }

        static void OnCompilationFinished(object context)
        {
            ExplicitCompilationBarrierParseResult current;
            ExplicitCompilationRuntimeEpoch epoch;
            lock (Gate)
            {
                current = CachedState();
                epoch = s_runtimeEpoch;
            }
            if (!current.Available || current.State == null || current.State.Status != "pending")
            {
                return;
            }
            var nowTicks = DateTime.UtcNow.Ticks;
            var handled = ExplicitCompilationBarrierCallbacks.HandleFinished(current.State, epoch, context, nowTicks, ConsoleLogBuffer.CompileErrorCount());
            lock (Gate)
            {
                s_runtimeEpoch = handled.Epoch;
            }
            var next = handled.State.WithReplacementObserved(UnityBridgeRuntime.InstanceId, nowTicks);
            PersistOnMainThread(next);

            if (next.Status == "verified")
            {
                UnityBridgeRuntime.MarkPackageRefreshObserved("explicit_compilation_barrier_verified");
            }
        }

        static ExplicitCompilationBarrierParseResult CachedState()
        {
            return s_initialized && s_cachedState != null
                ? s_cachedState.Clone()
                : ExplicitCompilationBarrierParseResult.Malformed("barrier_cache_not_initialized");
        }

        static void PersistOnMainThread(ExplicitCompilationBarrierState state)
        {
            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
            {
                return;
            }
            SessionState.SetString(SessionStateKey, state.Serialize());
            lock (Gate)
            {
                s_cachedState = ExplicitCompilationBarrierParseResult.Ok(state.Clone());
                s_initialized = true;
            }
        }
    }

    public sealed class ExplicitCompilationBarrierState
    {
        const int SchemaVersion = 3;
        const int MaxBlobChars = 3072;
        const string None = "none";
        static readonly string[] Keys =
        {
            "v", "status", "operation_id", "source_instance_id", "observed_generation", "request_generation", "started_generation", "finished_generation",
            "requested_ticks", "request_issued_ticks", "started_ticks", "finished_ticks", "request_issued", "started", "finished",
            "terminal_compile_result", "terminal_compile_error_count", "terminal_compile_observed_ticks", "reload_started_ticks", "reload_source_instance_id",
            "post_reload_observed_ticks", "replacement_instance_id",
            "failure", "recovery", "previous_failure",
        };

        ExplicitCompilationBarrierState(
            string status,
            string operationId,
            string sourceInstanceId,
            int observedGeneration,
            int requestGeneration,
            int startedGeneration,
            int finishedGeneration,
            long requestedTicks,
            long requestIssuedTicks,
            long startedTicks,
            long finishedTicks,
            bool requestIssued,
            bool started,
            bool finished,
            string terminalCompileResult,
            int terminalCompileErrorCount,
            long terminalCompileObservedTicks,
            long reloadStartedTicks,
            string reloadSourceInstanceId,
            long postReloadObservedTicks,
            string replacementInstanceId,
            string failure,
            string recovery,
            string previousFailure)
        {
            Status = status;
            OperationId = operationId;
            SourceInstanceId = sourceInstanceId;
            ObservedGeneration = observedGeneration;
            RequestGeneration = requestGeneration;
            StartedGeneration = startedGeneration;
            FinishedGeneration = finishedGeneration;
            RequestedTicks = requestedTicks;
            RequestIssuedTicks = requestIssuedTicks;
            StartedTicks = startedTicks;
            FinishedTicks = finishedTicks;
            RequestIssued = requestIssued;
            Started = started;
            Finished = finished;
            TerminalCompileResult = terminalCompileResult;
            TerminalCompileErrorCount = terminalCompileErrorCount;
            TerminalCompileObservedTicks = terminalCompileObservedTicks;
            ReloadStartedTicks = reloadStartedTicks;
            ReloadSourceInstanceId = reloadSourceInstanceId;
            PostReloadObservedTicks = postReloadObservedTicks;
            ReplacementInstanceId = replacementInstanceId;
            Failure = failure;
            Recovery = recovery;
            PreviousFailure = previousFailure;
        }

        public string Status { get; private set; }
        public string OperationId { get; private set; }
        public string SourceInstanceId { get; private set; }
        public int ObservedGeneration { get; private set; }
        public int RequestGeneration { get; private set; }
        public int StartedGeneration { get; private set; }
        public int FinishedGeneration { get; private set; }
        public long RequestedTicks { get; private set; }
        public long RequestIssuedTicks { get; private set; }
        public long StartedTicks { get; private set; }
        public long FinishedTicks { get; private set; }
        public bool RequestIssued { get; private set; }
        public bool Started { get; private set; }
        public bool Finished { get; private set; }
        public string TerminalCompileResult { get; private set; }
        public int TerminalCompileErrorCount { get; private set; }
        public long TerminalCompileObservedTicks { get; private set; }
        public long ReloadStartedTicks { get; private set; }
        public string ReloadSourceInstanceId { get; private set; }
        public long PostReloadObservedTicks { get; private set; }
        public string ReplacementInstanceId { get; private set; }
        public bool TerminalCompileFailedWithoutReload => Status == "verified" && Finished && TerminalCompileResult == "failed" && TerminalCompileErrorCount > 0 && ReloadStartedTicks == 0 && PostReloadObservedTicks == 0;
        public bool PostReloadObserved => Status == "verified" && Finished && TerminalCompileResult == "clean" && ReloadStartedTicks > 0 && PostReloadObservedTicks > 0;
        public string Failure { get; private set; }
        public string Recovery { get; private set; }
        public string PreviousFailure { get; private set; }

        public static ExplicitCompilationBarrierState CreatePending(string operationId, string sourceInstanceId, long requestedTicks, int observedGeneration = 0, string recovery = None, string previousFailure = None)
        {
            if (!IsValidOperationId(operationId) || !IsValidInstanceId(sourceInstanceId) || requestedTicks <= 0 || observedGeneration < 0 || !IsRecovery(recovery) || !IsFailure(previousFailure))
            {
                throw new ArgumentException("Bounded operation and source instance ids plus a valid pending state are required.");
            }
            return new ExplicitCompilationBarrierState("pending", operationId, sourceInstanceId, observedGeneration, 0, 0, 0, requestedTicks, 0, 0, 0, false, false, false, "unknown", 0, 0, 0, string.Empty, 0, string.Empty, None, recovery, previousFailure);
        }

        public static ExplicitCompilationBarrierBeginDecision BeginNew(ExplicitCompilationBarrierParseResult current, string operationId, string sourceInstanceId, long nowTicks, long maxOperationAgeTicks, bool compilationActive, bool terminalPostReloadObserved, bool terminalCompileFailedWithoutReload = false)
        {
            if (!IsValidOperationId(operationId) || !IsValidInstanceId(sourceInstanceId) || nowTicks <= 0 || maxOperationAgeTicks <= 0)
            {
                return ExplicitCompilationBarrierBeginDecision.Error("compile_freshness_unavailable", "invalid_begin_request", current == null ? null : current.State);
            }
            if (current == null || !current.Available)
            {
                if (compilationActive)
                {
                    return ExplicitCompilationBarrierBeginDecision.Error("unity_compiling", "active_compilation_blocks_state_reset", current == null ? null : current.State);
                }
                var priorFailure = current == null ? "malformed_session_state" : current.Failure;
                return ExplicitCompilationBarrierBeginDecision.Ok(CreatePending(operationId, sourceInstanceId, nowTicks, 0, "reset_invalid_state", SafePreviousFailure(priorFailure)));
            }
            if (current.State == null)
            {
                if (compilationActive)
                {
                    return ExplicitCompilationBarrierBeginDecision.Error("unity_compiling", "active_compilation_blocks_new_operation", null);
                }
                return ExplicitCompilationBarrierBeginDecision.Ok(CreatePending(operationId, sourceInstanceId, nowTicks));
            }

            var prior = current.State;
            if (prior.Status == "verified" && !terminalPostReloadObserved && !terminalCompileFailedWithoutReload)
            {
                return ExplicitCompilationBarrierBeginDecision.Error("compile_freshness_unverified", "explicit_compilation_reload_pending", prior);
            }
            if (prior.Status == "pending" && !prior.RequestIssued)
            {
                return ExplicitCompilationBarrierBeginDecision.Ok(CreatePending(operationId, sourceInstanceId, nowTicks, prior.ObservedGeneration, "replaced_unarmed_pending", "request_not_issued"));
            }
            if (prior.Status == "pending" && prior.RequestIssued)
            {
                var age = nowTicks >= prior.RequestIssuedTicks ? nowTicks - prior.RequestIssuedTicks : 0;
                if (age <= maxOperationAgeTicks)
                {
                    return ExplicitCompilationBarrierBeginDecision.Error("compile_freshness_unverified", "explicit_compilation_operation_pending", prior);
                }
                if (compilationActive)
                {
                    return ExplicitCompilationBarrierBeginDecision.Error("unity_compiling", "active_compilation_blocks_stale_replacement", prior);
                }
                return ExplicitCompilationBarrierBeginDecision.Ok(CreatePending(operationId, sourceInstanceId, nowTicks, prior.ObservedGeneration, "abandoned_stale_armed", "operation_max_age_exceeded"));
            }

            if (compilationActive)
            {
                return ExplicitCompilationBarrierBeginDecision.Error("unity_compiling", "active_compilation_blocks_terminal_replacement", prior);
            }
            var recovery = prior.Status == "verified"
                ? terminalCompileFailedWithoutReload && !terminalPostReloadObserved
                    ? "replaced_verified_compile_failed_no_reload"
                    : "replaced_verified"
                : "reset_terminal_state";
            var previousFailure = prior.Failure == None ? prior.Status : prior.Failure;
            return ExplicitCompilationBarrierBeginDecision.Ok(CreatePending(operationId, sourceInstanceId, nowTicks, prior.ObservedGeneration, recovery, SafePreviousFailure(previousFailure)));
        }

        public ExplicitCompilationBarrierState WithRequestIssued(long ticks)
        {
            if (Status != "pending" || RequestIssued || Started || Finished || ticks < RequestedTicks || ObservedGeneration == int.MaxValue)
            {
                return Failed("invalid_request_transition");
            }
            return Copy(requestGeneration: ObservedGeneration + 1, requestIssuedTicks: ticks, requestIssued: true);
        }

        public ExplicitCompilationBarrierState OnCompilationStarted(long ticks)
        {
            return OnCompilationStarted(OperationId, ticks);
        }

        public ExplicitCompilationBarrierState OnCompilationStarted(string operationId, long ticks)
        {
            if (Status != "pending" || !string.Equals(OperationId, operationId, StringComparison.Ordinal))
            {
                return this;
            }
            if (!RequestIssued)
            {
                var observedBeforeRequest = ObservedGeneration == int.MaxValue ? int.MaxValue : ObservedGeneration + 1;
                return Copy(observedGeneration: observedBeforeRequest);
            }
            if (Started || ObservedGeneration == int.MaxValue)
            {
                return Failed("multiple_generation_overlap");
            }
            var observed = ObservedGeneration + 1;
            if (observed != RequestGeneration || ticks < RequestIssuedTicks)
            {
                return Failed("generation_or_timestamp_mismatch", observed);
            }
            return Copy(observedGeneration: observed, startedGeneration: observed, startedTicks: ticks, started: true);
        }

        public ExplicitCompilationBarrierState OnCompilationFinished(long ticks)
        {
            return OnCompilationFinished(OperationId, StartedGeneration, ticks, 0);
        }

        public ExplicitCompilationBarrierState OnCompilationFinished(string operationId, int generation, long ticks)
        {
            return OnCompilationFinished(operationId, generation, ticks, 0);
        }

        public ExplicitCompilationBarrierState OnCompilationFinished(string operationId, int generation, long ticks, int compileErrorCount)
        {
            if (!string.Equals(OperationId, operationId, StringComparison.Ordinal))
            {
                return this;
            }
            if (Status == "verified" && Finished)
            {
                return Failed("multiple_finish");
            }
            if (Status != "pending")
            {
                return this;
            }
            if (!RequestIssued)
            {
                return this;
            }
            if (!Started)
            {
                return Failed("finish_without_start");
            }
            if (Finished || generation <= 0 || generation != StartedGeneration || ObservedGeneration != StartedGeneration || ticks < StartedTicks
                || compileErrorCount < 0 || compileErrorCount > 100)
            {
                return Failed("generation_or_timestamp_mismatch");
            }
            if (ReloadStartedTicks > 0 && (ticks < ReloadStartedTicks || ticks - ReloadStartedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks))
            {
                return Failed("reload_marker_stale");
            }
            if (compileErrorCount > 0 && ReloadStartedTicks > 0)
            {
                return Failed("reload_marker_mismatch");
            }
            return Copy(
                status: "verified",
                finishedGeneration: generation,
                finishedTicks: ticks,
                finished: true,
                terminalCompileResult: compileErrorCount > 0 ? "failed" : "clean",
                terminalCompileErrorCount: compileErrorCount,
                terminalCompileObservedTicks: ticks);
        }

        public ExplicitCompilationBarrierState WithReloadStarted(string sourceInstanceId, long ticks)
        {
            if (!IsValidInstanceId(sourceInstanceId) || !string.Equals(SourceInstanceId, sourceInstanceId, StringComparison.Ordinal)
                || !RequestIssued || !Started || TerminalCompileResult == "failed" || ticks < StartedTicks
                || (Status != "pending" && Status != "verified"))
            {
                return this;
            }
            var referenceTicks = Finished ? FinishedTicks : StartedTicks;
            if (ticks - referenceTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks)
            {
                return this;
            }
            if (ReloadStartedTicks > 0)
            {
                return string.Equals(ReloadSourceInstanceId, sourceInstanceId, StringComparison.Ordinal) ? this : Failed("reload_marker_mismatch");
            }
            return Copy(reloadStartedTicks: ticks, reloadSourceInstanceId: sourceInstanceId);
        }

        public ExplicitCompilationBarrierState WithReplacementObserved(string replacementInstanceId, long ticks)
        {
            // SessionState is scoped to this Editor process. Once one exact post-compile
            // replacement is recorded, later domain reloads cannot invalidate that proof.
            if (PostReloadObservedTicks > 0)
            {
                return this;
            }
            if (!IsValidInstanceId(replacementInstanceId) || string.Equals(replacementInstanceId, SourceInstanceId, StringComparison.Ordinal))
            {
                return this;
            }
            if (Status == "pending" && !Started)
            {
                return Copy(status: "abandoned", failure: "runtime_replaced_before_compilation_started");
            }
            if (Status != "verified" || TerminalCompileResult != "clean" || !Finished)
            {
                return this;
            }
            if (ReloadStartedTicks <= 0 || !string.Equals(ReloadSourceInstanceId, SourceInstanceId, StringComparison.Ordinal)
                || ticks < ReloadStartedTicks || ticks < FinishedTicks
                || ticks - ReloadStartedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks
                || ticks - FinishedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks)
            {
                return Copy(status: "abandoned", failure: "runtime_replaced_without_correlated_reload_proof");
            }
            return Copy(postReloadObservedTicks: ticks, replacementInstanceId: replacementInstanceId);
        }

        internal ExplicitCompilationBarrierState RecoverSupersededReplacementMarker()
        {
            if (Status != "failed" || Failure != "replacement_marker_mismatch" || !RequestIssued || !Started || !Finished
                || TerminalCompileResult != "clean" || TerminalCompileErrorCount != 0 || ReloadStartedTicks <= 0
                || PostReloadObservedTicks <= 0 || !string.Equals(ReloadSourceInstanceId, SourceInstanceId, StringComparison.Ordinal)
                || !IsValidInstanceId(ReplacementInstanceId) || string.Equals(ReplacementInstanceId, SourceInstanceId, StringComparison.Ordinal))
            {
                return this;
            }
            return Copy(
                status: "verified",
                failure: None,
                recovery: "preserved_verified_replacement",
                previousFailure: "replacement_marker_mismatch");
        }

        public string Serialize()
        {
            return "v=" + SchemaVersion
                + ";status=" + Status
                + ";operation_id=" + OperationId
                + ";source_instance_id=" + SourceInstanceId
                + ";observed_generation=" + ObservedGeneration
                + ";request_generation=" + RequestGeneration
                + ";started_generation=" + StartedGeneration
                + ";finished_generation=" + FinishedGeneration
                + ";requested_ticks=" + RequestedTicks
                + ";request_issued_ticks=" + RequestIssuedTicks
                + ";started_ticks=" + StartedTicks
                + ";finished_ticks=" + FinishedTicks
                + ";request_issued=" + Bool(RequestIssued)
                + ";started=" + Bool(Started)
                + ";finished=" + Bool(Finished)
                + ";terminal_compile_result=" + TerminalCompileResult
                + ";terminal_compile_error_count=" + TerminalCompileErrorCount
                + ";terminal_compile_observed_ticks=" + TerminalCompileObservedTicks
                + ";reload_started_ticks=" + ReloadStartedTicks
                + ";reload_source_instance_id=" + ReloadSourceInstanceId
                + ";post_reload_observed_ticks=" + PostReloadObservedTicks
                + ";replacement_instance_id=" + ReplacementInstanceId
                + ";failure=" + Failure
                + ";recovery=" + Recovery
                + ";previous_failure=" + PreviousFailure;
        }

        public ExplicitCompilationBarrierState Clone()
        {
            return new ExplicitCompilationBarrierState(
                Status,
                OperationId,
                SourceInstanceId,
                ObservedGeneration,
                RequestGeneration,
                StartedGeneration,
                FinishedGeneration,
                RequestedTicks,
                RequestIssuedTicks,
                StartedTicks,
                FinishedTicks,
                RequestIssued,
                Started,
                Finished,
                TerminalCompileResult,
                TerminalCompileErrorCount,
                TerminalCompileObservedTicks,
                ReloadStartedTicks,
                ReloadSourceInstanceId,
                PostReloadObservedTicks,
                ReplacementInstanceId,
                Failure,
                Recovery,
                PreviousFailure);
        }

        public static ExplicitCompilationBarrierParseResult Parse(string blob)
        {
            if (string.IsNullOrEmpty(blob) || blob.Length > MaxBlobChars)
            {
                return ExplicitCompilationBarrierParseResult.Malformed("malformed_session_state");
            }
            var fields = blob.Split(';');
            if (fields.Length != Keys.Length)
            {
                return ExplicitCompilationBarrierParseResult.Malformed("malformed_session_state");
            }
            var values = new string[Keys.Length];
            for (var index = 0; index < fields.Length; index++)
            {
                var prefix = Keys[index] + "=";
                if (!fields[index].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return ExplicitCompilationBarrierParseResult.Malformed("malformed_session_state");
                }
                values[index] = fields[index].Substring(prefix.Length);
            }

            if (values[0] != "3"
                || !IsStatus(values[1])
                || !IsValidOperationId(values[2])
                || !IsValidInstanceId(values[3])
                || !TryNonNegativeInt(values[4], out var observedGeneration)
                || !TryNonNegativeInt(values[5], out var requestGeneration)
                || !TryNonNegativeInt(values[6], out var startedGeneration)
                || !TryNonNegativeInt(values[7], out var finishedGeneration)
                || !TryPositiveLong(values[8], out var requestedTicks)
                || !TryNonNegativeLong(values[9], out var requestIssuedTicks)
                || !TryNonNegativeLong(values[10], out var startedTicks)
                || !TryNonNegativeLong(values[11], out var finishedTicks)
                || !TryBool(values[12], out var requestIssued)
                || !TryBool(values[13], out var started)
                || !TryBool(values[14], out var finished)
                || !IsTerminalCompileResult(values[15])
                || !TryNonNegativeInt(values[16], out var terminalCompileErrorCount)
                || terminalCompileErrorCount > 100
                || !TryNonNegativeLong(values[17], out var terminalCompileObservedTicks)
                || !TryNonNegativeLong(values[18], out var reloadStartedTicks)
                || !IsValidOptionalInstanceId(values[19])
                || !TryNonNegativeLong(values[20], out var postReloadObservedTicks)
                || !IsValidOptionalInstanceId(values[21])
                || !IsFailure(values[22])
                || !IsRecovery(values[23])
                || !IsFailure(values[24]))
            {
                return ExplicitCompilationBarrierParseResult.Malformed("malformed_session_state");
            }

            var state = new ExplicitCompilationBarrierState(values[1], values[2], values[3], observedGeneration, requestGeneration, startedGeneration, finishedGeneration, requestedTicks, requestIssuedTicks, startedTicks, finishedTicks, requestIssued, started, finished, values[15], terminalCompileErrorCount, terminalCompileObservedTicks, reloadStartedTicks, values[19], postReloadObservedTicks, values[21], values[22], values[23], values[24]);
            return state.IsStructurallyValid()
                ? ExplicitCompilationBarrierParseResult.Ok(state)
                : ExplicitCompilationBarrierParseResult.Malformed("malformed_session_state");
        }

        public static bool IsValidOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
            {
                return false;
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsValidInstanceId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 256)
            {
                return false;
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!(char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.'))
                {
                    return false;
                }
            }
            return true;
        }

        bool IsStructurallyValid()
        {
            if (RequestedTicks <= 0 || ObservedGeneration < 0 || !IsValidInstanceId(SourceInstanceId)
                || !IsFailure(Failure) || !IsRecovery(Recovery) || !IsFailure(PreviousFailure)
                || !IsTerminalCompileResult(TerminalCompileResult) || TerminalCompileErrorCount < 0 || TerminalCompileErrorCount > 100
                || RequestedTicks > DateTime.MaxValue.Ticks || RequestIssuedTicks > DateTime.MaxValue.Ticks
                || StartedTicks > DateTime.MaxValue.Ticks || FinishedTicks > DateTime.MaxValue.Ticks
                || TerminalCompileObservedTicks > DateTime.MaxValue.Ticks || ReloadStartedTicks > DateTime.MaxValue.Ticks
                || PostReloadObservedTicks > DateTime.MaxValue.Ticks)
            {
                return false;
            }
            if (!Finished && (TerminalCompileResult != "unknown" || TerminalCompileErrorCount != 0 || TerminalCompileObservedTicks != 0))
            {
                return false;
            }
            if (Finished && (TerminalCompileResult == "unknown" || TerminalCompileObservedTicks < FinishedTicks
                || TerminalCompileObservedTicks - FinishedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks
                || (TerminalCompileResult == "clean" && TerminalCompileErrorCount != 0)
                || (TerminalCompileResult == "failed" && TerminalCompileErrorCount <= 0)))
            {
                return false;
            }
            if (ReloadStartedTicks == 0)
            {
                if (!string.IsNullOrEmpty(ReloadSourceInstanceId) || PostReloadObservedTicks != 0 || !string.IsNullOrEmpty(ReplacementInstanceId)) return false;
            }
            else if (!Started || ReloadStartedTicks < StartedTicks || !string.Equals(ReloadSourceInstanceId, SourceInstanceId, StringComparison.Ordinal))
            {
                return false;
            }
            if (ReloadStartedTicks > 0 && ReloadStartedTicks - StartedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks)
            {
                return false;
            }
            if (Finished && ReloadStartedTicks > 0 && Math.Abs(FinishedTicks - ReloadStartedTicks) > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks)
            {
                return false;
            }
            if (TerminalCompileResult == "failed" && ReloadStartedTicks != 0)
            {
                return false;
            }
            if (PostReloadObservedTicks > 0 && (!Finished || TerminalCompileResult != "clean" || ReloadStartedTicks <= 0
                || PostReloadObservedTicks < ReloadStartedTicks || PostReloadObservedTicks < FinishedTicks
                || PostReloadObservedTicks < TerminalCompileObservedTicks
                || PostReloadObservedTicks - ReloadStartedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks
                || PostReloadObservedTicks - FinishedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks
                || PostReloadObservedTicks - TerminalCompileObservedTicks > ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks
                || !IsValidInstanceId(ReplacementInstanceId) || string.Equals(ReplacementInstanceId, SourceInstanceId, StringComparison.Ordinal)))
            {
                return false;
            }
            if (!RequestIssued)
            {
                return (Status == "pending" || Status == "failed" || Status == "abandoned") && !Started && !Finished && RequestGeneration == 0 && StartedGeneration == 0 && FinishedGeneration == 0
                    && RequestIssuedTicks == 0 && StartedTicks == 0 && FinishedTicks == 0 && (Status == "pending" ? Failure == None : Failure != None);
            }
            if (RequestGeneration <= 0 || RequestIssuedTicks < RequestedTicks || ObservedGeneration > RequestGeneration)
            {
                return false;
            }
            if (!Started)
            {
                return (Status == "pending" || Status == "failed" || Status == "abandoned") && !Finished && StartedGeneration == 0 && FinishedGeneration == 0 && StartedTicks == 0 && FinishedTicks == 0
                    && (Status == "pending" ? Failure == None : Failure != None);
            }
            if (StartedGeneration <= 0 || StartedGeneration != RequestGeneration || ObservedGeneration != StartedGeneration || StartedTicks < RequestIssuedTicks)
            {
                return false;
            }
            if (!Finished)
            {
                return (Status == "pending" || Status == "failed") && FinishedGeneration == 0 && FinishedTicks == 0 && (Status == "pending" ? Failure == None : Failure != None);
            }
            return (Status == "verified" || Status == "failed" || Status == "abandoned") && FinishedGeneration == StartedGeneration && FinishedTicks >= StartedTicks
                && (Status == "verified" ? Failure == None : Failure != None);
        }

        ExplicitCompilationBarrierState Failed(string failure, int? observedGeneration = null)
        {
            return Copy(status: "failed", observedGeneration: observedGeneration ?? ObservedGeneration, failure: failure);
        }

        public ExplicitCompilationBarrierState FailForCallback(string failure)
        {
            return IsFailure(failure) && failure != None ? Failed(failure) : Failed("generation_or_timestamp_mismatch");
        }

        ExplicitCompilationBarrierState Copy(
            string status = null,
            int? observedGeneration = null,
            int? requestGeneration = null,
            int? startedGeneration = null,
            int? finishedGeneration = null,
            long? requestIssuedTicks = null,
            long? startedTicks = null,
            long? finishedTicks = null,
            bool? requestIssued = null,
            bool? started = null,
            bool? finished = null,
            string terminalCompileResult = null,
            int? terminalCompileErrorCount = null,
            long? terminalCompileObservedTicks = null,
            long? reloadStartedTicks = null,
            string reloadSourceInstanceId = null,
            long? postReloadObservedTicks = null,
            string replacementInstanceId = null,
            string failure = null,
            string recovery = null,
            string previousFailure = null)
        {
            return new ExplicitCompilationBarrierState(
                status ?? Status,
                OperationId,
                SourceInstanceId,
                observedGeneration ?? ObservedGeneration,
                requestGeneration ?? RequestGeneration,
                startedGeneration ?? StartedGeneration,
                finishedGeneration ?? FinishedGeneration,
                RequestedTicks,
                requestIssuedTicks ?? RequestIssuedTicks,
                startedTicks ?? StartedTicks,
                finishedTicks ?? FinishedTicks,
                requestIssued ?? RequestIssued,
                started ?? Started,
                finished ?? Finished,
                terminalCompileResult ?? TerminalCompileResult,
                terminalCompileErrorCount ?? TerminalCompileErrorCount,
                terminalCompileObservedTicks ?? TerminalCompileObservedTicks,
                reloadStartedTicks ?? ReloadStartedTicks,
                reloadSourceInstanceId ?? ReloadSourceInstanceId,
                postReloadObservedTicks ?? PostReloadObservedTicks,
                replacementInstanceId ?? ReplacementInstanceId,
                failure ?? Failure,
                recovery ?? Recovery,
                previousFailure ?? PreviousFailure);
        }

        static string SafePreviousFailure(string value)
        {
            return IsFailure(value) ? value : "malformed_session_state";
        }

        static bool IsStatus(string value)
        {
            return value == "pending" || value == "verified" || value == "failed" || value == "abandoned";
        }

        static bool IsFailure(string value)
        {
            return value == None
                || value == "malformed_session_state"
                || value == "request_not_issued"
                || value == "operation_max_age_exceeded"
                || value == "finish_without_start"
                || value == "multiple_generation_overlap"
                || value == "multiple_finish"
                || value == "invalid_request_transition"
                || value == "generation_or_timestamp_mismatch"
                || value == "callback_context_unavailable"
                || value == "callback_context_mismatch"
                || value == "runtime_epoch_unavailable"
                || value == "reload_marker_stale"
                || value == "reload_marker_mismatch"
                || value == "replacement_marker_mismatch"
                || value == "runtime_replaced_before_compilation_started"
                || value == "runtime_replaced_without_correlated_reload_proof"
                || value == "verified" || value == "failed" || value == "abandoned";
        }

        static bool IsTerminalCompileResult(string value)
        {
            return value == "unknown" || value == "clean" || value == "failed";
        }

        static bool IsValidOptionalInstanceId(string value)
        {
            return string.IsNullOrEmpty(value) || IsValidInstanceId(value);
        }

        static bool IsRecovery(string value)
        {
            return value == None
                || value == "replaced_unarmed_pending"
                || value == "abandoned_stale_armed"
                || value == "reset_invalid_state"
                || value == "reset_terminal_state"
                || value == "replaced_verified"
                || value == "replaced_verified_compile_failed_no_reload"
                || value == "preserved_verified_replacement";
        }

        static bool TryNonNegativeInt(string value, out int parsed)
        {
            parsed = 0;
            return IsDigits(value) && int.TryParse(value, out parsed) && parsed >= 0;
        }

        static bool TryPositiveLong(string value, out long parsed)
        {
            parsed = 0;
            return IsDigits(value) && long.TryParse(value, out parsed) && parsed > 0;
        }

        static bool TryNonNegativeLong(string value, out long parsed)
        {
            parsed = 0;
            return IsDigits(value) && long.TryParse(value, out parsed) && parsed >= 0;
        }

        static bool TryBool(string value, out bool parsed)
        {
            parsed = value == "1";
            return parsed || value == "0";
        }

        static string Bool(bool value)
        {
            return value ? "1" : "0";
        }

        static bool IsDigits(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }
            return true;
        }
    }

    public sealed class ExplicitCompilationRuntimeEpoch
    {
        public ExplicitCompilationRuntimeEpoch(string operationId, int expectedGeneration)
        {
            OperationId = operationId ?? string.Empty;
            ExpectedGeneration = expectedGeneration;
        }

        public string OperationId { get; private set; }
        public int ExpectedGeneration { get; private set; }
        public object StartedContext { get; set; }
    }

    public sealed class ExplicitCompilationCallbackResult
    {
        public ExplicitCompilationCallbackResult(ExplicitCompilationBarrierState state, ExplicitCompilationRuntimeEpoch epoch)
        {
            State = state;
            Epoch = epoch;
        }
        public ExplicitCompilationBarrierState State { get; private set; }
        public ExplicitCompilationRuntimeEpoch Epoch { get; private set; }
    }

    public static class ExplicitCompilationBarrierCallbacks
    {
        public static ExplicitCompilationCallbackResult HandleStarted(ExplicitCompilationBarrierState state, ExplicitCompilationRuntimeEpoch epoch, object context, long ticks)
        {
            if (state == null || state.Status != "pending")
            {
                return new ExplicitCompilationCallbackResult(state, epoch);
            }
            if (!state.RequestIssued)
            {
                return new ExplicitCompilationCallbackResult(state.OnCompilationStarted(state.OperationId, ticks), epoch);
            }
            if (epoch == null || !string.Equals(epoch.OperationId, state.OperationId, StringComparison.Ordinal) || epoch.ExpectedGeneration != state.RequestGeneration)
            {
                return new ExplicitCompilationCallbackResult(state.FailForCallback("runtime_epoch_unavailable"), null);
            }
            if (context == null)
            {
                return new ExplicitCompilationCallbackResult(state.FailForCallback("callback_context_unavailable"), null);
            }

            var next = state.OnCompilationStarted(epoch.OperationId, ticks);
            if (next.Status == "pending" && next.Started)
            {
                epoch.StartedContext = context;
                return new ExplicitCompilationCallbackResult(next, epoch);
            }
            return new ExplicitCompilationCallbackResult(next, null);
        }

        public static ExplicitCompilationCallbackResult HandleFinished(ExplicitCompilationBarrierState state, ExplicitCompilationRuntimeEpoch epoch, object context, long ticks, int compileErrorCount = 0)
        {
            if (state == null || state.Status != "pending")
            {
                return new ExplicitCompilationCallbackResult(state, epoch);
            }
            if (!state.RequestIssued)
            {
                return new ExplicitCompilationCallbackResult(state.OnCompilationFinished(state.OperationId, state.StartedGeneration, ticks), epoch);
            }
            if (!state.Started)
            {
                return new ExplicitCompilationCallbackResult(state.FailForCallback("finish_without_start"), null);
            }

            // Unity documents context as a per-cycle token valid only for that cycle. After domain reload
            // the token is unavailable, so persisted started generation is the only accepted finish fence.
            if (epoch != null)
            {
                if (!string.Equals(epoch.OperationId, state.OperationId, StringComparison.Ordinal)
                    || epoch.ExpectedGeneration != state.StartedGeneration
                    || epoch.StartedContext == null
                    || context == null
                    || !ReferenceEquals(epoch.StartedContext, context))
                {
                    return new ExplicitCompilationCallbackResult(state.FailForCallback("callback_context_mismatch"), null);
                }
            }

            var next = state.OnCompilationFinished(state.OperationId, state.StartedGeneration, ticks, compileErrorCount);
            return new ExplicitCompilationCallbackResult(next, null);
        }
    }

    public sealed class ExplicitCompilationBarrierParseResult
    {
        ExplicitCompilationBarrierParseResult(bool available, ExplicitCompilationBarrierState state, string failure)
        {
            Available = available;
            State = state;
            Failure = failure ?? string.Empty;
        }
        public bool Available { get; private set; }
        public ExplicitCompilationBarrierState State { get; private set; }
        public string Failure { get; private set; }
        public ExplicitCompilationBarrierParseResult Clone() { return new ExplicitCompilationBarrierParseResult(Available, State == null ? null : State.Clone(), Failure); }
        public static ExplicitCompilationBarrierParseResult Ok(ExplicitCompilationBarrierState state) { return new ExplicitCompilationBarrierParseResult(true, state, string.Empty); }
        public static ExplicitCompilationBarrierParseResult NotRequired() { return new ExplicitCompilationBarrierParseResult(true, null, string.Empty); }
        public static ExplicitCompilationBarrierParseResult Malformed(string failure) { return new ExplicitCompilationBarrierParseResult(false, null, failure); }
    }

    public sealed class ExplicitCompilationBarrierBeginDecision
    {
        ExplicitCompilationBarrierBeginDecision(bool success, string errorCode, string failure, ExplicitCompilationBarrierState state)
        {
            Success = success;
            ErrorCode = errorCode ?? string.Empty;
            Failure = failure ?? string.Empty;
            State = state;
        }
        public bool Success { get; private set; }
        public string ErrorCode { get; private set; }
        public string Failure { get; private set; }
        public ExplicitCompilationBarrierState State { get; private set; }
        public static ExplicitCompilationBarrierBeginDecision Ok(ExplicitCompilationBarrierState state) { return new ExplicitCompilationBarrierBeginDecision(true, string.Empty, string.Empty, state); }
        public static ExplicitCompilationBarrierBeginDecision Error(string code, string failure, ExplicitCompilationBarrierState state) { return new ExplicitCompilationBarrierBeginDecision(false, code, failure, state); }
    }

    public sealed class ExplicitCompilationBarrierResult
    {
        ExplicitCompilationBarrierResult(bool success, string errorCode, string failure, ExplicitCompilationBarrierState state)
        {
            Success = success;
            ErrorCode = errorCode ?? string.Empty;
            Failure = failure ?? string.Empty;
            State = state;
        }
        public bool Success { get; private set; }
        public string ErrorCode { get; private set; }
        public string Failure { get; private set; }
        public ExplicitCompilationBarrierState State { get; private set; }
        public static ExplicitCompilationBarrierResult Ok(ExplicitCompilationBarrierState state) { return new ExplicitCompilationBarrierResult(true, string.Empty, string.Empty, state); }
        public static ExplicitCompilationBarrierResult Error(string code, string failure, ExplicitCompilationBarrierState state) { return new ExplicitCompilationBarrierResult(false, code, failure, state); }
    }

    public sealed class ExplicitCompilationBarrierSnapshot
    {
        public ExplicitCompilationBarrierSnapshot(string state, ExplicitCompilationBarrierState operation, string failure, bool required, bool postReloadObserved, long postReloadObservedTicks)
        {
            State = state ?? "unavailable";
            Operation = operation;
            Failure = failure ?? string.Empty;
            Required = required;
            PostReloadObserved = postReloadObserved;
            PostReloadObservedTicks = postReloadObservedTicks;
        }
        public string State { get; private set; }
        public ExplicitCompilationBarrierState Operation { get; private set; }
        public string Failure { get; private set; }
        public bool Required { get; private set; }
        public bool PostReloadObserved { get; private set; }
        public long PostReloadObservedTicks { get; private set; }
        public bool Verified => State == "verified" && Operation != null && Operation.RequestIssued && Operation.Started && Operation.Finished;
        public bool Superseded => State == "abandoned" && Operation != null
            && (Operation.Failure == "runtime_replaced_before_compilation_started" || Operation.Failure == "runtime_replaced_without_correlated_reload_proof");
        public bool Unavailable => State == "unavailable" || State == "failed" || (State == "abandoned" && (Required || !Superseded));
        public bool TerminalCompileFailureWithoutReload => Verified && Operation.TerminalCompileFailedWithoutReload;
        public bool ReloadPending => Verified && Operation.TerminalCompileResult == "clean" && !PostReloadObserved;
        public bool Settling => State == "pending" || ReloadPending || (Verified && Operation.TerminalCompileResult == "unknown");
        public bool Stable => (!Required && (State == "not_required" || Superseded)) || (Verified && Operation.TerminalCompileResult == "clean" && PostReloadObserved);

        public string ToJson()
        {
            var operation = Operation;
            return "{\"state\":" + JsonUtil.String(State)
                + ",\"required\":" + JsonUtil.Bool(Required)
                + ",\"operation_id\":" + JsonUtil.String(operation == null ? string.Empty : operation.OperationId)
                + ",\"source_instance_id\":" + JsonUtil.String(operation == null ? string.Empty : operation.SourceInstanceId)
                + ",\"requested\":" + JsonUtil.Bool(operation != null)
                + ",\"request_issued\":" + JsonUtil.Bool(operation != null && operation.RequestIssued)
                + ",\"started\":" + JsonUtil.Bool(operation != null && operation.Started)
                + ",\"finished\":" + JsonUtil.Bool(operation != null && operation.Finished)
                + ",\"terminal_compile_result\":" + JsonUtil.String(operation == null ? "unknown" : operation.TerminalCompileResult)
                + ",\"terminal_compile_error_count\":" + (operation == null ? 0 : operation.TerminalCompileErrorCount)
                + ",\"terminal_compile_observed_utc\":" + JsonUtil.String(Timestamp(operation == null ? 0 : operation.TerminalCompileObservedTicks))
                + ",\"terminal_compile_failure_without_reload\":" + JsonUtil.Bool(TerminalCompileFailureWithoutReload)
                + ",\"reload_started\":" + JsonUtil.Bool(operation != null && operation.ReloadStartedTicks > 0)
                + ",\"reload_started_utc\":" + JsonUtil.String(Timestamp(operation == null ? 0 : operation.ReloadStartedTicks))
                + ",\"reload_source_instance_id\":" + JsonUtil.String(operation == null ? string.Empty : operation.ReloadSourceInstanceId)
                + ",\"replacement_instance_id\":" + JsonUtil.String(operation == null ? string.Empty : operation.ReplacementInstanceId)
                + ",\"post_reload_observed\":" + JsonUtil.Bool(PostReloadObserved)
                + ",\"post_reload_observed_utc\":" + JsonUtil.String(Timestamp(PostReloadObservedTicks))
                + ",\"settling\":" + JsonUtil.Bool(Settling)
                + ",\"reload_pending\":" + JsonUtil.Bool(ReloadPending)
                + ",\"stable\":" + JsonUtil.Bool(Stable)
                + ",\"requested_utc\":" + JsonUtil.String(Timestamp(operation == null ? 0 : operation.RequestedTicks))
                + ",\"request_issued_utc\":" + JsonUtil.String(Timestamp(operation == null ? 0 : operation.RequestIssuedTicks))
                + ",\"started_utc\":" + JsonUtil.String(Timestamp(operation == null ? 0 : operation.StartedTicks))
                + ",\"finished_utc\":" + JsonUtil.String(Timestamp(operation == null ? 0 : operation.FinishedTicks))
                + ",\"observed_generation\":" + (operation == null ? 0 : operation.ObservedGeneration)
                + ",\"request_generation\":" + (operation == null ? 0 : operation.RequestGeneration)
                + ",\"started_generation\":" + (operation == null ? 0 : operation.StartedGeneration)
                + ",\"finished_generation\":" + (operation == null ? 0 : operation.FinishedGeneration)
                + ",\"max_operation_age_ms\":3600000"
                + ",\"recovery\":" + JsonUtil.String(operation == null ? string.Empty : operation.Recovery)
                + ",\"previous_failure\":" + JsonUtil.String(operation == null ? string.Empty : operation.PreviousFailure)
                + ",\"auto_refresh_enabled\":\"unknown\""
                + ",\"failure\":" + JsonUtil.String(Failure)
                + ",\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\"]}";
        }

        static string Timestamp(long ticks)
        {
            return ticks <= 0 || ticks > DateTime.MaxValue.Ticks ? string.Empty : new DateTime(ticks, DateTimeKind.Utc).ToString("O");
        }
    }
}
