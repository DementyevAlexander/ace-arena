using System;

namespace SGG.UnityMCP.Editor.Paths
{
    public static class CanonicalPathBridge
    {
        public const string PackageId = "com.romanilyin.canonicalpath";
        public const string PackageVersion = "2026.10.10-4";
        public const string PackageSpec = PackageId + "@" + PackageVersion;

        public delegate string NormalizeUnityPathDelegate(string input);
        public delegate string NormalizeDelegate(string input);
        public delegate string MakeSafeFileNameDelegate(string input, int maxLength);

        static NormalizeUnityPathDelegate s_normalizeUnityPath;
        static NormalizeDelegate s_normalize;
        static MakeSafeFileNameDelegate s_makeSafeFileName;

        public static bool IsAvailable => s_normalizeUnityPath != null && s_normalize != null && s_makeSafeFileName != null;

        public static void Register(NormalizeUnityPathDelegate normalizeUnityPath, NormalizeDelegate normalize, MakeSafeFileNameDelegate makeSafeFileName)
        {
            s_normalizeUnityPath = normalizeUnityPath ?? throw new ArgumentNullException(nameof(normalizeUnityPath));
            s_normalize = normalize ?? throw new ArgumentNullException(nameof(normalize));
            s_makeSafeFileName = makeSafeFileName ?? throw new ArgumentNullException(nameof(makeSafeFileName));
        }

        public static bool TryNormalizeUnityPath(string input, out string normalized)
        {
            normalized = null;
            var normalize = s_normalizeUnityPath;
            if (normalize == null)
            {
                return false;
            }

            normalized = normalize(input);
            return !string.IsNullOrEmpty(normalized);
        }

        public static bool TryNormalize(string input, out string normalized)
        {
            normalized = null;
            var normalize = s_normalize;
            if (normalize == null)
            {
                return false;
            }

            normalized = normalize(input);
            return !string.IsNullOrEmpty(normalized);
        }

        public static bool TryMakeSafeFileName(string input, int maxLength, out string safeFileName)
        {
            safeFileName = null;
            var makeSafeFileName = s_makeSafeFileName;
            if (makeSafeFileName == null)
            {
                return false;
            }

            safeFileName = makeSafeFileName(input, maxLength);
            return !string.IsNullOrEmpty(safeFileName);
        }
    }

    internal sealed class CanonicalPathDependencyMissingException : InvalidOperationException
    {
        public CanonicalPathDependencyMissingException()
            : base("CanonicalPath provider is unavailable. Install or update " + CanonicalPathBridge.PackageSpec + " from the SGG Unity MCP Setup window, then check package resolution and assembly reload before using bridge discovery or path-bound MCP commands.")
        {
        }
    }
}
