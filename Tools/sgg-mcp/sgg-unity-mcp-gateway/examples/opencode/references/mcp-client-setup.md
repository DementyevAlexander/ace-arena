# MCP Client Setup

The default Gateway MCP endpoint is:

```text
http://127.0.0.1:27182/mcp
```

If OpenCode runs in WSL and the Gateway runs on Windows, `127.0.0.1` may point to WSL instead of Windows. Prefer the MCP Window `Setup -> MCP Connection -> WSL via Windows stdio shim` mode, which creates/updates project `opencode.jsonc` without opening the Gateway outside Windows loopback.

Recommended OpenCode local MCP block:

```jsonc
{
  "mcp": {
    "servers": { "sgg-unity-mcp": {
      "type": "local",
      "command": ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "$root=Join-Path $env:LOCALAPPDATA 'SGG\\UnityMcpGateway'; $pointer=Join-Path $root 'gateway\\active-runtime.json'; $runtime=(Get-Content -Raw $pointer | ConvertFrom-Json).runtime_root; $node=Join-Path $root 'runtime\\node-v22.16.0-win-x64\\node.exe'; $cli=Join-Path $runtime 'gateway-cli.mjs'; & $node $cli stdio-shim --url 'http://127.0.0.1:27182/mcp' --token-env 'SGG_UNITY_MCP_TOKEN' --legacy-text-result"],
      "disabled": false,
      "protocol": "legacy",
      "timeout": { "startup": 30000, "catalog": 30000, "execution": 3900000 }
    } }
  }
}
```

For Remote HTTP mode, bind the Gateway to a trusted non-loopback interface, configure token auth/firewall controls, and use the same token value in OpenCode through the `X-SGG-Unity-MCP-Token` header.

Remote HTTP MCP block:

```jsonc
{
  "mcp": {
    "servers": { "sgg-unity-mcp": {
      "type": "remote",
      "url": "http://127.0.0.1:27182/mcp",
      "headers": {
        "X-SGG-Unity-MCP-Token": "{env:SGG_UNITY_MCP_TOKEN}"
      },
      "oauth": false,
      "disabled": false,
      "timeout": { "startup": 30000, "catalog": 30000, "execution": 3900000 }
    } }
  }
}
```

Use native OpenCode v2 configuration. Execution `3900000` ms covers the longest supported one-hour wait plus overhead; startup/catalog are separate. Preserve deliberate disable and larger execution values. Never replace this with the unrelated inbound request-body timeout or write the token itself into JSON.

Code Mode is enabled by default: discover tools in its catalog and call through `execute`. `codemode:false` is an explicit direct-call alternative. A missing provider-native tool alone does not prove missing MCP exposure. Reload and verify the active client; one-file setup does not verify all global/ancestor/hidden overrides.

The updated legacy shim relays forms only for outer `elicitation.form` clients, binds concurrent call ownership and never replays after reconnect. Real human UI acceptance is separate. Plugin metadata alone does not fix stock v2 Web/Code Mode rendering; see `docs/en/maintainers/opencode-v2-client-follow-up.md`.

The URL points to the Gateway, not to Unity Editor. Do not claim OAuth, JWT, PKCE, or enterprise authentication unless it is actually implemented in the deployed Gateway.
