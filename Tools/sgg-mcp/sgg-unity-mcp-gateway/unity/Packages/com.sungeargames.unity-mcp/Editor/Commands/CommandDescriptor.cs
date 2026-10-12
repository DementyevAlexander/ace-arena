namespace SGG.UnityMCP.Editor.Commands
{
    public sealed class CommandDescriptor
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Package { get; set; } = "com.sungeargames.unity-mcp";
        public string Risk { get; set; } = "read";
        public string Idempotency { get; set; } = "safe";
        public int TimeoutMs { get; set; } = 10000;
        public string SchemaJson { get; set; } = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        public string[] Tags { get; set; } = new string[0];
        public string Capability { get; set; } = string.Empty;
        public string[] RequiresEditorStatus { get; set; } = new string[0];
        public string ResultShape { get; set; } = string.Empty;
        public string OutputLimitJson { get; set; } = string.Empty;
        public bool SupportsJob { get; set; }
        public bool SupportsCancel { get; set; }
        public string WrapperWaitJson { get; set; } = string.Empty;
        public string FeatureFlag { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string PackageVersion { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public string Trust { get; set; } = string.Empty;
    }

    public sealed class ManifestScanDiagnostic
    {
        public string Code { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string PackageVersion { get; set; } = string.Empty;
        public string ManifestPath { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    public sealed class ProjectManifestCandidate
    {
        public string ManifestPath { get; set; } = string.Empty;
        public bool Loadable { get; set; }
        public string Reason { get; set; } = string.Empty;
    }
}
