[English](./quick-start.md) | [Русский](../../ru/extensions/quick-start.md)

# Add MCP Support To Your Unity Asset


Add an agent-facing command to a reusable Unity package without running another MCP server and without making the package depend on the Gateway at runtime.

The recommended first integration is a dependency-free command manifest.

## What You Will Build

```text
Packages/com.example.unity-mcp-module/
  package.json
  Editor/
    Example.UnityMcpModule.Editor.asmdef
    ExampleCommands.cs
    Mcp/
      mcp.commands.json
```

The package remains usable when SGG Unity MCP Gateway is not installed. When it is installed, the bridge discovers the manifest and adds the command to that project's catalog.

## 1. Create An Editor Assembly

`Editor/Example.UnityMcpModule.Editor.asmdef`:

```json
{
  "name": "Example.UnityMcpModule.Editor",
  "references": [],
  "includePlatforms": [
    "Editor"
  ],
  "autoReferenced": true
}
```

No SGG assembly reference is required for the manifest-only path.

## 2. Add A Static Handler

`Editor/ExampleCommands.cs`:

```csharp
namespace Example.UnityMcpModule.Editor
{
    public static class ExampleCommands
    {
        public static string Status()
        {
            return "{\"ok\":true}";
        }
    }
}
```

The returned string must be valid JSON for the command `data` payload.

## 3. Add The Manifest

`Editor/Mcp/mcp.commands.json`:

```json
{
  "schema_version": 1,
  "package": "com.example.unity-mcp-module",
  "commands": [
    {
      "id": "example.status",
      "title": "Example status",
      "description": "Read example module status",
      "handler": "Example.UnityMcpModule.Editor.ExampleCommands.Status",
      "risk": "read",
      "idempotency": "safe",
      "timeout_ms": 10000,
      "schema": {
        "type": "object",
        "properties": {},
        "additionalProperties": false
      },
      "output_limit": {
        "max_chars": 2048
      }
    }
  ]
}
```

The manifest package id must match the installed Unity package identity.

## 4. Let Unity Compile

Open the consumer project and wait until the Editor assembly compiles. The package command appears only in projects where that package is installed and successfully loaded.

## 5. Discover The Command

```text
sgg_unity_projects
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="command:example.status")
```

Never assume package commands exist across every project.

## 6. Call It

```json
{
  "project": "game",
  "command": "example.status",
  "args_json": "{}",
  "mode": "wait"
}
```

The command remains a catalog entry. It does not add a seventh top-level MCP tool.

## Handler Signatures

Manifest-only handlers support:

```text
static string Handler()
static string Handler(string argsJson)
static string Handler(string argsJson, string settingsJson)
```

Use `argsJson` only after defining a strict JSON Schema. Keep output bounded and return structured JSON instead of free-form Console text.

## When To Use `[McpCommand]`

Use compile-time bridge integration when the package intentionally references the SGG Editor API and needs:

- `McpCommandContext`;
- typed `CommandExecutionResult`;
- direct bridge helpers;
- richer job/artifact integration;
- descriptor metadata colocated with the handler.

See [C# handlers](./csharp-handlers.md).

## Trust And Policy

A package does not become trusted by shipping a manifest. The Gateway resolves package identity from Unity package metadata and applies:

```text
feature visibility
package trust
command schema
risk and policy
approval when action is ask
output limits
audit
```

Use [Extension governance](./governance.md) before adding write, execute, destructive, or network commands.

## Done Criteria

```text
[ ] Package works with no Gateway package installed.
[ ] Editor assembly has no accidental runtime inclusion.
[ ] Manifest package id matches package.json.
[ ] Handler name is fully qualified.
[ ] Schema rejects unknown fields.
[ ] Risk and idempotency are accurate.
[ ] Text output has a hard limit.
[ ] Command is discoverable only in the intended project.
[ ] Read-only command returns valid JSON.
[ ] Local manifest checks pass.
```

## Next Docs

- [Command manifests](./command-manifest.md)
- [C# handlers](./csharp-handlers.md)
- [Extension governance](./governance.md)
- [Extension testing](./testing.md)
