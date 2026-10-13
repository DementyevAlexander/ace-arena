using SGG.UnityMCP.Editor.Paths;
using UnityEditor;

namespace SGG.UnityMCP.Editor.CanonicalPath
{
    [InitializeOnLoad]
    public static class CanonicalPathBridgeRegistration
    {
        static CanonicalPathBridgeRegistration()
        {
            CanonicalPathBridge.Register(
                global::CanonicalPath.PathGuard.NormalizeUnityPath,
                CanonicalPathProviderAdapter.Normalize,
                CanonicalPathProviderAdapter.MakeSafeFileName);
        }
    }

    public static class CanonicalPathProviderAdapter
    {
        static readonly global::CanonicalPath.BridgeCanonicalPathService s_bridge = new global::CanonicalPath.BridgeCanonicalPathService();

        public static string Normalize(string input)
        {
            return s_bridge.Normalize(MapDefaultWslDrive(input)).ToString();
        }

        public static string MakeSafeFileName(string input, int maxLength)
        {
            return s_bridge.MakeSafeFileName(input, maxLength);
        }

        static string MapDefaultWslDrive(string input)
        {
            if (input == null || input.IndexOf('\0') >= 0)
            {
                return input;
            }

            var candidate = input.Replace('\\', '/');
            if (!candidate.StartsWith("/mnt/", System.StringComparison.Ordinal))
            {
                return input;
            }

            var rest = candidate.Substring(5);
            if (rest.Length == 0 || !IsAsciiLetter(rest[0]) || (rest.Length > 1 && rest[1] != '/'))
            {
                return input;
            }

            var drive = char.ToLowerInvariant(rest[0]);
            return rest.Length == 1 ? drive + ":/" : drive + ":/" + rest.Substring(2);
        }

        static bool IsAsciiLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }
    }
}
