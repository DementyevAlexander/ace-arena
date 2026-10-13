using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal sealed class BridgeInvokeRequest
    {
        public string RequestId;
        public string Project;
        public string Command;
        public string ArgsJson;
        public string SettingsJson;
        public bool HasCatalogBinding;
        public string ExpectedInstanceId;
        public long ExpectedCatalogGeneration;
        public string ExpectedCommandsHash;
    }

    internal static class BridgeProtocol
    {
        internal const int MaxInvokeBytes = 4 * 1024 * 1024;
        const int MaxIdentifierChars = 256;
        const int MaxPathChars = 4096;
        const int MaxDescriptionChars = 16384;
        const int MaxFeatureFlags = 256;
        const int MaxAllowlistEntries = 1000;
        const int MaxDepth = 32;
        const int MaxNodes = 50000;
        const int MaxObjectKeys = 2048;
        const int MaxArrayItems = 4096;
        const int MaxKeyChars = 256;
        const int MaxStringChars = 262144;
        const int MaxTotalStringChars = 1048576;

        internal static bool TryParseInvoke(string json, out BridgeInvokeRequest request, out string errorCode)
        {
            request = null;
            errorCode = "protocol_validation_error";
            JsonValue root;
            try
            {
                root = new Parser(json).Parse();
            }
            catch
            {
                return false;
            }

            if (root.Kind != JsonKind.Object)
            {
                return false;
            }

            if (!root.Object.TryGetValue("schema_version", out var version))
            {
                errorCode = "protocol_version_mismatch";
                return false;
            }
            if (version.Kind != JsonKind.Number || version.Number != 1)
            {
                errorCode = "protocol_version_mismatch";
                return false;
            }

            var parsed = new BridgeInvokeRequest();
            if (root.Object.TryGetValue("request_id", out var requestId))
            {
                if (requestId.Kind != JsonKind.String || !IsSafeRequestId(requestId.String)) return false;
                parsed.RequestId = requestId.String;
            }
            if (!RequiredBoundedString(root, "project", MaxIdentifierChars, out parsed.Project)
                || !RequiredBoundedString(root, "command", MaxIdentifierChars, out parsed.Command)
                || !root.Object.TryGetValue("args", out var args) || args.Kind != JsonKind.Object)
            {
                return false;
            }

            parsed.ArgsJson = args.Raw(json);
            parsed.SettingsJson = "{}";
            if (root.Object.TryGetValue("settings", out var settings))
            {
                if (!ValidateSettings(settings)) return false;
                parsed.SettingsJson = settings.Raw(json);
            }
            if (root.Object.TryGetValue("deadline_unix_ms", out var deadline)
                && (deadline.Kind != JsonKind.Number || deadline.Number <= 0 || deadline.Number > 9007199254740991d || Math.Truncate(deadline.Number) != deadline.Number))
            {
                return false;
            }
            if (root.Object.TryGetValue("risk", out var risk)
                && (risk.Kind != JsonKind.String || !IsRisk(risk.String)))
            {
                return false;
            }

            var hasExpectedInstance = root.Object.ContainsKey("expected_instance_id");
            var hasExpectedGeneration = root.Object.ContainsKey("expected_catalog_generation");
            var hasExpectedHash = root.Object.ContainsKey("expected_commands_hash");
            if (hasExpectedInstance || hasExpectedGeneration || hasExpectedHash)
            {
                if (!hasExpectedInstance || !hasExpectedGeneration || !hasExpectedHash
                    || !RequiredBoundedString(root, "expected_instance_id", MaxIdentifierChars, out parsed.ExpectedInstanceId)
                    || !RequiredBoundedString(root, "expected_commands_hash", MaxIdentifierChars, out parsed.ExpectedCommandsHash)
                    || !root.Object.TryGetValue("expected_catalog_generation", out var generation)
                    || generation.Kind != JsonKind.Number || generation.Number < 1 || generation.Number > 9007199254740991d || Math.Truncate(generation.Number) != generation.Number)
                {
                    return false;
                }

                parsed.ExpectedCatalogGeneration = (long)generation.Number;
                parsed.HasCatalogBinding = true;
            }

            request = parsed;
            return true;
        }

        // Reuse the bounded protocol parser for persisted evidence without relaxing
        // its complete-document, typed-value or duplicate-key requirements.
        internal static bool IsValidUniqueObjectJson(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > MaxInvokeBytes) return false;
            try { return new Parser(json).Parse().Kind == JsonKind.Object; }
            catch { return false; }
        }

        internal static bool TryHasObjectMarker(string json, string marker, string[] stringFields, string presenceField, out bool hasMarker)
        {
            hasMarker = false;
            if (string.IsNullOrEmpty(json) || json.Length > MaxInvokeBytes || stringFields == null) return false;
            try
            {
                var root = new Parser(json).Parse();
                if (root.Kind != JsonKind.Object) return false;
                hasMarker = root.Object.ContainsKey(presenceField);
                foreach (var field in stringFields)
                {
                    if (root.Object.TryGetValue(field, out var value) && value.Kind == JsonKind.String && value.String == marker)
                        hasMarker = true;
                }
                return true;
            }
            catch { return false; }
        }

        internal static string SafeRequestId(string json)
        {
            try
            {
                var root = new Parser(json).Parse();
                return root.Kind == JsonKind.Object && root.Object.TryGetValue("request_id", out var value)
                    && value.Kind == JsonKind.String && IsSafeRequestId(value.String) ? value.String : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static bool TryReadStringArray(string json, string property, int maxItems, out string[] values, out string failureReason)
        {
            values = Array.Empty<string>();
            failureReason = string.Empty;
            JsonValue root;
            try
            {
                root = new Parser(json).Parse();
            }
            catch
            {
                failureReason = "invalid_json";
                return false;
            }

            if (root.Kind != JsonKind.Object || !root.Object.TryGetValue(property, out var field) || field.Kind != JsonKind.Array)
            {
                failureReason = "array_required";
                return false;
            }
            if (field.Array.Count > maxItems)
            {
                failureReason = "max_items_exceeded";
                return false;
            }

            var result = new string[field.Array.Count];
            for (var index = 0; index < field.Array.Count; index++)
            {
                if (field.Array[index].Kind != JsonKind.String)
                {
                    failureReason = "string_items_required";
                    return false;
                }
                result[index] = field.Array[index].String;
            }

            values = result;
            return true;
        }

        internal static bool TryValidateProject(BridgeInvokeRequest request, string expectedProject, long durationMs, out string errorJson)
        {
            if (request != null && string.Equals(request.Project, expectedProject, StringComparison.Ordinal))
            {
                errorJson = string.Empty;
                return true;
            }

            errorJson = ErrorResponse("protocol_validation_error", "Invoke request project does not match this Unity bridge", request == null ? string.Empty : request.RequestId, durationMs);
            return false;
        }

        internal static string ErrorResponse(string code, string message, string requestId, long durationMs, string detailsJson = "")
        {
            return "{\"schema_version\":1"
                + (IsSafeRequestId(requestId) ? ",\"request_id\":" + JsonUtil.String(requestId) : string.Empty)
                + ",\"status\":\"error\",\"error\":{\"code\":" + JsonUtil.String(code)
                + ",\"message\":" + JsonUtil.String(message)
                + (string.IsNullOrEmpty(detailsJson) ? string.Empty : ",\"details\":" + detailsJson)
                + "},\"diagnostics\":[],\"duration_ms\":"
                + Math.Min(3600000, Math.Max(0, durationMs)) + "}";
        }

        internal static bool IsSafeRequestId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
            foreach (var character in value)
            {
                if (!(character >= 'A' && character <= 'Z') && !(character >= 'a' && character <= 'z')
                    && !(character >= '0' && character <= '9') && character != '.' && character != '_' && character != '-') return false;
            }
            return true;
        }

        static bool RequiredBoundedString(JsonValue root, string name, int max, out string value)
        {
            value = string.Empty;
            if (!root.Object.TryGetValue(name, out var field) || field.Kind != JsonKind.String
                || string.IsNullOrEmpty(field.String) || field.String.Length > max) return false;
            value = field.String;
            return true;
        }

        static bool ValidateSettings(JsonValue settings)
        {
            if (settings.Kind != JsonKind.Object) return false;
            if (settings.Object.TryGetValue("asset_index_cache_dir", out var path)
                && (path.Kind != JsonKind.String || string.IsNullOrEmpty(path.String) || path.String.Length > MaxPathChars)) return false;
            if (settings.Object.TryGetValue("feature_flags", out var flags))
            {
                if (flags.Kind != JsonKind.Object || flags.Object.Count > MaxFeatureFlags) return false;
                foreach (var pair in flags.Object)
                {
                    if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > MaxIdentifierChars || !IsSafeKey(pair.Key) || pair.Value.Kind != JsonKind.Boolean) return false;
                }
            }
            if (settings.Object.TryGetValue("mono_behaviour_allowlist", out var allowlist))
            {
                if (allowlist.Kind != JsonKind.Array || allowlist.Array.Count > MaxAllowlistEntries) return false;
                foreach (var entry in allowlist.Array)
                {
                    if (entry.Kind != JsonKind.Object
                        || !RequiredBoundedString(entry, "assembly_name", MaxIdentifierChars, out _)
                        || !RequiredBoundedString(entry, "full_type_name", MaxIdentifierChars, out _)) return false;
                    if (entry.Object.TryGetValue("note", out var note) && (note.Kind != JsonKind.String || note.String.Length > MaxDescriptionChars)) return false;
                }
            }
            return true;
        }

        static bool IsSafeKey(string value)
        {
            foreach (var character in value)
            {
                if (!(character >= 'A' && character <= 'Z') && !(character >= 'a' && character <= 'z')
                    && !(character >= '0' && character <= '9') && character != '.' && character != '_' && character != '-') return false;
            }
            return true;
        }

        static bool IsRisk(string value)
        {
            return value == "read" || value == "write" || value == "execute" || value == "destructive" || value == "network";
        }

        enum JsonKind { Null, Boolean, Number, String, Object, Array }

        sealed class JsonValue
        {
            public JsonKind Kind;
            public string String;
            public double Number;
            public Dictionary<string, JsonValue> Object;
            public List<JsonValue> Array;
            public int Start;
            public int End;
            public string Raw(string json) => json.Substring(Start, End - Start);
        }

        sealed class Parser
        {
            readonly string m_json;
            int m_index;
            int m_nodes;
            int m_stringChars;

            public Parser(string json) { m_json = json ?? string.Empty; }

            public JsonValue Parse()
            {
                SkipWhitespace();
                var value = Value(0);
                SkipWhitespace();
                if (m_index != m_json.Length) throw new FormatException();
                return value;
            }

            JsonValue Value(int depth)
            {
                if (depth > MaxDepth || ++m_nodes > MaxNodes || m_index >= m_json.Length) throw new FormatException();
                var start = m_index;
                JsonValue value;
                switch (m_json[m_index])
                {
                    case '{': value = Object(depth); break;
                    case '[': value = Array(depth); break;
                    case '"': value = new JsonValue { Kind = JsonKind.String, String = String() }; break;
                    case 't': Literal("true"); value = new JsonValue { Kind = JsonKind.Boolean }; break;
                    case 'f': Literal("false"); value = new JsonValue { Kind = JsonKind.Boolean }; break;
                    case 'n': Literal("null"); value = new JsonValue { Kind = JsonKind.Null }; break;
                    default: value = Number(); break;
                }
                value.Start = start;
                value.End = m_index;
                return value;
            }

            JsonValue Object(int depth)
            {
                m_index++;
                var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                SkipWhitespace();
                if (Take('}')) return new JsonValue { Kind = JsonKind.Object, Object = fields };
                while (true)
                {
                    if (m_index >= m_json.Length || m_json[m_index] != '"') throw new FormatException();
                    var key = String();
                    if (key.Length > MaxKeyChars || fields.Count >= MaxObjectKeys || fields.ContainsKey(key)) throw new FormatException();
                    SkipWhitespace();
                    Require(':');
                    SkipWhitespace();
                    fields.Add(key, Value(depth + 1));
                    SkipWhitespace();
                    if (Take('}')) break;
                    Require(',');
                    SkipWhitespace();
                }
                return new JsonValue { Kind = JsonKind.Object, Object = fields };
            }

            JsonValue Array(int depth)
            {
                m_index++;
                var items = new List<JsonValue>();
                SkipWhitespace();
                if (Take(']')) return new JsonValue { Kind = JsonKind.Array, Array = items };
                while (true)
                {
                    if (items.Count >= MaxArrayItems) throw new FormatException();
                    items.Add(Value(depth + 1));
                    SkipWhitespace();
                    if (Take(']')) break;
                    Require(',');
                    SkipWhitespace();
                }
                return new JsonValue { Kind = JsonKind.Array, Array = items };
            }

            string String()
            {
                Require('"');
                var builder = new StringBuilder();
                while (m_index < m_json.Length)
                {
                    var character = m_json[m_index++];
                    if (character == '"')
                    {
                        if (builder.Length > MaxStringChars) throw new FormatException();
                        AddStringChars(builder.Length);
                        return builder.ToString();
                    }
                    if (character < 0x20) throw new FormatException();
                    if (character != '\\') { builder.Append(character); continue; }
                    if (m_index >= m_json.Length) throw new FormatException();
                    var escaped = m_json[m_index++];
                    switch (escaped)
                    {
                        case '"': case '\\': case '/': builder.Append(escaped); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (m_index + 4 > m_json.Length || !ushort.TryParse(m_json.Substring(m_index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new FormatException();
                            builder.Append((char)code);
                            m_index += 4;
                            break;
                        default: throw new FormatException();
                    }
                }
                throw new FormatException();
            }

            JsonValue Number()
            {
                var start = m_index;
                if (Take('-')) { }
                if (Take('0')) { }
                else { Digits(true); }
                if (Take('.')) Digits(true);
                if (m_index < m_json.Length && (m_json[m_index] == 'e' || m_json[m_index] == 'E'))
                {
                    m_index++;
                    if (m_index < m_json.Length && (m_json[m_index] == '+' || m_json[m_index] == '-')) m_index++;
                    Digits(true);
                }
                if (!double.TryParse(m_json.Substring(start, m_index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || double.IsNaN(number) || double.IsInfinity(number)) throw new FormatException();
                return new JsonValue { Kind = JsonKind.Number, Number = number };
            }

            void Digits(bool required)
            {
                var start = m_index;
                while (m_index < m_json.Length && m_json[m_index] >= '0' && m_json[m_index] <= '9') m_index++;
                if (required && start == m_index) throw new FormatException();
            }

            void Literal(string value)
            {
                if (m_index + value.Length > m_json.Length || string.CompareOrdinal(m_json, m_index, value, 0, value.Length) != 0) throw new FormatException();
                m_index += value.Length;
            }

            void AddStringChars(int count)
            {
                m_stringChars += count;
                if (m_stringChars > MaxTotalStringChars) throw new FormatException();
            }

            void SkipWhitespace()
            {
                while (m_index < m_json.Length && (m_json[m_index] == ' ' || m_json[m_index] == '\t' || m_json[m_index] == '\r' || m_json[m_index] == '\n')) m_index++;
            }

            bool Take(char character)
            {
                if (m_index >= m_json.Length || m_json[m_index] != character) return false;
                m_index++;
                return true;
            }

            void Require(char character) { if (!Take(character)) throw new FormatException(); }
        }
    }
}
