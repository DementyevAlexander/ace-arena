using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal static class BridgeReloadTrace
    {
        const long MaxTraceBytes = 262144;
        const int TrimToBytes = 131072;
        const int MaxReadLines = 500;
        const int MaxReadChars = 262144;
        const int MaxStackChars = 4000;
        static readonly object FileLock = new object();
        static readonly string ProcessStartUtc = ReadProcessStartUtc();

        public static string RelativeTracePath => "Library/SGGUnityMcp/bridge-reload-trace.log";

        public static string TracePath => Path.Combine(ProjectPaths.ProjectRoot, "Library", "SGGUnityMcp", "bridge-reload-trace.log");

        public static void Log(string eventName)
        {
            Write(eventName, null, -1, null, null, null, false);
        }

        public static void Log(string eventName, long durationMs)
        {
            Write(eventName, null, durationMs, null, null, null, false);
        }

        public static void Log(string eventName, string detail)
        {
            Write(eventName, detail, -1, null, null, null, false);
        }

        public static void Log(string eventName, string detail, long durationMs)
        {
            Write(eventName, detail, durationMs, null, null, null, false);
        }

        public static void LogFingerprints(string eventName, string oldFingerprint, string newFingerprint)
        {
            Write(eventName, null, -1, null, oldFingerprint, newFingerprint, false);
        }

        public static void LogWithStack(string eventName, string detail)
        {
            Write(eventName, detail, -1, null, null, null, true);
        }

        public static void LogError(string eventName, Exception exception)
        {
            Write(eventName, null, -1, exception, null, null, true);
        }

        public static string ReadRecentJson(int maxLines, int maxChars)
        {
            maxLines = Clamp(maxLines, 1, MaxReadLines);
            maxChars = Clamp(maxChars, 1024, MaxReadChars);
            var path = SafeTracePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "{\"trace_path\":" + JsonString(RelativeTracePath)
                    + ",\"exists\":false,\"lines\":[],\"truncated\":false}";
            }

            bool truncated;
            long byteLength;
            var lines = ReadTailLines(path, maxLines, maxChars, out truncated, out byteLength);
            var builder = new StringBuilder();
            builder.Append("{\"trace_path\":").Append(JsonString(RelativeTracePath));
            builder.Append(",\"exists\":true");
            builder.Append(",\"byte_length\":").Append(Math.Max(0, byteLength));
            builder.Append(",\"max_lines\":").Append(maxLines);
            builder.Append(",\"max_chars\":").Append(maxChars);
            builder.Append(",\"truncated\":").Append(truncated ? "true" : "false");
            builder.Append(",\"lines\":[");
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonString(lines[index]));
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static void Write(string eventName, string detail, long durationMs, Exception exception, string oldFingerprint, string newFingerprint, bool includeStack)
        {
            try
            {
                var path = SafeTracePath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                var line = BuildLine(eventName, detail, durationMs, exception, oldFingerprint, newFingerprint, includeStack);
                lock (FileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    TrimIfNeeded(path);
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Reload tracing must never affect Unity startup or command execution.
            }
        }

        static string BuildLine(string eventName, string detail, long durationMs, Exception exception, string oldFingerprint, string newFingerprint, bool includeStack)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            AppendField(builder, "utc", DateTime.UtcNow.ToString("o"));
            AppendField(builder, "event", string.IsNullOrEmpty(eventName) ? "unknown" : eventName);
            AppendField(builder, "unity_version", SafeString(() => Application.unityVersion));
            AppendField(builder, "project_id", SafeString(() => ProjectPaths.ProjectId));
            AppendField(builder, "package_version", UnityBridgeRuntime.PackageVersion);
            AppendNumber(builder, "time_since_startup", SafeDouble(() => EditorApplication.timeSinceStartup));
            AppendBool(builder, "is_compiling", SafeBool(() => EditorApplication.isCompiling));
            AppendBool(builder, "is_updating", SafeBool(() => EditorApplication.isUpdating));
            AppendBool(builder, "is_playing", SafeBool(() => EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying));
            AppendBool(builder, "is_batchmode", SafeBool(() => Application.isBatchMode));
            AppendNumber(builder, "process_id", SafeInt(() => Process.GetCurrentProcess().Id));
            AppendField(builder, "process_start_utc", ProcessStartUtc);
            if (durationMs >= 0)
            {
                AppendNumber(builder, "duration_ms", durationMs);
            }

            if (!string.IsNullOrEmpty(detail))
            {
                AppendField(builder, "detail", detail);
            }

            if (!string.IsNullOrEmpty(oldFingerprint))
            {
                AppendField(builder, "fingerprint_old_prefix", Prefix(oldFingerprint));
            }

            if (!string.IsNullOrEmpty(newFingerprint))
            {
                AppendField(builder, "fingerprint_new_prefix", Prefix(newFingerprint));
            }

            if (exception != null)
            {
                AppendField(builder, "error_type", exception.GetType().Name);
                AppendField(builder, "error_message", exception.Message);
            }

            if (includeStack)
            {
                AppendField(builder, "stack", Bounded(exception == null ? Environment.StackTrace : exception.ToString(), MaxStackChars));
            }

            builder.Append('}');
            return builder.ToString();
        }

        static string[] ReadTailLines(string path, int maxLines, int maxChars, out bool truncated, out long byteLength)
        {
            truncated = false;
            byteLength = 0;
            try
            {
                var info = new FileInfo(path);
                byteLength = info.Exists ? info.Length : 0;
                if (!info.Exists || info.Length == 0)
                {
                    return new string[0];
                }

                var bytesToRead = (int)Math.Min(info.Length, maxChars);
                var buffer = new byte[bytesToRead];
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Seek(-bytesToRead, SeekOrigin.End);
                    var read = stream.Read(buffer, 0, bytesToRead);
                    if (read < bytesToRead)
                    {
                        Array.Resize(ref buffer, read);
                    }
                }

                var text = Encoding.UTF8.GetString(buffer);
                if (info.Length > bytesToRead)
                {
                    truncated = true;
                    var firstNewline = text.IndexOf('\n');
                    text = firstNewline >= 0 && firstNewline + 1 < text.Length ? text.Substring(firstNewline + 1) : string.Empty;
                }

                var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length <= maxLines)
                {
                    return lines;
                }

                truncated = true;
                var result = new string[maxLines];
                Array.Copy(lines, lines.Length - maxLines, result, 0, maxLines);
                return result;
            }
            catch
            {
                truncated = true;
                return new string[0];
            }
        }

        static void TrimIfNeeded(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxTraceBytes)
            {
                return;
            }

            var bytesToRead = (int)Math.Min(info.Length, TrimToBytes);
            var buffer = new byte[bytesToRead];
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(-bytesToRead, SeekOrigin.End);
                var read = stream.Read(buffer, 0, bytesToRead);
                if (read < bytesToRead)
                {
                    Array.Resize(ref buffer, read);
                }
            }

            var text = Encoding.UTF8.GetString(buffer);
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0 && firstNewline + 1 < text.Length)
            {
                text = text.Substring(firstNewline + 1);
            }

            File.WriteAllText(path, text, Encoding.UTF8);
        }

        static string SafeTracePath()
        {
            try
            {
                return TracePath;
            }
            catch
            {
                return string.Empty;
            }
        }

        static void AppendField(StringBuilder builder, string name, string value)
        {
            builder.Append(builder.Length == 1 ? string.Empty : ",");
            builder.Append(JsonString(name)).Append(':').Append(JsonString(value ?? string.Empty));
        }

        static void AppendBool(StringBuilder builder, string name, bool value)
        {
            builder.Append(',').Append(JsonString(name)).Append(':').Append(value ? "true" : "false");
        }

        static void AppendNumber(StringBuilder builder, string name, long value)
        {
            builder.Append(',').Append(JsonString(name)).Append(':').Append(value);
        }

        static void AppendNumber(StringBuilder builder, string name, double value)
        {
            builder.Append(',').Append(JsonString(name)).Append(':').Append(value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        static string JsonString(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        static string SafeString(Func<string> getter)
        {
            try { return getter() ?? string.Empty; }
            catch { return string.Empty; }
        }

        static bool SafeBool(Func<bool> getter)
        {
            try { return getter(); }
            catch { return false; }
        }

        static int SafeInt(Func<int> getter)
        {
            try { return getter(); }
            catch { return 0; }
        }

        static double SafeDouble(Func<double> getter)
        {
            try { return getter(); }
            catch { return 0; }
        }

        static string ReadProcessStartUtc()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    return process.StartTime.ToUniversalTime().ToString("O");
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        static string Prefix(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= 12 ? value : value.Substring(0, 12);
        }

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
