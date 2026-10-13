using System;

namespace SGG.UnityMCP.Editor.UI.State
{
    internal static class McpWindowTimingViewModel
    {
        public const string EmptySummary = "No MCP Window timing captured yet.";
        public const int MaxDetailChars = 900;
        public const int MaxSourceChars = 120;

        public static string LatestSummary(string summary)
        {
            return string.IsNullOrEmpty(summary) ? EmptySummary : summary;
        }

        public static string BuildSummary(string source, long totalMs, string details)
        {
            var safeSource = Bound(string.IsNullOrEmpty(source) ? "Unknown" : source, MaxSourceChars);
            var safeDetails = string.IsNullOrEmpty(details) ? "no measured substeps" : Bound(details, MaxDetailChars);
            return safeSource + " total=" + Math.Max(0L, totalMs) + "ms; " + safeDetails;
        }

        static string Bound(string value, int maxChars)
        {
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
        }
    }
}
