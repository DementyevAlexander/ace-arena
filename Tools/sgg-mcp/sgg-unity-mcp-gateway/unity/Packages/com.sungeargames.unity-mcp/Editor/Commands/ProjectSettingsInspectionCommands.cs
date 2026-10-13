using System;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ProjectSettingsInspectionCommands
    {
        const string SchemaJson = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"enum\":[\"AudioManager\",\"PhysicsManager\",\"NavMeshProjectSettings\",\"MemorySettings\",\"Physics2DSettings\",\"EditorSettings\",\"GraphicsSettings\",\"ShaderGraphSettings\",\"UnityConnectSettings\",\"VFXManager\",\"XRSettings\",\"PresetManager\",\"TagManager\",\"TimeManager\",\"VersionControlSettings\",\"InputManager\",\"PlayerSettings\",\"QualitySettings\",\"MultiplayerManager\"]},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"max_depth\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6},\"max_array_elements\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20},\"max_chars\":{\"type\":\"integer\",\"minimum\":256,\"maximum\":16384}},\"required\":[\"name\"],\"additionalProperties\":false}";
        const string OutputLimitJson = "{\"max_depth\":6,\"max_array_elements\":20,\"max_chars\":16384}";
        const int DefaultMaxDepth = 6;
        const int DefaultMaxArrayElements = 20;
        const int DefaultMaxChars = 16384;
        const int MaxPropertyPathLength = 512;
        const int MaxStringChars = 512;

        [McpCommand(
            "project.settings.inspect",
            Title = "Inspect project settings",
            Description = "Read bounded persisted serialized Unity ProjectSettings data by allowlisted settings name in Edit or Play Mode",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SchemaJson,
            Tags = new string[] { "project", "settings", "unity-ai-assistant" },
            Capability = "project_readonly",
            ResultShape = "object_details",
            OutputLimitJson = OutputLimitJson)]
        public static CommandExecutionResult ProjectSettingsInspect(McpCommandContext context)
        {
            var validation = RequireArgs(context.ArgsJson, out var name, out var propertyPath, out var maxDepth, out var maxArrayElements, out var maxChars);
            if (validation != null)
            {
                return validation;
            }

            UnityEngine.Object[] loadedSettings = null;
            try
            {
                var resolveResult = TryResolveSettingsAsset(name, out var assetPath, out var settingsAsset, out loadedSettings);
                if (resolveResult != null)
                {
                    return resolveResult;
                }

                return ProjectSettingsJson(name, assetPath, settingsAsset, propertyPath, maxDepth, maxArrayElements, maxChars);
            }
            finally
            {
                if (loadedSettings != null)
                {
                    DestroyLoadedSettings(loadedSettings);
                }
            }
        }

        static CommandExecutionResult RequireArgs(string argsJson, out string name, out string propertyPath, out int maxDepth, out int maxArrayElements, out int maxChars)
        {
            name = JsonUtil.ExtractString(argsJson, "name");
            propertyPath = JsonUtil.ExtractString(argsJson, "property_path");
            maxDepth = Clamp(JsonUtil.ExtractInt(argsJson, "max_depth", DefaultMaxDepth), 1, 6);
            maxArrayElements = Clamp(JsonUtil.ExtractInt(argsJson, "max_array_elements", DefaultMaxArrayElements), 1, 20);
            maxChars = Clamp(JsonUtil.ExtractInt(argsJson, "max_chars", DefaultMaxChars), 256, 16384);

            if (string.IsNullOrEmpty(name))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument name is required");
            }

            name = CanonicalSettingsName(name.Trim());
            if (string.IsNullOrEmpty(name))
            {
                return CommandExecutionResult.Error("invalid_field_value", "name must be an allowlisted Unity ProjectSettings name");
            }

            if (string.IsNullOrEmpty(propertyPath))
            {
                return null;
            }

            propertyPath = propertyPath.Trim();
            if (propertyPath.Length < 1 || propertyPath.Length > MaxPropertyPathLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "property_path must be 1..512 characters");
            }

            if (PropertyPathLooksUnsafe(propertyPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "property_path must be a SerializedProperty path returned by this command, not a URL or filesystem path");
            }

            return null;
        }

        static CommandExecutionResult TryResolveSettingsAsset(string name, out string assetPath, out UnityEngine.Object settingsAsset, out UnityEngine.Object[] loadedSettings)
        {
            assetPath = string.Empty;
            settingsAsset = null;
            loadedSettings = null;
            var candidates = SettingsAssetPaths(name);
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                UnityEngine.Object[] loaded;
                try
                {
                    loaded = InternalEditorUtility.LoadSerializedFileAndForget(candidate);
                }
                catch
                {
                    continue;
                }
                if (loaded == null)
                {
                    continue;
                }

                for (var loadedIndex = 0; loadedIndex < loaded.Length; loadedIndex++)
                {
                    if (loaded[loadedIndex] == null)
                    {
                        continue;
                    }

                    assetPath = candidate;
                    settingsAsset = loaded[loadedIndex];
                    loadedSettings = loaded;
                    return null;
                }

                DestroyLoadedSettings(loaded);
            }

            return CommandExecutionResult.Error("asset_not_found", "Project settings asset was not found for name");
        }

        static void DestroyLoadedSettings(UnityEngine.Object[] loadedSettings)
        {
            var loaded = loadedSettings ?? Array.Empty<UnityEngine.Object>();
            for (var index = 0; index < loaded.Length; index++)
            {
                if (loaded[index] == null)
                {
                    continue;
                }

                try
                {
                    UnityEngine.Object.DestroyImmediate(loaded[index]);
                }
                catch
                {
                }
            }
        }

        static CommandExecutionResult ProjectSettingsJson(string name, string assetPath, UnityEngine.Object settingsAsset, string propertyPath, int maxDepth, int maxArrayElements, int maxChars)
        {
            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(settingsAsset);
            }
            catch (Exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Project settings asset cannot be inspected with SerializedObject");
            }

            var dataResult = SerializedDataJson(serializedObject, propertyPath, maxDepth, maxArrayElements, maxChars, out var dataJson, out var truncated, out var propertyCount);
            if (dataResult != null)
            {
                return dataResult;
            }

            var type = settingsAsset.GetType();
            return CommandExecutionResult.Ok("{\"name\":" + JsonUtil.String(name)
                + ",\"settings_asset\":" + JsonUtil.String(assetPath)
                + ",\"asset_type\":" + JsonUtil.String(type.FullName ?? type.Name)
                + ",\"property_path\":" + JsonUtil.String(propertyPath)
                + ",\"property_count\":" + propertyCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"redaction_applied\":true"
                + ",\"value_source\":\"persisted_serialized_file\""
                + ",\"limits\":{\"max_depth\":" + maxDepth
                + ",\"max_array_elements\":" + maxArrayElements
                + ",\"max_chars\":" + maxChars + "}"
                + ",\"data\":" + dataJson
                + ",\"compatible_with\":\"Unity.GetProjectSettings\"}");
        }

        static CommandExecutionResult SerializedDataJson(SerializedObject serializedObject, string propertyPath, int maxDepth, int maxArrayElements, int maxChars, out string dataJson, out bool truncated, out int propertyCount)
        {
            dataJson = string.Empty;
            truncated = false;
            propertyCount = 0;

            var builder = new StringBuilder();
            builder.Append("{\"properties\":[");
            if (!string.IsNullOrEmpty(propertyPath))
            {
                var property = FindSerializedProperty(serializedObject, propertyPath);
                if (property == null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "property_path was not found on the project settings asset");
                }

                AppendSerializedPropertyTree(builder, property, property.depth, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount);
            }
            else
            {
                AppendSerializedObjectProperties(builder, serializedObject, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount);
            }

            builder.Append("]}");
            dataJson = builder.ToString();
            return null;
        }

        static SerializedProperty FindSerializedProperty(SerializedObject serializedObject, string propertyPath)
        {
            var property = serializedObject.FindProperty(propertyPath);
            if (property != null || propertyPath.IndexOf('/') < 0)
            {
                return property;
            }

            return serializedObject.FindProperty(propertyPath.Replace('/', '.'));
        }

        static void AppendSerializedObjectProperties(StringBuilder builder, SerializedObject serializedObject, int maxDepth, int maxArrayElements, int maxChars, ref bool truncated, ref int propertyCount)
        {
            var property = serializedObject.GetIterator();
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = AppendSerializedProperty(builder, property, property.depth, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount, out var stop);
                if (stop)
                {
                    break;
                }
            }
        }

        static void AppendSerializedPropertyTree(StringBuilder builder, SerializedProperty rootProperty, int baseDepth, int maxDepth, int maxArrayElements, int maxChars, ref bool truncated, ref int propertyCount)
        {
            var property = rootProperty.Copy();
            var endProperty = rootProperty.GetEndProperty();
            var enterChildren = true;
            while (!SerializedProperty.EqualContents(property, endProperty))
            {
                var relativeDepth = property.depth - baseDepth;
                enterChildren = AppendSerializedProperty(builder, property, relativeDepth, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount, out var stop);
                if (stop || !property.NextVisible(enterChildren))
                {
                    break;
                }
            }
        }

        static bool AppendSerializedProperty(StringBuilder builder, SerializedProperty property, int relativeDepth, int maxDepth, int maxArrayElements, int maxChars, ref bool truncated, ref int propertyCount, out bool stop)
        {
            stop = false;
            if (relativeDepth > maxDepth || IsArrayElementPastLimit(property.propertyPath, maxArrayElements))
            {
                truncated = true;
                return false;
            }

            if (relativeDepth >= maxDepth && property.hasVisibleChildren)
            {
                truncated = true;
            }

            if (property.isArray && property.propertyType != SerializedPropertyType.String && property.arraySize > maxArrayElements)
            {
                truncated = true;
            }

            var entryJson = SerializedPropertyEntryJson(property, relativeDepth);
            var separatorLength = propertyCount > 0 ? 1 : 0;
            if (builder.Length + separatorLength + entryJson.Length + 2 > maxChars)
            {
                truncated = true;
                stop = true;
                return false;
            }

            if (propertyCount > 0)
            {
                builder.Append(',');
            }

            builder.Append(entryJson);
            propertyCount++;
            return relativeDepth < maxDepth;
        }

        static string SerializedPropertyEntryJson(SerializedProperty property, int relativeDepth)
        {
            return "{\"path\":" + JsonUtil.String(property.propertyPath)
                + ",\"display_name\":" + JsonUtil.String(property.displayName)
                + ",\"type\":" + JsonUtil.String(property.propertyType.ToString())
                + ",\"depth\":" + relativeDepth
                + ",\"value\":" + SerializedPropertyValueJson(property) + "}";
        }

        static string SerializedPropertyValueJson(SerializedProperty property)
        {
            if (ShouldRedactProperty(property))
            {
                return JsonUtil.String("[redacted]");
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return JsonUtil.Bool(property.boolValue);
                case SerializedPropertyType.Float: return FloatJson(property.floatValue);
                case SerializedPropertyType.String: return JsonUtil.String(TruncateString(property.stringValue));
                case SerializedPropertyType.ObjectReference: return ObjectReferenceJson(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum: return JsonUtil.String(property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString(CultureInfo.InvariantCulture));
                case SerializedPropertyType.Vector2: return Vector2Json(property.vector2Value);
                case SerializedPropertyType.Vector3: return Vector3Json(property.vector3Value);
                case SerializedPropertyType.Vector4: return Vector4Json(property.vector4Value);
                case SerializedPropertyType.ArraySize: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character: return JsonUtil.String(((char)property.intValue).ToString());
                default: return "null";
            }
        }

        static bool ShouldRedactProperty(SerializedProperty property)
        {
            var path = property.propertyPath.ToLowerInvariant();
            if (path.IndexOf("password", StringComparison.Ordinal) >= 0 || path.IndexOf("secret", StringComparison.Ordinal) >= 0 || path.IndexOf("token", StringComparison.Ordinal) >= 0 || path.IndexOf("apikey", StringComparison.Ordinal) >= 0 || path.IndexOf("api_key", StringComparison.Ordinal) >= 0 || path.IndexOf("keystore", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            return property.propertyType == SerializedPropertyType.String && LooksLikeHostPath(property.stringValue);
        }

        static bool LooksLikeHostPath(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("\\\\", StringComparison.Ordinal) || (value.Length >= 3 && char.IsLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/'));
        }

        static string ObjectReferenceJson(UnityEngine.Object value)
        {
            if (value == null)
            {
                return "null";
            }

            var type = value.GetType();
            var assetPath = AssetDatabase.GetAssetPath(value);
            return "{\"name\":" + JsonUtil.String(value.name)
                + ",\"type\":" + JsonUtil.String(type.FullName ?? type.Name)
                + ",\"asset_path\":" + JsonUtil.String(SafeAssetPath(assetPath)) + "}";
        }

        static string SafeAssetPath(string assetPath)
        {
            return assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath.StartsWith("Packages/", StringComparison.Ordinal) ? assetPath : string.Empty;
        }

        static bool IsArrayElementPastLimit(string propertyPath, int maxArrayElements)
        {
            var searchFrom = 0;
            const string Marker = "Array.data[";
            while (searchFrom < propertyPath.Length)
            {
                var marker = propertyPath.IndexOf(Marker, searchFrom, StringComparison.Ordinal);
                if (marker < 0)
                {
                    return false;
                }

                var start = marker + Marker.Length;
                var end = propertyPath.IndexOf(']', start);
                if (end < 0)
                {
                    return false;
                }

                if (int.TryParse(propertyPath.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var arrayIndex) && arrayIndex >= maxArrayElements)
                {
                    return true;
                }

                searchFrom = end + 1;
            }

            return false;
        }

        static bool PropertyPathLooksUnsafe(string propertyPath)
        {
            if (propertyPath.IndexOf("://", StringComparison.Ordinal) >= 0 || propertyPath.IndexOf('\\') >= 0)
            {
                return true;
            }

            for (var index = 0; index < propertyPath.Length; index++)
            {
                var character = propertyPath[index];
                if (char.IsControl(character) || character == '"' || character == '{' || character == '}')
                {
                    return true;
                }
            }

            return false;
        }

        static string[] SettingsAssetPaths(string name)
        {
            if (name == "PhysicsManager") return new[] { "ProjectSettings/DynamicsManager.asset", "ProjectSettings/PhysicsManager.asset" };
            if (name == "NavMeshProjectSettings") return new[] { "ProjectSettings/NavMeshAreas.asset", "ProjectSettings/NavMeshProjectSettings.asset" };
            if (name == "PlayerSettings") return new[] { "ProjectSettings/ProjectSettings.asset", "ProjectSettings/PlayerSettings.asset" };
            return new[] { "ProjectSettings/" + name + ".asset" };
        }

        static string CanonicalSettingsName(string value)
        {
            if (string.Equals(value, "AudioManager", StringComparison.OrdinalIgnoreCase)) return "AudioManager";
            if (string.Equals(value, "PhysicsManager", StringComparison.OrdinalIgnoreCase)) return "PhysicsManager";
            if (string.Equals(value, "NavMeshProjectSettings", StringComparison.OrdinalIgnoreCase)) return "NavMeshProjectSettings";
            if (string.Equals(value, "MemorySettings", StringComparison.OrdinalIgnoreCase)) return "MemorySettings";
            if (string.Equals(value, "Physics2DSettings", StringComparison.OrdinalIgnoreCase)) return "Physics2DSettings";
            if (string.Equals(value, "EditorSettings", StringComparison.OrdinalIgnoreCase)) return "EditorSettings";
            if (string.Equals(value, "GraphicsSettings", StringComparison.OrdinalIgnoreCase)) return "GraphicsSettings";
            if (string.Equals(value, "ShaderGraphSettings", StringComparison.OrdinalIgnoreCase)) return "ShaderGraphSettings";
            if (string.Equals(value, "UnityConnectSettings", StringComparison.OrdinalIgnoreCase)) return "UnityConnectSettings";
            if (string.Equals(value, "VFXManager", StringComparison.OrdinalIgnoreCase)) return "VFXManager";
            if (string.Equals(value, "XRSettings", StringComparison.OrdinalIgnoreCase)) return "XRSettings";
            if (string.Equals(value, "PresetManager", StringComparison.OrdinalIgnoreCase)) return "PresetManager";
            if (string.Equals(value, "TagManager", StringComparison.OrdinalIgnoreCase)) return "TagManager";
            if (string.Equals(value, "TimeManager", StringComparison.OrdinalIgnoreCase)) return "TimeManager";
            if (string.Equals(value, "VersionControlSettings", StringComparison.OrdinalIgnoreCase)) return "VersionControlSettings";
            if (string.Equals(value, "InputManager", StringComparison.OrdinalIgnoreCase)) return "InputManager";
            if (string.Equals(value, "PlayerSettings", StringComparison.OrdinalIgnoreCase)) return "PlayerSettings";
            if (string.Equals(value, "QualitySettings", StringComparison.OrdinalIgnoreCase)) return "QualitySettings";
            if (string.Equals(value, "MultiplayerManager", StringComparison.OrdinalIgnoreCase)) return "MultiplayerManager";
            return string.Empty;
        }

        static string Vector2Json(Vector2 value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + "}";
        }

        static string Vector3Json(Vector3 value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + ",\"z\":" + FloatJson(value.z) + "}";
        }

        static string Vector4Json(Vector4 value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + ",\"z\":" + FloatJson(value.z) + ",\"w\":" + FloatJson(value.w) + "}";
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? JsonUtil.String(value.ToString(CultureInfo.InvariantCulture)) : value.ToString(CultureInfo.InvariantCulture);
        }

        static string TruncateString(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxStringChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, MaxStringChars);
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
