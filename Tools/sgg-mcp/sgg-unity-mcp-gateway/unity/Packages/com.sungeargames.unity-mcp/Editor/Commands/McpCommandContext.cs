using SGG.UnityMCP.Editor.Bridge;

namespace SGG.UnityMCP.Editor.Commands
{
    public sealed class McpCommandContext
    {
        public McpCommandContext(BridgeRuntimeState state, string requestId, string project, string argsJson, string settingsJson)
        {
            State = state;
            RequestId = requestId ?? string.Empty;
            Project = project ?? string.Empty;
            ArgsJson = string.IsNullOrEmpty(argsJson) ? "{}" : argsJson;
            SettingsJson = string.IsNullOrEmpty(settingsJson) ? "{}" : settingsJson;
        }

        public BridgeRuntimeState State { get; }
        public string RequestId { get; }
        public string Project { get; }
        public string ArgsJson { get; }
        public string SettingsJson { get; }
    }
}
