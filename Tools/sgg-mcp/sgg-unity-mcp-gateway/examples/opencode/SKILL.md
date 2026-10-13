---
name: sgg-unity-mcp-gateway
description: Use SGG Unity MCP Gateway from OpenCode to inspect, validate, troubleshoot, or safely modify Unity projects through MCP
compatibility: opencode
---

# SGG Unity MCP Gateway

Use this skill when the user asks to install, configure, validate, troubleshoot, inspect, or modify a Unity project through SGG Unity MCP Gateway.

Core rules:

- Always call `sgg_unity_projects` before using a Unity project unless the user already provided a known project id.
- Never assume a global active Unity project, and never call or invent `set_active`, `set_active_instance`, or `switch_project`.
- Every Unity command must include an explicit `project`.
- Use `sgg_unity_describe` for just-in-time command discovery before calling feature-gated or package-contributed commands.
- Pass command arguments through `args_json` as a JSON object encoded as a string.
- Use only Unity-relative paths: `Assets/...` and `Packages/...`.
- Do not bypass `permission_denied`, `confirmation_required`, feature flags, or policy approvals.
- Treat dynamic C# execution, runtime debugging, safe writes, and package-contributed commands as higher-risk workflows that require focused references and current command schemas.

Reference guide:

- Install the Gateway and Unity bridge: `examples/opencode/references/install.md`.
- Configure OpenCode MCP access: `examples/opencode/references/mcp-client-setup.md`.
- Discover projects, commands, schemas, feature flags, and package-contributed commands: `examples/opencode/references/command-discovery.md`.
- Handle permissions, policy, approvals, trust, and denials: `examples/opencode/references/policy-and-approvals.md`.
- Validate Unity-relative paths and PathGuard expectations: `examples/opencode/references/paths-and-pathguard.md`.
- Inspect scenes, prefabs, and assets semantically: `examples/opencode/references/semantic-inspection.md`.
- Perform bounded write operations safely: `examples/opencode/references/safe-writes.md`.
- Debug Play Mode/runtime state safely: `examples/opencode/references/runtime-debugging.md`.
- Use dynamic Unity Editor C# only when explicitly needed: `examples/opencode/references/dynamic-csharp.md`.
- Recover from common errors and logs: `examples/opencode/references/troubleshooting.md`.
- Validate a session before acting: `examples/opencode/references/validation-checklist.md`.
