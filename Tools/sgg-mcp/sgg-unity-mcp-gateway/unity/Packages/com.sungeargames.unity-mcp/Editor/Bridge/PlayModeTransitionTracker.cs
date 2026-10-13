using System;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Bridge
{
    [InitializeOnLoad]
    public static class PlayModeTransitionTracker
    {
        const string SessionStateKey = "SGG.UnityMCP.PlayModeTransition.state";
        const string BarrierSessionStateKey = "SGG.UnityMCP.PlayModeTransition.barrier";
        const string BarrierGenerationSessionStateKey = "SGG.UnityMCP.PlayModeTransition.barrier_generation";
        static readonly object Gate = new object();
        static TransitionState s_state;
        static PlayModeBarrierState s_barrier;
        static bool s_postReloadObserved;
        static long s_postReloadObservedTicks;
        static bool s_restoreReconciliationRegistered;

        static PlayModeTransitionTracker()
        {
            s_state = TransitionState.Parse(SessionState.GetString(SessionStateKey, string.Empty));
            s_barrier = PlayModeBarrierState.Parse(SessionState.GetString(BarrierSessionStateKey, string.Empty));
            if (s_state != null)
            {
                s_postReloadObserved = true;
                s_postReloadObservedTicks = DateTime.UtcNow.Ticks;
                if (!s_state.Completed && TargetReachedForRestoration(s_state.Target, EditorApplication.isPlaying, EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    s_state = s_state.Complete(s_postReloadObservedTicks);
                    SessionState.SetString(SessionStateKey, s_state.Serialize());
                }
                if (!s_state.Completed)
                {
                    EditorApplication.update += ReconcileRestoredTransition;
                    s_restoreReconciliationRegistered = true;
                }
            }
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static bool TryGetCurrentPlayModeBarrier(out PlayModeBarrierEvidence evidence)
        {
            lock (Gate)
            {
                if (!EditorApplication.isPlaying || s_barrier == null || !s_barrier.Active)
                {
                    evidence = default;
                    return false;
                }

                evidence = new PlayModeBarrierEvidence(s_barrier.BarrierId, s_barrier.Generation, s_barrier.EnteredTicks);
                return true;
            }
        }

        public static string CurrentPlayModeBarrierJson()
        {
            lock (Gate)
            {
                var current = EditorApplication.isPlaying && s_barrier != null && s_barrier.Active;
                return "{\"observed\":" + JsonUtil.Bool(current)
                    + ",\"barrier_id\":" + (current ? JsonUtil.String(s_barrier.BarrierId) : "null")
                    + ",\"generation\":" + (current ? s_barrier.Generation : 0)
                    + ",\"entered_utc\":" + (current ? JsonUtil.String(Timestamp(s_barrier.EnteredTicks)) : "null")
                    + "}";
            }
        }

        public static string Begin(string expectedOperationId, string target, bool transitionRequested, bool targetAlreadyReached, bool reloadExpected)
        {
            var operationId = IsValidOperationId(expectedOperationId) ? expectedOperationId : Guid.NewGuid().ToString("N");
            var now = DateTime.UtcNow.Ticks;
            var state = new TransitionState(operationId, target, now, transitionRequested, reloadExpected, targetAlreadyReached, targetAlreadyReached ? now : 0);
            lock (Gate)
            {
                s_state = state;
                s_postReloadObserved = false;
                s_postReloadObservedTicks = 0;
                SessionState.SetString(SessionStateKey, state.Serialize());
            }
            return operationId;
        }

        public static string StatusJson(string expectedOperationId = "")
        {
            lock (Gate)
            {
                var state = s_state;
                var matches = state != null && (!IsValidOperationId(expectedOperationId) || string.Equals(state.OperationId, expectedOperationId, StringComparison.Ordinal));
                return "{\"state\":" + JsonUtil.String(state == null ? "not_required" : matches ? state.Completed ? "verified" : "pending" : "unknown")
                    + ",\"operation_id\":" + JsonUtil.String(matches ? state.OperationId : string.Empty)
                    + ",\"target\":" + JsonUtil.String(matches ? state.Target : string.Empty)
                    + ",\"requested\":" + JsonUtil.Bool(matches)
                    + ",\"transition_requested\":" + JsonUtil.Bool(matches && state.TransitionRequested)
                    + ",\"reload_expected\":" + JsonUtil.Bool(matches && state.ReloadExpected)
                    + ",\"completed\":" + JsonUtil.Bool(matches && state.Completed)
                    + ",\"requested_utc\":" + JsonUtil.String(matches ? Timestamp(state.RequestedTicks) : string.Empty)
                    + ",\"completed_utc\":" + JsonUtil.String(matches ? Timestamp(state.CompletedTicks) : string.Empty)
                    + ",\"post_reload_observed\":" + JsonUtil.Bool(matches && s_postReloadObserved)
                    + ",\"post_reload_observed_utc\":" + JsonUtil.String(matches && s_postReloadObserved ? Timestamp(s_postReloadObservedTicks) : string.Empty)
                    + "}";
            }
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            var target = change == PlayModeStateChange.EnteredPlayMode ? "playing"
                : change == PlayModeStateChange.EnteredEditMode ? "editing"
                : string.Empty;
            lock (Gate)
            {
                if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode)
                {
                    s_barrier = s_barrier == null ? null : s_barrier.Deactivate();
                    SessionState.SetString(BarrierSessionStateKey, s_barrier == null ? string.Empty : s_barrier.Serialize());
                }
                if (change == PlayModeStateChange.EnteredPlayMode)
                {
                    var generation = Math.Max(0, SessionState.GetInt(BarrierGenerationSessionStateKey, 0)) + 1;
                    s_barrier = new PlayModeBarrierState(Guid.NewGuid().ToString("N"), generation, DateTime.UtcNow.Ticks, true);
                    SessionState.SetInt(BarrierGenerationSessionStateKey, generation);
                    SessionState.SetString(BarrierSessionStateKey, s_barrier.Serialize());
                }

                if (string.IsNullOrEmpty(target) || s_state == null || s_state.Completed || !string.Equals(s_state.Target, target, StringComparison.Ordinal)) return;
                s_state = s_state.Complete(DateTime.UtcNow.Ticks);
                SessionState.SetString(SessionStateKey, s_state.Serialize());
                StopRestoreReconciliation();
            }
        }

        static void ReconcileRestoredTransition()
        {
            lock (Gate)
            {
                if (s_state != null && !s_state.Completed
                    && TargetReachedForRestoration(s_state.Target, EditorApplication.isPlaying, EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    s_state = s_state.Complete(DateTime.UtcNow.Ticks);
                    SessionState.SetString(SessionStateKey, s_state.Serialize());
                }
                if (s_state == null || s_state.Completed)
                {
                    StopRestoreReconciliation();
                }
            }
        }

        static void StopRestoreReconciliation()
        {
            if (!s_restoreReconciliationRegistered) return;
            EditorApplication.update -= ReconcileRestoredTransition;
            s_restoreReconciliationRegistered = false;
        }

        public readonly struct PlayModeBarrierEvidence
        {
            public PlayModeBarrierEvidence(string barrierId, int generation, long enteredTicks)
            {
                BarrierId = barrierId;
                Generation = generation;
                EnteredUtc = new DateTime(enteredTicks, DateTimeKind.Utc);
            }

            public string BarrierId { get; }
            public int Generation { get; }
            public DateTime EnteredUtc { get; }
        }

        internal static bool ReloadExpectedForTransition(bool transitionRequested, bool enteringPlayMode, bool enterPlayModeOptionsEnabled, EnterPlayModeOptions options)
        {
            return transitionRequested && enteringPlayMode && (!enterPlayModeOptionsEnabled || (options & EnterPlayModeOptions.DisableDomainReload) == 0);
        }

        internal static bool TargetReachedForRestoration(string target, bool isPlaying, bool isPlayingOrWillChangePlaymode)
        {
            return target == "playing"
                ? isPlaying
                : target == "editing" && !isPlaying && !isPlayingOrWillChangePlaymode;
        }

        static bool IsValidOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }
            return true;
        }

        static string Timestamp(long ticks)
        {
            return ticks <= 0 || ticks > DateTime.MaxValue.Ticks ? string.Empty : new DateTime(ticks, DateTimeKind.Utc).ToString("O");
        }

        sealed class TransitionState
        {
            public TransitionState(string operationId, string target, long requestedTicks, bool transitionRequested, bool reloadExpected, bool completed, long completedTicks)
            {
                OperationId = operationId;
                Target = target;
                RequestedTicks = requestedTicks;
                TransitionRequested = transitionRequested;
                ReloadExpected = reloadExpected;
                Completed = completed;
                CompletedTicks = completedTicks;
            }

            public string OperationId { get; private set; }
            public string Target { get; private set; }
            public long RequestedTicks { get; private set; }
            public bool TransitionRequested { get; private set; }
            public bool ReloadExpected { get; private set; }
            public bool Completed { get; private set; }
            public long CompletedTicks { get; private set; }

            public TransitionState Complete(long ticks)
            {
                return new TransitionState(OperationId, Target, RequestedTicks, TransitionRequested, ReloadExpected, true, ticks < RequestedTicks ? RequestedTicks : ticks);
            }

            public string Serialize()
            {
                return "v=2;operation_id=" + OperationId
                    + ";target=" + Target
                    + ";requested_ticks=" + RequestedTicks
                    + ";transition_requested=" + (TransitionRequested ? "1" : "0")
                    + ";reload_expected=" + (ReloadExpected ? "1" : "0")
                    + ";completed=" + (Completed ? "1" : "0")
                    + ";completed_ticks=" + CompletedTicks;
            }

            public static TransitionState Parse(string blob)
            {
                if (string.IsNullOrEmpty(blob) || blob.Length > 512) return null;
                var fields = blob.Split(';');
                var legacy = fields.Length == 7 && fields[0] == "v=1";
                if (!legacy && (fields.Length != 8 || fields[0] != "v=2")) return null;
                var operationId = Value(fields[1], "operation_id=");
                var target = Value(fields[2], "target=");
                if (!IsValidOperationId(operationId) || (target != "playing" && target != "editing")) return null;
                if (!long.TryParse(Value(fields[3], "requested_ticks="), out var requestedTicks) || requestedTicks <= 0) return null;
                var transitionRequested = Value(fields[4], "transition_requested=");
                var reloadExpected = legacy ? transitionRequested : Value(fields[5], "reload_expected=");
                var completed = Value(fields[legacy ? 5 : 6], "completed=");
                if ((transitionRequested != "0" && transitionRequested != "1") || (completed != "0" && completed != "1")) return null;
                if (reloadExpected != "0" && reloadExpected != "1") return null;
                if (!long.TryParse(Value(fields[legacy ? 6 : 7], "completed_ticks="), out var completedTicks) || completedTicks < 0) return null;
                if ((completed == "1" && completedTicks < requestedTicks) || (completed == "0" && completedTicks != 0)) return null;
                return new TransitionState(operationId, target, requestedTicks, transitionRequested == "1", reloadExpected == "1", completed == "1", completedTicks);
            }

            static string Value(string field, string prefix)
            {
                return field != null && field.StartsWith(prefix, StringComparison.Ordinal) ? field.Substring(prefix.Length) : string.Empty;
            }
        }

        sealed class PlayModeBarrierState
        {
            public PlayModeBarrierState(string barrierId, int generation, long enteredTicks, bool active)
            {
                BarrierId = barrierId;
                Generation = generation;
                EnteredTicks = enteredTicks;
                Active = active;
            }

            public string BarrierId { get; private set; }
            public int Generation { get; private set; }
            public long EnteredTicks { get; private set; }
            public bool Active { get; private set; }

            public PlayModeBarrierState Deactivate()
            {
                return new PlayModeBarrierState(BarrierId, Generation, EnteredTicks, false);
            }

            public string Serialize()
            {
                return "v=1;barrier_id=" + BarrierId
                    + ";generation=" + Generation
                    + ";entered_ticks=" + EnteredTicks
                    + ";active=" + (Active ? "1" : "0");
            }

            public static PlayModeBarrierState Parse(string blob)
            {
                if (string.IsNullOrEmpty(blob) || blob.Length > 256) return null;
                var fields = blob.Split(';');
                if (fields.Length != 5 || fields[0] != "v=1") return null;
                var barrierId = Value(fields[1], "barrier_id=");
                if (!IsValidOperationId(barrierId)) return null;
                if (!int.TryParse(Value(fields[2], "generation="), out var generation) || generation < 1) return null;
                if (!long.TryParse(Value(fields[3], "entered_ticks="), out var enteredTicks) || enteredTicks < 1 || enteredTicks > DateTime.MaxValue.Ticks) return null;
                var active = Value(fields[4], "active=");
                if (active != "0" && active != "1") return null;
                return new PlayModeBarrierState(barrierId, generation, enteredTicks, active == "1");
            }

            static string Value(string field, string prefix)
            {
                return field != null && field.StartsWith(prefix, StringComparison.Ordinal) ? field.Substring(prefix.Length) : string.Empty;
            }
        }
    }
}
