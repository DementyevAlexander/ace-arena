using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class EditorSavePromptCommands
    {
        const string EmptyObjectSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string SavePromptRespondSchemaJson = "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"save\",\"dont_save\",\"cancel\"]},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"action\"],\"additionalProperties\":false}";
        const string ActionSave = "save";
        const string ActionDontSave = "dont_save";
        const string ActionCancel = "cancel";

        [McpCommand(
            "editor.save_prompt.get",
            Title = "Get scene save prompt",
            Description = "Detect whether Unity's unsaved scene changes save prompt is currently open without using the Unity main thread",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 5000,
            SchemaJson = EmptyObjectSchemaJson,
            Tags = new string[] { "editor", "status", "rescue", "save-prompt" },
            ResultShape = "status")]
        public static CommandExecutionResult EditorSavePromptGet(McpCommandContext context)
        {
            return GetSavePrompt();
        }

        [McpCommand(
            "editor.save_prompt.respond",
            Title = "Respond to scene save prompt",
            Description = "Click Save, Don't Save, or Cancel on Unity's unsaved scene changes save prompt without using the Unity main thread",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 5000,
            SchemaJson = SavePromptRespondSchemaJson,
            Tags = new string[] { "editor", "rescue", "save-prompt" },
            ResultShape = "mutation_result")]
        public static CommandExecutionResult EditorSavePromptRespond(McpCommandContext context)
        {
            return RespondToSavePrompt(context.ArgsJson);
        }

        public static bool IsBackgroundCommand(string commandId)
        {
            return commandId == "editor.save_prompt.get" || commandId == "editor.save_prompt.respond";
        }

        public static CommandExecutionResult InvokeBackground(string commandId, string argsJson)
        {
            if (commandId == "editor.save_prompt.get")
            {
                return GetSavePrompt();
            }

            if (commandId == "editor.save_prompt.respond")
            {
                return RespondToSavePrompt(argsJson);
            }

            return CommandExecutionResult.Error("command_not_found", "Background command not found");
        }

        static CommandExecutionResult GetSavePrompt()
        {
#if UNITY_EDITOR_WIN
            return CommandExecutionResult.Ok(WindowsSavePromptDetector.StatusJson());
#else
            return CommandExecutionResult.Ok("{\"is_open\":false,\"supported\":false,\"platform\":\"unsupported\",\"matched_window_count\":0}");
#endif
        }

        static CommandExecutionResult RespondToSavePrompt(string argsJson)
        {
            var action = JsonUtil.ExtractString(argsJson, "action").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument action is required");
            }

            if (action != ActionSave && action != ActionDontSave && action != ActionCancel)
            {
                return CommandExecutionResult.Error("invalid_field_value", "action must be save, dont_save, or cancel");
            }

            var dryRun = JsonUtil.ExtractBool(argsJson, "dry_run", false);
#if UNITY_EDITOR_WIN
            return WindowsSavePromptDetector.Respond(action, dryRun);
#else
            return CommandExecutionResult.Error("unsupported_platform", "editor.save_prompt.respond is only supported on Windows Unity Editor instances", "{\"platform\":\"unsupported\"}");
#endif
        }

