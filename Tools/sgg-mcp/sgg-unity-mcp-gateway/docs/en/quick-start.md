[English](./quick-start.md) | [Русский](../ru/quick-start.md)

# Quick Start


This path gets one local Unity project from package installation to the first successful read-only MCP call.

## Goal

You are done when all four conditions are true:

```text
[ ] Unity MCP Window reports an active Bridge.
[ ] Gateway Health is green.
[ ] sgg_unity_projects lists the open project.
[ ] compile.status returns a read-only result for that explicit project.
```

## Requirements

- Unity Editor on Windows.
- A Unity project that can compile Editor assemblies.
- Access to a pinned repository tag or a local checkout of this package.
- OpenCode, Cursor, Unity AI Assistant, or another MCP client.
- For normal Setup, no system-wide Node.js installation is required.

Unity `6000.4+` is the recommended development baseline. The package manifest currently keeps Unity `2022.3` as an import/compile floor; do not infer full release support from that metadata alone.

## 1. Install The Unity Package

Pinned Git UPM dependency:

```json
{
  "dependencies": {
    "com.sungeargames.unity-mcp": "https://github.com/romanilyin/sgg-unity-mcp-gateway.git?path=/unity/Packages/com.sungeargames.unity-mcp#2026.10.12-2"
  }
}
```

For a local checkout, use Unity Package Manager **Add package from disk...** and select:

```text
<gateway-repository>/unity/Packages/com.sungeargames.unity-mcp/package.json
```

Keep Unity open and wait until the package finishes compiling.

## 2. Complete Required Setup

Open:

```text
SGG/Unity MCP/MCP Window
```

On the `Setup` page:

```text
1. Install or configure CanonicalPath when Setup reports it missing.
2. Install Portable Node.
3. Install Runtime.
4. Start Gateway.
5. Refresh Health.
```

CanonicalPath is required for deterministic project identity and path-bound Bridge commands. Depending on the package build, the button may be grouped under dependencies or optional tools; treat a missing required provider as a Setup action, not as an optional production configuration.

The normal flow installs files under:

```text
%LOCALAPPDATA%/SGG/UnityMcpGateway/
```

It does not modify the system `PATH`.

If Health is not green, use `Copy Log` on the same page before changing network or policy settings.

## 3. Create Config And Select A First Profile

On `Setup -> Permission Setup`:

```text
1. Create Config/Policy Files.
2. Optionally apply Read-only for the first smoke test.
3. Restart Gateway if Setup changed feature/config state.
```

Read-only is intentionally limited. It is enough for project discovery, compile status, bounded logs, and a few safe status reads. Use `Permissions -> Apply + Enable Flags` later when the task needs Inspect, Safe Writes, Package Management, Runtime Debug, or another profile.

## 4. Connect One MCP Client

Use the copy helper in `Setup -> MCP Connection`.

Recommended starting points:

- OpenCode on Windows or in WSL: [OpenCode setup](./connect/opencode.md).
- Cursor: [Cursor setup](./connect/cursor.md).
- Unity AI Assistant: [Unity AI Assistant setup](./connect/unity-ai-assistant.md).
- Any stdio-only client: [Generic stdio setup](./connect/generic-stdio.md).

For OpenCode in WSL on the same machine, prefer **WSL via Windows stdio shim**. It keeps the Gateway bound to Windows loopback and does not require a non-loopback listener or firewall rule.

## 5. Run The First Calls

Start with project discovery:

```text
sgg_unity_projects
```

Select a live `project` id from the result, then discover that project's catalog:

```text
sgg_unity_describe(project="<project-id>", selector="commands")
```

Run the safe compile-state read:

```json
{
  "project": "<project-id>",
  "command": "compile.status",
  "args_json": "{}",
  "mode": "wait"
}
```

Expected behavior:

- no project asset is modified;
- the result identifies current compile/readiness state;
- an unavailable project returns a typed error instead of silently switching to another Editor;
- the agent never relies on an implicit active project.

## 6. Try A Bounded Console Read

First verify that the relevant log command appears in the selected project catalog. A typical call is:

```json
{
  "project": "<project-id>",
  "command": "logs.console.read",
  "args_json": "{\"limit\":50,\"types\":\"error,warning\",\"include_stack_trace\":false}",
  "mode": "wait"
}
```

Keep result counts and stack traces bounded.

## Native Approval Prompts

The first smoke test is read-only and should not require approval.

For later `ask`-gated commands:

- An advertised/rendered MCP form connection can display native requests. OpenCode v2 implements forms; the updated shim relays them capability-aware. Actual frontend/human acceptance is separate.
- The user explicitly accepts, declines, or cancels the one exact request.
- The Gateway revalidates project, command, feature, schema, and policy state before execution.
- Clients without elicitation receive `confirmation_required` and use fallback behavior; exact stale-registry cleanup has a separately configured narrow compatibility exception.

See [Permissions and approvals](./guides/permissions.md).

## Something Failed?

Use this order:

```text
1. Setup -> Refresh Health
2. Setup -> Copy Log
3. Status -> verify Project Id and Bridge state
4. Call sgg_unity_projects again
5. Read the typed error code and next_valid_actions
6. Open Troubleshooting
```

Do not enable all feature flags or `Allow All` merely to make the first read-only smoke test pass.

## Next Steps

- [Common workflows](./guides/workflows.md)
- [Permissions and approvals](./guides/permissions.md)
- [Multi-project routing](./guides/multi-project.md)
- [Troubleshooting](./troubleshooting.md)
