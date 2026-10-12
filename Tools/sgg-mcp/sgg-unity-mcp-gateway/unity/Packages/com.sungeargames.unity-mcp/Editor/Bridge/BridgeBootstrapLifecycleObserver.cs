using System;
using System.IO;
using UnityEditor.Callbacks;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal static class BridgeBootstrapLifecycleObserver
    {
        static readonly string DomainId = Guid.NewGuid().ToString("N").Substring(0, 12);
        static bool s_runtimeInitializerObserved;
        static bool s_editorUpdateObserved;
        static bool s_waitingHeartbeatResultObserved;
        static string s_idleGateState = string.Empty;
        static long s_sequence;

        [DidReloadScripts(-1000)]
        static void OnScriptsReloaded()
        {
            var runtimeInitializerObserved = s_runtimeInitializerObserved;
            Log(runtimeInitializerObserved
                ? "bridge.bootstrap.did_reload_scripts.runtime_initializer_observed"
                : "bridge.bootstrap.did_reload_scripts.runtime_initializer_not_observed");
            if (!runtimeInitializerObserved && UnityBridgeRuntime.EnsureBootstrapScheduled("did_reload_scripts"))
            {
                Log("bridge.bootstrap.did_reload_scripts.recovery_scheduled");
            }
        }

        internal static void MarkRuntimeInitializerObserved()
        {
            s_runtimeInitializerObserved = true;
        }

        internal static void MarkEditorUpdateObserved()
        {
            if (s_editorUpdateObserved)
            {
                return;
            }

            s_editorUpdateObserved = true;
            Log("bridge.bootstrap.runtime_initializer_observed");
            Log("bridge.bootstrap.editor_update_observed");
        }

        internal static void ObserveIdleGate(bool isCompiling, bool isUpdating)
        {
            var state = isCompiling && isUpdating
                ? "compiling_and_updating"
                : isCompiling
                    ? "compiling"
                    : isUpdating
                        ? "updating"
                        : "idle";
            if (string.Equals(state, s_idleGateState, StringComparison.Ordinal))
            {
                return;
            }

            s_idleGateState = state;
            Log("bridge.bootstrap.idle_gate." + state);
        }

        internal static void MarkBootstrapStarted()
        {
            Log("bridge.bootstrap.lifecycle_started");
        }

        internal static void MarkBootstrapFailed(Exception exception)
        {
            Log("bridge.bootstrap.lifecycle_failed." + FailureKind(exception));
        }

        internal static void MarkHeartbeatResult(bool written, string failureKind)
        {
            Log(written
                ? "bridge.bootstrap.first_heartbeat_published"
                : "bridge.bootstrap.first_heartbeat_failed." + NormalizeFailureKind(failureKind));
        }

        internal static void MarkWaitingHeartbeatResult(bool written, string failureKind)
        {
            if (s_waitingHeartbeatResultObserved && !written)
            {
                return;
            }

            s_waitingHeartbeatResultObserved = true;
            Log(written
                ? "bridge.bootstrap.waiting_heartbeat_published"
                : "bridge.bootstrap.waiting_heartbeat_failed." + NormalizeFailureKind(failureKind));
        }

        internal static void MarkHeartbeatRetryPublished()
        {
            Log("bridge.bootstrap.heartbeat_retry_published");
        }

        internal static void MarkBootstrapRecoveryScheduled(int completedAttempts)
        {
            Log("bridge.bootstrap.lifecycle_recovery_scheduled." + Math.Max(1, completedAttempts));
        }

        internal static void MarkBootstrapRecoveryExhausted()
        {
            Log("bridge.bootstrap.lifecycle_recovery_exhausted");
        }

        internal static string FailureKind(Exception exception)
        {
            if (exception is UnauthorizedAccessException)
            {
                return "unauthorized";
            }
            if (exception is IOException)
            {
                return "io";
            }

            return "unexpected";
        }

        static string NormalizeFailureKind(string failureKind)
        {
            switch (failureKind)
            {
                case "io":
                case "unauthorized":
                case "canonical_path_dependency_missing":
                    return failureKind;
                default:
                    return "unexpected";
            }
        }

        static void Log(string eventName)
        {
            var sequence = System.Threading.Interlocked.Increment(ref s_sequence);
            BridgeReloadTrace.Log(eventName, "domain=" + DomainId + ";sequence=" + sequence);
        }
    }
}
