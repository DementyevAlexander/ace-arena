using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class CompileProgressWindowDetector
    {
        public static string StatusJson()
        {
#if UNITY_EDITOR_WIN
            return WindowsCompileProgressWindowDetector.StatusJson();
#else
            return "{\"is_open\":false,\"supported\":false,\"platform\":\"unsupported\",\"matched_window_count\":0}";
#endif
        }

#if UNITY_EDITOR_WIN
        static class WindowsCompileProgressWindowDetector
        {
            const int MaxTextChars = 1024;
            const int MaxChildTexts = 20;
            const int MaxScannedWindows = 100;
            const int MaxScannedChildWindows = 100;
            const int MaxMatchedWindows = 10;
            const int ScanBudgetMs = 500;
            const int DomainReloadStallSeconds = 120;
            const uint WindowTextTimeoutMs = 100;
            const uint WmGetText = 0x000D;
            const uint SmtoBlock = 0x0001;
            const uint SmtoAbortIfHung = 0x0002;
            const uint SmtoErrorOnExit = 0x0020;
            const uint WindowTextSendFlags = SmtoBlock | SmtoAbortIfHung | SmtoErrorOnExit;

            delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

            public static string StatusJson()
            {
                try
                {
                    var windows = FindWindows(out var scanTruncated);
                    var builder = new StringBuilder();
                    builder.Append("{\"is_open\":").Append(JsonUtil.Bool(windows.Count > 0));
                    builder.Append(",\"supported\":true");
                    builder.Append(",\"platform\":\"windows\"");
                    builder.Append(",\"matched_window_count\":").Append(windows.Count);
                    builder.Append(",\"scan_soft_budget_ms\":").Append(ScanBudgetMs);
                    builder.Append(",\"window_text_timeout_ms\":").Append(WindowTextTimeoutMs);
                    builder.Append(",\"scan_truncated\":").Append(JsonUtil.Bool(scanTruncated));
                    if (windows.Count > 0)
                    {
                        builder.Append(",\"window\":");
                        AppendWindowJson(builder, windows[0]);
                    }

                    builder.Append('}');
                    return builder.ToString();
                }
                catch (Exception exception)
                {
                    return "{\"is_open\":false,\"supported\":true,\"platform\":\"windows\",\"matched_window_count\":0,\"scan_soft_budget_ms\":"
                        + ScanBudgetMs + ",\"window_text_timeout_ms\":" + WindowTextTimeoutMs + ",\"scan_truncated\":true,\"scan_failed\":true,\"error_type\":" + JsonUtil.String(exception.GetType().Name) + "}";
                }
            }

            static List<WindowInfo> FindWindows(out bool scanTruncated)
            {
                var processId = Process.GetCurrentProcess().Id;
                var windows = new List<WindowInfo>();
                var stopwatch = Stopwatch.StartNew();
                var scannedWindows = 0;
                var truncated = false;
                EnumWindows((window, _) =>
                {
                    if (stopwatch.ElapsedMilliseconds >= ScanBudgetMs || scannedWindows >= MaxScannedWindows || windows.Count >= MaxMatchedWindows)
                    {
                        truncated = true;
                        return false;
                    }

                    scannedWindows++;
                    if (!IsWindowVisible(window))
                    {
                        return true;
                    }

                    GetWindowThreadProcessId(window, out var windowProcessId);
                    if (windowProcessId != processId)
                    {
                        return true;
                    }

                    var info = TryReadWindow(window, stopwatch, out var childScanTruncated);
                    truncated |= childScanTruncated;
                    if (info != null)
                    {
                        windows.Add(info);
                    }

                    return true;
                }, IntPtr.Zero);
                scanTruncated = truncated;
                return windows;
            }

            static WindowInfo TryReadWindow(IntPtr window, Stopwatch stopwatch, out bool scanTruncated)
            {
                scanTruncated = false;
                if (!TryReadWindowText(window, out var title))
                {
                    scanTruncated = true;
                    return null;
                }

                title = title.Trim();
                var className = ReadClassName(window);
                var childTexts = new List<string>();
                var hasCancelButton = false;
                var scannedChildWindows = 0;
                var truncated = false;

                EnumChildWindows(window, (child, _) =>
                {
                    if (stopwatch.ElapsedMilliseconds >= ScanBudgetMs || scannedChildWindows >= MaxScannedChildWindows)
                    {
                        truncated = true;
                        return false;
                    }

                    scannedChildWindows++;
                    if (!TryReadWindowText(child, out var text))
                    {
                        truncated = true;
                        return false;
                    }

                    text = text.Trim();
                    if (!string.IsNullOrEmpty(text) && childTexts.Count < MaxChildTexts)
                    {
                        childTexts.Add(text);
                    }

                    var childClass = ReadClassName(child);
                    if (IsCancelText(text) && IsButtonClass(childClass))
                    {
                        hasCancelButton = true;
                    }

                    return true;
                }, IntPtr.Zero);
                scanTruncated = truncated;

                var combinedText = title + " " + string.Join(" ", childTexts.ToArray());
                if (!LooksLikeCompileProgressWindow(combinedText))
                {
                    return null;
                }

                var progressKind = ProgressKind(combinedText);
                var busySeconds = BusySeconds(combinedText);

                return new WindowInfo(
                    title,
                    className,
                    progressKind,
                    StageText(childTexts),
                    busySeconds,
                    progressKind == "domain_reload" && busySeconds >= DomainReloadStallSeconds,
                    childTexts.Count,
                    hasCancelButton);
            }

            static bool LooksLikeCompileProgressWindow(string text)
            {
                var key = NormalizeKey(text);
                return key.Contains("compilingscripts")
                    || (key.Contains("compiling") && key.Contains("runningbackend"))
                    || key.Contains("reloadingdomain")
                    || key.Contains("domainreload")
                    || (key.Contains("busyfor") && key.Contains("backend") && key.Contains("cancel"));
            }

            static string ProgressKind(string text)
            {
                var key = NormalizeKey(text);
                if (key.Contains("reloadingdomain") || key.Contains("domainreload"))
                {
                    return "domain_reload";
                }

                if (key.Contains("compilingscripts") || key.Contains("runningbackend"))
                {
                    return "compile";
                }

                return "unknown";
            }

            static string StageText(List<string> childTexts)
            {
                for (var index = 0; index < childTexts.Count; index++)
                {
                    var text = childTexts[index];
                    var key = NormalizeKey(text);
                    if (key.Contains("runningbackend"))
                    {
                        return text;
                    }
                }

                for (var index = 0; index < childTexts.Count; index++)
                {
                    var text = childTexts[index];
                    var key = NormalizeKey(text);
                    if (!string.IsNullOrEmpty(key) && key != "cancel" && !key.Contains("compilingscripts") && !key.Contains("reloadingdomain") && !key.Contains("domainreload") && !key.Contains("busyfor"))
                    {
                        return text;
                    }
                }

                return string.Empty;
            }

            static int BusySeconds(string text)
            {
                var lower = (text ?? string.Empty).ToLowerInvariant();
                const string marker = "busy for ";
                var index = lower.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0)
                {
                    return -1;
                }

                index += marker.Length;
                var parts = new List<int>();
                var current = 0;
                var parsedDigit = false;
                while (index < lower.Length && char.IsDigit(lower[index]))
                {
                    parsedDigit = true;
                    current = current * 10 + (lower[index] - '0');
                    index++;
                }

                if (!parsedDigit)
                {
                    return -1;
                }

                parts.Add(current);
                while (index < lower.Length && lower[index] == ':' && parts.Count < 3)
                {
                    index++;
                    current = 0;
                    parsedDigit = false;
                    while (index < lower.Length && char.IsDigit(lower[index]))
                    {
                        parsedDigit = true;
                        current = current * 10 + (lower[index] - '0');
                        index++;
                    }

                    if (!parsedDigit)
                    {
                        break;
                    }

                    parts.Add(current);
                }

                if (parts.Count == 1)
                {
                    return parts[0];
                }

                if (parts.Count == 2)
                {
                    return parts[0] * 60 + parts[1];
                }

                return parts[0] * 3600 + parts[1] * 60 + parts[2];
            }

            static bool IsCancelText(string value)
            {
                return NormalizeKey(value) == "cancel";
            }

            static bool IsButtonClass(string className)
            {
                return string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase);
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

            static void AppendWindowJson(StringBuilder builder, WindowInfo window)
            {
                builder.Append("{\"title\":").Append(JsonUtil.String(window.Title));
                builder.Append(",\"class\":").Append(JsonUtil.String(window.ClassName));
                builder.Append(",\"progress_kind\":").Append(JsonUtil.String(window.ProgressKind));
                builder.Append(",\"is_domain_reload\":").Append(JsonUtil.Bool(window.ProgressKind == "domain_reload"));
                builder.Append(",\"stage\":").Append(JsonUtil.String(window.Stage));
                builder.Append(",\"busy_seconds\":");
                builder.Append(window.BusySeconds >= 0 ? window.BusySeconds.ToString() : "null");
                builder.Append(",\"stall_threshold_seconds\":").Append(DomainReloadStallSeconds);
                builder.Append(",\"is_stalled\":").Append(JsonUtil.Bool(window.IsStalled));
                builder.Append(",\"child_text_count\":").Append(window.ChildTextCount);
                builder.Append(",\"has_cancel_button\":").Append(JsonUtil.Bool(window.HasCancelButton));
                builder.Append(",\"diagnostic_only\":true,\"automation_attempted\":false");
                builder.Append('}');
            }

            static bool TryReadWindowText(IntPtr window, out string text)
            {
                text = string.Empty;
                if (window == IntPtr.Zero)
                {
                    return false;
                }

                var builder = new StringBuilder(MaxTextChars + 1);
                var sent = SendMessageTimeoutW(
                    window,
                    WmGetText,
                    new UIntPtr((uint)builder.Capacity),
                    builder,
                    WindowTextSendFlags,
                    WindowTextTimeoutMs,
                    out var copied);
                if (sent == IntPtr.Zero)
                {
                    return false;
                }

                var copiedChars = copied.ToUInt64();
                if (copiedChars > MaxTextChars)
                {
                    return false;
                }

                var safeLength = Math.Min((int)copiedChars, builder.Length);
                text = safeLength == 0 ? string.Empty : builder.ToString(0, safeLength);
                return true;
            }

            static string ReadClassName(IntPtr window)
            {
                var builder = new StringBuilder(256);
                var length = GetClassNameW(window, builder, builder.Capacity);
                return length <= 0 ? string.Empty : builder.ToString();
            }

            sealed class WindowInfo
            {
                public readonly string Title;
                public readonly string ClassName;
                public readonly string ProgressKind;
                public readonly string Stage;
                public readonly int BusySeconds;
                public readonly bool IsStalled;
                public readonly int ChildTextCount;
                public readonly bool HasCancelButton;

                public WindowInfo(string title, string className, string progressKind, string stage, int busySeconds, bool isStalled, int childTextCount, bool hasCancelButton)
                {
                    Title = title ?? string.Empty;
                    ClassName = className ?? string.Empty;
                    ProgressKind = progressKind ?? "unknown";
                    Stage = stage ?? string.Empty;
                    BusySeconds = busySeconds;
                    IsStalled = isStalled;
                    ChildTextCount = childTextCount;
                    HasCancelButton = hasCancelButton;
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

            [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, UIntPtr wParam, [Out] StringBuilder lParam, uint flags, uint timeoutMilliseconds, out UIntPtr result);

            [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            static extern int GetClassNameW(IntPtr hWnd, [Out] StringBuilder lpClassName, int nMaxCount);
        }
#endif
    }
}
