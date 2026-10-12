using System;

namespace SGG.UnityMCP.Editor.Commands
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class McpCommandAttribute : Attribute
    {
        public McpCommandAttribute(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Package { get; set; } = string.Empty;
        public string Risk { get; set; } = "read";
        public string Idempotency { get; set; } = "safe";
        public int TimeoutMs { get; set; } = 10000;
        public string SchemaJson { get; set; } = "";
        public string[] Tags { get; set; } = new string[0];
        public string Capability { get; set; } = "";
        public string[] RequiresEditorStatus { get; set; } = new string[0];
        public string ResultShape { get; set; } = "";
        public string OutputLimitJson { get; set; } = "";
        public bool SupportsJob { get; set; }
        public bool SupportsCancel { get; set; }
        public string WrapperWaitJson { get; set; } = "";
        public string FeatureFlag { get; set; } = "";
    }
}
