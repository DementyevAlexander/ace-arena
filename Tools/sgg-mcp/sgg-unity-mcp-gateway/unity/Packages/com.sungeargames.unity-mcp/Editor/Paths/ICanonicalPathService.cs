namespace SGG.UnityMCP.Editor.Paths
{
    [System.Serializable]
    public sealed class PathAlias
    {
        public PathAlias()
        {
        }

        public PathAlias(string environment, string hostPath, string canonicalPath)
        {
            this.environment = environment;
            host_path = hostPath;
            canonical_path = canonicalPath;
        }

        public string environment;
        public string host_path;
        public string canonical_path;
    }

    public readonly struct CanonicalPathValue
    {
        public CanonicalPathValue(string value)
        {
            Value = value;
        }

        public string Value { get; }

        public override string ToString()
        {
            return Value;
        }
    }

    public interface ICanonicalPathService
    {
        CanonicalPathValue Normalize(string input);
        CanonicalPathValue NormalizeProjectRoot(string input);
        PathAlias[] ProjectRootAliases(string input);
        CanonicalPathValue FromUnityAssetPath(CanonicalPathValue projectRoot, string unityPath);
        string ToUnityAssetPath(CanonicalPathValue projectRoot, CanonicalPathValue fullPath);
        void AssertInsideProject(CanonicalPathValue projectRoot, CanonicalPathValue candidate);
        string MakeSafeFileName(string input, int maxLength);
    }
}
