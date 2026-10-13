namespace SGG.UnityMCP.Editor.UI.State
{
    internal sealed class McpFtueState
    {
        public bool UnityBridgeReady;
        public bool PortableNodeReady;
        public bool GatewayRuntimeReady;
        public bool GatewayProcessReady;
        public bool TokenContractReady;
        public bool ClientConfigReady;
        public bool PermissionsReady;
        public bool PermissionProfileReady;
        public bool SmokeTestReady;

        public bool IsComplete
        {
            get
            {
                return UnityBridgeReady
                    && PortableNodeReady
                    && GatewayRuntimeReady
                    && GatewayProcessReady
                    && TokenContractReady
                    && ClientConfigReady
                    && PermissionsReady
                    && PermissionProfileReady
                    && SmokeTestReady;
            }
        }
    }
}
