using System;

namespace Example.SggMcpDemo.Editor
{
    public static class ExampleCommands
    {
        public static string Status()
        {
            return "{\"ok\":true,\"package\":\"com.example.sgg-mcp-demo\",\"mode\":\"manifest-only\"}";
        }

        public static string GenerateReport(string argsJson)
        {
            var maxItems = ExtractInt(argsJson, "max_items", 10, 1, 100);
            var dryRun = ExtractBool(argsJson, "dry_run", true);
            return "{\"ok\":true,\"dry_run\":" + Bool(dryRun)
                + ",\"max_items\":" + maxItems
                + ",\"inline_summary\":\"Demo report command keeps output bounded. Real packages should store large reports under Library/SGGUnityMcp/job-artifacts/.\"}";
        }

        static int ExtractInt(string json, string field, int fallback, int min, int max)
        {
            if (string.IsNullOrEmpty(json))
            {
                return fallback;
            }

            var marker = "\"" + field + "\":";
            var index = json.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return fallback;
            }

            index += marker.Length;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }

            var end = index;
            while (end < json.Length && char.IsDigit(json[end]))
            {
                end++;
            }

            if (!int.TryParse(json.Substring(index, end - index), out var value))
            {
                return fallback;
            }

            return Math.Min(max, Math.Max(min, value));
        }

        static bool ExtractBool(string json, string field, bool fallback)
        {
            if (string.IsNullOrEmpty(json))
            {
                return fallback;
            }

            if (json.IndexOf("\"" + field + "\":true", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            if (json.IndexOf("\"" + field + "\":false", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return fallback;
        }

        static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
