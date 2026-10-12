[English](./generic-stdio.md) | [Русский](../../ru/connect/generic-stdio.md)

# Connect A Generic Stdio Client


Use direct `--stdio` when the client should launch the Gateway process itself. It supports modern MCP `2026-07-28`, legacy MCP 2025, and modern multi-round approval requests. Use `stdio-shim` only to adapt a stdio-only client to an already-running HTTP Gateway.

| Mode | Protocol posture | Gateway lifecycle |
| --- | --- | --- |
| `sgg-unity-mcp-gateway --stdio` | Modern `2026-07-28` + legacy 2025 | The client launches the Gateway process. |
| `sgg-unity-mcp-gateway stdio-shim ...` | Legacy 2025 only | The HTTP Gateway must already be running. |

## Direct Stdio

```text
sgg-unity-mcp-gateway --stdio --config "%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json"
```

Use this mode for a full direct stdio connection. The connection negotiates one protocol era and pins one server instance for its lifetime. Modern calls use stateless request envelopes and MRTR; legacy calls retain initialization, Roots, and form elicitation compatibility.

## Architecture

```text
stdio-only MCP client
        │
        │ stdin/stdout
        ▼
sgg-unity-mcp-gateway stdio-shim
        │
        │ local HTTP MCP
        ▼
already-running SGG Unity MCP Gateway
        │
        ▼
Unity Bridge
```

The shim:

- does not connect to Unity directly;
- does not discover projects by itself;
- does not start a second Gateway;
- exposes only the eight stable SGG MCP tools;
- remains a legacy compatibility transport and does not relay modern MRTR.

## Start The Gateway First

Use the Unity MCP Window normal Setup flow. Verify Health before configuring the client.

Default endpoint:

```text
http://127.0.0.1:27182/mcp
```

## Config-Based Shim

Preferred when the client can launch the installed CLI:

```text
sgg-unity-mcp-gateway stdio-shim --config "%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json"
```

Generic JSON shape:

```json
{
  "mcpServers": {
    "sgg-unity-mcp": {
      "command": "sgg-unity-mcp-gateway",
      "args": [
        "stdio-shim",
        "--config",
        "%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json"
      ]
    }
  }
}
```

The shim derives the endpoint and token environment variable name from the same Gateway config.

## URL-Based Shim

```text
sgg-unity-mcp-gateway stdio-shim --url http://127.0.0.1:27182/mcp --token-env SGG_UNITY_MCP_TOKEN
```

```json
{
  "mcpServers": {
    "sgg-unity-mcp": {
      "command": "sgg-unity-mcp-gateway",
      "args": [
        "stdio-shim",
        "--url",
        "http://127.0.0.1:27182/mcp",
        "--token-env",
        "SGG_UNITY_MCP_TOKEN"
      ],
      "env": {
        "SGG_UNITY_MCP_TOKEN": "<local-placeholder>"
      }
    }
  }
}
```

Prefer the client secret store or process environment. Do not commit the real token.

## Validate

The client should expose exactly:

```text
sgg_unity_projects
sgg_unity_describe
sgg_unity_call
sgg_unity_batch
sgg_unity_job
sgg_unity_logs
```

Then run the read-only smoke test from [Quick Start](../quick-start.md).

## Approvals

Direct `--stdio` supports modern MRTR approvals and legacy form elicitation. The updated `stdio-shim` proxies legacy forms only when the outer client advertises `elicitation.form`; it is not a modern MRTR proxy. Non-capable, cancelled or failed exchanges use `confirmation_required` fallback, never implicit approval or replay.

## Troubleshooting

- A startup error usually means the HTTP Gateway is not running or the config/URL does not match.
- A token error means the shim process does not have the same `SGG_UNITY_MCP_TOKEN`.
- No projects means the MCP transport works but the Unity Bridge registry is empty or stale.
- Do not point the client at a Unity Bridge port; clients connect only to the Gateway.

## Related Docs

- [Installation](../installation.md)
- [MCP tools](../reference/mcp-tools.md)
- [Security](../reference/security.md)
