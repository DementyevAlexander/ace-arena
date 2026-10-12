using System;
using System.Globalization;
using System.Text;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class EditorWindowSelector
    {
        public const int MaxWindows = 100;

        public static CommandExecutionResult Select(string argsJson, out EditorWindow selectedWindow, out int selectedIndex, out int ambiguityCount)
        {
            return Select(argsJson, out selectedWindow, out selectedIndex, out ambiguityCount, out _);
        }

        public static CommandExecutionResult Select(string argsJson, out EditorWindow selectedWindow, out int selectedIndex, out int ambiguityCount, out string windowsHash)
        {
            selectedWindow = null;
            selectedIndex = -1;
            ambiguityCount = 0;
            windowsHash = string.Empty;
            var windows = UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>() ?? new EditorWindow[0];
            var title = JsonUtil.ExtractString(argsJson, "title");
            var typeName = JsonUtil.ExtractString(argsJson, "type_name");
            var requestedIndex = JsonUtil.ExtractInt(argsJson, "window_index", -1);
            var hasFocusedSelector = JsonUtil.HasProperty(argsJson, "focused");
            var focused = JsonUtil.ExtractBool(argsJson, "focused", false);
            if (requestedIndex >= MaxWindows)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "window_index must be between 0 and 99", "window_index", "out_of_range");
            }

            if (hasFocusedSelector && !focused)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "focused selector only supports true", "focused", "enum_value_not_allowed");
            }

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(typeName) && !hasFocusedSelector && requestedIndex < 0)
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "Provide title, type_name, focused:true, or window_index from editor.windows.list", "title", "relational_constraint_failed");
            }

            var focusedWindow = EditorWindow.focusedWindow;
            windowsHash = EditorUtilityCommands.EditorWindowsHash(windows, focusedWindow, out _);
            for (var index = 0; index < windows.Length && index < MaxWindows; index++)
            {
                var window = windows[index];
                if (window == null)
                {
                    continue;
                }

                if (requestedIndex >= 0 && requestedIndex != index)
                {
                    continue;
                }

                var windowTitle = window.titleContent != null ? window.titleContent.text : string.Empty;
                var windowType = window.GetType().FullName ?? window.GetType().Name;
                if (!string.IsNullOrEmpty(title) && !string.Equals(windowTitle, title, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(typeName) && !string.Equals(windowType, typeName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (hasFocusedSelector && focusedWindow != window)
                {
                    continue;
                }

                ambiguityCount++;
                selectedWindow = window;
                selectedIndex = index;
            }

            if (ambiguityCount == 0)
            {
                return CommandExecutionResult.Error("not_found", "No matching Unity Editor window is available");
            }

            if (ambiguityCount > 1)
            {
                selectedWindow = null;
                selectedIndex = -1;
                return CommandExecutionResult.Error("ambiguous_selector", "Selector matched multiple Unity Editor windows; add type_name, title, focused:true, or window_index from editor.windows.list");
            }

            return null;
        }

        public static string MetadataJson(EditorWindow window, int selectedIndex)
        {
            if (window == null)
            {
                return "null";
            }

            var title = window.titleContent != null ? window.titleContent.text : string.Empty;
            var typeName = window.GetType().FullName ?? window.GetType().Name;
            var builder = new StringBuilder();
            builder.Append("{\"title\":").Append(JsonUtil.String(title));
            builder.Append(",\"type_name\":").Append(JsonUtil.String(typeName));
            builder.Append(",\"window_index\":").Append(selectedIndex);
            builder.Append(",\"is_focused\":").Append(JsonUtil.Bool(EditorWindow.focusedWindow == window));
#if UNITY_6000_4_OR_NEWER
            var entityId = UnityEngine.EntityId.ToULong(window.GetEntityId());
            var legacyInstanceId = entityId <= int.MaxValue ? (int)entityId : 0;
            builder.Append(",\"instance_id\":").Append(legacyInstanceId);
            builder.Append(",\"entity_id\":").Append(JsonUtil.String(entityId.ToString(CultureInfo.InvariantCulture)));
#else
            builder.Append(",\"instance_id\":").Append(window.GetInstanceID());
#endif
            builder.Append(",\"position\":").Append(RectJson(window.position));
            builder.Append('}');
            return builder.ToString();
        }

        public static string RuntimeIdentity(EditorWindow window)
        {
            if (window == null) return string.Empty;
#if UNITY_6000_4_OR_NEWER
            return "entity:" + UnityEngine.EntityId.ToULong(window.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return "instance:" + window.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        public static string RectJson(UnityEngine.Rect value)
        {
            return "{\"x\":" + FloatJson(value.x)
                + ",\"y\":" + FloatJson(value.y)
                + ",\"width\":" + FloatJson(value.width)
                + ",\"height\":" + FloatJson(value.height) + "}";
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? JsonUtil.String(value.ToString(CultureInfo.InvariantCulture))
                : value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
