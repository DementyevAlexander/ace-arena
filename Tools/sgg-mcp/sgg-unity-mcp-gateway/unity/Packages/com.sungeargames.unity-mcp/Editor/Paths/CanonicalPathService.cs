using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SGG.UnityMCP.Editor.Paths
{
    public sealed class CanonicalPathService : ICanonicalPathService
    {
        public CanonicalPathValue Normalize(string input)
        {
            PathGuard.AssertNoNul(input);
            return new CanonicalPathValue(MapCanonicalException(() =>
            {
                string normalized;
                if (!CanonicalPathBridge.TryNormalize(input, out normalized))
                {
                    throw new CanonicalPathDependencyMissingException();
                }

                return normalized;
            }));
        }

        public CanonicalPathValue NormalizeProjectRoot(string input)
        {
            RejectEmptyRoot(input);
            RejectUncPath(input.Replace('\\', '/'));
            var normalized = TrimTrailingSlash(Normalize(input).Value);
            RejectEmptyRoot(normalized);
            RejectUncPath(normalized);
            return new CanonicalPathValue(normalized);
        }

        public PathAlias[] ProjectRootAliases(string input)
        {
            var canonicalRoot = NormalizeProjectRoot(input).Value;
            var aliases = new List<PathAlias>();
            var original = TrimTrailingSlash(input.Replace('\\', '/'));

            if (IsWslDrivePath(original))
            {
                AddAlias(aliases, "wsl", original, canonicalRoot);
            }
            else if (Regex.IsMatch(original, "^[A-Za-z]:/"))
            {
                AddAlias(aliases, "unity", UppercaseDriveLetter(original), canonicalRoot);
            }
            else if (!original.StartsWith("//", StringComparison.Ordinal))
            {
                AddAlias(aliases, "posix", original, canonicalRoot);
            }

            if (Regex.IsMatch(canonicalRoot, "^[a-z]:/", RegexOptions.IgnoreCase))
            {
                AddAlias(aliases, "unity", UppercaseDriveLetter(canonicalRoot), canonicalRoot);
                AddAlias(aliases, "windows", ToWindowsDrivePath(canonicalRoot), canonicalRoot);
                AddAlias(aliases, "wsl", ToWslDrivePath(canonicalRoot), canonicalRoot);
            }

            return aliases.ToArray();
        }

        public CanonicalPathValue FromUnityAssetPath(CanonicalPathValue projectRoot, string unityPath)
        {
            return Normalize(projectRoot.Value + "/" + PathGuard.RequireUnityAssetPath(unityPath));
        }

        public string ToUnityAssetPath(CanonicalPathValue projectRoot, CanonicalPathValue fullPath)
        {
            AssertInsideProject(projectRoot, fullPath);
            var root = TrimTrailingSlash(Normalize(projectRoot.Value).Value);
            var candidate = Normalize(fullPath.Value).Value;
            var relative = candidate == root ? string.Empty : candidate.Substring(root.Length + 1);
            return PathGuard.RequireUnityAssetPath(relative);
        }

        public void AssertInsideProject(CanonicalPathValue projectRoot, CanonicalPathValue candidate)
        {
            RejectUncPath(projectRoot.Value.Replace('\\', '/'));
            RejectUncPath(candidate.Value.Replace('\\', '/'));
            if (PathGuard.HasTraversal(candidate.Value.Replace('\\', '/')))
            {
                throw new ArgumentException("path_traversal_rejected", nameof(candidate));
            }

            var root = TrimTrailingSlash(Normalize(projectRoot.Value).Value);
            var normalizedCandidate = TrimTrailingSlash(Normalize(candidate.Value).Value);
            RejectEmptyRoot(root);
            if (normalizedCandidate != root && !normalizedCandidate.StartsWith(root + "/", StringComparison.Ordinal))
            {
                throw new ArgumentException("path_not_allowed", nameof(candidate));
            }
        }

        public string MakeSafeFileName(string input, int maxLength)
        {
            PathGuard.AssertNoNul(input);
            return MapCanonicalException(() =>
            {
                string safeFileName;
                if (!CanonicalPathBridge.TryMakeSafeFileName(input, maxLength, out safeFileName))
                {
                    throw new CanonicalPathDependencyMissingException();
                }

                return safeFileName;
            });
        }

        static string TrimTrailingSlash(string input)
        {
            if (Regex.IsMatch(input, "^[a-z]:/$", RegexOptions.IgnoreCase) || input == "/")
            {
                return input;
            }

            return input.TrimEnd('/');
        }

        static void AddAlias(List<PathAlias> aliases, string environment, string hostPath, string canonicalPath)
        {
            foreach (var alias in aliases)
            {
                if (alias.environment == environment && alias.host_path == hostPath)
                {
                    return;
                }
            }

            aliases.Add(new PathAlias(environment, hostPath, canonicalPath));
        }

        static string UppercaseDriveLetter(string input)
        {
            return Regex.Replace(input, "^([A-Za-z]):/", match => match.Groups[1].Value.ToUpperInvariant() + ":/");
        }

        static string ToWindowsDrivePath(string input)
        {
            return UppercaseDriveLetter(input).Replace('/', '\\');
        }

        static string ToWslDrivePath(string input)
        {
            var match = Regex.Match(input, "^([a-z]):/(.*)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return input;
            }

            var drive = match.Groups[1].Value.ToLowerInvariant();
            var rest = match.Groups[2].Value;
            return "/mnt/" + drive + (string.IsNullOrEmpty(rest) ? string.Empty : "/" + rest);
        }

        static bool IsWslDrivePath(string input)
        {
            return Regex.IsMatch(input, "^/mnt/[A-Za-z](/|$)");
        }

        static void RejectEmptyRoot(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new ArgumentException("path_not_allowed");
            }
        }

        static void RejectUncPath(string input)
        {
            if (input.StartsWith("//", StringComparison.Ordinal))
            {
                throw new ArgumentException("absolute_path_rejected");
            }
        }

        static string MapCanonicalException(Func<string> action)
        {
            try
            {
                return action();
            }
            catch (CanonicalPathDependencyMissingException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var message = exception.Message ?? string.Empty;
                if (message.Contains("absolute_path_rejected", StringComparison.Ordinal) || message.Contains("absolute", StringComparison.OrdinalIgnoreCase) || message.Contains("drive", StringComparison.OrdinalIgnoreCase) || message.Contains("UNC", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("absolute_path_rejected", exception);
                }

                if (message.Contains("path_traversal_rejected", StringComparison.Ordinal) || message.Contains("traversal", StringComparison.OrdinalIgnoreCase) || message.Contains("..", StringComparison.Ordinal))
                {
                    throw new ArgumentException("path_traversal_rejected", exception);
                }

                throw new ArgumentException("path_not_allowed", exception);
            }
        }
    }
}
