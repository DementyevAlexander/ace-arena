using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class UnityApiReflectionCommands
    {
        const string FeatureFlag = "unity_api_reflection";
        const string LoadedAssembliesSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string SearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"minLength\":2,\"maxLength\":128},\"kind\":{\"type\":\"string\",\"enum\":[\"type\",\"member\",\"all\"]},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"query\"],\"additionalProperties\":false}";
        const string TypeSchemaJson = "{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"include_members\":{\"type\":\"boolean\"},\"max_members\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"type\"],\"additionalProperties\":false}";
        const string MemberSchemaJson = "{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"member\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"required\":[\"type\",\"member\"],\"additionalProperties\":false}";
        const string ListOutputLimitJson = "{\"max_results\":200}";
        const string MemberOutputLimitJson = "{\"max_results\":100}";
        const BindingFlags PublicMemberFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        [McpCommand(
            "unity.api.loaded_assemblies",
            Title = "Unity API loaded assemblies",
            Description = "List bounded loaded Unity Editor assemblies for local Unity API verification",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = LoadedAssembliesSchemaJson,
            Tags = new string[] { "unity-api", "api" },
            Capability = "unity_api_reflection",
            ResultShape = "bounded_list",
            OutputLimitJson = ListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult LoadedAssemblies(McpCommandContext context)
        {
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 200);
            var assemblies = EditorAssemblyDiscovery.LoadedAssemblies();
            Array.Sort(assemblies, CompareAssemblies);

            var builder = new StringBuilder();
            var count = 0;
            var matched = 0;
            foreach (var assembly in assemblies)
            {
                var name = assembly.GetName();
                var assemblyName = name.Name ?? string.Empty;
                if (!Contains(assemblyName, query))
                {
                    continue;
                }

                matched++;
                if (count >= maxResults)
                {
                    continue;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"assembly\":").Append(JsonUtil.String(assemblyName));
                builder.Append(",\"version\":").Append(JsonUtil.String(name.Version == null ? string.Empty : name.Version.ToString()));
                builder.Append(",\"type_count\":").Append(SafeGetTypes(assembly).Count);
                builder.Append('}');
                count++;
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"max_results\":" + maxResults
                + ",\"matched\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(matched > count)
                + ",\"results\":[" + builder + "]}");
        }

        [McpCommand(
            "unity.api.reflect.search",
            Title = "Unity API reflection search",
            Description = "Search public types and members in loaded Unity Editor assemblies without network access",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SearchSchemaJson,
            Tags = new string[] { "unity-api", "api" },
            Capability = "unity_api_reflection",
            ResultShape = "bounded_list",
            OutputLimitJson = ListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Search(McpCommandContext context)
        {
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            if (query.Trim().Length < 2)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query must be at least 2 characters");
            }

            var kind = JsonUtil.ExtractString(context.ArgsJson, "kind");
            if (string.IsNullOrEmpty(kind))
            {
                kind = "all";
            }

            if (kind != "type" && kind != "member" && kind != "all")
            {
                return CommandExecutionResult.Error("invalid_field_value", "Argument kind must be type, member, or all");
            }

            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 200);
            var results = new List<string>();
            var matched = 0;
            foreach (var type in LoadedTypesSorted())
            {
                if ((kind == "type" || kind == "all") && TypeMatches(type, query))
                {
                    matched++;
                    if (results.Count < maxResults)
                    {
                        results.Add(TypeSearchResult(type));
                    }
                }

                if (kind != "member" && kind != "all")
                {
                    continue;
                }

                foreach (var member in PublicMembersSorted(type))
                {
                    if (!Contains(member.Name, query))
                    {
                        continue;
                    }

                    matched++;
                    if (results.Count < maxResults)
                    {
                        results.Add(MemberResult(type, member));
                    }
                }
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"kind\":" + JsonUtil.String(kind)
                + ",\"max_results\":" + maxResults
                + ",\"matched\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(matched > results.Count)
                + ",\"results\":[" + string.Join(",", results.ToArray()) + "]}");
        }

        [McpCommand(
            "unity.api.reflect.type",
            Title = "Unity API reflection type",
            Description = "Describe one public loaded type and optionally bounded public members",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TypeSchemaJson,
            Tags = new string[] { "unity-api", "api" },
            Capability = "unity_api_reflection",
            ResultShape = "object_details",
            OutputLimitJson = ListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TypeDetails(McpCommandContext context)
        {
            var typeName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "type"), 256);
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument type is required");
            }

            var type = ResolvePublicType(typeName);
            if (type == null)
            {
                return CommandExecutionResult.Error("type_not_found", "Type was not found in loaded public types");
            }

            var includeMembers = JsonUtil.ExtractBool(context.ArgsJson, "include_members", false);
            var maxMembers = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_members", 50), 1, 200);
            var json = new StringBuilder();
            json.Append("{\"type\":").Append(TypeJson(type));
            if (includeMembers)
            {
                json.Append(",\"members\":[");
                AppendMembers(json, type, string.Empty, maxMembers, out var matched, out var included);
                json.Append(']');
                json.Append(",\"member_count\":").Append(matched);
                json.Append(",\"members_truncated\":").Append(JsonUtil.Bool(matched > included));
            }

            json.Append('}');
            return CommandExecutionResult.Ok(json.ToString());
        }

        [McpCommand(
            "unity.api.reflect.member",
            Title = "Unity API reflection member",
            Description = "Describe bounded public members on one loaded public type",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = MemberSchemaJson,
            Tags = new string[] { "unity-api", "api" },
            Capability = "unity_api_reflection",
            ResultShape = "bounded_list",
            OutputLimitJson = MemberOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MemberDetails(McpCommandContext context)
        {
            var typeName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "type"), 256);
            var memberName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "member"), 128);
            if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(memberName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Arguments type and member are required");
            }

            var type = ResolvePublicType(typeName);
            if (type == null)
            {
                return CommandExecutionResult.Error("type_not_found", "Type was not found in loaded public types");
            }

            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 20), 1, 100);
            var json = new StringBuilder();
            AppendMembers(json, type, memberName, maxResults, out var matched, out var included);
            if (matched == 0)
            {
                return CommandExecutionResult.Error("member_not_found", "Member was not found on the loaded public type");
            }

            return CommandExecutionResult.Ok("{\"type\":" + TypeJson(type)
                + ",\"member\":" + JsonUtil.String(memberName)
                + ",\"max_results\":" + maxResults
                + ",\"matched\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(matched > included)
                + ",\"results\":[" + json + "]}");
        }

        static void AppendMembers(StringBuilder builder, Type type, string query, int maxResults, out int matched, out int included)
        {
            matched = 0;
            included = 0;
            foreach (var member in PublicMembersSorted(type))
            {
                if (!string.IsNullOrEmpty(query) && !string.Equals(member.Name, query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched++;
                if (included >= maxResults)
                {
                    continue;
                }

                if (included > 0)
                {
                    builder.Append(',');
                }

                builder.Append(MemberResult(type, member));
                included++;
            }
        }

        static string TypeSearchResult(Type type)
        {
            return "{\"kind\":\"type\",\"assembly\":" + JsonUtil.String(AssemblyName(type.Assembly))
                + ",\"type\":" + JsonUtil.String(Bounded(type.FullName ?? type.Name, 256))
                + ",\"name\":" + JsonUtil.String(Bounded(type.Name, 128))
                + ",\"namespace\":" + JsonUtil.String(Bounded(type.Namespace ?? string.Empty, 160)) + "}";
        }

        static string TypeJson(Type type)
        {
            return "{\"assembly\":" + JsonUtil.String(AssemblyName(type.Assembly))
                + ",\"full_name\":" + JsonUtil.String(Bounded(type.FullName ?? type.Name, 256))
                + ",\"name\":" + JsonUtil.String(Bounded(type.Name, 128))
                + ",\"namespace\":" + JsonUtil.String(Bounded(type.Namespace ?? string.Empty, 160))
                + ",\"kind\":" + JsonUtil.String(TypeKind(type))
                + ",\"base_type\":" + JsonUtil.String(type.BaseType == null ? string.Empty : Bounded(type.BaseType.FullName ?? type.BaseType.Name, 256))
                + ",\"is_public\":" + JsonUtil.Bool(IsPublic(type)) + "}";
        }

        static string MemberResult(Type type, MemberInfo member)
        {
            return "{\"kind\":\"member\",\"assembly\":" + JsonUtil.String(AssemblyName(type.Assembly))
                + ",\"type\":" + JsonUtil.String(Bounded(type.FullName ?? type.Name, 256))
                + ",\"member\":" + JsonUtil.String(Bounded(member.Name, 128))
                + ",\"member_kind\":" + JsonUtil.String(member.MemberType.ToString().ToLowerInvariant())
                + ",\"static\":" + JsonUtil.Bool(IsStatic(member))
                + ",\"visibility\":" + JsonUtil.String(Visibility(member))
                + ",\"signature\":" + JsonUtil.String(Bounded(Signature(member), 360)) + "}";
        }

        static string Signature(MemberInfo member)
        {
            if (member is MethodInfo method)
            {
                return TypeName(method.ReturnType) + " " + method.Name + "(" + Parameters(method.GetParameters()) + ")";
            }

            if (member is ConstructorInfo constructor)
            {
                return constructor.DeclaringType == null ? member.Name : constructor.DeclaringType.Name + "(" + Parameters(constructor.GetParameters()) + ")";
            }

            if (member is PropertyInfo property)
            {
                return TypeName(property.PropertyType) + " " + property.Name;
            }

            if (member is FieldInfo field)
            {
                return TypeName(field.FieldType) + " " + field.Name;
            }

            if (member is EventInfo eventInfo)
            {
                return TypeName(eventInfo.EventHandlerType) + " " + eventInfo.Name;
            }

            return member.Name;
        }

        static string Parameters(ParameterInfo[] parameters)
        {
            var values = new List<string>();
            for (var index = 0; index < parameters.Length && index < 16; index++)
            {
                values.Add(TypeName(parameters[index].ParameterType) + " " + parameters[index].Name);
            }

            if (parameters.Length > 16)
            {
                values.Add("...");
            }

            return string.Join(", ", values.ToArray());
        }

        static string TypeName(Type type)
        {
            if (type == null)
            {
                return string.Empty;
            }

            if (!type.IsGenericType)
            {
                return Bounded(type.FullName ?? type.Name, 160);
            }

            var name = type.FullName ?? type.Name;
            var tick = name.IndexOf('`');
            return Bounded(tick >= 0 ? name.Substring(0, tick) : name, 160);
        }

        static List<Type> LoadedTypesSorted()
        {
            var types = new List<Type>();
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type != null && IsPublic(type))
                    {
                        types.Add(type);
                    }
                }
            }

            types.Sort((left, right) => string.Compare(left.FullName ?? left.Name, right.FullName ?? right.Name, StringComparison.Ordinal));
            return types;
        }

        static Type ResolvePublicType(string typeName)
        {
            foreach (var type in LoadedTypesSorted())
            {
                if (string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    return type;
                }
            }

            return null;
        }

        static List<MemberInfo> PublicMembersSorted(Type type)
        {
            var members = new List<MemberInfo>();
            foreach (var member in type.GetMembers(PublicMemberFlags))
            {
                if (member.MemberType == MemberTypes.Method && ((MethodInfo)member).IsSpecialName)
                {
                    continue;
                }

                members.Add(member);
            }

            members.Sort((left, right) => string.Compare(left.MemberType + ":" + left.Name + ":" + Signature(left), right.MemberType + ":" + right.Name + ":" + Signature(right), StringComparison.Ordinal));
            return members;
        }

        static List<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return new List<Type>(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                var types = new List<Type>();
                foreach (var type in exception.Types)
                {
                    if (type != null)
                    {
                        types.Add(type);
                    }
                }

                return types;
            }
        }

        static bool TypeMatches(Type type, string query)
        {
            return Contains(type.FullName ?? string.Empty, query) || Contains(type.Name, query) || Contains(type.Namespace ?? string.Empty, query);
        }

        static bool Contains(string value, string query)
        {
            return string.IsNullOrEmpty(query) || (!string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsPublic(Type type)
        {
            return type.IsPublic || type.IsNestedPublic;
        }

        static bool IsStatic(MemberInfo member)
        {
            if (member is MethodBase method)
            {
                return method.IsStatic;
            }

            if (member is FieldInfo field)
            {
                return field.IsStatic;
            }

            if (member is PropertyInfo property)
            {
                var accessor = property.GetGetMethod(false) ?? property.GetSetMethod(false);
                return accessor != null && accessor.IsStatic;
            }

            if (member is EventInfo eventInfo)
            {
                var add = eventInfo.GetAddMethod(false);
                return add != null && add.IsStatic;
            }

            return false;
        }

        static string Visibility(MemberInfo member)
        {
            if (member is MethodBase method)
            {
                return method.IsPublic ? "public" : "nonpublic";
            }

            if (member is FieldInfo field)
            {
                return field.IsPublic ? "public" : "nonpublic";
            }

            if (member is PropertyInfo property)
            {
                return property.GetGetMethod(false) != null || property.GetSetMethod(false) != null ? "public" : "nonpublic";
            }

            if (member is EventInfo eventInfo)
            {
                return eventInfo.GetAddMethod(false) != null ? "public" : "nonpublic";
            }

            return "public";
        }

        static string TypeKind(Type type)
        {
            if (type.IsEnum)
            {
                return "enum";
            }

            if (type.IsInterface)
            {
                return "interface";
            }

            if (type.IsValueType)
            {
                return "struct";
            }

            return "class";
        }

        static int CompareAssemblies(Assembly left, Assembly right)
        {
            return string.Compare(AssemblyName(left), AssemblyName(right), StringComparison.Ordinal);
        }

        static string AssemblyName(Assembly assembly)
        {
            return assembly.GetName().Name ?? string.Empty;
        }

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length > maxChars ? value.Substring(0, maxChars) : value;
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
