# Basic Command Module

This is a generic Unity package example that contributes one command to the Unity bridge catalog:

```text
example.status
```

## Install

Add this folder as a local Unity package for experimentation:

```text
examples/unity-modules/basic-command-module/package.json
```

The package does not depend on the bridge assembly API. It contributes a command through a manifest-only handler.

## Command

The command handler is declared as a regular static Editor method in `Editor/ExampleCommands.cs`. `Editor/Mcp/mcp.commands.json` points to it with a fully qualified handler name and provides the command metadata.

The package asmdef has no `SGG.UnityMCP.Editor` reference, so this package still compiles in Unity projects that do not install SGG Unity MCP.

Agents should discover it through:

```text
sgg_unity_describe(project, selector="commands")
```

Then invoke it through:

```text
sgg_unity_call(project, command="example.status", args_json="{}")
```

See [Add MCP Support To Your Unity Asset](../../../docs/en/extensions/quick-start.md) for the full custom command guide.
