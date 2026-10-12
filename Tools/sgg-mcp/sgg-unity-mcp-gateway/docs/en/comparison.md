[English](./comparison.md) | [Русский](../ru/comparison.md)

# Comparison And Positioning


SGG Unity MCP Gateway is optimized as a local control plane for external coding agents working across several Unity projects. It is not positioned as a universal replacement for every Unity AI, automation, or Editor workflow.

## What It Optimizes For

```text
explicit project routing
small stable top-level MCP surface
just-in-time project catalogs
one Gateway for multiple open Editors
Windows/WSL path identity
local policy and exact human approvals
package-contributed agent APIs
bounded jobs, artifacts, diagnostics, and audit
```

## Compared With Unity AI Assistant / Official Unity MCP

Unity AI Assistant is the official in-Editor AI experience and is the natural choice when the user wants Unity-provided UI, services, generators, and official integrations.

SGG differs:

- it targets external clients such as OpenCode and Cursor;
- it works as a local-first baseline without Unity Assistant chat, Unity Cloud, credits/subscription, or official relay;
- it routes every call to an explicit project;
- it keeps Unity capabilities inside a command catalog;
- it can still be connected to Unity AI Assistant as an optional MCP client;
- an optional relay adapter can import explicitly allowlisted official tools as catalog entries while preserving SGG policy.

These are complementary approaches rather than a claim that one supersedes every use of the other.

## Compared With Broad Unity MCP Toolsets

Broad open-source Unity MCP packages are useful when immediate breadth and direct top-level operations are the priority.

SGG intentionally trades some simplicity for:

```text
eight stable top-level tools
explicit project on every call
per-project discovery
one external Gateway
feature/policy/approval layers
extension package trust
bounded output and jobs
```

Tradeoff: the client must describe commands and pass `args_json`, while a broad toolset may expose a more direct function per operation.

## Compared With Editor-Embedded MCP Servers

An Editor-embedded server can have a simpler one-project setup.

SGG keeps the MCP server outside Unity and uses a lightweight bridge:

Advantages:

- one endpoint for several projects;
- project-explicit routing;
- shared policy, audit, job, and client compatibility layer;
- no separate MCP server lifecycle inside every Editor.

Costs:

- one external local process;
- setup/runtime version health must be maintained;
- every command needs a project selector;
- bridge/Gateway compatibility matters.

## Compared With Unrestricted Local Automation

Shell scripts, raw Editor C# execution, and broad filesystem tools may be faster for one trusted operator task.

SGG prefers reviewability:

```text
schemas
risk classes
feature visibility
allow/ask/deny
native/pending approval
dry runs and preconditions
project-local artifacts
audit
no automatic risky replay
```

This adds ceremony but reduces ambient authority and wrong-project mistakes.

## Compared With Cloud Or Enterprise Control Planes

SGG's documented baseline is local-first. It does not claim:

```text
managed runners
remote project hosting
organization identity
OAuth/JWT/SSO
multi-tenant isolation
central policy service
hosted audit retention
```

Use a dedicated enterprise platform when those are required today.

## Compared With A Separate MCP Server Per Asset

An asset author can create a dedicated MCP server, but that adds processes, client configuration, versioning, and another trust boundary.

SGG lets the package add catalog commands to the existing project bridge:

```text
manifest-only handler or [McpCommand]
 -> project catalog
 -> same eight tools
 -> same policy/approval/audit
```

This is useful when the asset's API is naturally Unity Editor-local.

## Good Fit

- OpenCode or another external MCP client is part of the Unity workflow.
- Several local Unity projects may be open.
- Context cost from large top-level tool sets matters.
- Explicit routing is preferred over an active-session singleton.
- Local approvals/audit are useful.
- Unity assets should expose structured agent APIs.
- Windows/WSL interoperability matters.

## May Not Be A Fit

- You need a hosted service today.
- You require enterprise SSO/multi-user isolation.
- You prefer an implicit active Editor.
- You want unrestricted shell/filesystem/network/C# authority by default.
- You need every operation exposed as a separate top-level tool.
- Your workflow never needs Unity Editor integration and is better served by normal source-control/code tools.

## Related Docs

- [Architecture](./reference/architecture.md)
- [Compatibility](./reference/compatibility.md)
- [Limitations](./limitations.md)
- [Extension quick start](./extensions/quick-start.md)
