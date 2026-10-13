# Install

Install Node dependencies from the Gateway repository:

```bash
npm ci
```

Start the Gateway with a local token and the public example config/policy:

```powershell
$env:SGG_UNITY_MCP_TOKEN = "replace-with-local-token"
npm start -- --config config/gateway.example.json --policy config/policy.example.json
```

Install the Unity bridge package in each Unity project that agents should control:

```text
unity/Packages/com.sungeargames.unity-mcp/package.json
```

Use Unity Package Manager `Add package from disk...`, or add an equivalent local `file:` dependency to the target project's `Packages/manifest.json`. Wait until Unity finishes compiling so the bridge can write heartbeat records.

Keep the Gateway local-first. Do not expose Gateway or bridge ports to untrusted networks, and do not log or commit local tokens.
