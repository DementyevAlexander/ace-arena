using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class FrameDebuggerReadonlyCommands
    {
        const string FeatureFlag = "profiler_readonly";
        const string StatusSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string EventsSchemaJson = "{\"type\":\"object\",\"properties\":{\"start_index\":{\"type\":\"integer\",\"minimum\":0},\"max_events\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"include_details\":{\"type\":\"boolean\"},\"max_string_chars\":{\"type\":\"integer\",\"minimum\":16,\"maximum\":2048}},\"additionalProperties\":false}";
        const string StatusOutputLimitJson = "{\"max_fields\":40,\"readonly\":true}";
        const string EventsOutputLimitJson = "{\"max_events\":500,\"max_detail_fields_per_event\":32,\"max_string_chars\":2048,\"readonly\":true}";
        const int MaxEvents = 500;
        const int MaxStringChars = 2048;
        const int MaxDetailFieldsPerEvent = 32;

        [McpCommand(
            "frame_debugger.status",
            Title = "Frame Debugger status",
            Description = "Read Unity Frame Debugger API availability and current status hints without enabling or changing debugger state",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = StatusSchemaJson,
            Tags = new string[] { "profiler", "frame-debugger", "rendering", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "status",
            OutputLimitJson = StatusOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Status()
        {
            var utilityType = ResolveFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                return CommandExecutionResult.Ok("{\"api_available\":false"
                    + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                    + ",\"frame_debugger_enabled\":{\"readable\":false,\"value\":null,\"source\":\"\"}"
                    + ",\"event_count\":{\"readable\":false,\"value\":null,\"source\":\"\"}"
                    + ",\"current_event_index\":{\"readable\":false,\"value\":null,\"source\":\"\"}"
                    + ",\"write_capabilities_exposed\":false"
                    + ",\"state_changed\":false"
                    + ",\"reason\":\"Unity Frame Debugger utility type was not detected\"}");
            }

            var enabledReadable = TryReadAnyStaticBool(utilityType, new string[] { "enabled", "isEnabled", "frameDebuggerEnabled" }, out var enabled, out var enabledSource);
            var countReadable = TryReadAnyStaticInt(utilityType, new string[] { "count", "eventCount", "eventsCount" }, out var eventCount, out var countSource);
            var currentReadable = TryReadAnyStaticInt(utilityType, new string[] { "currentEventIndex", "eventIndex", "limit" }, out var currentEventIndex, out var currentSource);
            return CommandExecutionResult.Ok("{\"api_available\":true"
                + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                + ",\"source\":" + JsonUtil.String(utilityType.FullName)
                + ",\"frame_debugger_enabled\":" + BoolStatusJson(enabledReadable, enabled, enabledSource)
                + ",\"event_count\":" + IntStatusJson(countReadable, eventCount, countSource)
                + ",\"current_event_index\":" + IntStatusJson(currentReadable, currentEventIndex, currentSource)
                + ",\"write_capabilities_exposed\":false"
                + ",\"state_changed\":false"
                + ",\"warnings\":[\"This command is read-only and does not enable Frame Debugger, disable it, step events or change the selected event index.\"]"
                + ",\"next_valid_actions\":[\"frame_debugger.events\",\"profiler.status\",\"profiler.frame.summary\"]}");
        }

        [McpCommand(
            "frame_debugger.events",
            Title = "Frame Debugger events",
            Description = "Read bounded already-available Unity Frame Debugger event metadata without enabling or stepping the debugger",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EventsSchemaJson,
            Tags = new string[] { "profiler", "frame-debugger", "rendering", "events", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = EventsOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Events(McpCommandContext context)
        {
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, int.MaxValue);
            var maxEvents = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_events", 100), 1, MaxEvents);
            var includeDetails = JsonUtil.ExtractBool(context.ArgsJson, "include_details", false);
            var maxStringChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_string_chars", 256), 16, MaxStringChars);
            var utilityType = ResolveFrameDebuggerUtilityType();
            if (utilityType == null)
            {
                return EventsUnavailable(startIndex, maxEvents, "Unity Frame Debugger utility type was not detected");
            }

            var enabledReadable = TryReadAnyStaticBool(utilityType, new string[] { "enabled", "isEnabled", "frameDebuggerEnabled" }, out var enabled, out _);
            if (enabledReadable && !enabled)
            {
                return EventsUnavailable(startIndex, maxEvents, "Frame Debugger is disabled; this read-only command does not enable it");
            }

            var countReadable = TryReadAnyStaticInt(utilityType, new string[] { "count", "eventCount", "eventsCount" }, out var eventCount, out _);
            var eventDataType = ResolveFrameDebuggerEventDataType();
            if (eventDataType == null)
            {
                return EventsUnavailable(startIndex, maxEvents, "Unity Frame Debugger event data type was not detected");
            }

            var method = ResolveGetFrameEventDataMethod(utilityType, eventDataType, out var eventDataByRef);
            if (method == null)
            {
                return EventsUnavailable(startIndex, maxEvents, "No known read-only Frame Debugger event data method was detected");
            }

            var upperBound = countReadable ? Math.Min(eventCount, startIndex + maxEvents) : startIndex + maxEvents;
            var eventsJson = new StringBuilder();
            var returned = 0;
            var skipped = 0;
            for (var eventIndex = startIndex; eventIndex < upperBound; eventIndex++)
            {
                var eventData = SafeCreate(eventDataType);
                if (eventData == null)
                {
                    skipped++;
                    continue;
                }

                if (!TryReadFrameEvent(method, eventDataByRef, eventIndex, eventData, out var populatedEventData))
                {
                    skipped++;
                    continue;
                }

                if (returned > 0)
                {
                    eventsJson.Append(',');
                }

                eventsJson.Append(FrameEventJson(eventDataType, populatedEventData, eventIndex, includeDetails, maxStringChars));
                returned++;
            }

            return CommandExecutionResult.Ok("{\"api_available\":true"
                + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                + ",\"source\":" + JsonUtil.String(utilityType.FullName + "." + method.Name)
                + ",\"event_data_type\":" + JsonUtil.String(eventDataType.FullName)
                + ",\"frame_debugger_enabled\":" + (enabledReadable ? JsonUtil.Bool(enabled) : "null")
                + ",\"event_count\":" + (countReadable ? eventCount.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"start_index\":" + startIndex
                + ",\"max_events\":" + maxEvents
                + ",\"returned_events\":" + returned
                + ",\"skipped_events\":" + skipped
                + ",\"truncated\":" + JsonUtil.Bool(countReadable && eventCount > upperBound)
                + ",\"include_details\":" + JsonUtil.Bool(includeDetails)
                + ",\"events\":[" + eventsJson + "]"
                + ",\"write_capabilities_exposed\":false"
                + ",\"state_changed\":false"
                + ",\"warnings\":[\"This command reads already-available Frame Debugger metadata only; it does not enable Frame Debugger or change the selected event index.\"]"
                + ",\"next_valid_actions\":[\"frame_debugger.status\",\"profiler.frame.summary\",\"profiler.marker.search\"]}");
        }

        static CommandExecutionResult EventsUnavailable(int startIndex, int maxEvents, string reason)
        {
            return CommandExecutionResult.Ok("{\"api_available\":false"
                + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                + ",\"start_index\":" + startIndex
                + ",\"max_events\":" + maxEvents
                + ",\"returned_events\":0"
                + ",\"events\":[]"
                + ",\"write_capabilities_exposed\":false"
                + ",\"state_changed\":false"
                + ",\"reason\":" + JsonUtil.String(reason) + "}");
        }

        internal static Type ResolveFrameDebuggerUtilityType(Func<string, Type> resolver = null)
        {
            // Preserve legacy preference; an available legacy type with an
            // unsupported member shape is not permission to widen discovery.
            return ResolveType("UnityEditorInternal.FrameDebuggerUtility", resolver)
                ?? ResolveType("UnityEditor.FrameDebuggerUtility", resolver)
                ?? ResolveType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility", resolver);
        }

        internal static Type ResolveFrameDebuggerEventDataType(Func<string, Type> resolver = null)
        {
            return ResolveType("UnityEditorInternal.FrameDebuggerEventData", resolver)
                ?? ResolveType("UnityEditor.FrameDebuggerEventData", resolver)
                ?? ResolveType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData", resolver);
        }

        internal static MethodInfo ResolveGetFrameEventDataMethod(Type utilityType, Type eventDataType, out bool byRef)
        {
            byRef = false;
            if (utilityType == null || eventDataType == null)
            {
                return null;
            }
            var methods = utilityType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (var index = 0; index < methods.Length; index++)
            {
                var method = methods[index];
                if (!string.Equals(method.Name, "GetFrameEventData", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType != typeof(int))
                {
                    continue;
                }

                var parameterType = parameters[1].ParameterType;
                if (parameterType == eventDataType)
                {
                    byRef = false;
                    return method;
                }

                if (parameterType.IsByRef && parameterType.GetElementType() == eventDataType)
                {
                    byRef = true;
                    return method;
                }
            }

            return null;
        }

        static bool TryReadFrameEvent(MethodInfo method, bool eventDataByRef, int eventIndex, object eventData, out object populatedEventData)
        {
            populatedEventData = eventData;
            try
            {
                var args = new object[] { eventIndex, eventData };
                var result = method.Invoke(null, args);
                if (eventDataByRef && args.Length > 1 && args[1] != null)
                {
                    populatedEventData = args[1];
                }

                return !(result is bool boolResult) || boolResult;
            }
            catch
            {
                populatedEventData = eventData;
                return false;
            }
        }

        static string FrameEventJson(Type eventDataType, object eventData, int eventIndex, bool includeDetails, int maxStringChars)
        {
            var builder = new StringBuilder();
            builder.Append("{\"event_index\":").Append(eventIndex);
            AppendKnownMember(builder, eventDataType, eventData, "name", "name", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "eventName", "name", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "passName", "pass_name", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "shaderName", "shader_name", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "meshName", "mesh_name", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "rtName", "render_target", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "drawCallIndex", "draw_call_index", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "passIndex", "pass_index", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "vertexCount", "vertex_count", maxStringChars);
            AppendKnownMember(builder, eventDataType, eventData, "indexCount", "index_count", maxStringChars);
            if (includeDetails)
            {
                builder.Append(",\"details\":{");
                AppendPrimitiveDetails(builder, eventDataType, eventData, maxStringChars);
                builder.Append('}');
            }

            builder.Append('}');
            return builder.ToString();
        }

        static void AppendKnownMember(StringBuilder builder, Type type, object instance, string memberName, string jsonName, int maxStringChars)
        {
            if (TryReadPrimitiveMember(type, instance, memberName, maxStringChars, out var json))
            {
                builder.Append(',').Append(JsonUtil.String(jsonName)).Append(':').Append(json);
            }
        }

        static void AppendPrimitiveDetails(StringBuilder builder, Type type, object instance, int maxStringChars)
        {
            var appended = 0;
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance);
            for (var index = 0; index < members.Length && appended < MaxDetailFieldsPerEvent; index++)
            {
                var member = members[index];
                if (!TryReadPrimitiveMember(type, instance, member.Name, maxStringChars, out var json))
                {
                    continue;
                }

                if (appended > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(member.Name)).Append(':').Append(json);
                appended++;
            }
        }

        static bool TryReadPrimitiveMember(Type type, object instance, string memberName, int maxStringChars, out string json)
        {
            json = string.Empty;
            object value = null;
            Type valueType = null;
            try
            {
                var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(instance, null);
                    valueType = property.PropertyType;
                }
                else
                {
                    var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (field == null)
                    {
                        return false;
                    }

                    value = field.GetValue(instance);
                    valueType = field.FieldType;
                }
            }
            catch
            {
                return false;
            }

            return PrimitiveValueJson(value, valueType, maxStringChars, out json);
        }

        static bool PrimitiveValueJson(object value, Type valueType, int maxStringChars, out string json)
        {
            json = string.Empty;
            if (value == null)
            {
                json = "null";
                return valueType == typeof(string) || (valueType != null && !valueType.IsValueType);
            }

            if (value is string stringValue)
            {
                json = JsonUtil.String(Bounded(stringValue, maxStringChars));
                return true;
            }

            if (value is bool boolValue)
            {
                json = JsonUtil.Bool(boolValue);
                return true;
            }

            if (value is int || value is long || value is short || value is byte)
            {
                json = Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (value is float floatValue)
            {
                json = NumberJson(floatValue);
                return true;
            }

            if (value is double doubleValue)
            {
                json = NumberJson(doubleValue);
                return true;
            }

            if (valueType != null && valueType.IsEnum)
            {
                json = JsonUtil.String(Bounded(Convert.ToString(value), maxStringChars));
                return true;
            }

            return false;
        }

        static string BoolStatusJson(bool readable, bool value, string source)
        {
            return "{\"readable\":" + JsonUtil.Bool(readable)
                + ",\"value\":" + (readable ? JsonUtil.Bool(value) : "null")
                + ",\"source\":" + JsonUtil.String(readable ? source : string.Empty) + "}";
        }

        static string IntStatusJson(bool readable, int value, string source)
        {
            return "{\"readable\":" + JsonUtil.Bool(readable)
                + ",\"value\":" + (readable ? value.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"source\":" + JsonUtil.String(readable ? source : string.Empty) + "}";
        }

        static bool TryReadAnyStaticBool(Type type, string[] names, out bool value, out string source)
        {
            foreach (var name in names)
            {
                if (TryReadStaticMember(type, name, out var rawValue, out source) && rawValue is bool boolValue)
                {
                    value = boolValue;
                    return true;
                }
            }

            value = false;
            source = string.Empty;
            return false;
        }

        static bool TryReadAnyStaticInt(Type type, string[] names, out int value, out string source)
        {
            foreach (var name in names)
            {
                if (TryReadStaticMember(type, name, out var rawValue, out source))
                {
                    try
                    {
                        value = Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
                        return true;
                    }
                    catch
                    {
                        value = 0;
                    }
                }
            }

            value = 0;
            source = string.Empty;
            return false;
        }

        static bool TryReadStaticMember(Type type, string name, out object value, out string source)
        {
            value = null;
            source = string.Empty;
            if (type == null)
            {
                return false;
            }

            try
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(null, null);
                    source = property.Name;
                    return true;
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null)
                {
                    value = field.GetValue(null);
                    source = field.Name;
                    return true;
                }
            }
            catch
            {
                value = null;
                source = string.Empty;
            }

            return false;
        }

        static Type ResolveType(string fullName, Func<string, Type> resolver = null)
        {
            // Internal metadata-only test seam. Public commands always retain
            // the existing loaded-assembly lookup and exact member gates.
            if (resolver != null)
            {
                try { return resolver(fullName); }
                catch { return null; }
            }
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // Ignore broken reflection surfaces and continue probing loaded assemblies.
                }
            }

            return null;
        }

        static object SafeCreate(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch
            {
                return null;
            }
        }

        static string NumberJson(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string Bounded(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength);
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }
}
