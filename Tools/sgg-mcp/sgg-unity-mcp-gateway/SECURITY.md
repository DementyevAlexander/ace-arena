# Security Policy

SGG Unity MCP Gateway is a local-first MCP gateway and Unity Editor bridge package. Treat it as trusted local developer tooling, not as an internet-facing service.

## Reporting Vulnerabilities

Report suspected vulnerabilities privately. Do not open a public issue or discussion with exploit details, traversal payloads, bridge tokens, local project paths, registry paths, or private project names.

Use GitHub private vulnerability reporting or a private GitHub security advisory for `romanilyin/sgg-unity-mcp-gateway` when available. If that route is unavailable, contact the repository owner `@romanilyin` privately and include enough detail to reproduce the issue.

Please include:

- Affected surface: MCP HTTP endpoint, project registry, Unity bridge transport, command policy, PathGuard, CanonicalPath integration, package installer, dynamic C# execution, runtime debugging, logs, config, bundled runtime, or workflow/release automation.
- Minimal reproduction steps and relevant platform details.
- Expected impact: path traversal, project escape, unauthorized command execution, token exposure, policy bypass, network exposure, unsafe package install, secret disclosure, denial of service, or supply-chain issue.
- Whether the issue is already public.

Expected response targets are best-effort and do not create a support SLA:

- Initial acknowledgement within 7 days.
- Triage update within 14 days after acknowledgement when the report is reproducible.
- Coordinated disclosure timing agreed per issue severity and fix availability.

## Current Guarantees

- The gateway binds to loopback by default in example configuration.
- Project selection is explicit; there is no global active Unity project.
- Public MCP tools route Unity capabilities through command catalog entries and policy checks.
- Unity asset/file payloads are expected to use Unity-relative `Assets/...` and `Packages/...` paths.
- CanonicalPath is used for deterministic lexical path identity across Windows and WSL spellings.
- Unity-side PathGuard validates bridge payload paths before Unity asset operations.
- Feature-gated high-risk command families are hidden unless enabled in local configuration.

## Non-Guarantees

- CanonicalPath lexical normalization is not a filesystem sandbox.
- PathGuard is bridge payload validation and is not a TOCTOU-proof filesystem security boundary.
- The gateway does not provide enterprise OAuth/JWT/PKCE unless those controls are explicitly implemented.
- Do not expose gateway or bridge ports to untrusted networks without separately reviewed authentication, authorization, and network controls.
- Gateway policy uses allow/ask/deny decisions; approvals are created outside MCP, and an agent cannot approve its own command.
- `checkpoint_id` and `plan_id` are audit correlation metadata only and are not authorization tokens.
- Dynamic Unity Editor C# execution runs inside a trusted local Unity Editor process and is not a sandbox.

## Scope

Security reports are especially relevant for path traversal, project escape, command policy bypass, token leaks, unsafe local/network package installation, bridge transport exposure, log redaction failures, bundled runtime tampering, and workflow supply-chain issues.

For the local-first security model and command risk policy, see `docs/en/reference/security.md`.
