[English](./architecture.md) | [Русский](../../ru/reference/architecture.md)

# Architecture


SGG Unity MCP Gateway is a local control plane between MCP clients and one or more Unity Editor projects.

## Topology

```text
MCP client
  OpenCode / Cursor / Unity AI Assistant / generic client
                         │
             direct HTTP / direct stdio / stdio shim
                         ▼
              SGG Unity MCP Gateway
         routing / catalog / policy / jobs / audit
              │             │             │
              ▼             ▼             ▼
        Unity Bridge A  Unity Bridge B  Unity Bridge C
        Project A       Project B       Project C
```

The Gateway is the MCP server. The Unity package is an Editor-only bridge, not another MCP server.

## Dual-Era MCP Transport

The same eight tools are served across two protocol eras:

```text
/mcp request
  modern 2026-07-28 -> stateless SDK 2 handler, fresh server per request, MRTR
  legacy MCP 2025   -> existing SDK 1 sessionful/stateless compatibility handler

direct --stdio      -> SDK 2 negotiates modern or legacy for one connection
stdio-shim          -> legacy-only compatibility adapter to the HTTP Gateway
```

Modern requests do not initialize a protocol session and never use `Mcp-Session-Id`. Legacy Roots observation remains on legacy instances only. Jobs, operation ids, queues, persisted approvals, policy, registry, and audit are application state shared across per-request modern server instances.

## Stable MCP Surface

The public top-level surface stays at eight tools:

```text
sgg_unity_projects
sgg_unity_stale_projects
sgg_unity_stale_registry_cleanup
sgg_unity_describe
sgg_unity_call
sgg_unity_batch
sgg_unity_job
sgg_unity_logs
```

Unity operations such as `scene.hierarchy`, `asset.search`, `test.run`, or package-contributed commands remain catalog entries. This allows capabilities to grow without putting every operation permanently into model tool context.

## Explicit Project Routing

There is no global active project.

Every command call resolves a supplied selector to one current project/instance. Selectors can come from the project discovery result, including the public project id, instance id, canonical root, display name when unambiguous, or a reported alias.

After resolution, queues, jobs, policy, cache, approval binding, and audit use the resolved identity. Ambiguous or duplicate identities fail instead of silently selecting an Editor.

## Project Discovery

Each Unity bridge publishes bounded heartbeat and command-catalog metadata to a user-local registry. The Gateway reads that registry and reports live, stale, reloading, or offline state.

The bridge remains responsible for Unity main-thread execution. The Gateway remains responsible for MCP transport, routing, local policy, approvals, jobs, virtual commands, audit, and external-client compatibility.

## Command Catalog

Each project has its own catalog because:

- package sets differ;
- package versions differ;
- feature flags can hide families;
- package-contributed manifests differ;
- Unity/editor capabilities can differ;
- descriptors can change after compilation or domain reload.

Agents use just-in-time discovery:

```text
projects -> describe commands -> describe exact command -> call
```

A catalog hash/revision lets the Gateway detect stale descriptors. On `catalog_stale`, describe again; do not reuse an old schema blindly.

## Invocation Pipeline

Conceptual order for one call:

```text
1. Parse top-level tool input.
2. Resolve explicit project/instance.
3. Resolve current command descriptor.
4. Verify feature visibility.
5. Validate args_json against the current schema.
6. Resolve package trust.
7. Evaluate project policy and risk.
8. If ask, use modern MRTR, legacy form elicitation, or pending approval.
9. Revalidate context after approval.
10. Admit to the project queue or create a job.
11. Invoke Gateway virtual logic, official relay adapter, or Unity Bridge.
12. Normalize and bound the result.
13. Write correlated redacted audit outcome.
```

Deny, invalid schema, stale project, failed revalidation, or unavailable feature stop execution before the mutation path.

## Concurrency

Commands within one Unity project are serialized where Unity/editor state requires it. `parallel_by_project` can run independent project queues concurrently.

This prevents an agent from accidentally interleaving multiple edits in one Editor while still allowing Project A and Project B to make progress independently.

## Wait And Job Modes

`mode:"wait"` returns the bounded operation result in the original call.

`mode:"job"` creates a Gateway-local job only for commands that support it. The client polls with `sgg_unity_job`. Large results should be stored as project-local artifacts and read in bounded slices.

## Path Identity And Safety

CanonicalPath provides deterministic lexical identity for project roots and Windows/WSL aliases. It is not a filesystem sandbox.

Security for Unity/project paths remains in:

```text
command schema
Unity-relative Assets/... and Packages/... rules
fixed Gateway storage roots
CanonicalFS where used
Unity-side PathGuard
package trust and policy
```

Agent payloads should not contain arbitrary Windows or WSL absolute paths.

## Approval Architecture

Ask-gated commands do not add an MCP self-approval tool.

For modern MCP `2026-07-28`, the Gateway returns `input_required` with an HMAC-signed opaque correlation state; the client fulfils the form and retries the original call. Legacy clients can use form elicitation. In both eras, Accept triggers full context revalidation and one exact execution at most once; request state is not proof of consent.

For clients without elicitation, `confirmation_required` creates a pending identity that a human reviews outside MCP. Approval authorizes only the exact bound request; the agent retries it explicitly.

## Extension Architecture

An installed Unity package can contribute commands through:

```text
[McpCommand] attributed Editor handler
Editor/Mcp/mcp.commands.json manifest-only handler
```

The bridge publishes descriptors in that project's catalog. The Gateway resolves package source and trust, then applies the same schemas, policy, approval, output limits, and audit.

A package cannot make itself trusted or add a new top-level tool.

## Local-First Boundary

The documented baseline is:

```text
trusted local developer machine
Gateway on Windows loopback
Unity Editors on Windows
optional OpenCode client in WSL through Windows stdio shim
user-controlled LocalAppData config, policy, approvals, audit, and runtime
```

This architecture does not claim hosted execution, multi-user tenant isolation, OAuth/JWT/SSO, or a sandbox against another process running as the same OS user.

## Related Docs

- [MCP tools](./mcp-tools.md)
- [Command catalog](./commands.md)
- [Configuration](./configuration.md)
- [Policy](./policy.md)
- [Security](./security.md)
