[English](./csharp-handlers.md) | [Русский](../../ru/extensions/csharp-handlers.md)

# C# Command Handlers


Use compile-time C# integration when a package intentionally depends on the SGG Unity MCP Editor API and needs typed bridge context or result helpers.

## Assembly Boundary

The handler must be in an Editor-only assembly. Reference the bridge Editor assembly intentionally and keep the package's runtime assemblies independent.

Example shape:

```csharp
using SGG.UnityMCP.Editor.Commands;

namespace Example.UnityMcpModule.Editor
{
    public static class ExampleCommands
    {
        [McpCommand("example.status", Risk = "read", Idempotency = "safe")]
        public static CommandExecutionResult Status(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(
                "{\"ok\":true,\"project\":" +
                JsonUtil.String(context.Project) +
                "}");
        }
    }
}
```

Use the actual public API from the installed package as the compile-time source of truth; examples may evolve with package versions.

## Supported High-Level Shapes

```text
static CommandExecutionResult Handler()
static CommandExecutionResult Handler(McpCommandContext context)
```

The attribute provides command identity and descriptor metadata. Use a metadata-only manifest only when package-level information is needed; do not add a second manifest `handler` for the same command id.

## Context Rules

Use `McpCommandContext` for current resolved invocation context, not as permission to access arbitrary state.

Good uses:

- the already resolved project identity;
- validated command arguments through supported context APIs;
- bridge-provided bounded helpers;
- request correlation metadata;
- project-local artifact conventions.

Avoid:

- inventing another active-project singleton;
- reading arbitrary absolute paths;
- starting external processes from a read command;
- network access hidden behind a non-network descriptor;
- bypassing PathGuard or policy;
- creating your own approval mechanism.

## Result Design

A command result should be:

```text
structured
bounded
deterministic where possible
explicit about changed state
explicit about warnings and next actions
free of secrets and raw host paths
```

For writes, report fields such as:

```text
changed
created_paths
changed_objects
warnings
validation
next_valid_actions
dry_run
```

Do not return success before Unity APIs completed the intended operation or the command explicitly returned an asynchronous operation/job id.

## Read Command Pattern

```csharp
[McpCommand(
    "example.settings.inspect",
    Risk = "read",
    Idempotency = "safe",
    Description = "Read bounded example package settings without changing assets")]
public static CommandExecutionResult Inspect(McpCommandContext context)
{
    // Validate supported selectors and return a bounded JSON object.
}
```

A read command must not silently rebuild, import, save, modify selection, enter Play Mode, or create files unless that side effect is part of its declared contract and risk.

## Write Command Pattern

A reviewable write should normally provide:

```text
read-only inspect/preflight command
dry_run=true
strict target selectors
precondition hash/value when practical
overwrite=false by default
ask-gated policy recommendation
post-write validation and next actions
```

Example descriptor intent:

```csharp
[McpCommand(
    "example.asset.generate",
    Risk = "write",
    Idempotency = "idempotent",
    Description = "Create one generated asset under a Unity-relative Assets/... output root")]
```

The implementation should reject absolute paths, traversal, package writes unless explicitly supported, existing destinations without reviewed overwrite, and output outside the documented root.

## Long Operations

Advertise job support only when the command integrates with the Gateway/bridge job contract. Large reports should be artifact-backed rather than returned inline.

A job-capable operation should document:

- whether cancellation is real;
- progress semantics, or that progress is unavailable;
- timeout and maximum work bounds;
- artifact family and storage root;
- terminal status contract;
- whether a retry could duplicate side effects.

## Domain Reload And Unity State

Unity compilation, package changes, and domain reload can rotate bridge instances. Do not keep unsafe static references across reload. Let the Gateway re-resolve project/instance state and never auto-repeat risky work after an uncertain delivery or timeout.

## Errors

Prefer typed stable error codes and safe bounded details. Do not return raw exception text, request bodies, credentials, absolute paths, or arbitrary Unity object dumps.

When arguments are wrong, let descriptor/schema validation fail before the handler where possible. Agents can then follow `schema_lookup` to the current descriptor.

## Tests

At minimum test:

```text
[ ] descriptor discovery;
[ ] strict schema acceptance/rejection;
[ ] read command has no mutation;
[ ] dry-run and real write parity;
[ ] path traversal/absolute path rejection;
[ ] output bounds;
[ ] expected Unity exception normalization;
[ ] package trust and policy behavior;
[ ] domain reload or stale-instance behavior when relevant;
[ ] package remains usable without Gateway for manifest-independent runtime code.
```

## Related Docs

- [Extension quick start](./quick-start.md)
- [Command manifests](./command-manifest.md)
- [Extension governance](./governance.md)
- [Extension testing](./testing.md)
