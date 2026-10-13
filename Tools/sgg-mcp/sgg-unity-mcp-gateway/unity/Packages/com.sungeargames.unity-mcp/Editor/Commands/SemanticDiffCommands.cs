using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class SemanticDiffCommands
    {
        const string FeatureFlag = "semantic_unity_diff";
        const string DiffSchemaJson = "{\"type\":\"object\",\"properties\":{\"left_path\":{\"type\":\"string\"},\"right_path\":{\"type\":\"string\"},\"max_changes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}},\"required\":[\"left_path\",\"right_path\"],\"additionalProperties\":false}";
        const string InspectChangeSchemaJson = "{\"type\":\"object\",\"properties\":{\"left_path\":{\"type\":\"string\"},\"right_path\":{\"type\":\"string\"},\"local_id\":{\"type\":\"string\"},\"max_field_changes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"left_path\",\"right_path\",\"local_id\"],\"additionalProperties\":false}";
        const string MergeSchemaJson = "{\"type\":\"object\",\"properties\":{\"base_path\":{\"type\":\"string\"},\"ours_path\":{\"type\":\"string\"},\"theirs_path\":{\"type\":\"string\"},\"max_conflicts\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}},\"required\":[\"base_path\",\"ours_path\",\"theirs_path\"],\"additionalProperties\":false}";
        const string MergePlanSchemaJson = "{\"type\":\"object\",\"properties\":{\"base_path\":{\"type\":\"string\"},\"ours_path\":{\"type\":\"string\"},\"theirs_path\":{\"type\":\"string\"},\"max_actions\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"required\":[\"base_path\",\"ours_path\",\"theirs_path\"],\"additionalProperties\":false}";
        const string MergeApplySchemaJson = "{\"type\":\"object\",\"properties\":{\"base_path\":{\"type\":\"string\"},\"ours_path\":{\"type\":\"string\"},\"theirs_path\":{\"type\":\"string\"},\"output_path\":{\"type\":\"string\"},\"overwrite\":{\"type\":\"boolean\"}},\"required\":[\"base_path\",\"ours_path\",\"theirs_path\",\"output_path\"],\"additionalProperties\":false}";
        const string OutputLimitJson = "{\"max_changes\":500,\"max_field_changes_per_object\":20,\"max_merge_actions\":1000}";

        [McpCommand(
            "diff.unity_yaml.semantic",
            Title = "Semantic Unity YAML diff",
            Description = "Compare Unity YAML objects by local id, type and bounded field summaries",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = DiffSchemaJson,
            Tags = new string[] { "diff", "unity-yaml" },
            Capability = "semantic_unity_diff",
            ResultShape = "bounded_list",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SemanticDiff(McpCommandContext context)
        {
            var inputs = RequireDiffInputs(context.ArgsJson, out var leftPath, out var rightPath, out var maxChanges);
            if (inputs != null)
            {
                return inputs;
            }

            var left = ParseObjects(leftPath);
            var right = ParseObjects(rightPath);
            var changes = BuildSemanticChanges(left, right, maxChanges, out var truncated);
            return CommandExecutionResult.Ok("{\"left_path\":" + JsonUtil.String(leftPath)
                + ",\"right_path\":" + JsonUtil.String(rightPath)
                + ",\"left_object_count\":" + left.Count
                + ",\"right_object_count\":" + right.Count
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"changes\":[" + changes + "]}");
        }

        [McpCommand(
            "diff.unity_yaml.preview",
            Title = "Unity YAML text diff preview",
            Description = "Read a bounded text-level preview for two Unity YAML files",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = DiffSchemaJson,
            Tags = new string[] { "diff", "unity-yaml" },
            Capability = "semantic_unity_diff",
            ResultShape = "bounded_list",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TextDiffPreview(McpCommandContext context)
        {
            var inputs = RequireDiffInputs(context.ArgsJson, out var leftPath, out var rightPath, out var maxChanges);
            if (inputs != null)
            {
                return inputs;
            }

            var leftLines = ReadAllLines(leftPath);
            var rightLines = ReadAllLines(rightPath);
            var preview = BuildTextPreview(leftLines, rightLines, maxChanges, out var truncated);
            return CommandExecutionResult.Ok("{\"left_path\":" + JsonUtil.String(leftPath)
                + ",\"right_path\":" + JsonUtil.String(rightPath)
                + ",\"left_line_count\":" + leftLines.Length
                + ",\"right_line_count\":" + rightLines.Length
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"changes\":[" + preview + "]}");
        }

        [McpCommand(
            "diff.unity_yaml.inspect_change",
            Title = "Inspect Unity YAML change",
            Description = "Inspect one changed Unity YAML object by local id",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = InspectChangeSchemaJson,
            Tags = new string[] { "diff", "unity-yaml" },
            Capability = "semantic_unity_diff",
            ResultShape = "object_details",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult InspectChange(McpCommandContext context)
        {
            var inputs = RequireDiffInputs(context.ArgsJson, out var leftPath, out var rightPath, out _);
            if (inputs != null)
            {
                return inputs;
            }

            var localId = JsonUtil.ExtractString(context.ArgsJson, "local_id");
            if (string.IsNullOrEmpty(localId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument local_id is required");
            }

            var left = ParseObjects(leftPath);
            var right = ParseObjects(rightPath);
            left.TryGetValue(localId, out var leftObject);
            right.TryGetValue(localId, out var rightObject);
            if (leftObject == null && rightObject == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Object local_id was not found");
            }

            var current = rightObject ?? leftObject;
            var maxFieldChanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_field_changes", 50), 1, 200);
            var kind = leftObject == null ? "added" : rightObject == null ? "removed" : leftObject.Hash == rightObject.Hash ? "unchanged" : "changed";
            var builder = new StringBuilder();
            builder.Append("{\"left_path\":").Append(JsonUtil.String(leftPath));
            builder.Append(",\"right_path\":").Append(JsonUtil.String(rightPath));
            builder.Append(",\"kind\":").Append(JsonUtil.String(kind));
            AppendObjectIdentity(builder, current, true);
            if (leftObject != null && rightObject != null)
            {
                builder.Append(",\"field_changes\":").Append('[').Append(BuildFieldChanges(leftObject, rightObject, maxFieldChanges, out var truncated)).Append(']');
                builder.Append(",\"field_changes_truncated\":").Append(JsonUtil.Bool(truncated));
            }

            builder.Append('}');
            return CommandExecutionResult.Ok(builder.ToString());
        }

        [McpCommand(
            "merge.unity_yaml.preview",
            Title = "Unity YAML merge preview",
            Description = "Preview object-level three-way Unity YAML merge conflicts without writing files",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = MergeSchemaJson,
            Tags = new string[] { "diff", "merge", "unity-yaml" },
            Capability = "semantic_unity_diff",
            ResultShape = "bounded_list",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MergePreview(McpCommandContext context)
        {
            var inputs = RequireMergeInputs(context.ArgsJson, out var basePath, out var oursPath, out var theirsPath, out var maxConflicts);
            if (inputs != null)
            {
                return inputs;
            }

            var baseObjects = ParseObjects(basePath);
            var oursObjects = ParseObjects(oursPath);
            var theirsObjects = ParseObjects(theirsPath);
            var conflicts = BuildMergeConflicts(baseObjects, oursObjects, theirsObjects, maxConflicts, out var truncated);
            return CommandExecutionResult.Ok("{\"base_path\":" + JsonUtil.String(basePath)
                + ",\"ours_path\":" + JsonUtil.String(oursPath)
                + ",\"theirs_path\":" + JsonUtil.String(theirsPath)
                + ",\"can_auto_merge\":" + JsonUtil.Bool(conflicts.Length == 0 && !truncated)
                + ",\"merged_output_supported\":false"
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"conflicts\":[" + conflicts + "]}");
        }

        [McpCommand(
            "merge.unity_yaml.plan",
            Title = "Unity YAML merge plan",
            Description = "Build a bounded object-level Unity YAML merge plan without writing files",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = MergePlanSchemaJson,
            Tags = new string[] { "diff", "merge", "unity-yaml" },
            Capability = "semantic_unity_diff",
            ResultShape = "bounded_list",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MergePlan(McpCommandContext context)
        {
            var inputs = RequireMergeInputs(context.ArgsJson, out var basePath, out var oursPath, out var theirsPath, out _);
            if (inputs != null)
            {
                return inputs;
            }

            var maxActions = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_actions", 200), 1, 1000);
            var baseObjects = ParseObjects(basePath);
            var oursObjects = ParseObjects(oursPath);
            var theirsObjects = ParseObjects(theirsPath);
            var actions = BuildMergePlan(baseObjects, oursObjects, theirsObjects, maxActions, out var truncated, out var conflictCount);
            return CommandExecutionResult.Ok("{\"base_path\":" + JsonUtil.String(basePath)
                + ",\"ours_path\":" + JsonUtil.String(oursPath)
                + ",\"theirs_path\":" + JsonUtil.String(theirsPath)
                + ",\"can_auto_merge\":" + JsonUtil.Bool(conflictCount == 0 && !truncated)
                + ",\"conflict_count\":" + conflictCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"actions\":[" + actions + "]}");
        }

        [McpCommand(
            "merge.unity_yaml.apply",
            Title = "Apply Unity YAML merge",
            Description = "Conservatively apply a no-conflict Unity YAML merge to an output asset path",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = MergeApplySchemaJson,
            Tags = new string[] { "diff", "merge", "unity-yaml" },
            Capability = "semantic_unity_diff",
            ResultShape = "object_details",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MergeApply(McpCommandContext context)
        {
            var inputs = RequireMergeInputs(context.ArgsJson, out var basePath, out var oursPath, out var theirsPath, out _);
            if (inputs != null)
            {
                return inputs;
            }

            var outputResult = RequireUnityYamlPathForWrite(context.ArgsJson, "output_path", out var outputPath);
            if (outputResult != null)
            {
                return outputResult;
            }

            var baseObjects = ParseObjects(basePath);
            var oursObjects = ParseObjects(oursPath);
            var theirsObjects = ParseObjects(theirsPath);
            var conflicts = BuildMergeConflicts(baseObjects, oursObjects, theirsObjects, 1, out var truncated);
            if (conflicts.Length > 0 || truncated)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Merge has object-level conflicts; run merge.unity_yaml.preview before applying");
            }

            var sourcePath = ConservativeMergeSource(basePath, oursPath, theirsPath);
            if (string.IsNullOrEmpty(sourcePath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "This apply slice only writes when one side is unchanged or both sides are identical");
            }

            var overwrite = JsonUtil.ExtractBool(context.ArgsJson, "overwrite", false);
            var outputFullPath = AbsolutePath(outputPath);
            if (File.Exists(outputFullPath) && !overwrite)
            {
                return CommandExecutionResult.Error("invalid_field_value", "output_path already exists; pass overwrite=true to replace it");
            }

            var directory = Path.GetDirectoryName(outputFullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(AbsolutePath(sourcePath), outputFullPath, overwrite);
            AssetDatabase.ImportAsset(outputPath);
            return CommandExecutionResult.Ok("{\"applied\":true"
                + ",\"base_path\":" + JsonUtil.String(basePath)
                + ",\"ours_path\":" + JsonUtil.String(oursPath)
                + ",\"theirs_path\":" + JsonUtil.String(theirsPath)
                + ",\"source_path\":" + JsonUtil.String(sourcePath)
                + ",\"output_path\":" + JsonUtil.String(outputPath)
                + ",\"overwrite\":" + JsonUtil.Bool(overwrite) + "}");
        }

        static CommandExecutionResult RequireDiffInputs(string argsJson, out string leftPath, out string rightPath, out int maxChanges)
        {
            leftPath = string.Empty;
            rightPath = string.Empty;
            maxChanges = Clamp(JsonUtil.ExtractInt(argsJson, "max_changes", 100), 1, 500);
            var leftResult = RequireUnityYamlPath(argsJson, "left_path", out leftPath);
            if (leftResult != null)
            {
                return leftResult;
            }

            return RequireUnityYamlPath(argsJson, "right_path", out rightPath);
        }

        static CommandExecutionResult RequireMergeInputs(string argsJson, out string basePath, out string oursPath, out string theirsPath, out int maxConflicts)
        {
            basePath = string.Empty;
            oursPath = string.Empty;
            theirsPath = string.Empty;
            maxConflicts = Clamp(JsonUtil.ExtractInt(argsJson, "max_conflicts", 100), 1, 500);
            var baseResult = RequireUnityYamlPath(argsJson, "base_path", out basePath);
            if (baseResult != null)
            {
                return baseResult;
            }

            var oursResult = RequireUnityYamlPath(argsJson, "ours_path", out oursPath);
            if (oursResult != null)
            {
                return oursResult;
            }

            return RequireUnityYamlPath(argsJson, "theirs_path", out theirsPath);
        }

        static CommandExecutionResult RequireUnityYamlPath(string argsJson, string field, out string assetPath)
        {
            assetPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!IsSupportedUnityYaml(assetPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be .unity, .prefab, .mat or .asset");
            }

            if (!File.Exists(AbsolutePath(assetPath)))
            {
                return CommandExecutionResult.Error("asset_not_found", "Asset path was not found");
            }

            return null;
        }

        static CommandExecutionResult RequireUnityYamlPathForWrite(string argsJson, string field, out string assetPath)
        {
            assetPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!IsSupportedUnityYaml(assetPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be .unity, .prefab, .mat or .asset");
            }

            return null;
        }

        static Dictionary<string, YamlObject> ParseObjects(string assetPath)
        {
            var lines = ReadAllLines(assetPath);
            var objects = new Dictionary<string, YamlObject>(StringComparer.Ordinal);
            YamlObject current = null;
            var block = new StringBuilder();
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                {
                    StoreObject(objects, current, block, index);
                    current = ParseHeader(line, index + 1);
                    block.Length = 0;
                }

                if (current == null)
                {
                    continue;
                }

                block.AppendLine(line);
                if (current.Name.Length == 0 && line.TrimStart().StartsWith("m_Name:", StringComparison.Ordinal))
                {
                    current.Name = line.Substring(line.IndexOf(':') + 1).Trim();
                }
            }

            StoreObject(objects, current, block, lines.Length);
            return objects;
        }

        static YamlObject ParseHeader(string line, int startLine)
        {
            var classStart = "--- !u!".Length;
            var ampersand = line.IndexOf('&');
            var classId = ampersand > classStart ? line.Substring(classStart, ampersand - classStart).Trim() : string.Empty;
            var localId = ampersand >= 0 ? line.Substring(ampersand + 1).Trim() : "line-" + startLine;
            return new YamlObject
            {
                LocalId = localId,
                ClassId = classId,
                Type = ClassName(classId),
                StartLine = startLine,
            };
        }

        static void StoreObject(Dictionary<string, YamlObject> objects, YamlObject current, StringBuilder block, int endLine)
        {
            if (current == null)
            {
                return;
            }

            current.EndLine = endLine;
            var blockText = block.ToString();
            current.Hash = StableHash(blockText);
            current.Fields = ParseScalarFields(blockText);
            objects[current.LocalId] = current;
        }

        static Dictionary<string, string> ParseScalarFields(string blockText)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = new string[32];
            var lines = blockText.Split('\n');
            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r');
                if (line.Length == 0)
                {
                    continue;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("---", StringComparison.Ordinal) || trimmed.StartsWith("-", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = trimmed.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = trimmed.Substring(0, separator).Trim();
                var value = trimmed.Substring(separator + 1).Trim();
                var depth = Math.Min((line.Length - trimmed.Length) / 2, path.Length - 1);
                path[depth] = key;
                for (var index = depth + 1; index < path.Length; index++)
                {
                    path[index] = null;
                }

                if (value.Length == 0 || value == "[]" || value == "{}")
                {
                    continue;
                }

                fields[FieldPath(path, depth)] = value.Length > 240 ? value.Substring(0, 240) : value;
            }

            return fields;
        }

        static string FieldPath(string[] path, int depth)
        {
            var builder = new StringBuilder();
            for (var index = 0; index <= depth; index++)
            {
                if (string.IsNullOrEmpty(path[index]))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(path[index]);
            }

            return builder.ToString();
        }

        static string BuildSemanticChanges(Dictionary<string, YamlObject> left, Dictionary<string, YamlObject> right, int maxChanges, out bool truncated)
        {
            var builder = new StringBuilder();
            var count = 0;
            truncated = false;
            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var rightObject))
                {
                    if (!AppendChange(builder, "removed", pair.Value, null, ref count, maxChanges, ref truncated))
                    {
                        break;
                    }
                }
                else if (pair.Value.Hash != rightObject.Hash)
                {
                    if (!AppendChange(builder, "changed", pair.Value, rightObject, ref count, maxChanges, ref truncated))
                    {
                        break;
                    }
                }
            }

            if (!truncated)
            {
                foreach (var pair in right)
                {
                    if (left.ContainsKey(pair.Key))
                    {
                        continue;
                    }

                    if (!AppendChange(builder, "added", null, pair.Value, ref count, maxChanges, ref truncated))
                    {
                        break;
                    }
                }
            }

            return builder.ToString();
        }

        static string BuildTextPreview(string[] leftLines, string[] rightLines, int maxChanges, out bool truncated)
        {
            var prefix = 0;
            while (prefix < leftLines.Length && prefix < rightLines.Length && leftLines[prefix] == rightLines[prefix])
            {
                prefix++;
            }

            var leftSuffix = leftLines.Length - 1;
            var rightSuffix = rightLines.Length - 1;
            while (leftSuffix >= prefix && rightSuffix >= prefix && leftLines[leftSuffix] == rightLines[rightSuffix])
            {
                leftSuffix--;
                rightSuffix--;
            }

            var builder = new StringBuilder();
            var count = 0;
            truncated = false;
            for (var index = prefix; index <= leftSuffix; index++)
            {
                if (!AppendLineChange(builder, "removed", index + 1, leftLines[index], ref count, maxChanges, ref truncated))
                {
                    return builder.ToString();
                }
            }

            for (var index = prefix; index <= rightSuffix; index++)
            {
                if (!AppendLineChange(builder, "added", index + 1, rightLines[index], ref count, maxChanges, ref truncated))
                {
                    return builder.ToString();
                }
            }

            return builder.ToString();
        }

        static string BuildMergeConflicts(Dictionary<string, YamlObject> baseObjects, Dictionary<string, YamlObject> oursObjects, Dictionary<string, YamlObject> theirsObjects, int maxConflicts, out bool truncated)
        {
            var builder = new StringBuilder();
            var count = 0;
            truncated = false;
            foreach (var pair in baseObjects)
            {
                oursObjects.TryGetValue(pair.Key, out var ours);
                theirsObjects.TryGetValue(pair.Key, out var theirs);
                var oursChanged = ours == null || ours.Hash != pair.Value.Hash;
                var theirsChanged = theirs == null || theirs.Hash != pair.Value.Hash;
                if (!oursChanged || !theirsChanged || SameObject(ours, theirs))
                {
                    continue;
                }

                if (!AppendConflict(builder, pair.Value, ours, theirs, ref count, maxConflicts, ref truncated))
                {
                    break;
                }
            }

            return builder.ToString();
        }

        static string BuildMergePlan(Dictionary<string, YamlObject> baseObjects, Dictionary<string, YamlObject> oursObjects, Dictionary<string, YamlObject> theirsObjects, int maxActions, out bool truncated, out int conflictCount)
        {
            var localIds = new List<string>(baseObjects.Keys);
            AddMissingLocalIds(localIds, oursObjects);
            AddMissingLocalIds(localIds, theirsObjects);
            localIds.Sort(StringComparer.Ordinal);

            var builder = new StringBuilder();
            var count = 0;
            truncated = false;
            conflictCount = 0;
            foreach (var localId in localIds)
            {
                baseObjects.TryGetValue(localId, out var baseObject);
                oursObjects.TryGetValue(localId, out var ours);
                theirsObjects.TryGetValue(localId, out var theirs);
                var action = MergeAction(baseObject, ours, theirs, out var conflict);
                if (action == "unchanged")
                {
                    continue;
                }

                if (conflict)
                {
                    conflictCount++;
                }

                if (!AppendMergeAction(builder, action, conflict, baseObject, ours, theirs, ref count, maxActions, ref truncated))
                {
                    break;
                }
            }

            return builder.ToString();
        }

        static void AddMissingLocalIds(List<string> localIds, Dictionary<string, YamlObject> objects)
        {
            foreach (var localId in objects.Keys)
            {
                if (!localIds.Contains(localId))
                {
                    localIds.Add(localId);
                }
            }
        }

        static string MergeAction(YamlObject baseObject, YamlObject ours, YamlObject theirs, out bool conflict)
        {
            conflict = false;
            if (baseObject == null)
            {
                if (ours == null)
                {
                    return theirs == null ? "unchanged" : "add_theirs";
                }

                if (theirs == null)
                {
                    return "add_ours";
                }

                if (SameObject(ours, theirs))
                {
                    return "add_both";
                }

                conflict = true;
                return "conflict_added_differently";
            }

            var oursChanged = !SameObject(baseObject, ours);
            var theirsChanged = !SameObject(baseObject, theirs);
            if (!oursChanged && !theirsChanged)
            {
                return "unchanged";
            }

            if (oursChanged && !theirsChanged)
            {
                return ours == null ? "remove_ours" : "take_ours";
            }

            if (!oursChanged)
            {
                return theirs == null ? "remove_theirs" : "take_theirs";
            }

            if (SameObject(ours, theirs))
            {
                return ours == null ? "remove_both" : "take_both";
            }

            conflict = true;
            return "conflict";
        }

        static bool SameObject(YamlObject left, YamlObject right)
        {
            if (left == null || right == null)
            {
                return left == right;
            }

            return left.Hash == right.Hash;
        }

        static bool AppendChange(StringBuilder builder, string kind, YamlObject left, YamlObject right, ref int count, int max, ref bool truncated)
        {
            if (count >= max)
            {
                truncated = true;
                return false;
            }

            if (count > 0)
            {
                builder.Append(',');
            }

            var current = right ?? left;
            builder.Append("{\"kind\":").Append(JsonUtil.String(kind));
            AppendObjectIdentity(builder, current, true);
            if (left != null)
            {
                builder.Append(",\"left_line\":").Append(left.StartLine);
            }

            if (right != null)
            {
                builder.Append(",\"right_line\":").Append(right.StartLine);
            }

            if (left != null && right != null)
            {
                builder.Append(",\"field_changes\":").Append('[').Append(BuildFieldChanges(left, right, 20, out var fieldChangesTruncated)).Append(']');
                builder.Append(",\"field_changes_truncated\":").Append(JsonUtil.Bool(fieldChangesTruncated));
            }

            builder.Append('}');
            count++;
            return true;
        }

        static void AppendObjectIdentity(StringBuilder builder, YamlObject current, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append("\"local_id\":").Append(JsonUtil.String(current.LocalId));
            builder.Append(",\"class_id\":").Append(JsonUtil.String(current.ClassId));
            builder.Append(",\"type\":").Append(JsonUtil.String(current.Type));
            builder.Append(",\"name\":").Append(JsonUtil.String(current.Name));
        }

        static string BuildFieldChanges(YamlObject left, YamlObject right, int maxChanges, out bool truncated)
        {
            var builder = new StringBuilder();
            var count = 0;
            truncated = false;
            var leftKeys = new List<string>(left.Fields.Keys);
            leftKeys.Sort(StringComparer.Ordinal);
            foreach (var key in leftKeys)
            {
                if (!right.Fields.TryGetValue(key, out var rightValue))
                {
                    if (!AppendFieldChange(builder, "field_removed", key, left.Fields[key], null, ref count, maxChanges, ref truncated))
                    {
                        return builder.ToString();
                    }
                }
                else if (left.Fields[key] != rightValue)
                {
                    if (!AppendFieldChange(builder, "field_changed", key, left.Fields[key], rightValue, ref count, maxChanges, ref truncated))
                    {
                        return builder.ToString();
                    }
                }
            }

            var rightKeys = new List<string>(right.Fields.Keys);
            rightKeys.Sort(StringComparer.Ordinal);
            foreach (var key in rightKeys)
            {
                if (left.Fields.ContainsKey(key))
                {
                    continue;
                }

                if (!AppendFieldChange(builder, "field_added", key, null, right.Fields[key], ref count, maxChanges, ref truncated))
                {
                    return builder.ToString();
                }
            }

            return builder.ToString();
        }

        static bool AppendFieldChange(StringBuilder builder, string kind, string path, string leftValue, string rightValue, ref int count, int max, ref bool truncated)
        {
            if (count >= max)
            {
                truncated = true;
                return false;
            }

            if (count > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"kind\":").Append(JsonUtil.String(kind));
            builder.Append(",\"path\":").Append(JsonUtil.String(path));
            if (leftValue != null)
            {
                builder.Append(",\"left_value\":").Append(JsonUtil.String(leftValue));
            }

            if (rightValue != null)
            {
                builder.Append(",\"right_value\":").Append(JsonUtil.String(rightValue));
            }

            builder.Append('}');
            count++;
            return true;
        }

        static bool AppendLineChange(StringBuilder builder, string kind, int lineNumber, string line, ref int count, int max, ref bool truncated)
        {
            if (count >= max)
            {
                truncated = true;
                return false;
            }

            if (count > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"kind\":").Append(JsonUtil.String(kind));
            builder.Append(",\"line\":").Append(lineNumber);
            builder.Append(",\"text\":").Append(JsonUtil.String(line));
            builder.Append('}');
            count++;
            return true;
        }

        static bool AppendConflict(StringBuilder builder, YamlObject baseObject, YamlObject ours, YamlObject theirs, ref int count, int max, ref bool truncated)
        {
            if (count >= max)
            {
                truncated = true;
                return false;
            }

            if (count > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"local_id\":").Append(JsonUtil.String(baseObject.LocalId));
            builder.Append(",\"type\":").Append(JsonUtil.String(baseObject.Type));
            builder.Append(",\"name\":").Append(JsonUtil.String(baseObject.Name));
            builder.Append(",\"ours\":").Append(ours == null ? "\"removed\"" : JsonUtil.String("changed"));
            builder.Append(",\"theirs\":").Append(theirs == null ? "\"removed\"" : JsonUtil.String("changed"));
            builder.Append('}');
            count++;
            return true;
        }

        static bool AppendMergeAction(StringBuilder builder, string action, bool conflict, YamlObject baseObject, YamlObject ours, YamlObject theirs, ref int count, int max, ref bool truncated)
        {
            if (count >= max)
            {
                truncated = true;
                return false;
            }

            if (count > 0)
            {
                builder.Append(',');
            }

            var current = ours ?? theirs ?? baseObject;
            builder.Append("{\"action\":").Append(JsonUtil.String(action));
            AppendObjectIdentity(builder, current, true);
            builder.Append(",\"conflict\":").Append(JsonUtil.Bool(conflict));
            builder.Append(",\"base_present\":").Append(JsonUtil.Bool(baseObject != null));
            builder.Append(",\"ours_present\":").Append(JsonUtil.Bool(ours != null));
            builder.Append(",\"theirs_present\":").Append(JsonUtil.Bool(theirs != null));
            builder.Append(",\"ours_changed\":").Append(JsonUtil.Bool(!SameObject(baseObject, ours)));
            builder.Append(",\"theirs_changed\":").Append(JsonUtil.Bool(!SameObject(baseObject, theirs)));
            builder.Append('}');
            count++;
            return true;
        }

        static string[] ReadAllLines(string assetPath)
        {
            return File.ReadAllLines(AbsolutePath(assetPath));
        }

        static string ConservativeMergeSource(string basePath, string oursPath, string theirsPath)
        {
            if (FilesEqual(oursPath, theirsPath))
            {
                return oursPath;
            }

            if (FilesEqual(basePath, oursPath))
            {
                return theirsPath;
            }

            return FilesEqual(basePath, theirsPath) ? oursPath : string.Empty;
        }

        static bool FilesEqual(string leftPath, string rightPath)
        {
            return string.Equals(File.ReadAllText(AbsolutePath(leftPath)), File.ReadAllText(AbsolutePath(rightPath)), StringComparison.Ordinal);
        }

        static string AbsolutePath(string assetPath)
        {
            return Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        static bool IsSupportedUnityYaml(string assetPath)
        {
            return assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                || assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                || assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase);
        }

        static string ClassName(string classId)
        {
            switch (classId)
            {
                case "1": return "GameObject";
                case "4": return "Transform";
                case "21": return "Material";
                case "23": return "MeshRenderer";
                case "33": return "MeshFilter";
                case "114": return "MonoBehaviour";
                case "224": return "RectTransform";
                case "1001": return "PrefabInstance";
                default: return "UnityObject";
            }
        }

        static int StableHash(string value)
        {
            unchecked
            {
                var hash = 2166136261u;
                for (var index = 0; index < value.Length; index++)
                {
                    hash ^= value[index];
                    hash *= 16777619;
                }

                return (int)hash;
            }
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        sealed class YamlObject
        {
            public string LocalId = string.Empty;
            public string ClassId = string.Empty;
            public string Type = string.Empty;
            public string Name = string.Empty;
            public int StartLine;
            public int EndLine;
            public int Hash;
            public Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
