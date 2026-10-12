using System;
using System.Text.RegularExpressions;

namespace SGG.UnityMCP.Editor.Paths
{
    public static class PathGuard
    {
        public static string RequireUnityAssetPath(string input)
        {
            RejectUnsafeUnityPayload(input, true);

            string normalized;
            try
            {
                if (!CanonicalPathBridge.TryNormalizeUnityPath(input, out normalized))
                {
                    throw new CanonicalPathDependencyMissingException();
                }
            }
            catch (CanonicalPathDependencyMissingException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw MapCanonicalException(exception);
            }

            RejectUnsafeUnityPayload(normalized, false);
            return normalized;
        }

        public static void AssertNoNul(string input)
        {
            if (input == null || input.IndexOf('\0') >= 0)
            {
                throw new ArgumentException("path_not_allowed");
            }
        }

        public static bool HasTraversal(string input)
        {
            var parts = input.Split('/');
            foreach (var part in parts)
            {
                if (part == "..")
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsDriveAbsolute(string input)
        {
            return input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':';
        }

        static void RejectUnsafeUnityPayload(string input, bool allowCollapsedSeparators)
        {
            AssertNoNul(input);
            RejectEncodedSeparators(input);
            var normalized = input.Replace('\\', '/');

            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal) || IsDriveAbsolute(normalized))
            {
                throw new ArgumentException("absolute_path_rejected");
            }

            if (HasTraversal(normalized))
            {
                throw new ArgumentException("path_traversal_rejected");
            }

            if (normalized == "Assets" || normalized == "Packages")
            {
                throw new ArgumentException("path_not_allowed");
            }

            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) && !normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                throw new ArgumentException("path_not_allowed");
            }

            foreach (var part in normalized.Split('/'))
            {
                if ((part.Length == 0 && !allowCollapsedSeparators) || part.Length > 255 || part == "." || part.IndexOf(':') >= 0 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || IsWindowsReservedName(part))
                {
                    throw new ArgumentException("path_not_allowed");
                }
            }
        }

        static bool IsWindowsReservedName(string component)
        {
            var dotIndex = component.IndexOf('.');
            var stem = (dotIndex >= 0 ? component.Substring(0, dotIndex) : component).ToUpperInvariant();
            return stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || Regex.IsMatch(stem, "^(COM[1-9]|LPT[1-9])$");
        }

        static void RejectEncodedSeparators(string input)
        {
            var candidate = input;
            for (var index = 0; index < 3; index++)
            {
                if (Regex.IsMatch(candidate, "%2[fF]|%5[cC]"))
                {
                    throw new ArgumentException("path_not_allowed");
                }

                if (candidate.IndexOf('%') < 0)
                {
                    return;
                }

                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(candidate);
                }
                catch (UriFormatException)
                {
                    throw new ArgumentException("path_not_allowed");
                }
                if (decoded == candidate)
                {
                    return;
                }

                candidate = decoded;
            }
        }

        static ArgumentException MapCanonicalException(Exception exception)
        {
            var message = exception.Message ?? string.Empty;
            if (message.Contains("absolute_path_rejected", StringComparison.Ordinal) || message.Contains("absolute", StringComparison.OrdinalIgnoreCase) || message.Contains("drive", StringComparison.OrdinalIgnoreCase) || message.Contains("UNC", StringComparison.OrdinalIgnoreCase))
            {
                return new ArgumentException("absolute_path_rejected", exception);
            }

            if (message.Contains("path_traversal_rejected", StringComparison.Ordinal) || message.Contains("traversal", StringComparison.OrdinalIgnoreCase) || message.Contains("..", StringComparison.Ordinal))
            {
                return new ArgumentException("path_traversal_rejected", exception);
            }

            return new ArgumentException("path_not_allowed", exception);
        }
    }
}
