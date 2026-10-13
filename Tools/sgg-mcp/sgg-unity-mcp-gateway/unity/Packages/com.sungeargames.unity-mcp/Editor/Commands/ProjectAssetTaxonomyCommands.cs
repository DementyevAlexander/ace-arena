using System;
using System.Collections.Generic;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ProjectAssetTaxonomyCommands
    {
        const string FeatureFlag = "asset_index";
        const string SchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\"},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_chars\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":100000}},\"additionalProperties\":false}";
        const string OutputLimitJson = "{\"max_depth\":16,\"max_items\":5000,\"max_chars\":100000}";

        [McpCommand(
            "project.assets.taxonomy",
            Title = "Project asset taxonomy",
            Description = "Read a bounded Unity asset folder taxonomy for project overview context",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SchemaJson,
            Tags = new string[] { "project", "asset", "taxonomy", "unity-ai-assistant" },
            Capability = "asset_index",
            ResultShape = "bounded_tree",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ProjectAssetsTaxonomy(McpCommandContext context)
        {
            var prefixResult = RequireFolderPrefix(context.ArgsJson, out var folderPath);
            if (prefixResult != null)
            {
                return prefixResult;
            }

            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 2), 0, 16);
            var maxItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_items", 500), 1, 5000);
            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 20000), 1000, 100000);
            return CommandExecutionResult.Ok(TaxonomyJson(folderPath, maxDepth, maxItems, maxChars));
        }

        static string TaxonomyJson(string folderPath, int maxDepth, int maxItems, int maxChars)
        {
            var root = new TaxonomyNode(folderPath, false, string.Empty);
            var paths = AssetPaths(folderPath, maxDepth);
            var included = Math.Min(paths.Count, maxItems);
            for (var index = 0; index < included; index++)
            {
                AddPath(root, folderPath, paths[index]);
            }

            var markdown = Markdown(root, maxChars, out var charsTruncated);
            var truncated = paths.Count > included || charsTruncated;
            return "{\"path_prefix\":" + JsonUtil.String(DisplayPrefix(folderPath))
                + ",\"max_depth\":" + maxDepth
                + ",\"max_items\":" + maxItems
                + ",\"max_chars\":" + maxChars
                + ",\"matched_items\":" + paths.Count
                + ",\"included_items\":" + included
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"taxonomy_markdown\":" + JsonUtil.String(markdown)
                + ",\"compatible_with\":\"Unity.GetProjectData\"}";
        }

        static List<string> AssetPaths(string folderPath, int maxDepth)
        {
            var guids = AssetDatabase.FindAssets(string.Empty, new[] { folderPath });
            var paths = new List<string>();
            for (var index = 0; index < guids.Length; index++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (string.IsNullOrEmpty(path) || !IsUnderFolder(path, folderPath) || RelativeDepth(folderPath, path) > maxDepth)
                {
                    continue;
                }

                paths.Add(path);
            }

            paths.Sort((left, right) =>
            {
                var byDepth = RelativeDepth(folderPath, left).CompareTo(RelativeDepth(folderPath, right));
                return byDepth != 0 ? byDepth : string.Compare(left, right, StringComparison.Ordinal);
            });
            return paths;
        }

        static void AddPath(TaxonomyNode root, string folderPath, string assetPath)
        {
            var relative = RelativePath(folderPath, assetPath);
            if (string.IsNullOrEmpty(relative))
            {
                return;
            }

            var parts = relative.Split('/');
            var current = root;
            for (var index = 0; index < parts.Length; index++)
            {
                if (string.IsNullOrEmpty(parts[index]))
                {
                    continue;
                }

                var isLeaf = index == parts.Length - 1;
                if (!current.Children.TryGetValue(parts[index], out var child))
                {
                    child = new TaxonomyNode(parts[index], isLeaf && !AssetDatabase.IsValidFolder(assetPath), string.Empty);
                    current.Children[parts[index]] = child;
                }

                if (isLeaf && !AssetDatabase.IsValidFolder(assetPath))
                {
                    child.IsAsset = true;
                    var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                    child.Type = type == null ? "Unknown" : type.Name;
                }

                current = child;
            }
        }

        static string Markdown(TaxonomyNode root, int maxChars, out bool truncated)
        {
            truncated = false;
            var builder = new StringBuilder();
            AppendNode(builder, root, 0, maxChars, ref truncated);
            return builder.ToString();
        }

        static void AppendNode(StringBuilder builder, TaxonomyNode node, int depth, int maxChars, ref bool truncated)
        {
            if (truncated)
            {
                return;
            }

            var line = new string(' ', depth * 2) + "- " + node.Name + (node.IsAsset ? " (" + node.Type + ")" : "/") + "\n";
            if (builder.Length + line.Length > maxChars)
            {
                builder.Append("... [truncated]\n");
                truncated = true;
                return;
            }

            builder.Append(line);
            var names = new List<string>(node.Children.Keys);
            names.Sort(StringComparer.Ordinal);
            for (var index = 0; index < names.Count; index++)
            {
                AppendNode(builder, node.Children[names[index]], depth + 1, maxChars, ref truncated);
            }
        }

        static CommandExecutionResult RequireFolderPrefix(string argsJson, out string folderPath)
        {
            folderPath = JsonUtil.ExtractString(argsJson, "path_prefix");
            if (string.IsNullOrEmpty(folderPath) || folderPath == "Assets" || folderPath == "Assets/")
            {
                folderPath = "Assets";
                return null;
            }

            if (folderPath == "Packages" || folderPath == "Packages/")
            {
                folderPath = "Packages";
                return null;
            }

            try
            {
                folderPath = PathGuard.RequireUnityAssetPath(folderPath.TrimEnd('/', '\\'));
            }
            catch (ArgumentException)
            {
                folderPath = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", "path_prefix must be a safe Unity-relative folder under Assets/ or Packages/");
            }

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "path_prefix folder was not found");
            }

            return null;
        }

        static bool IsUnderFolder(string path, string folderPath)
        {
            return string.Equals(path, folderPath, StringComparison.Ordinal) || path.StartsWith(folderPath + "/", StringComparison.Ordinal);
        }

        static int RelativeDepth(string folderPath, string path)
        {
            var relative = RelativePath(folderPath, path);
            if (string.IsNullOrEmpty(relative))
            {
                return 0;
            }

            var depth = 1;
            for (var index = 0; index < relative.Length; index++)
            {
                if (relative[index] == '/')
                {
                    depth++;
                }
            }

            return depth;
        }

        static string RelativePath(string folderPath, string path)
        {
            return string.Equals(path, folderPath, StringComparison.Ordinal)
                ? string.Empty
                : path.Substring(folderPath.Length).TrimStart('/');
        }

        static string DisplayPrefix(string folderPath)
        {
            return folderPath.EndsWith("/", StringComparison.Ordinal) ? folderPath : folderPath + "/";
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        sealed class TaxonomyNode
        {
            public TaxonomyNode(string name, bool isAsset, string type)
            {
                Name = name;
                IsAsset = isAsset;
                Type = type;
            }

            public string Name;
            public bool IsAsset;
            public string Type;
            public readonly Dictionary<string, TaxonomyNode> Children = new Dictionary<string, TaxonomyNode>(StringComparer.Ordinal);
        }
    }
}
