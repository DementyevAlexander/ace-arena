using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    internal sealed class DynamicCSharpSerializationResult
    {
        internal DynamicCSharpSerializationResult(
            bool success,
            string json,
            string kind,
            int totalCount,
            int returnedCount,
            bool truncated,
            int truncationCount,
            int utf8Bytes,
            string errorCode,
            string errorMessage,
            string errorDetailsJson)
        {
            Success = success;
            Json = json ?? string.Empty;
            Kind = kind ?? string.Empty;
            TotalCount = Math.Max(0, totalCount);
            ReturnedCount = Math.Max(0, returnedCount);
            Truncated = truncated;
            TruncationCount = Math.Max(0, truncationCount);
            Utf8Bytes = Math.Max(0, utf8Bytes);
            ErrorCode = errorCode ?? string.Empty;
            ErrorMessage = errorMessage ?? string.Empty;
            ErrorDetailsJson = errorDetailsJson ?? string.Empty;
        }

        public bool Success { get; }
        public string Json { get; }
        public string Kind { get; }
        public int TotalCount { get; }
        public int ReturnedCount { get; }
        public bool Truncated { get; }
        public int TruncationCount { get; }
        public int Utf8Bytes { get; }
        public string ErrorCode { get; }
        public string ErrorMessage { get; }
        public string ErrorDetailsJson { get; }

        public string SummaryJson()
        {
            return "{\"kind\":" + JsonUtil.String(Kind)
                + ",\"total_count\":" + TotalCount.ToString(CultureInfo.InvariantCulture)
                + ",\"returned_count\":" + ReturnedCount.ToString(CultureInfo.InvariantCulture)
                + ",\"truncated\":" + JsonUtil.Bool(Truncated)
                + ",\"truncation_count\":" + TruncationCount.ToString(CultureInfo.InvariantCulture)
                + ",\"utf8_bytes\":" + Utf8Bytes.ToString(CultureInfo.InvariantCulture) + "}";
        }

        internal DynamicCSharpSerializationResult WithUtf8Bytes(int utf8Bytes)
        {
            return new DynamicCSharpSerializationResult(
                Success,
                Json,
                Kind,
                TotalCount,
                ReturnedCount,
                Truncated,
                TruncationCount,
                utf8Bytes,
                ErrorCode,
                ErrorMessage,
                ErrorDetailsJson);
        }
    }

    internal static class DynamicCSharpResultSerializer
    {
        internal const int MaxDepth = 4;
        internal const int MaxItems = 100;
        internal const int MaxEnumerableScanItems = 4096;
        internal const int MaxVisitedValues = 128;
        internal const int MaxStringJsonContentBytes = 256;
        internal const int MaxResultUtf8Bytes = 65536;
        const int MaxNameChars = 128;
        const int MaxNameJsonContentBytes = 128;

        public static DynamicCSharpSerializationResult Serialize(object value)
        {
            var result = SerializeValue(value, 0, new SerializationContext());
            if (!result.Success)
            {
                return result;
            }

            var utf8Bytes = Encoding.UTF8.GetByteCount(result.Json);
            if (utf8Bytes > MaxResultUtf8Bytes)
            {
                return Failure(
                    "result_serialization_too_large",
                    "Dynamic C# result exceeded the bounded serialized response size.",
                    "max_result_utf8_bytes",
                    result.Kind,
                    result.TotalCount,
                    result.ReturnedCount);
            }

            return result.WithUtf8Bytes(utf8Bytes);
        }

        static DynamicCSharpSerializationResult SerializeValue(object value, int depth, SerializationContext context)
        {
            if (!context.TryVisit())
            {
                return TruncatedPlaceholder("max_values", value, KnownCount(value));
            }

            if (value == null)
            {
                return Success("null", "null", 0, 0, false, 0);
            }

            if (value is string stringValue)
            {
                return SerializeString(stringValue);
            }

            if (value is bool boolValue)
            {
                return Success(JsonUtil.Bool(boolValue), "boolean", 1, 1, false, 0);
            }

            if (value is char charValue)
            {
                return SerializeString(charValue.ToString());
            }

            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal)
            {
                return Success(Convert.ToString(value, CultureInfo.InvariantCulture), "number", 1, 1, false, 0);
            }

            if (value is float floatValue)
            {
                var json = float.IsNaN(floatValue) || float.IsInfinity(floatValue)
                    ? JsonUtil.String(floatValue.ToString(CultureInfo.InvariantCulture))
                    : floatValue.ToString("R", CultureInfo.InvariantCulture);
                return Success(json, "number", 1, 1, false, 0);
            }

            if (value is double doubleValue)
            {
                var json = double.IsNaN(doubleValue) || double.IsInfinity(doubleValue)
                    ? JsonUtil.String(doubleValue.ToString(CultureInfo.InvariantCulture))
                    : doubleValue.ToString("R", CultureInfo.InvariantCulture);
                return Success(json, "number", 1, 1, false, 0);
            }

            if (value is Enum enumValue)
            {
                return Success(JsonUtil.String(enumValue.ToString()), "enum", 1, 1, false, 0);
            }

            if (value is DateTime dateTimeValue)
            {
                return Success(JsonUtil.String(dateTimeValue.ToString("O", CultureInfo.InvariantCulture)), "date_time", 1, 1, false, 0);
            }

            if (value is DateTimeOffset dateTimeOffsetValue)
            {
                return Success(JsonUtil.String(dateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture)), "date_time_offset", 1, 1, false, 0);
            }

            if (value is TimeSpan timeSpanValue)
            {
                return Success(JsonUtil.String(timeSpanValue.ToString("c", CultureInfo.InvariantCulture)), "time_span", 1, 1, false, 0);
            }

            if (value is Guid guidValue)
            {
                return Success(JsonUtil.String(guidValue.ToString("D")), "guid", 1, 1, false, 0);
            }

            if (value is Uri uriValue)
            {
                return SerializeString(uriValue.OriginalString);
            }

            if (value is UnityEngine.Object unityObject)
            {
                return SerializeUnityObject(unityObject, depth, context);
            }

            if (value is Delegate || value is Type || value is MemberInfo || value is Stream)
            {
                return Unsupported(value, "unsupported_shape");
            }

            if (depth >= MaxDepth)
            {
                return TruncatedPlaceholder("max_depth", value, KnownCount(value));
            }

            var trackReference = !value.GetType().IsValueType;
            if (trackReference && !context.Active.Add(value))
            {
                return TruncatedPlaceholder("reference_cycle", value, KnownCount(value));
            }

            try
            {
                if (value is IDictionary dictionary)
                {
                    return SerializeDictionary(dictionary, depth + 1, context);
                }

                if (value is IEnumerable enumerable)
                {
                    return SerializeEnumerable(enumerable, depth + 1, context);
                }

                return SerializeObject(value, depth + 1, context);
            }
            finally
            {
                if (trackReference)
                {
                    context.Active.Remove(value);
                }
            }
        }

        static DynamicCSharpSerializationResult SerializeString(string value)
        {
            if (HasInvalidSurrogate(value))
            {
                return Failure(
                    "result_serialization_invalid_unicode",
                    "Dynamic C# returned a string with invalid UTF-16 data.",
                    "invalid_utf16",
                    "string",
                    value.Length,
                    0);
            }

            var totalUtf8Bytes = Encoding.UTF8.GetByteCount(value);
            var returnedChars = JsonStringPrefixLength(value, MaxStringJsonContentBytes);
            if (returnedChars >= value.Length)
            {
                return Success(JsonUtil.String(value), "string", value.Length, value.Length, false, 0);
            }

            var prefix = value.Substring(0, returnedChars);
            return Success(
                "{\"kind\":\"string\",\"value\":" + JsonUtil.String(prefix)
                    + ",\"total_count\":" + value.Length.ToString(CultureInfo.InvariantCulture)
                    + ",\"returned_count\":" + returnedChars.ToString(CultureInfo.InvariantCulture)
                    + ",\"total_utf8_bytes\":" + totalUtf8Bytes.ToString(CultureInfo.InvariantCulture)
                    + ",\"returned_utf8_bytes\":" + Encoding.UTF8.GetByteCount(prefix).ToString(CultureInfo.InvariantCulture)
                    + ",\"truncated\":true,\"reason\":\"max_string_json_content_bytes\",\"applied_limit\":"
                    + MaxStringJsonContentBytes.ToString(CultureInfo.InvariantCulture) + "}",
                "string",
                value.Length,
                returnedChars,
                true,
                1);
        }

        static DynamicCSharpSerializationResult SerializeUnityObject(UnityEngine.Object value, int depth, SerializationContext context)
        {
            if (value == null)
            {
                return Success("null", "unity_object", 0, 0, false, 0);
            }

            DynamicCSharpSerializationResult name;
            try
            {
                name = SerializeValue(value.name ?? string.Empty, depth + 1, context);
            }
            catch
            {
                return Unsupported(value, "unity_object_unavailable");
            }

            if (!name.Success)
            {
                return name;
            }

            return Success(
                "{\"kind\":\"unity_object\",\"value\":{\"type\":" + JsonUtil.String(BoundedName(value.GetType().Name))
                    + ",\"name\":" + name.Json + "},\"total_count\":2,\"returned_count\":2,\"truncated\":" + JsonUtil.Bool(name.Truncated) + "}",
                "unity_object",
                2,
                2,
                name.Truncated,
                name.TruncationCount);
        }

        static DynamicCSharpSerializationResult SerializeDictionary(IDictionary dictionary, int depth, SerializationContext context)
        {
            int totalCount;
            try
            {
                totalCount = dictionary.Count;
            }
            catch
            {
                return Unsupported(dictionary, "count_unavailable");
            }

            if (totalCount > MaxEnumerableScanItems)
            {
                return Failure(
                    "result_serialization_unsupported",
                    "Dynamic C# dictionary exceeds the deterministic key ordering bound.",
                    "dictionary_sort_bound",
                    "dictionary",
                    totalCount,
                    0);
            }

            var entries = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var observedCount = 0;
            try
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    observedCount++;
                    if (observedCount > MaxEnumerableScanItems)
                    {
                        return Failure(
                            "result_serialization_unsupported",
                            "Dynamic C# dictionary exceeded the bounded deterministic enumeration.",
                            "dictionary_enumeration_bound",
                            "dictionary",
                            totalCount,
                            0);
                    }

                    if (!TryDictionaryKey(entry.Key, out var key) || !IsBoundedJsonName(key) || entries.ContainsKey(key))
                    {
                        return Unsupported(dictionary, "unsupported_dictionary_key");
                    }

                    entries.Add(key, entry.Value);
                }
            }
            catch
            {
                return Unsupported(dictionary, "enumeration_failed");
            }

            if (entries.Count != totalCount)
            {
                return Unsupported(dictionary, "unstable_count");
            }

            var builder = new StringBuilder();
            builder.Append("{\"kind\":\"dictionary\",\"entries\":{");
            var returnedCount = 0;
            var childTruncated = false;
            var truncationCount = 0;
            foreach (var entry in entries)
            {
                if (returnedCount >= MaxItems || context.Exhausted)
                {
                    break;
                }

                var child = SerializeValue(entry.Value, depth, context);
                if (!child.Success)
                {
                    return child;
                }

                if (returnedCount > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(entry.Key)).Append(':').Append(child.Json);
                returnedCount++;
                childTruncated |= child.Truncated;
                truncationCount += child.TruncationCount;
            }

            var locallyTruncated = returnedCount < totalCount;
            builder.Append("},\"total_count\":").Append(totalCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\"returned_count\":").Append(returnedCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\"truncated\":").Append(JsonUtil.Bool(locallyTruncated || childTruncated)).Append('}');
            return Success(builder.ToString(), "dictionary", totalCount, returnedCount, locallyTruncated || childTruncated, truncationCount + (locallyTruncated ? 1 : 0));
        }

        static DynamicCSharpSerializationResult SerializeEnumerable(IEnumerable enumerable, int depth, SerializationContext context)
        {
            var retained = new List<object>(MaxItems);
            var totalCount = -1;
            if (enumerable is ICollection collection)
            {
                try
                {
                    totalCount = collection.Count;
                }
                catch
                {
                    return Unsupported(enumerable, "count_unavailable");
                }
            }

            var observedCount = 0;
            try
            {
                var enumerator = enumerable.GetEnumerator();
                try
                {
                    while (enumerator.MoveNext())
                    {
                        observedCount++;
                        if (retained.Count < MaxItems)
                        {
                            retained.Add(enumerator.Current);
                        }

                        if (totalCount >= 0 && retained.Count >= MaxItems)
                        {
                            break;
                        }

                        if (totalCount < 0 && observedCount > MaxEnumerableScanItems)
                        {
                            return Failure(
                                "result_serialization_count_unavailable",
                                "Dynamic C# enumerable exceeded the bounded exact-count scan.",
                                "enumerable_count_bound",
                                "array",
                                observedCount,
                                retained.Count);
                        }
                    }
                }
                finally
                {
                    (enumerator as IDisposable)?.Dispose();
                }
            }
            catch
            {
                return Unsupported(enumerable, "enumeration_failed");
            }

            if (totalCount < 0)
            {
                totalCount = observedCount;
            }
            else if (totalCount <= MaxItems && observedCount != totalCount)
            {
                return Unsupported(enumerable, "unstable_count");
            }

            var builder = new StringBuilder();
            builder.Append("{\"kind\":\"array\",\"items\":[");
            var returnedCount = 0;
            var childTruncated = false;
            var truncationCount = 0;
            for (var index = 0; index < retained.Count && !context.Exhausted; index++)
            {
                var child = SerializeValue(retained[index], depth, context);
                if (!child.Success)
                {
                    return child;
                }

                if (returnedCount > 0)
                {
                    builder.Append(',');
                }

                builder.Append(child.Json);
                returnedCount++;
                childTruncated |= child.Truncated;
                truncationCount += child.TruncationCount;
            }

            var locallyTruncated = returnedCount < totalCount;
            builder.Append("],\"total_count\":").Append(totalCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\"returned_count\":").Append(returnedCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\"truncated\":").Append(JsonUtil.Bool(locallyTruncated || childTruncated)).Append('}');
            return Success(builder.ToString(), "array", totalCount, returnedCount, locallyTruncated || childTruncated, truncationCount + (locallyTruncated ? 1 : 0));
        }

        static DynamicCSharpSerializationResult SerializeObject(object value, int depth, SerializationContext context)
        {
            var members = new SortedDictionary<string, MemberInfo>(StringComparer.Ordinal);
            try
            {
                foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    if (!TryAddMember(members, property))
                    {
                        return Unsupported(value, "duplicate_or_unbounded_member_name");
                    }
                }

                foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!TryAddMember(members, field))
                    {
                        return Unsupported(value, "duplicate_or_unbounded_member_name");
                    }
                }
            }
            catch
            {
                return Unsupported(value, "member_discovery_failed");
            }

            if (members.Count == 0)
            {
                return Unsupported(value, "unsupported_shape");
            }

            if (members.Count > MaxEnumerableScanItems)
            {
                return Unsupported(value, "member_count_bound");
            }

            var builder = new StringBuilder();
            builder.Append("{\"kind\":\"object\",\"type\":").Append(JsonUtil.String(BoundedName(value.GetType().Name))).Append(",\"properties\":{");
            var returnedCount = 0;
            var childTruncated = false;
            var truncationCount = 0;
            foreach (var pair in members)
            {
                if (returnedCount >= MaxItems || context.Exhausted)
                {
                    break;
                }

                object memberValue;
                try
                {
                    memberValue = pair.Value is PropertyInfo property
                        ? property.GetValue(value, null)
                        : ((FieldInfo)pair.Value).GetValue(value);
                }
                catch
                {
                    return Failure(
                        "result_serialization_failed",
                        "Dynamic C# result member could not be read safely.",
                        "member_read_failed",
                        BoundedName(value.GetType().Name),
                        members.Count,
                        returnedCount,
                        pair.Key);
                }

                var child = SerializeValue(memberValue, depth, context);
                if (!child.Success)
                {
                    return child;
                }

                if (returnedCount > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(pair.Key)).Append(':').Append(child.Json);
                returnedCount++;
                childTruncated |= child.Truncated;
                truncationCount += child.TruncationCount;
            }

            var locallyTruncated = returnedCount < members.Count;
            builder.Append("},\"total_count\":").Append(members.Count.ToString(CultureInfo.InvariantCulture))
                .Append(",\"returned_count\":").Append(returnedCount.ToString(CultureInfo.InvariantCulture))
                .Append(",\"truncated\":").Append(JsonUtil.Bool(locallyTruncated || childTruncated)).Append('}');
            return Success(builder.ToString(), "object", members.Count, returnedCount, locallyTruncated || childTruncated, truncationCount + (locallyTruncated ? 1 : 0));
        }

        static bool TryAddMember(SortedDictionary<string, MemberInfo> members, MemberInfo member)
        {
            return member != null
                && !string.IsNullOrEmpty(member.Name)
                && IsBoundedJsonName(member.Name)
                && !members.ContainsKey(member.Name)
                && TryAddMemberCore(members, member);
        }

        static bool TryAddMemberCore(SortedDictionary<string, MemberInfo> members, MemberInfo member)
        {
            members.Add(member.Name, member);
            return true;
        }

        static bool TryDictionaryKey(object value, out string key)
        {
            if (value == null)
            {
                key = "null";
                return true;
            }

            if (value is string stringValue)
            {
                key = stringValue;
                return true;
            }

            if (value is char charValue)
            {
                key = charValue.ToString();
                return true;
            }

            if (value is bool boolValue)
            {
                key = boolValue ? "true" : "false";
                return true;
            }

            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal)
            {
                key = Convert.ToString(value, CultureInfo.InvariantCulture);
                return true;
            }

            if (value is Enum enumValue)
            {
                key = enumValue.ToString();
                return true;
            }

            if (value is Guid guidValue)
            {
                key = guidValue.ToString("D");
                return true;
            }

            key = string.Empty;
            return false;
        }

        static int JsonStringPrefixLength(string value, int maxJsonContentBytes)
        {
            var index = 0;
            var bytes = 0;
            while (index < value.Length)
            {
                var charCount = char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]) ? 2 : 1;
                var character = value[index];
                int nextBytes;
                if (character == '\\' || character == '"' || character == '\n' || character == '\r' || character == '\t')
                {
                    nextBytes = 2;
                }
                else if (char.IsControl(character))
                {
                    nextBytes = 6;
                }
                else
                {
                    nextBytes = Encoding.UTF8.GetByteCount(value, index, charCount);
                }

                if (bytes + nextBytes > maxJsonContentBytes)
                {
                    break;
                }

                bytes += nextBytes;
                index += charCount;
            }

            return index;
        }

        static bool IsBoundedJsonName(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.Length <= MaxNameChars
                && !HasInvalidSurrogate(value)
                && JsonStringPrefixLength(value, MaxNameJsonContentBytes) == value.Length;
        }

        static bool HasInvalidSurrogate(string value)
        {
            for (var index = 0; index < (value ?? string.Empty).Length; index++)
            {
                if (char.IsHighSurrogate(value[index]))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    {
                        return true;
                    }

                    index++;
                }
                else if (char.IsLowSurrogate(value[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static DynamicCSharpSerializationResult TruncatedPlaceholder(string reason, object value, int totalCount)
        {
            var typeName = value == null ? "null" : BoundedName(value.GetType().Name);
            return Success(
                "{\"kind\":\"truncated\",\"reason\":" + JsonUtil.String(reason)
                    + ",\"type\":" + JsonUtil.String(typeName)
                    + ",\"total_count\":" + Math.Max(0, totalCount).ToString(CultureInfo.InvariantCulture)
                    + ",\"returned_count\":0,\"truncated\":true}",
                "truncated",
                Math.Max(0, totalCount),
                0,
                true,
                1);
        }

        static int KnownCount(object value)
        {
            try
            {
                if (value is IDictionary dictionary)
                {
                    return dictionary.Count;
                }

                if (value is ICollection collection)
                {
                    return collection.Count;
                }
            }
            catch
            {
                return 1;
            }

            return value == null ? 0 : 1;
        }

        static DynamicCSharpSerializationResult Unsupported(object value, string reason)
        {
            return Failure(
                "result_serialization_unsupported",
                "Dynamic C# returned a shape that cannot be serialized safely.",
                reason,
                value == null ? "null" : BoundedName(value.GetType().Name),
                KnownCount(value),
                0);
        }

        static DynamicCSharpSerializationResult Failure(string code, string message, string reason, string kind, int totalCount, int returnedCount, string member = "")
        {
            var details = "{\"serialization_contract\":\"sgg.unity_mcp.dynamic_csharp_result.v1\""
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"kind\":" + JsonUtil.String(BoundedName(kind))
                + ",\"total_count\":" + Math.Max(0, totalCount).ToString(CultureInfo.InvariantCulture)
                + ",\"returned_count\":" + Math.Max(0, returnedCount).ToString(CultureInfo.InvariantCulture)
                + (string.IsNullOrEmpty(member) ? string.Empty : ",\"member\":" + JsonUtil.String(BoundedName(member)))
                + ",\"max_depth\":" + MaxDepth.ToString(CultureInfo.InvariantCulture)
                + ",\"max_items\":" + MaxItems.ToString(CultureInfo.InvariantCulture)
                + ",\"max_result_utf8_bytes\":" + MaxResultUtf8Bytes.ToString(CultureInfo.InvariantCulture) + "}";
            return new DynamicCSharpSerializationResult(false, string.Empty, kind, totalCount, returnedCount, false, 0, 0, code, message, details);
        }

        static DynamicCSharpSerializationResult Success(string json, string kind, int totalCount, int returnedCount, bool truncated, int truncationCount)
        {
            return new DynamicCSharpSerializationResult(true, json, kind, totalCount, returnedCount, truncated, truncationCount, 0, string.Empty, string.Empty, string.Empty);
        }

        static string BoundedName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unknown";
            }

            if (HasInvalidSurrogate(value))
            {
                return "invalid_name";
            }

            if (value.Length <= MaxNameChars)
            {
                return value;
            }

            var returnedChars = MaxNameChars;
            if (char.IsHighSurrogate(value[returnedChars - 1]) && char.IsLowSurrogate(value[returnedChars]))
            {
                returnedChars--;
            }

            return value.Substring(0, returnedChars);
        }

        sealed class SerializationContext
        {
            public readonly HashSet<object> Active = new HashSet<object>(ReferenceEqualityComparer.Instance);
            int m_visited;

            public bool Exhausted => m_visited >= MaxVisitedValues;

            public bool TryVisit()
            {
                if (m_visited >= MaxVisitedValues)
                {
                    return false;
                }

                m_visited++;
                return true;
            }
        }

        sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return RuntimeHelpers.GetHashCode(value);
            }
        }
    }
}
