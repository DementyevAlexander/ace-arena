[English](../../en/reference/security.md) | [Русский](./security.md)

# Безопасность


Security model local-first. Он снижает accidental authority и wrong-project risk на trusted developer workstation, но не sandbox против hostile code того же OS user.

## Security Quick Start

```text
1. Gateway на 127.0.0.1.
2. SGG_UNITY_MCP_TOKEN до non-loopback.
3. Same-machine WSL через Windows stdio shim.
4. read allow, write ask, execute deny, destructive ask, network deny.
5. Только required feature families.
6. Explicit project каждый call.
7. Stop на permission_denied/confirmation_required.
8. Audit для sensitive sessions.
9. Unity-relative paths/fixed roots.
10. Dynamic C# — high-risk automation.
```

## Threats

- wrong project/instance;
- stale/ambiguous selection;
- accidental mutation;
- execute/network/destructive authority;
- extension self-trust;
- unbounded output;
- Windows/WSL path confusion;
- secret leakage;
- auto-replay uncertain delivery;
- model self-approval.

## Controls

```text
explicit project
eight tools + JIT catalog
strict schemas
feature flags
risk metadata
allow/ask/deny
exact one-shot approval
package trust
Unity-relative paths
PathGuard/fixed roots
bounds
redacted audit/diagnostics
no risky auto-replay
```

## Token И Network

Не expose-ите Gateway untrusted LAN/internet. Non-loopback требует token, `allow_non_loopback=true`, non-empty `allowed_origins`, scoped firewall и reviewed clients.

Unity bridge остаётся loopback-local.

## Approvals

Ask-gated Unity commands используют native elicitation или pending fallback. Agent не создаёт general approval через MCP. Отдельное configured временное no-elicitation exception действует только для exact precondition-bound `registry.stale.cleanup`; оно не разрешает Unity commands и не интерпретирует chat text как approval.

Approval bound к exact resolved context; Accept revalidates. Changed/expired context invalid.

Approval id/hash/checkpoint_id/plan_id не reusable auth token.

## Policy Failure

Invalid policy не fail-open. Latest valid policy остаётся для safe reads; risky classes fail closed до recovery.

## Extension Trust

Package не может self-trust/self-approve/enable feature/impersonate built-ins/bypass controls. `trusted_packages` только после review; untrusted default `ask`/`deny`.

## Paths

```text
Assets/...
Packages/...
```

Workflow roots fixed under `Library/SGGUnityMcp/` или `.mcp/`. CanonicalPath identity не grant-ит access; enforcement — schemas/fixed roots/CanonicalFS/PathGuard.

## Dynamic C#

Не sandbox. Prefer normal/read-only commands; short/bounded; no process/network/arbitrary path; no approval bypass; trusted projects only.

## Sensitive Read Features

- `features.addressables_readonly`, `features.gameplay_data_readonly`, `features.ui_text_readonly` и `features.ui_readonly` открывают bounded read-only families; `gameplay.data.validation.report` остаётся read-only.
- `frame_debugger.status` и `frame_debugger.events` читают state без enable, step или capture Frame Debugger.
- Editor screenshots и Desktop region screenshots могут содержать privacy-sensitive read output. Проверяйте выбранное окно/region и храните artifacts только в documented project-local roots.

## Audit И Redaction

Audit хранит safe ids, risk/policy/source/trust, timing/status и hashes redacted args, не raw payload.

Не возвращать tokens, auth headers, cookies, passwords, approval key или unrestricted paths.

Roots — owner-controlled LocalAppData. Windows ACL не полный same-user sandbox.

## Uncertain Delivery

Risky commands не auto-retry-ятся после timeout/stale/protocol uncertainty. Сначала inspect state, потом fresh explicit call.

## Non-Goals

```text
hosted/cloud trust
multi-user isolation
OAuth/JWT/SSO
Unity sandbox
hostile same-user defense
safe untrusted C#
complete rollback
```

## Security Reports

Используйте `SECURITY.md`. Не кладите secrets/private files/raw approval stores/unredacted logs в public issue.

## Связанные Документы

- [Права и подтверждения](../guides/permissions.md)
- [Policy](./policy.md)
- [Configuration](./configuration.md)
- [Extension governance](../extensions/governance.md)
