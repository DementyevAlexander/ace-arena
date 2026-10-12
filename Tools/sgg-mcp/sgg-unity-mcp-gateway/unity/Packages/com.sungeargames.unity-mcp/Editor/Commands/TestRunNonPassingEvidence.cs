using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace SGG.UnityMCP.Editor.Commands
{
    // Diagnostic only: never decides execution, counts, success or ownership.
    internal sealed class TestRunNonPassingEvidence
    {
        internal const int MaxEntries = 64;
        internal const int MaxNodes = 20000;
        internal const int MaxDepth = 64;
        internal const int MaxEvidenceBytes = 48000;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly List<Entry> m_entries = new List<Entry>();
        readonly HashSet<string> m_keys = new HashSet<string>(StringComparer.Ordinal);
        bool m_observed;
        bool m_terminal;
        bool m_partial;
        bool m_truncated;
        int m_leafCount;
        int m_nonPassingCount;
        string Status => !m_observed ? "unavailable" : m_partial ? "partial" : m_truncated ? "truncated" : m_terminal ? "complete" : "partial";

        internal void Capture(object result)
        {
            m_observed = true;
            m_terminal = false;
            m_partial = true;
            try { Visit(result, 0, string.Empty, new Traversal(), false); }
            catch { m_partial = true; }
        }

        internal void CaptureTree(object result)
        {
            // Terminal observations replace callback observations: a complete
            // tree cannot certify stale outcomes or callback-only identities.
            var terminal = new TestRunNonPassingEvidence();
            if (result != null)
            {
                terminal.m_observed = true;
                terminal.m_terminal = true;
                try { terminal.Visit(result, 0, string.Empty, new Traversal(), true); }
                catch { terminal.m_partial = true; }
            }
            Replace(terminal);
        }

        void Replace(TestRunNonPassingEvidence other)
        {
            m_entries.Clear(); m_entries.AddRange(other.m_entries);
            m_keys.Clear(); foreach (var key in other.m_keys) m_keys.Add(key);
            m_observed = other.m_observed; m_terminal = other.m_terminal;
            m_partial = other.m_partial; m_truncated = other.m_truncated;
            m_leafCount = other.m_leafCount; m_nonPassingCount = other.m_nonPassingCount;
        }

        internal void CheckTerminalCoverage(int leafCount, int nonPassingCount)
        {
            // Aggregate evidence is not changed. It can only downgrade our
            // coverage witness when a structurally valid tree omitted leaves.
            if (!m_terminal || m_leafCount != leafCount || m_nonPassingCount != nonPassingCount) m_partial = true;
        }

        void Visit(object result, int depth, string inheritedAssembly, Traversal traversal, bool tree)
        {
            if (++traversal.Attempts > MaxNodes || depth > MaxDepth) { m_truncated = true; return; }
            if (result == null || !traversal.Seen.Add(result)) { m_partial = true; return; }
            if (!ReadMember(result, new[] { "Test" }, out var test, out _)) { m_partial = true; return; }
            var metadata = test ?? result;
            if (!ReadMember(metadata, new[] { "IsSuite" }, out var suiteValue, out _) || !(suiteValue is bool))
            {
                m_partial = true;
                ReadChildren(result, depth, inheritedAssembly, traversal, tree, out _);
                return;
            }
            var isSuite = (bool)suiteValue;
            if (!ReadMember(metadata, new[] { "IsTestAssembly" }, out var assemblyValue, out _)
                || (assemblyValue != null && !(assemblyValue is bool))) { m_partial = true; return; }
            var isAssembly = assemblyValue is bool assemblyFlag && assemblyFlag;
            if (!ReadText(metadata, new[] { "AssemblyName" }, out var assembly)
                || (assembly.Length == 0 && !ReferenceEquals(metadata, result) && !ReadText(result, new[] { "AssemblyName" }, out assembly)))
            { m_partial = true; return; }
            if (assembly.Length == 0 && isAssembly
                && !ReadText(metadata, new[] { "FullName", "Name" }, out assembly)) { m_partial = true; return; }
            if (assembly.Length == 0) assembly = inheritedAssembly;
            if (assembly.Length > 256 || !ValidUtf8(assembly)) { m_partial = true; return; }
            var childrenReadable = ReadChildren(result, depth, assembly, traversal, tree, out var hasChildren);
            if (!childrenReadable) { m_partial = true; return; }
            if (isSuite || isAssembly) return;
            if (hasChildren) { m_partial = true; return; }

            if (!ReadStatus(result, out var rawStatus)) { m_partial = true; return; }
            m_leafCount++;
            if (StatusHead(rawStatus) == "Passed") return;
            if (!NonPassingStatus(rawStatus)) { m_partial = true; return; }
            m_nonPassingCount++;
            if (!ReadText(metadata, new[] { "FullName", "FullTestName" }, out var fullName)
                || !ReadText(metadata, new[] { "UniqueName" }, out var uniqueName)
                || !ReadText(metadata, new[] { "Name" }, out var name)
                || fullName.Length > 2048 || uniqueName.Length > 2048
                || (string.IsNullOrWhiteSpace(fullName) && string.IsNullOrWhiteSpace(uniqueName))
                || !ValidUtf8(fullName) || !ValidUtf8(uniqueName)) { m_partial = true; return; }
            var entry = new Entry { FullName = fullName, UniqueName = uniqueName, Assembly = assembly, RawStatus = rawStatus };
            entry.Name = Clip(name, 200);
            if (!ReadText(result, new[] { "Message", "Output" }, out var message)) m_partial = true;
            if (!ReadText(result, new[] { "StackTrace" }, out var stackTrace)) m_partial = true;
            entry.Message = Clip(message, 1000); entry.StackTrace = Clip(stackTrace, 2000);
            Add(entry);
        }

        bool ReadChildren(object result, int depth, string assembly, Traversal traversal, bool tree, out bool hasChildren)
        {
            hasChildren = false;
            if (!ReadMember(result, new[] { "Children", "Results", "TestResults" }, out var children, out var found)
                || !found || !(children is IEnumerable enumerable) || children is string) return false;
            IEnumerator iterator = null;
            var readable = true;
            try
            {
                iterator = enumerable.GetEnumerator();
                if (iterator == null) return false;
                while (iterator.MoveNext())
                {
                    hasChildren = true;
                    if (!tree) break;
                    Visit(iterator.Current, depth + 1, assembly, traversal, true);
                    if (traversal.Attempts > MaxNodes) break;
                }
            }
            catch { readable = false; }
            finally
            {
                try { (iterator as IDisposable)?.Dispose(); }
                catch { readable = false; }
            }
            return readable;
        }

        void Add(Entry entry)
        {
            var key = entry.Key;
            if (m_keys.Contains(key)) return;
            if (m_entries.Count >= MaxEntries) { m_truncated = true; return; }
            // Keys are retained only for the bounded retained records.
            m_keys.Add(key); m_entries.Add(entry);
        }

        string Clip(string value, int limit)
        {
            value = value ?? string.Empty;
            if (value.Length > limit)
            {
                m_truncated = true;
                if (limit > 0 && char.IsHighSurrogate(value[limit - 1])) limit--;
                value = value.Substring(0, limit);
            }
            if (!ValidUtf8(value)) { m_partial = true; return string.Empty; }
            return value;
        }

        static bool ReadMember(object instance, string[] names, out object value, out bool found)
        {
            value = null; found = false;
            if (instance == null) return false;
            try
            {
                var type = instance.GetType();
                foreach (var name in names)
                {
                    var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var field = property == null ? type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                    if (property == null && field == null) continue;
                    found = true;
                    if (property != null && property.GetIndexParameters().Length != 0) return false;
                    value = property != null ? property.GetValue(instance, null) : field.GetValue(instance);
                    if (value != null) return true;
                }
                return true;
            }
            catch { return false; }
        }

        static bool ReadText(object instance, string[] names, out string text)
        {
            text = string.Empty;
            if (!ReadMember(instance, names, out var value, out _)) return false;
            if (value == null) return true;
            if (!(value is string stringValue)) return false;
            text = stringValue; return true;
        }

        static bool ReadStatus(object result, out string status)
        {
            status = string.Empty;
            if (!ReadMember(result, new[] { "ResultState", "TestStatus", "Outcome" }, out var value, out _) || value == null) return false;
            try { status = value.ToString() ?? string.Empty; } catch { return false; }
            return status.Length > 0 && status.Length <= 120 && ValidUtf8(status);
        }

        static string StatusHead(string status)
        {
            var colon = status.IndexOf(':');
            return colon < 0 ? status : status.Substring(0, colon);
        }

        static bool NonPassingStatus(string status)
        {
            switch (StatusHead(status))
            {
                case "Failed": case "Failure": case "Error": case "Inconclusive": case "Skipped": case "Ignored": return true;
                default: return false;
            }
        }

        static bool ValidUtf8(string text)
        {
            try { Utf8.GetByteCount(text); return true; }
            catch (EncoderFallbackException) { return false; }
        }

        internal void AppendJson(StringBuilder builder, int maxArtifactBytes = 200000)
        {
            // Invalid legacy base text or a new diagnostic encoding failure
            // must not turn artifact publication into a different outcome.
            try { AppendJsonCore(builder, maxArtifactBytes); }
            catch { }
        }

        void AppendJsonCore(StringBuilder builder, int maxArtifactBytes)
        {
            // Stage the suffix without trimming base fields or memory records.
            var remaining = Math.Min(MaxEvidenceBytes, maxArtifactBytes - Utf8.GetByteCount(builder.ToString()) - 1);
            const int WitnessReserve = 160;
            if (remaining < WitnessReserve) return;
            var items = new StringBuilder();
            var bytes = 0; var omitted = false;
            foreach (var entry in m_entries)
            {
                var json = entry.Json();
                var addition = Utf8.GetByteCount(json) + (items.Length == 0 ? 0 : 1);
                if (bytes + addition > remaining - WitnessReserve) { omitted = true; break; }
                if (items.Length > 0) items.Append(',');
                items.Append(json); bytes += addition;
            }
            var truncated = m_truncated || omitted;
            var outputStatus = Status == "partial" ? "partial" : truncated ? "truncated" : Status;
            var suffix = ",\"non_passing_evidence_status\":" + JsonUtil.String(outputStatus)
                + ",\"non_passing_tests_truncated\":" + JsonUtil.Bool(truncated)
                + ",\"non_passing_tests\":[" + items + "]";
            if (Utf8.GetByteCount(suffix) <= remaining) builder.Append(suffix);
        }

        internal void Restore(string receipt)
        {
            var restored = new TestRunNonPassingEvidence();
            try
            {
                if (string.IsNullOrEmpty(receipt) || receipt.Length > 200000 || Utf8.GetByteCount(receipt) > 200000) { Replace(restored); return; }
                var root = new EvidenceJsonReader(receipt).Object();
                if (!root.TryGetValue("non_passing_evidence_status", out var statusJson)
                    || !root.TryGetValue("non_passing_tests_truncated", out var flagJson)
                    || !root.TryGetValue("non_passing_tests", out var testsJson)) { Replace(restored); return; }
                var status = new EvidenceJsonReader(statusJson).String();
                if ((status != "complete" && status != "partial" && status != "truncated" && status != "unavailable")
                    || (flagJson != "true" && flagJson != "false") || (status == "truncated" && flagJson != "true"))
                { Replace(restored); return; }
                var items = new EvidenceJsonReader(testsJson).Array();
                if (status == "unavailable" && (items.Count != 0 || flagJson != "false")) { Replace(restored); return; }
                restored.m_observed = status != "unavailable"; restored.m_terminal = status == "complete";
                restored.m_partial = status == "partial"; restored.m_truncated = flagJson == "true";
                foreach (var item in items)
                {
                    if (item.Length == 0 || item[0] != '{') { restored.m_partial = true; continue; }
                    var fields = new EvidenceJsonReader(item).Object();
                    if (!Entry.TryRead(fields, out var entry)) { restored.m_partial = true; continue; }
                    entry.Name = restored.Clip(entry.Name, 200); entry.Message = restored.Clip(entry.Message, 1000);
                    entry.StackTrace = restored.Clip(entry.StackTrace, 2000); restored.Add(entry);
                }
                if (restored.m_partial) restored.m_observed = true;
            }
            catch { restored = new TestRunNonPassingEvidence(); }
            Replace(restored);
        }

        sealed class Traversal
        {
            public int Attempts;
            public readonly HashSet<object> Seen = new HashSet<object>(ReferenceComparer.Instance);
        }
        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }

        sealed class Entry
        {
            public string Name = string.Empty, FullName = string.Empty, UniqueName = string.Empty, Assembly = string.Empty;
            public string RawStatus = string.Empty, Message = string.Empty, StackTrace = string.Empty;
            public string Key => Part(Assembly) + Part(FullName) + Part(UniqueName) + Part(RawStatus);
            static string Part(string value) => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
            public string Json() => "{\"name\":" + JsonUtil.String(Name) + ",\"full_name\":" + JsonUtil.String(FullName)
                + ",\"unique_name\":" + JsonUtil.String(UniqueName) + ",\"assembly\":" + JsonUtil.String(Assembly)
                + ",\"raw_status\":" + JsonUtil.String(RawStatus) + ",\"message\":" + JsonUtil.String(Message)
                + ",\"stack_trace\":" + JsonUtil.String(StackTrace) + "}";

            public static bool TryRead(Dictionary<string, string> fields, out Entry entry)
            {
                entry = new Entry();
                if (!Text(fields, "name", out entry.Name) || !Text(fields, "full_name", out entry.FullName)
                    || !Text(fields, "unique_name", out entry.UniqueName) || !Text(fields, "assembly", out entry.Assembly)
                    || !Text(fields, "raw_status", out entry.RawStatus) || !Text(fields, "message", out entry.Message)
                    || !Text(fields, "stack_trace", out entry.StackTrace)) return false;
                return entry.FullName.Length <= 2048 && entry.UniqueName.Length <= 2048 && entry.Assembly.Length <= 256
                    && (!string.IsNullOrWhiteSpace(entry.FullName) || !string.IsNullOrWhiteSpace(entry.UniqueName))
                    && entry.RawStatus.Length > 0 && entry.RawStatus.Length <= 120 && NonPassingStatus(entry.RawStatus)
                    && ValidUtf8(entry.FullName) && ValidUtf8(entry.UniqueName) && ValidUtf8(entry.Assembly) && ValidUtf8(entry.RawStatus);
            }
            static bool Text(Dictionary<string, string> fields, string key, out string value)
            {
                value = string.Empty;
                if (!fields.TryGetValue(key, out var raw)) return key != "raw_status";
                if (raw.Length == 0 || raw[0] != '"') return false;
                value = new EvidenceJsonReader(raw).String(); return true;
            }
        }

        // Local bounded strict shape reader: validates the entire document,
        // retaining raw values only at the requested object or array level.
        sealed class EvidenceJsonReader
        {
            readonly string m_json;
            int m_index, m_nodes;
            public EvidenceJsonReader(string json) { m_json = json; }
            public Dictionary<string, string> Object() { Space(); var value = ReadObject(0, true); End(); return value; }
            public List<string> Array() { Space(); var value = ReadArray(0, true); End(); return value; }
            public string String() { Space(); var value = ReadString(); End(); return value; }
            void End() { Space(); if (m_index != m_json.Length) throw new FormatException(); }
            void Charge(int depth) { if (depth > 32 || ++m_nodes > MaxNodes) throw new FormatException(); }
            Dictionary<string, string> ReadObject(int depth, bool retain)
            {
                Charge(depth); Require('{'); Space();
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (Take('}')) return fields;
                while (true)
                {
                    var key = ReadString();
                    if (key.Length > 256 || fields.Count >= 512 || fields.ContainsKey(key)) throw new FormatException();
                    Space(); Require(':'); Space(); var start = m_index; Value(depth + 1);
                    fields.Add(key, retain ? m_json.Substring(start, m_index - start) : string.Empty);
                    Space(); if (Take('}')) return fields;
                    Require(','); Space();
                }
            }
            List<string> ReadArray(int depth, bool retain)
            {
                Charge(depth); Require('['); Space(); var items = new List<string>();
                if (Take(']')) return items;
                while (true)
                {
                    var start = m_index; Value(depth + 1);
                    if (retain) items.Add(m_json.Substring(start, m_index - start));
                    Space(); if (Take(']')) return items;
                    Require(','); Space();
                }
            }
            void Value(int depth)
            {
                Space(); if (m_index >= m_json.Length) throw new FormatException();
                switch (m_json[m_index])
                {
                    case '{': ReadObject(depth, false); return;
                    case '[': ReadArray(depth, false); return;
                    case '"': Charge(depth); ReadString(); return;
                    case 't': Charge(depth); Literal("true"); return;
                    case 'f': Charge(depth); Literal("false"); return;
                    case 'n': Charge(depth); Literal("null"); return;
                    default: Charge(depth); Number(); return;
                }
            }
            string ReadString()
            {
                Require('"'); var value = new StringBuilder();
                while (m_index < m_json.Length)
                {
                    var character = m_json[m_index++];
                    if (character == '"') return value.ToString();
                    if (character < ' ') throw new FormatException();
                    if (character != '\\') { value.Append(character); continue; }
                    if (m_index >= m_json.Length) throw new FormatException();
                    switch (m_json[m_index++])
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u':
                            var code = 0;
                            for (var count = 0; count < 4; count++)
                            {
                                if (m_index >= m_json.Length) throw new FormatException();
                                var digit = m_json[m_index++];
                                var hex = digit >= '0' && digit <= '9' ? digit - '0' : digit >= 'a' && digit <= 'f' ? digit - 'a' + 10 : digit >= 'A' && digit <= 'F' ? digit - 'A' + 10 : -1;
                                if (hex < 0) throw new FormatException();
                                code = code * 16 + hex;
                            }
                            value.Append((char)code); break;
                        default: throw new FormatException();
                    }
                }
                throw new FormatException();
            }
            void Number()
            {
                Take('-');
                if (!Take('0'))
                {
                    if (m_index >= m_json.Length || m_json[m_index] < '1' || m_json[m_index] > '9') throw new FormatException();
                    Digits();
                }
                if (Take('.')) { var start = m_index; Digits(); if (start == m_index) throw new FormatException(); }
                if (Take('e') || Take('E'))
                {
                    if (!Take('+')) Take('-');
                    var start = m_index; Digits(); if (start == m_index) throw new FormatException();
                }
            }
            void Digits() { while (m_index < m_json.Length && m_json[m_index] >= '0' && m_json[m_index] <= '9') m_index++; }
            void Literal(string value) { foreach (var character in value) Require(character); }
            bool Take(char value) { if (m_index >= m_json.Length || m_json[m_index] != value) return false; m_index++; return true; }
            void Require(char value) { if (!Take(value)) throw new FormatException(); }
            void Space() { while (m_index < m_json.Length && (m_json[m_index] == ' ' || m_json[m_index] == '\t' || m_json[m_index] == '\r' || m_json[m_index] == '\n')) m_index++; }
        }
    }
}
