[English](./security.md) | [Русский](../../ru/reference/security.md)

# Security


The security model is local-first. It reduces accidental authority and confused-project risk for trusted developer workstations; it is not a sandbox against hostile code running as the same operating-system user.

## Security Quick Start

```text
1. Keep the Gateway on 127.0.0.1 by default.
2. Configure SGG_UNITY_MCP_TOKEN before any intentional non-loopback access.
3. Use the Windows stdio shim for same-machine WSL where possible.
4. Start with read allow, write ask, execute deny, destructive ask, network deny.
5. Enable only required feature families.
6. Use explicit project routing on every call.
7. Stop on permission_denied or confirmation_required.
8. Keep audit enabled for sensitive sessions.
9. Use Unity-relative paths and fixed artifact roots.
10. Treat dynamic C# execution as high-risk local automation.
```

## Threats Addressed

- command sent to the wrong open Unity project;
- stale/ambiguous project instance selection;
- accidental scene, prefab, asset, package, or settings mutation;
- model-driven execute/network/destructive actions;
- extension package self-trust;
- unbounded logs/results/artifacts;
- Windows/WSL path confusion;
- secrets copied into logs or reports;
- automatic replay after uncertain Unity delivery;
- model self-approval.

## Main Controls

```text
explicit project selector
eight-tool surface plus JIT catalog
strict command schemas
feature flags
risk metadata
allow / ask / deny policy
exact one-shot human approval
package trust boundary
Unity-relative path rules
PathGuard and fixed storage roots
bounded time/size/rate/session limits
redacted audit and diagnostics
no automatic risky-command replay
```

## Token And Network Exposure

Loopback clients can use the local default. A token is recommended even locally and required by the documented non-loopback safety posture.

Never expose the Gateway to an untrusted LAN or the internet. Non-loopback requires:

```text
token authentication
allow_non_loopback=true
non-empty exact allowed_origins
scoped Windows Firewall/local network controls
reviewed client identities
```

The Unity bridge itself remains loopback-local and is contacted by the Gateway, not directly by WSL/network clients.

## Approvals

Ask-gated Unity commands use either native MCP elicitation or the pending fallback. The agent cannot create a general approval through MCP. The separately configured temporary no-elicitation exception applies only to exact precondition-bound `registry.stale.cleanup`; it does not authorize Unity commands or interpret chat text as approval.

Approval is bound to the exact resolved project/instance/catalog/descriptor, validated arguments, mode, risk, and effective policy context. Accept triggers revalidation. Changed/expired context invalidates the request.

An approval id, hash, `checkpoint_id`, or `plan_id` is not a reusable authorization token.

## Policy Failure

A missing or invalid configured policy does not fail open.

The Gateway retains the latest valid policy for safe read behavior and enters degraded state. Write, execute, destructive, network, and not-safely-classified actions fail closed until a valid policy is restored.

## Extension Trust

Package manifests are untrusted metadata. A package cannot:

```text
mark itself trusted
set its own project allow rules
approve itself
enable its feature
impersonate built-ins
bypass schema/output/path controls
```

Use `trusted_packages` only after reviewing package identity and implementation. Keep untrusted default at `ask` or `deny`.

## Paths

Normal agent payloads use:

```text
Assets/...
Packages/...
```

Some Gateway-local workflow commands use fixed roots under `Library/SGGUnityMcp/` or `.mcp/` and do not accept arbitrary host paths.

CanonicalPath creates lexical identity. It does not grant access. Root-bound enforcement remains in schemas, fixed roots, CanonicalFS where used, and Unity-side PathGuard.

## Dynamic C# Execution

Dynamic Editor C# is not a sandbox. When enabled:

- prefer normal catalog commands;
- prefer read-only execution for inspection;
- keep snippets short and output bounded;
- do not spawn processes;
- do not access network;
- do not touch arbitrary paths;
- do not bypass approval;
- use only in trusted local projects.

## Sensitive Read Features

- `features.addressables_readonly`, `features.gameplay_data_readonly`, `features.ui_text_readonly`, and `features.ui_readonly` expose bounded read-only families; `gameplay.data.validation.report` remains read-only.
- `frame_debugger.status` and `frame_debugger.events` inspect state without enabling, stepping, or capturing the Frame Debugger.
- Editor screenshots and Desktop region screenshots can contain privacy-sensitive read output. Review the selected window/region and keep artifacts under the documented project-local roots.

## Audit And Redaction

Sensitive calls should have correlated admission and terminal evidence. Audit records safe identifiers, risk/policy/source/trust metadata, timing, status, and hashes of redacted arguments rather than raw payloads.

Diagnostics and logs should never return token values, authorization headers, cookies, passwords, approval key material, or unrestricted host paths.

Keep audit, approvals, registry, runtime, and secrets under the current user's controlled LocalAppData. Windows ACL ownership cannot be treated as a complete same-user sandbox boundary.

## Uncertain Delivery

Write/execute/destructive/network commands are not automatically retried after bridge timeout, stale instance rotation, protocol failure, or other uncertain delivery.

Inspect current Unity/project/artifact state, then issue a fresh explicit call only when the operation is still intended.

## Non-Goals

The baseline does not claim:

```text
hosted/cloud trust boundary
multi-user tenant isolation
OAuth, JWT, SSO, enterprise identity
container/VM sandboxing of Unity
defense against hostile same-user processes
safe execution of untrusted C#
complete rollback of Editor/project state
```

## Reporting Security Issues

Use the repository's `SECURITY.md`. Do not include live secrets, private project files, raw approval stores, or unredacted logs in a public issue.

## Related Docs

- [Permissions and approvals](../guides/permissions.md)
- [Policy](./policy.md)
- [Configuration](./configuration.md)
- [Extension governance](../extensions/governance.md)
