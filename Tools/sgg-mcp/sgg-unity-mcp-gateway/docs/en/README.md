[English](./README.md) | [Русский](../ru/README.md)

# Documentation


Choose the shortest path that matches your task.

## Start

- [Quick Start](./quick-start.md) — install the package, connect one MCP client, and reach the first successful `compile.status`.
- [Installation](./installation.md) — Git UPM, local checkout, portable runtime, updates, removal, and advanced manual mode.
- [Troubleshooting](./troubleshooting.md) — symptom-first recovery for Setup Health, connection, project discovery, policy, and Unity state.
- [Limitations](./limitations.md) — the explicit product contract and non-goals.

## Connect A Client

- [OpenCode v2](./connect/opencode.md) — native setup, Code Mode, WSL via Windows stdio shim, remote HTTP, and capability-aware approvals.
- [Cursor](./connect/cursor.md) — stdio shim or an intentionally configured remote endpoint.
- [Unity AI Assistant](./connect/unity-ai-assistant.md) — project-local `UserSettings/mcp.json` and optional official-package compatibility.
- [Generic stdio clients](./connect/generic-stdio.md) — adapt a stdio-only client to the already-running HTTP Gateway.
- [Windows and WSL](./connect/windows-wsl.md) — topology, NAT versus mirrored networking, firewall, and path rules.

## Use The Gateway

- [Common workflows](./guides/workflows.md) — compile diagnosis, semantic inspection, reviewed edits, tests, builds, plans, skills, and project knowledge.
- [Permissions and approvals](./guides/permissions.md) — feature flags, `allow`/`ask`/`deny`, capability-based elicitation, and fallback approvals.
- [Multi-project routing](./guides/multi-project.md) — project ids, aliases, duplicate instances, and parallel project queues.
- [Jobs and artifacts](./guides/jobs-and-artifacts.md) — long operations, polling, cancellation, bounded output, and project-local artifacts.
- [Diagnostics](./guides/diagnostics.md) — redacted problem reports, audit reads, recovery hints, and schema lookup.
- [Canvas/uGUI to UI Toolkit](./guides/canvas-to-uitk.md) — safe side-by-side migration planning and generation.

## Add MCP Support To Your Unity Asset

- [Extension quick start](./extensions/quick-start.md) — a dependency-free manifest command in one small package.
- [Command manifests](./extensions/command-manifest.md) — descriptor schema, handler signatures, risk, output limits, and jobs.
- [C# handlers](./extensions/csharp-handlers.md) — `[McpCommand]`, `McpCommandContext`, and direct Editor integration.
- [Extension governance](./extensions/governance.md) — built-in, trusted-package, and untrusted-package boundaries.
- [Extension testing](./extensions/testing.md) — compile-without-Gateway, descriptor checks, policy tests, and negative fixtures.

## Reference

- [Architecture](./reference/architecture.md)
- [MCP tools](./reference/mcp-tools.md)
- [Command catalog](./reference/commands.md)
- [Configuration](./reference/configuration.md)
- [Policy](./reference/policy.md)
- [Error codes](./reference/error-codes.md)
- [Compatibility](./reference/compatibility.md)
- [Security](./reference/security.md)
- [License FAQ](./reference/license.md)
- [Comparison](./comparison.md)

## Maintainers

- [Versioning](./maintainers/versioning.md)
- [Validation evidence](./maintainers/validation.md)
- [Manual checks](./maintainers/manual-checks.md)
- [OpenCode v2 client follow-up](./maintainers/opencode-v2-client-follow-up.md) — proposed client-only work; OpenCode is not patched by Gateway setup.
- [OpenCode v2 Gateway validation](./maintainers/opencode-v2-gateway-validation.md) — executed evidence and remaining client acceptance gates.

Machine-facing usage rules stay in [`AGENTS.md`](../../AGENTS.md). The generated command inventory remains the authoritative descriptor-derived list and is linked from [Command catalog](./reference/commands.md).
