using System;
using System.IO;
using SGG.UnityMCP.Editor.Paths;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class OpenCodeConfigPathResolver
    {
        internal enum WorkspaceSource
        {
            Explicit,
            Saved,
            GitWorktree,
            UnityProjectFallback,
        }

        internal enum ConfigState
        {
            Missing,
            Jsonc,
            Json,
            Conflict,
            HiddenLayerConflict,
        }

        internal sealed class Result
        {
            public string UnityProjectRoot { get; set; }
            public string CanonicalUnityProjectRoot { get; set; }
            public string WorkspaceRoot { get; set; }
            public string CanonicalWorkspaceRoot { get; set; }
            public string ConfigPath { get; set; }
            public string JsoncPath { get; set; }
            public string JsonPath { get; set; }
            public string HiddenJsoncPath { get; set; }
            public string HiddenJsonPath { get; set; }
            public WorkspaceSource Source { get; set; }
            public ConfigState State { get; set; }
            public bool RequiresConfirmation { get; set; }
            public bool IsNestedProject { get; set; }
            public bool SavedWorkspaceRejected { get; set; }

            public bool HasHiddenLayer => State == ConfigState.HiddenLayerConflict;
            public bool HasConflict => State == ConfigState.Conflict || HasHiddenLayer;
        }

        public static Result Resolve(string unityProjectRoot, string explicitWorkspaceRoot = null, string savedWorkspaceRoot = null)
        {
            return ResolveWithKnownGitRoot(unityProjectRoot, explicitWorkspaceRoot, savedWorkspaceRoot, TryGetGitRoot(unityProjectRoot));
        }

        internal static Result ResolveWithKnownGitRoot(
            string unityProjectRoot,
            string explicitWorkspaceRoot,
            string savedWorkspaceRoot,
            string knownGitRoot)
        {
            var canonicalPath = new CanonicalPathService();
            var hostEnvironment = HostEnvironmentFor(unityProjectRoot, Path.DirectorySeparatorChar);
            return Resolve(
                unityProjectRoot,
                explicitWorkspaceRoot,
                savedWorkspaceRoot,
                path => canonicalPath.NormalizeProjectRoot(path).Value,
                path => NormalizeHostRoot(path, canonicalPath, hostEnvironment),
                _ => knownGitRoot,
                File.Exists);
        }

        internal static string DiscoverGitRoot(string unityProjectRoot)
        {
            return TryGetGitRoot(unityProjectRoot);
        }

        internal static Result Resolve(
            string unityProjectRoot,
            string explicitWorkspaceRoot,
            string savedWorkspaceRoot,
            Func<string, string> normalizeCanonicalRoot,
            Func<string, string> normalizeHostRoot,
            Func<string, string> resolveGitRoot,
            Func<string, bool> fileExists)
        {
            if (normalizeCanonicalRoot == null) throw new ArgumentNullException(nameof(normalizeCanonicalRoot));
            if (normalizeHostRoot == null) throw new ArgumentNullException(nameof(normalizeHostRoot));
            if (resolveGitRoot == null) throw new ArgumentNullException(nameof(resolveGitRoot));
            if (fileExists == null) throw new ArgumentNullException(nameof(fileExists));

            var unityRoot = normalizeHostRoot(unityProjectRoot);
            var canonicalUnityRoot = normalizeCanonicalRoot(unityProjectRoot);

            Candidate candidate;
            var savedWorkspaceRejected = false;
            if (!string.IsNullOrWhiteSpace(explicitWorkspaceRoot))
            {
                candidate = RequireContainingCandidate(explicitWorkspaceRoot, WorkspaceSource.Explicit, canonicalUnityRoot, normalizeCanonicalRoot, normalizeHostRoot);
            }
            else if (!string.IsNullOrWhiteSpace(savedWorkspaceRoot))
            {
                if (!TryCreateContainingCandidate(savedWorkspaceRoot, WorkspaceSource.Saved, canonicalUnityRoot, normalizeCanonicalRoot, normalizeHostRoot, out candidate))
                {
                    savedWorkspaceRejected = true;
                    candidate = ResolveAutomaticCandidate(unityRoot, canonicalUnityRoot, normalizeCanonicalRoot, normalizeHostRoot, resolveGitRoot);
                }
            }
            else
            {
                candidate = ResolveAutomaticCandidate(unityRoot, canonicalUnityRoot, normalizeCanonicalRoot, normalizeHostRoot, resolveGitRoot);
            }

            var jsoncPath = Path.Combine(candidate.HostRoot, "opencode.jsonc");
            var jsonPath = Path.Combine(candidate.HostRoot, "opencode.json");
            var jsoncExists = fileExists(jsoncPath);
            var jsonExists = fileExists(jsonPath);
            var hiddenJsoncPath = Path.Combine(candidate.HostRoot, ".opencode", "opencode.jsonc");
            var hiddenJsonPath = Path.Combine(candidate.HostRoot, ".opencode", "opencode.json");
            var hasHiddenLayer = fileExists(hiddenJsoncPath) || fileExists(hiddenJsonPath);
            // V2 loads hidden layers after every direct layer and replaces an entire same-named
            // MCP server. Without inspecting all layers, choosing or migrating one is not safe.
            var state = jsoncExists && jsonExists
                ? ConfigState.Conflict
                : jsoncExists
                    ? ConfigState.Jsonc
                    : jsonExists
                        ? ConfigState.Json
                        : ConfigState.Missing;
            if (hasHiddenLayer) state = ConfigState.HiddenLayerConflict;

            return new Result
            {
                UnityProjectRoot = unityRoot,
                CanonicalUnityProjectRoot = canonicalUnityRoot,
                WorkspaceRoot = candidate.HostRoot,
                CanonicalWorkspaceRoot = candidate.CanonicalRoot,
                ConfigPath = state == ConfigState.Conflict || hasHiddenLayer ? null : state == ConfigState.Json ? jsonPath : jsoncPath,
                JsoncPath = jsoncPath,
                JsonPath = jsonPath,
                HiddenJsoncPath = hiddenJsoncPath,
                HiddenJsonPath = hiddenJsonPath,
                Source = candidate.Source,
                State = state,
                RequiresConfirmation = candidate.Source == WorkspaceSource.UnityProjectFallback,
                IsNestedProject = !string.Equals(candidate.CanonicalRoot, canonicalUnityRoot, StringComparison.Ordinal),
                SavedWorkspaceRejected = savedWorkspaceRejected,
            };
        }

        static Candidate ResolveAutomaticCandidate(
            string unityRoot,
            string canonicalUnityRoot,
            Func<string, string> normalizeCanonicalRoot,
            Func<string, string> normalizeHostRoot,
            Func<string, string> resolveGitRoot)
        {
            Candidate candidate;
            var gitRoot = resolveGitRoot(unityRoot);
            if (!string.IsNullOrWhiteSpace(gitRoot)
                && TryCreateContainingCandidate(gitRoot, WorkspaceSource.GitWorktree, canonicalUnityRoot, normalizeCanonicalRoot, normalizeHostRoot, out candidate))
            {
                return candidate;
            }

            return RequireContainingCandidate(unityRoot, WorkspaceSource.UnityProjectFallback, canonicalUnityRoot, normalizeCanonicalRoot, normalizeHostRoot);
        }

        static Candidate RequireContainingCandidate(
            string workspaceRoot,
            WorkspaceSource source,
            string canonicalUnityRoot,
            Func<string, string> normalizeCanonicalRoot,
            Func<string, string> normalizeHostRoot)
        {
            Candidate candidate;
            if (!TryCreateContainingCandidate(workspaceRoot, source, canonicalUnityRoot, normalizeCanonicalRoot, normalizeHostRoot, out candidate))
            {
                throw new InvalidOperationException("OpenCode workspace must contain the Unity project.");
            }

            return candidate;
        }

        static bool TryCreateContainingCandidate(
            string workspaceRoot,
            WorkspaceSource source,
            string canonicalUnityRoot,
            Func<string, string> normalizeCanonicalRoot,
            Func<string, string> normalizeHostRoot,
            out Candidate candidate)
        {
            candidate = default(Candidate);
            try
            {
                var canonicalRoot = normalizeCanonicalRoot(workspaceRoot);
                if (!Contains(canonicalRoot, canonicalUnityRoot))
                {
                    return false;
                }

                var hostRoot = normalizeHostRoot(workspaceRoot);
                candidate = new Candidate(hostRoot, canonicalRoot, source);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool Contains(string parent, string child)
        {
            return string.Equals(parent, child, StringComparison.Ordinal)
                || child.StartsWith(parent.EndsWith("/", StringComparison.Ordinal) ? parent : parent + "/", StringComparison.Ordinal);
        }

        internal static string HostEnvironmentFor(string unityProjectRoot, char directorySeparator)
        {
            return directorySeparator == '\\'
                ? "windows"
                : unityProjectRoot.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) ? "wsl" : "posix";
        }

        static string NormalizeHostRoot(string path, CanonicalPathService canonicalPath, string hostEnvironment)
        {
            foreach (var alias in canonicalPath.ProjectRootAliases(path))
            {
                if (string.Equals(alias.environment, hostEnvironment, StringComparison.Ordinal))
                {
                    return NormalizeNativeHostRoot(alias.host_path);
                }
            }

            return NormalizeNativeHostRoot(path);
        }

        static string NormalizeNativeHostRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("OpenCode workspace root is required.", nameof(path));
            }

            var fullPath = Path.GetFullPath(path);
            var pathRoot = Path.GetPathRoot(fullPath) ?? string.Empty;
            while (fullPath.Length > pathRoot.Length
                && (fullPath[fullPath.Length - 1] == Path.DirectorySeparatorChar
                    || fullPath[fullPath.Length - 1] == Path.AltDirectorySeparatorChar))
            {
                fullPath = fullPath.Substring(0, fullPath.Length - 1);
            }

            return fullPath;
        }

        static string TryGetGitRoot(string unityProjectRoot)
        {
            string stdout;
            string stderr;
            return ProcessUtil.TryRun(
                "git",
                "-C " + ProcessUtil.Quote(unityProjectRoot) + " rev-parse --show-toplevel",
                unityProjectRoot,
                out stdout,
                out stderr,
                1000)
                ? stdout.Trim()
                : null;
        }

        readonly struct Candidate
        {
            public Candidate(string hostRoot, string canonicalRoot, WorkspaceSource source)
            {
                HostRoot = hostRoot;
                CanonicalRoot = canonicalRoot;
                Source = source;
            }

            public string HostRoot { get; }
            public string CanonicalRoot { get; }
            public WorkspaceSource Source { get; }
        }
    }
}
