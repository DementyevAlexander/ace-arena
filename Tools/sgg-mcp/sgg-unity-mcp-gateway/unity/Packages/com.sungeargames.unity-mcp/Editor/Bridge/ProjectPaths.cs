using System.IO;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Bridge
{
    [InitializeOnLoad]
    public static class ProjectPaths
    {
        static readonly string s_projectRoot = ResolveProjectRoot();

        public static string ProjectRoot
        {
            get { return s_projectRoot; }
        }

        public static string ProjectDisplayName => new DirectoryInfo(ProjectRoot).Name;

        public static string ProjectId => SanitizeProjectId(ProjectDisplayName);

        static string ResolveProjectRoot()
        {
            var assetsDirectory = new DirectoryInfo(Application.dataPath);
            return assetsDirectory.Parent == null ? Application.dataPath : assetsDirectory.Parent.FullName.Replace('\\', '/');
        }

        static string SanitizeProjectId(string value)
        {
            var chars = value.ToCharArray();
            for (var index = 0; index < chars.Length; index++)
            {
                var current = chars[index];
                if (char.IsLetterOrDigit(current))
                {
                    chars[index] = char.ToLowerInvariant(current);
                }
                else if (current == '-' || current == '_')
                {
                    chars[index] = current;
                }
                else
                {
                    chars[index] = '-';
                }
            }

            return new string(chars).Trim('-');
        }
    }
}