#if UNITY_EDITOR_WIN
        static class WindowsSavePromptDetector
        {
            const uint BM_CLICK = 0x00F5;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            const uint ClickTimeoutMs = 1000;
            const int MaxTextChars = 1024;
            const string RussianSaveKey = "\u0441\u043e\u0445\u0440\u0430\u043d\u0438\u0442\u044c";
            const string RussianDontSaveKey = "\u043d\u0435\u0441\u043e\u0445\u0440\u0430\u043d\u044f\u0442\u044c";
            const string RussianCancelKey = "\u043e\u0442\u043c\u0435\u043d\u0430";
            const string RussianSceneKey = "\u0441\u0446\u0435\u043d";

            delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            static List<PromptInfo> FindPrompts()
            {
                var processId = Process.GetCurrentProcess().Id;
                var prompts = new List<PromptInfo>();
                EnumWindows((window, _) =>
                {
                    if (!IsWindowVisible(window))
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(window, out var windowProcessId);
                    if (windowProcessId != processId)
                    {
                        return true;
                    }

                    var prompt = TryReadPrompt(window);
                    if (prompt != null)
                    {
                        prompts.Add(prompt);
                    }

                    return true;
                }, IntPtr.Zero);
                return prompts;
            }

            public static CommandExecutionResult Respond(string action, bool dryRun)
            {
                var prompts = FindPrompts();
                if (prompts.Count == 0)
                {
                    return CommandExecutionResult.Ok("{\"responded\":false,\"is_open\":false,\"supported\":true,\"platform\":\"windows\",\"action\":"
                        + JsonUtil.String(action)
                        + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                        + ",\"reason\":\"no_save_prompt\",\"matched_window_count\":0}");
                }

                var prompt = prompts[0];
                var button = prompt.ButtonForAction(action);
                if (button == null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Requested save prompt action is not available on the detected prompt", "{\"action\":"
                        + JsonUtil.String(action)
                        + ",\"matched_window_count\":" + prompts.Count
                        + ",\"can_save\":" + JsonUtil.Bool(prompt.SaveButton != null)
                        + ",\"can_dont_save\":" + JsonUtil.Bool(prompt.DontSaveButton != null)
                        + ",\"can_cancel\":" + JsonUtil.Bool(prompt.CancelButton != null)
                        + "}");
                }

                if (dryRun)
                {
                    return CommandExecutionResult.Ok(ResponseJson(prompt, prompts.Count, action, false, true, true));
                }

                SetForegroundWindow(prompt.Window);
                var sent = SendMessageTimeout(button.Handle, BM_CLICK, UIntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, ClickTimeoutMs, out _);
                if (sent == IntPtr.Zero)
                {
                    return CommandExecutionResult.Error("unity_exception", "Save prompt button click failed or timed out", "{\"action\":" + JsonUtil.String(action) + "}");
                }

                return CommandExecutionResult.Ok(ResponseJson(prompt, prompts.Count, action, true, false, false));
            }

            public static string StatusJson()
            {
                var prompts = FindPrompts();
                var builder = new StringBuilder();
                builder.Append("{\"is_open\":").Append(JsonUtil.Bool(prompts.Count > 0));
                builder.Append(",\"supported\":true");
                builder.Append(",\"platform\":\"windows\"");
                builder.Append(",\"matched_window_count\":").Append(prompts.Count);
                if (prompts.Count > 0)
                {
                    builder.Append(",\"prompt\":");
                    AppendPromptJson(builder, prompts[0]);
                }

                builder.Append('}');
                return builder.ToString();
            }

            static PromptInfo TryReadPrompt(IntPtr window)
            {
                var title = ReadWindowText(window).Trim();
                var className = ReadClassName(window);
                var childTexts = new List<string>();
                var buttons = new List<ButtonInfo>();

                EnumChildWindows(window, (child, _) =>
                {
                    var text = ReadWindowText(child).Trim();
                    if (!string.IsNullOrEmpty(text))
                    {
                        childTexts.Add(text);
                    }

                    var childClass = ReadClassName(child);
                    var role = ButtonRole(text);
                    if (!string.IsNullOrEmpty(role) && IsButtonClass(childClass))
                    {
                        buttons.Add(new ButtonInfo(child, text, role));
                    }

                    return true;
                }, IntPtr.Zero);

                var saveButton = FirstButton(buttons, ActionSave);
                var dontSaveButton = FirstButton(buttons, ActionDontSave);
                var cancelButton = FirstButton(buttons, ActionCancel);
                if (saveButton == null || dontSaveButton == null)
                {
                    return null;
                }

                var combinedText = title + " " + string.Join(" ", childTexts.ToArray());
                if (!LooksLikeSceneSavePrompt(combinedText))
                {
                    return null;
                }

                return new PromptInfo(window, title, className, childTexts.Count, buttons, saveButton, dontSaveButton, cancelButton);
            }

            static bool LooksLikeSceneSavePrompt(string text)
            {
                var lower = (text ?? string.Empty).ToLowerInvariant();
                var key = NormalizeKey(lower);
                return key.Contains("scene") || lower.Contains(".unity") || key.Contains(RussianSceneKey);
            }

            static ButtonInfo FirstButton(List<ButtonInfo> buttons, string role)
            {
                for (var index = 0; index < buttons.Count; index++)
                {
                    if (buttons[index].Role == role)
                    {
                        return buttons[index];
                    }
                }

                return null;
            }

            static string ButtonRole(string label)
            {
                var key = NormalizeKey(label);
                if (key.Contains("dontsave") || key.Contains("donotsave") || key.Contains("discard") || key.Contains(RussianDontSaveKey))
                {
                    return ActionDontSave;
                }

                if (key == "save" || key == "saveall" || key == RussianSaveKey)
                {
                    return ActionSave;
                }

                if (key == "cancel" || key == RussianCancelKey)
                {
                    return ActionCancel;
                }

                return string.Empty;
            }

            static string NormalizeKey(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                var builder = new StringBuilder(value.Length);
                for (var index = 0; index < value.Length; index++)
                {
                    var character = char.ToLowerInvariant(value[index]);
                    if (char.IsLetterOrDigit(character))
                    {
                        builder.Append(character);
                    }
                }

                return builder.ToString();
            }

            static bool IsButtonClass(string className)
            {
                return string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase);
            }

            static string ResponseJson(PromptInfo prompt, int matchedWindowCount, string action, bool responded, bool dryRun, bool wouldClick)
            {
                var builder = new StringBuilder();
                builder.Append("{\"responded\":").Append(JsonUtil.Bool(responded));
                builder.Append(",\"is_open\":true");
                builder.Append(",\"supported\":true");
                builder.Append(",\"platform\":\"windows\"");
                builder.Append(",\"action\":").Append(JsonUtil.String(action));
                builder.Append(",\"dry_run\":").Append(JsonUtil.Bool(dryRun));
                builder.Append(",\"would_click\":").Append(JsonUtil.Bool(wouldClick));
                builder.Append(",\"matched_window_count\":").Append(matchedWindowCount);
                builder.Append(",\"prompt\":");
                AppendPromptJson(builder, prompt);
                builder.Append('}');
                return builder.ToString();
            }

            static void AppendPromptJson(StringBuilder builder, PromptInfo prompt)
            {
                builder.Append("{\"window_title\":").Append(JsonUtil.String(prompt.Title));
                builder.Append(",\"window_class\":").Append(JsonUtil.String(prompt.ClassName));
                builder.Append(",\"child_text_count\":").Append(prompt.ChildTextCount);
                builder.Append(",\"can_save\":").Append(JsonUtil.Bool(prompt.SaveButton != null));
                builder.Append(",\"can_dont_save\":").Append(JsonUtil.Bool(prompt.DontSaveButton != null));
                builder.Append(",\"can_cancel\":").Append(JsonUtil.Bool(prompt.CancelButton != null));
                builder.Append(",\"button_labels\":[");
                for (var index = 0; index < prompt.Buttons.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonUtil.String(prompt.Buttons[index].Label));
                }

                builder.Append("]}");
            }

            static string ReadWindowText(IntPtr window)
            {
                var builder = new StringBuilder(MaxTextChars + 1);
                var length = GetWindowText(window, builder, builder.Capacity);
                return length <= 0 ? string.Empty : builder.ToString();
            }

            static string ReadClassName(IntPtr window)
            {
                var builder = new StringBuilder(256);
                var length = GetClassName(window, builder, builder.Capacity);
                return length <= 0 ? string.Empty : builder.ToString();
            }

            sealed class PromptInfo
            {
                public readonly IntPtr Window;
                public readonly string Title;
                public readonly string ClassName;
                public readonly int ChildTextCount;
                public readonly List<ButtonInfo> Buttons;
                public readonly ButtonInfo SaveButton;
                public readonly ButtonInfo DontSaveButton;
                public readonly ButtonInfo CancelButton;

                public PromptInfo(IntPtr window, string title, string className, int childTextCount, List<ButtonInfo> buttons, ButtonInfo saveButton, ButtonInfo dontSaveButton, ButtonInfo cancelButton)
                {
                    Window = window;
                    Title = title ?? string.Empty;
                    ClassName = className ?? string.Empty;
                    ChildTextCount = childTextCount;
                    Buttons = buttons ?? new List<ButtonInfo>();
                    SaveButton = saveButton;
                    DontSaveButton = dontSaveButton;
                    CancelButton = cancelButton;
                }

                public ButtonInfo ButtonForAction(string action)
                {
                    if (action == ActionSave)
                    {
                        return SaveButton;
                    }

                    if (action == ActionDontSave)
                    {
                        return DontSaveButton;
                    }

                    return action == ActionCancel ? CancelButton : null;
                }
            }

            sealed class ButtonInfo
            {
                public readonly IntPtr Handle;
                public readonly string Label;
                public readonly string Role;

                public ButtonInfo(IntPtr handle, string label, string role)
                {
                    Handle = handle;
                    Label = label ?? string.Empty;
                    Role = role ?? string.Empty;
                }
            }

            [DllImport("user32.dll")]
            static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll")]
            static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

            [DllImport("user32.dll")]
            static extern bool IsWindowVisible(IntPtr hWnd);

            [DllImport("user32.dll")]
            static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll")]
            static extern bool SetForegroundWindow(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
        }
#endif
    }
}
