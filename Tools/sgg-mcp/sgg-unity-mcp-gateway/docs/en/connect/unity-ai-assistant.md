[English](./unity-ai-assistant.md) | [Русский](../../ru/connect/unity-ai-assistant.md)

# Connect Unity AI Assistant


SGG Unity MCP Gateway can be added to Unity AI Assistant as an external MCP server through the project's `UserSettings/mcp.json`. The local SGG workflow does not require Unity Assistant chat, Unity Cloud, paid credits, subscription state, or the official Unity relay.

## Baseline Versus Optional Integration

**Works without the official package**

```text
Gateway and Bridge installation
project discovery
command discovery
compile and Console status
asset/script/scene/prefab commands
local policy and approvals
plans, checkpoints, skills, diagnostics, and artifacts when enabled
```

**Added when `com.unity.ai.assistant` is installed**

```text
Unity Assistant UI
package-provided skill sources
project-local MCP config UI
optional official relay compatibility diagnostics
```

The official package is optional for the SGG baseline.

## Configuration File

In Unity AI Assistant MCP `2.8`, open:

```text
Edit -> Project Settings -> AI -> Assistant Extensions -> Open Config File
```

The project-local file is:

```text
<UnityProject>/UserSettings/mcp.json
```

Do not commit a real token in this file.

## Preferred Config-Based Shim

```json
{
  "enabled": true,
  "path": "",
  "mcpServers": {
    "sgg-unity-mcp": {
      "type": "stdio",
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

Use the Unity MCP Window generated form when the CLI is not available through `PATH`; it can point to the installed portable runtime explicitly.

## Explicit URL Form

```json
{
  "enabled": true,
  "path": "",
  "mcpServers": {
    "sgg-unity-mcp": {
      "type": "stdio",
      "command": "sgg-unity-mcp-gateway",
      "args": [
        "stdio-shim",
        "--url",
        "http://127.0.0.1:27182/mcp",
        "--token-env",
        "SGG_UNITY_MCP_TOKEN"
      ],
      "env": {
        "SGG_UNITY_MCP_TOKEN": "<set-locally>"
      }
    }
  }
}
```

Prefer a process environment or supported local secret mechanism.

## Tool Names

Unity Assistant may prettify function labels, but the exact callable top-level tool ids remain:

```text
sgg_unity_projects
sgg_unity_describe
sgg_unity_call
sgg_unity_batch
sgg_unity_job
sgg_unity_logs
```

Unity capabilities such as `scene.hierarchy` or `asset.search` remain command catalog ids passed to `sgg_unity_call`. Do not create PascalCase aliases or top-level `Unity_*` copies.

## Optional Official Relay

The local Gateway can expose diagnostic state through `unity.relay.status` when the relevant command family is enabled.

An explicitly configured relay adapter may import allowlisted official tools as catalog entries named:

```text
official.<toolName>
```

Those entries:

- remain behind `sgg_unity_call`;
- do not add top-level MCP tools;
- pass through SGG policy and approval;
- may validate configured Unity-relative path fields;
- are disabled by default;
- do not make local SGG commands depend on Unity Cloud or relay availability.

A missing, paywalled, or approval-blocked relay is diagnostic information, not a failure of the local Gateway baseline.

## Skill Sources

When skill commands are enabled, the Gateway can discover:

```text
.mcp/skills/<skill>/SKILL.md
Assets/**/SKILL.md
Packages/**/AIAssistantSkills/**/SKILL.md
%APPDATA%/Unity/AIAssistantSkills/**/SKILL.md   opt-in
built-in example skills
```

Skills are prompt/resources only. They do not grant policy, enable commands, approve requests, or configure external MCP servers.

Use `skill.import` with `dry_run:true` before copying a reviewed skill into `.mcp/skills/`.

## Validation

After Unity Assistant reloads the configuration:

```text
1. call sgg_unity_projects
2. select the explicit project
3. describe the command catalog
4. call compile.status
```

If the client does not render native MCP elicitation, `ask`-gated calls use the pending-approval fallback. The Gateway security model does not depend on one specific client UI.

## Local Compatibility Examples

| Unity Assistant concept | Local SGG command |
| --- | --- |
| List/find assets | `asset.list`, `asset.search` |
| Read/find text | `asset.text.read`, `asset.text.find`, `asset.text.search` |
| Script hash/validation | `script.hash`, `script.validate` |
| Apply bounded script edits | `script.text_edits.apply` |
| Project settings | `project.settings.summary`, `project.settings.inspect` |
| Package information | `package.list`, `package.info` |
| Console | `logs.console.query`, `logs.console.read`, `logs.console.tail` |
| Scene inspection | `scene.loaded`, `scene.hierarchy`, `scene.search`, `scene.inspect_object` |
| Editor metadata | `editor.state.get`, `editor.windows.list`, `editor.menu.list` |

Availability is always determined by `sgg_unity_describe` for the selected project.

## Related Docs

- [Generic stdio clients](./generic-stdio.md)
- [Command catalog](../reference/commands.md)
- [Permissions and approvals](../guides/permissions.md)
- [Security](../reference/security.md)
