using System;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    [InitializeOnLoad]
    internal static class EditorQuitLifecycle
    {
        const string OperationIdKey = "SGG.UnityMCP.EditorQuit.OperationId";
        const string CallerArgsHashKey = "SGG.UnityMCP.EditorQuit.CallerArgsHash";
        const string StateKey = "SGG.UnityMCP.EditorQuit.State";
        const string RequestedUtcKey = "SGG.UnityMCP.EditorQuit.RequestedUtc";
        const string UpdatedUtcKey = "SGG.UnityMCP.EditorQuit.UpdatedUtc";
        const string WantsToQuitObservedKey = "SGG.UnityMCP.EditorQuit.WantsToQuitObserved";
        const string ExitDispatchedKey = "SGG.UnityMCP.EditorQuit.ExitDispatched";
        const string AcceptanceDeadlineUtcKey = "SGG.UnityMCP.EditorQuit.AcceptanceDeadlineUtc";
        const double ExitReturnGraceSeconds = 1.0;

        static bool s_wantsToQuitObserved;
        static bool s_quittingObserved;

        static EditorQuitLifecycle()
        {
            EditorApplication.wantsToQuit -= WantsToQuit;
            EditorApplication.wantsToQuit += WantsToQuit;
            EditorApplication.quitting -= MarkAccepted;
            EditorApplication.quitting += MarkAccepted;
            var state = SessionState.GetString(StateKey, "none");
            if (state == "deferred" || state == "requesting")
            {
                SetState("uncertain", false);
            }
        }

        internal static EditorQuitLifecycleSnapshot Current => new EditorQuitLifecycleSnapshot
        {
            OperationId = SessionState.GetString(OperationIdKey, string.Empty),
            CallerArgsHash = SessionState.GetString(CallerArgsHashKey, string.Empty),
            State = SessionState.GetString(StateKey, "none"),
            RequestedUtc = SessionState.GetString(RequestedUtcKey, string.Empty),
            UpdatedUtc = SessionState.GetString(UpdatedUtcKey, string.Empty),
        };

        internal static void Begin(string operationId, string callerArgsHash, int waitTimeoutMs)
        {
            var requestedUtc = DateTime.UtcNow;
            SessionState.SetString(OperationIdKey, operationId ?? string.Empty);
            SessionState.SetString(CallerArgsHashKey, callerArgsHash ?? string.Empty);
            SessionState.SetString(RequestedUtcKey, requestedUtc.ToString("O"));
            SessionState.SetString(AcceptanceDeadlineUtcKey, requestedUtc.AddMilliseconds(waitTimeoutMs).ToString("O"));
            SessionState.SetBool(ExitDispatchedKey, false);
            s_wantsToQuitObserved = false;
            s_quittingObserved = false;
            SessionState.SetBool(WantsToQuitObservedKey, false);
            SetState("deferred", true);
        }

        internal static void ScheduleEditorExit()
        {
            EditorApplication.update -= RequestEditorExit;
            EditorApplication.update += RequestEditorExit;
        }

        internal static void RequestEditorExit()
        {
            EditorApplication.update -= RequestEditorExit;
            var snapshot = Current;
            if (string.IsNullOrEmpty(snapshot.OperationId) || snapshot.State != "deferred")
            {
                return;
            }

            DateTime acceptanceDeadlineUtc;
            if (!DateTime.TryParse(SessionState.GetString(AcceptanceDeadlineUtcKey, string.Empty), null, System.Globalization.DateTimeStyles.RoundtripKind, out acceptanceDeadlineUtc)
                || DateTime.UtcNow > acceptanceDeadlineUtc)
            {
                SetState("uncertain", true);
                return;
            }

            s_wantsToQuitObserved = false;
            s_quittingObserved = false;
            SetState("requesting", true);
            try
            {
                BridgeReloadTrace.Log("editor.quit.exit", "EditorApplication.Exit(0)");
                SessionState.SetBool(ExitDispatchedKey, true);
                EditorApplication.Exit(0);
                if (!s_quittingObserved)
                {
                    EditorApplication.update -= FinalizeReturnedExit;
                    EditorApplication.update += FinalizeReturnedExit;
                }
            }
            catch (Exception exception)
            {
                BridgeReloadTrace.LogError("editor.quit.exit.error", exception);
                SessionState.SetBool(ExitDispatchedKey, false);
                SetState("ignored", true);
            }
        }

        internal static void MarkAccepted()
        {
            var snapshot = Current;
            DateTime acceptanceDeadlineUtc;
            if (string.IsNullOrEmpty(snapshot.OperationId)
                || snapshot.State == "deferred"
                || snapshot.State == "accepted"
                || !SessionState.GetBool(ExitDispatchedKey, false)
                || !DateTime.TryParse(SessionState.GetString(AcceptanceDeadlineUtcKey, string.Empty), null, System.Globalization.DateTimeStyles.RoundtripKind, out acceptanceDeadlineUtc)
                || DateTime.UtcNow > acceptanceDeadlineUtc)
            {
                return;
            }

            s_quittingObserved = true;
            EditorApplication.update -= FinalizeReturnedExit;
            SetState("accepted", false);
        }

        static void FinalizeReturnedExit()
        {
            if (s_quittingObserved)
            {
                return;
            }

            var snapshot = Current;
            DateTime requestedUtc;
            if (snapshot.State != "requesting"
                || !DateTime.TryParse(snapshot.RequestedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out requestedUtc)
                || (DateTime.UtcNow - requestedUtc).TotalSeconds < ExitReturnGraceSeconds)
            {
                return;
            }

            EditorApplication.update -= FinalizeReturnedExit;
            SetState(SessionState.GetBool(WantsToQuitObservedKey, s_wantsToQuitObserved) ? "cancelled" : "ignored", true);
        }

        static bool WantsToQuit()
        {
            s_wantsToQuitObserved = true;
            SessionState.SetBool(WantsToQuitObservedKey, true);
            return true;
        }

        static void SetState(string state, bool publish)
        {
            SessionState.SetString(StateKey, state);
            SessionState.SetString(UpdatedUtcKey, DateTime.UtcNow.ToString("O"));
            BridgeReloadTrace.Log("editor.quit.lifecycle", state);
            if (publish)
            {
                UnityBridgeRuntime.PublishQuitLifecycleHeartbeat();
            }
        }
    }

    internal sealed class EditorQuitLifecycleSnapshot
    {
        internal string OperationId = string.Empty;
        internal string CallerArgsHash = string.Empty;
        internal string State = "none";
        internal string RequestedUtc = string.Empty;
        internal string UpdatedUtc = string.Empty;
    }
}
