using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal static class McpFtueSmokeTracker
    {
        const string KeyPrefix = "SGG.UnityMCP.FTUE.CompileStatus.";

        internal static void RecordSuccessfulCompileStatus()
        {
            if (!MainThreadDispatcher.IsMainThread)
            {
                MainThreadDispatcher.Enqueue(RecordSuccessfulCompileStatus);
                return;
            }

            EditorPrefs.SetString(Key(ProjectPaths.ProjectRoot), DateTime.UtcNow.ToString("O"));
        }

        internal static bool HasSuccessfulCompileStatus(string projectRoot)
        {
            return !string.IsNullOrWhiteSpace(projectRoot) && EditorPrefs.HasKey(Key(projectRoot));
        }

        static string Key(string project)
        {
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(NormalizeProjectRoot(project)));
                var builder = new StringBuilder(hash.Length * 2);
                for (var index = 0; index < hash.Length; index++)
                {
                    builder.Append(hash[index].ToString("x2"));
                }

                return KeyPrefix + builder;
            }
        }

        internal static string NormalizeProjectRoot(string projectRoot)
        {
            try
            {
                var normalized = Path.GetFullPath(projectRoot ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Environment.OSVersion.Platform == PlatformID.Win32NT ? normalized.ToUpperInvariant() : normalized;
            }
            catch
            {
                return projectRoot ?? string.Empty;
            }
        }
    }

    internal static class McpFtueProfileTracker
    {
        const string KeyPrefix = "SGG.UnityMCP.FTUE.PolicyProfile.";
        internal static void RecordExplicitProfile(string projectRoot, string profile, string rulesFingerprint)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(rulesFingerprint))
            {
                return;
            }

            EditorPrefs.SetString(KeyPrefix + KeyHash(projectRoot), (profile ?? string.Empty) + ":" + rulesFingerprint);
        }

        internal static bool HasCurrentExplicitProfile(string projectRoot, string rulesFingerprint)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(rulesFingerprint))
            {
                return false;
            }

            var evidence = EditorPrefs.GetString(KeyPrefix + KeyHash(projectRoot), string.Empty);
            return evidence.EndsWith(":" + rulesFingerprint, StringComparison.Ordinal);
        }

        internal static string RulesFingerprint(List<string> allow, List<string> ask, List<string> deny)
        {
            var builder = new StringBuilder();
            AppendRules(builder, "allow", allow);
            AppendRules(builder, "ask", ask);
            AppendRules(builder, "deny", deny);
            using (var sha256 = SHA256.Create())
            {
                return Hex(sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
            }
        }

        static void AppendRules(StringBuilder builder, string action, List<string> values)
        {
            var sorted = values == null ? new List<string>() : new List<string>(values);
            sorted.Sort(StringComparer.Ordinal);
            builder.Append(action.Length).Append(':').Append(action).Append(':').Append(sorted.Count).Append(';');
            for (var index = 0; index < sorted.Count; index++)
            {
                var value = sorted[index] ?? string.Empty;
                builder.Append(value.Length).Append(':').Append(value).Append(';');
            }
        }

        static string KeyHash(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                return Hex(sha256.ComputeHash(Encoding.UTF8.GetBytes(McpFtueSmokeTracker.NormalizeProjectRoot(value))));
            }
        }

        static string Hex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            for (var index = 0; index < hash.Length; index++) builder.Append(hash[index].ToString("x2"));
            return builder.ToString();
        }
    }
}
