using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class GameViewCaptureEditorContext
    {
        internal const string TemporaryResolutionName = "SGG Unity MCP Capture";

        internal static bool SupportsCustomRenderingResolution
        {
            get
            {
#if UNITY_6000_0_OR_NEWER
                return true;
#else
                return false;
#endif
            }
        }

        internal static bool TrySetCustomRenderingResolution(int width, int height, out string failureReason)
        {
            failureReason = string.Empty;
            if (width < 1 || height < 1 || width > 4096 || height > 4096)
            {
                failureReason = "custom_resolution_out_of_bounds";
                return false;
            }
#if UNITY_6000_0_OR_NEWER
            try
            {
                PlayModeWindow.SetCustomRenderingResolution((uint)width, (uint)height, TemporaryResolutionName);
                EditorApplication.QueuePlayerLoopUpdate();
                return true;
            }
            catch (Exception)
            {
                failureReason = "custom_resolution_api_failed";
                return false;
            }
#else
            failureReason = "custom_resolution_api_unavailable";
            return false;
#endif
        }

        internal static bool TryGetRenderingResolution(out int width, out int height)
        {
#if UNITY_6000_0_OR_NEWER
            try
            {
                PlayModeWindow.GetRenderingResolution(out var renderingWidth, out var renderingHeight);
                if (renderingWidth == 0 || renderingHeight == 0 || renderingWidth > int.MaxValue || renderingHeight > int.MaxValue)
                {
                    width = 0;
                    height = 0;
                    return false;
                }
                width = (int)renderingWidth;
                height = (int)renderingHeight;
                return true;
            }
            catch (Exception)
            {
                width = 0;
                height = 0;
                return false;
            }
#else
            var inspected = TryInspectLegacy(out var evidence);
            width = evidence.Width;
            height = evidence.Height;
            return inspected;
#endif
        }

        internal static bool TryInspect(out GameViewEditorEvidence evidence)
        {
#if UNITY_6000_0_OR_NEWER
            try
            {
                var viewType = PlayModeWindow.GetViewType();
                PlayModeWindow.GetRenderingResolution(out var width, out var height);
                if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
                {
                    evidence = GameViewEditorEvidence.Unsupported("rendering_resolution_unavailable");
                    return false;
                }
                evidence = new GameViewEditorEvidence(
                    true,
                    viewType == PlayModeWindow.PlayModeViewTypes.GameView,
                    viewType.ToString(),
                    (int)width,
                    (int)height,
                    string.Empty);
                return evidence.IsGameView;
            }
            catch (Exception)
            {
                evidence = GameViewEditorEvidence.Unsupported("play_mode_window_api_failed");
                return false;
            }
#else
            return TryInspectLegacy(out evidence);
#endif
        }

#if !UNITY_6000_0_OR_NEWER
        static bool TryInspectLegacy(out GameViewEditorEvidence evidence)
        {
            try
            {
                var editorAssembly = typeof(EditorWindow).Assembly;
                var playModeViewType = editorAssembly.GetType("UnityEditor.PlayModeView", false);
                var gameViewType = editorAssembly.GetType("UnityEditor.GameView", false);
                var getMainView = playModeViewType == null ? null : playModeViewType.GetMethod("GetMainPlayModeView", BindingFlags.Static | BindingFlags.NonPublic);
                var targetSizeProperty = playModeViewType == null ? null : playModeViewType.GetProperty("targetSize", BindingFlags.Instance | BindingFlags.NonPublic);
                if (playModeViewType == null || gameViewType == null || getMainView == null || targetSizeProperty == null)
                {
                    evidence = GameViewEditorEvidence.Unsupported("legacy_play_mode_view_api_unavailable");
                    return false;
                }

                var mainView = getMainView.Invoke(null, null) as EditorWindow;
                if (mainView == null)
                {
                    evidence = new GameViewEditorEvidence(true, false, "none", 0, 0, "main_play_mode_view_unavailable");
                    return false;
                }

                var isGameView = mainView.GetType() == gameViewType;
                var targetSize = targetSizeProperty.GetValue(mainView, null);
                if (!(targetSize is Vector2 size) || size.x < 1f || size.y < 1f || size.x > int.MaxValue || size.y > int.MaxValue)
                {
                    evidence = new GameViewEditorEvidence(true, isGameView, mainView.GetType().FullName, 0, 0, "legacy_target_size_unavailable");
                    return false;
                }

                evidence = new GameViewEditorEvidence(
                    true,
                    isGameView,
                    mainView.GetType().FullName,
                    (int)Math.Round(size.x, MidpointRounding.AwayFromZero),
                    (int)Math.Round(size.y, MidpointRounding.AwayFromZero),
                    isGameView ? string.Empty : "main_play_mode_view_is_not_game_view");
                return isGameView;
            }
            catch (Exception)
            {
                evidence = GameViewEditorEvidence.Unsupported("legacy_play_mode_view_api_failed");
                return false;
            }
        }
#endif
    }

    internal readonly struct GameViewEditorEvidence
    {
        internal GameViewEditorEvidence(bool apiSupported, bool isGameView, string viewType, int width, int height, string failureReason)
        {
            ApiSupported = apiSupported;
            IsGameView = isGameView;
            ViewType = viewType ?? string.Empty;
            Width = width;
            Height = height;
            FailureReason = failureReason ?? string.Empty;
        }

        internal bool ApiSupported { get; }
        internal bool IsGameView { get; }
        internal string ViewType { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal string FailureReason { get; }

        internal static GameViewEditorEvidence Unsupported(string reason)
        {
            return new GameViewEditorEvidence(false, false, "unsupported", 0, 0, reason);
        }
    }
}
