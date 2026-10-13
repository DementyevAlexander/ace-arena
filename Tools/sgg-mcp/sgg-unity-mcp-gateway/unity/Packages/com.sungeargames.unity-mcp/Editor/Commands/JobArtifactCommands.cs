using System;
using System.IO;
using SGG.UnityMCP.Editor.Bridge;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class JobArtifactCommands
    {
        const string ArtifactRoot = "Library/SGGUnityMcp/job-artifacts/";
        const string ReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"artifact_path\":{\"type\":\"string\"},\"max_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000}},\"required\":[\"artifact_path\"],\"additionalProperties\":false}";
        const string ReadOutputLimitJson = "{\"max_chars\":200000}";

        [McpCommand(
            "job.artifact.read",
            Title = "Read job artifact",
            Description = "Read a bounded gateway/bridge job artifact from the project-local artifact directory",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReadSchemaJson,
            Tags = new string[] { "job", "artifact" },
            Capability = "job_artifacts",
            ResultShape = "object_details",
            OutputLimitJson = ReadOutputLimitJson)]
        public static CommandExecutionResult Read(McpCommandContext context)
        {
            var artifactPath = JsonUtil.ExtractString(context.ArgsJson, "artifact_path");
            if (string.IsNullOrEmpty(artifactPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument artifact_path is required");
            }

            var relativePath = NormalizeArtifactPath(artifactPath);
            if (string.IsNullOrEmpty(relativePath))
            {
                return CommandExecutionResult.Error("path_not_allowed", "artifact_path must stay under Library/SGGUnityMcp/job-artifacts");
            }

            string fullPath;
            try
            {
                fullPath = RequireProjectLocalFilePath(relativePath, ArtifactRoot);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("path_not_allowed", "artifact_path must stay under Library/SGGUnityMcp/job-artifacts");
            }

            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "Artifact was not found");
            }

            var maxChars = Math.Max(1, Math.Min(200000, JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 20000)));
            var content = File.ReadAllText(fullPath);
            var truncated = content.Length > maxChars;
            if (truncated)
            {
                content = content.Substring(0, maxChars);
            }

            return CommandExecutionResult.Ok("{\"artifact_path\":" + JsonUtil.String(relativePath)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"content\":" + JsonUtil.String(content) + "}");
        }

        static string NormalizeArtifactPath(string artifactPath)
        {
            if (artifactPath.IndexOf('\0') >= 0)
            {
                return string.Empty;
            }

            var normalized = artifactPath.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal) || IsDriveAbsolute(normalized))
            {
                return string.Empty;
            }

            if (normalized.IndexOf(':') >= 0 || normalized.Contains("../") || normalized == ".." || normalized.Contains("/.."))
            {
                return string.Empty;
            }

            if (!normalized.StartsWith(ArtifactRoot, StringComparison.Ordinal))
            {
                normalized = ArtifactRoot + normalized;
            }

            return normalized.StartsWith(ArtifactRoot, StringComparison.Ordinal) ? normalized : string.Empty;
        }

        static string RequireProjectLocalFilePath(string relativePath, string requiredRoot)
        {
            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var fullRoot = Path.GetFullPath(Path.Combine(projectRoot, requiredRoot.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, fullRoot) || HasReparsePointInExistingChain(fullPath, fullRoot))
            {
                throw new ArgumentException("path_not_allowed");
            }

            return fullPath;
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static bool HasReparsePointInExistingChain(string path, string root)
        {
            var current = path;
            while (IsSameOrChildPath(current, root))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                var next = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.Ordinal))
                {
                    return false;
                }

                current = next;
            }

            return false;
        }

        static bool IsDriveAbsolute(string input)
        {
            return input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':';
        }
    }
}
