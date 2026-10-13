using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class EditorWindowCaptureBackend
    {
        internal const string BackendName = "unity_internal_capture_editor_window";
        internal const string TargetBinding = "managed_editor_window_to_host_view";

        static readonly BindingFlags StaticReflectionFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly BindingFlags InstanceReflectionFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static bool IsFocusedTarget(EditorWindow window)
        {
            try
            {
                return window != null && ReferenceEquals(EditorWindow.focusedWindow, window);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool TryGetCaptureMethod(out MethodInfo method, out string failureReason)
        {
            method = null;
            failureReason = string.Empty;
            try
            {
                method = typeof(UnityEditorInternal.InternalEditorUtility).GetMethod(
                    "CaptureEditorWindow",
                    StaticReflectionFlags,
                    null,
                    new[] { typeof(EditorWindow), typeof(RenderTexture) },
                    null);
                if (method == null || !method.IsStatic || method.ReturnType != typeof(bool))
                {
                    method = null;
                    failureReason = "UnityEditorInternal.InternalEditorUtility.CaptureEditorWindow(EditorWindow, RenderTexture) bool API is unavailable";
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                method = null;
                failureReason = "UnityEditorInternal.InternalEditorUtility.CaptureEditorWindow(EditorWindow, RenderTexture) bool API could not be inspected";
                return false;
            }
        }

        internal static bool TryProveHostViewBinding(EditorWindow window, out string failureReason)
        {
            failureReason = string.Empty;
            if (window == null)
            {
                failureReason = "The selected EditorWindow no longer exists";
                return false;
            }

            try
            {
                var parentField = typeof(EditorWindow).GetField("m_Parent", InstanceReflectionFlags);
                if (parentField == null)
                {
                    failureReason = "Unity EditorWindow.m_Parent is unavailable";
                    return false;
                }

                var parent = parentField.GetValue(window);
                var hostViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.HostView", false);
                if (parent == null || hostViewType == null || !hostViewType.IsInstanceOfType(parent))
                {
                    failureReason = "Selected EditorWindow.m_Parent is not a UnityEditor.HostView";
                    return false;
                }

                var actualView = ReadActualView(hostViewType, parent);
                if (!ReferenceEquals(actualView, window))
                {
                    failureReason = "UnityEditor.HostView.actualView is not the exact selected EditorWindow";
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                failureReason = "The selected EditorWindow HostView binding could not be inspected";
                return false;
            }
        }

        internal static bool TryValidateTarget(
            EditorWindow expectedWindow,
            string expectedWindowsHash,
            out string currentWindowsHash,
            out EditorWindowCaptureTargetFailure failure)
        {
            currentWindowsHash = string.Empty;
            failure = default;
            if (expectedWindow == null)
            {
                failure = EditorWindowCaptureTargetFailure.Unavailable("target_destroyed", "The selected EditorWindow no longer exists");
                return false;
            }

            try
            {
                var windows = Resources.FindObjectsOfTypeAll<EditorWindow>() ?? new EditorWindow[0];
                currentWindowsHash = EditorUtilityCommands.EditorWindowsHash(windows, EditorWindow.focusedWindow, out _);
                if (!string.Equals(expectedWindowsHash, currentWindowsHash, StringComparison.Ordinal))
                {
                    failure = EditorWindowCaptureTargetFailure.Unavailable("windows_hash_changed", "Editor window inventory changed after discovery; call editor.windows.list and select the target again");
                    return false;
                }

                var exactWindowPresent = false;
                for (var index = 0; index < windows.Length; index++)
                {
                    if (ReferenceEquals(windows[index], expectedWindow))
                    {
                        exactWindowPresent = true;
                        break;
                    }
                }

                if (!exactWindowPresent)
                {
                    failure = EditorWindowCaptureTargetFailure.Unavailable("target_identity_changed", "The exact selected EditorWindow is no longer present");
                    return false;
                }

                if (!IsFocusedTarget(expectedWindow))
                {
                    failure = EditorWindowCaptureTargetFailure.Unavailable("target_not_focused", "The selected EditorWindow must already be focused; capture does not change focus");
                    return false;
                }

                if (!TryProveHostViewBinding(expectedWindow, out var hostViewFailure))
                {
                    failure = EditorWindowCaptureTargetFailure.Unavailable("host_view_binding_unproven", hostViewFailure);
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                failure = EditorWindowCaptureTargetFailure.Unavailable("target_identity_unavailable", "The exact selected EditorWindow identity could not be revalidated");
                return false;
            }
        }

        internal static bool TryCapture(EditorWindow window, RenderTexture target, out string failureReason)
        {
            failureReason = string.Empty;
            if (!IsFocusedTarget(window))
            {
                failureReason = "The selected EditorWindow is no longer focused; capture does not change focus";
                return false;
            }

            if (!TryProveHostViewBinding(window, out var hostViewFailure))
            {
                failureReason = hostViewFailure;
                return false;
            }

            if (target == null)
            {
                failureReason = "The bounded EditorWindow capture framebuffer is unavailable";
                return false;
            }

            if (!TryGetCaptureMethod(out var method, out failureReason))
            {
                return false;
            }

            try
            {
                var result = method.Invoke(null, new object[] { window, target });
                if (result is bool && (bool)result)
                {
                    return true;
                }

                failureReason = "UnityEditorInternal.InternalEditorUtility.CaptureEditorWindow returned false";
                return false;
            }
            catch (Exception)
            {
                failureReason = "UnityEditorInternal.InternalEditorUtility.CaptureEditorWindow failed";
                return false;
            }
        }

        static object ReadActualView(Type hostViewType, object hostView)
        {
            var actualViewProperty = hostViewType.GetProperty("actualView", InstanceReflectionFlags);
            if (actualViewProperty != null && actualViewProperty.GetIndexParameters().Length == 0)
            {
                return actualViewProperty.GetValue(hostView, null);
            }

            var actualViewField = hostViewType.GetField("actualView", InstanceReflectionFlags);
            return actualViewField == null ? null : actualViewField.GetValue(hostView);
        }
    }

    internal readonly struct EditorWindowCaptureTargetFailure
    {
        internal EditorWindowCaptureTargetFailure(string reason, string message)
        {
            Reason = reason;
            Message = message;
        }

        internal string Reason { get; }
        internal string Message { get; }

        internal static EditorWindowCaptureTargetFailure Unavailable(string reason, string message)
        {
            return new EditorWindowCaptureTargetFailure(reason, message);
        }
    }
}
