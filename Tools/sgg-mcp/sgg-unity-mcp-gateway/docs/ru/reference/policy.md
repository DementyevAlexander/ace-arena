[English](../../en/reference/policy.md) | [Русский](./policy.md)

# Policy Reference


Policy решает, будет visible/schema-valid command `allow`, `ask` или `deny`.

Это local operator configuration. Gateway не добавляет MCP tool для self-permission.

## Defaults

```json
{
  "defaults": {
    "read": "allow",
    "write": "ask",
    "execute": "deny",
    "destructive": "ask",
    "network": "deny"
  }
}
```

Это fallbacks; project rules и package trust могут быть stricter.

## Project Rules

```json
{
  "projects": {
    "game": {
      "allow": ["compile.status", "logs.*", "asset.search"],
      "ask": ["scene.save", "asset.move", "test.run"],
      "deny": ["shell.*", "process.*", "network.*"]
    }
  }
}
```

Предпочитайте narrow patterns. Broad wildcards требуют review.

## Decision Inputs

```text
resolved project identity
current command/descriptor revision
risk
allow/ask/deny patterns
risk defaults
package trust
policy revision/state
feature visibility
schema validity
```

Allow не раскрывает hidden command и не исправляет invalid arguments.

## Precedence

```text
explicit deny wins
untrusted package gate
specific project patterns
risk default
ask -> exact approval
```

`policy.explain` показывает решение без execution/approval consumption.

## Native Elicitation

Clients с advertised и rendered MCP form elicitation:

```text
ask
 -> form prompt в original call
 -> Accept / Decline / Cancel
 -> full revalidation
 -> exact request at most once
```

Accept не меняет policy. Context drift инвалидирует generation.

## Pending Approval

Без elicitation возвращается `confirmation_required`. Human approve-ит один pending id вне MCP, затем agent повторяет exact call.

Approval не создаётся из arbitrary model hash и не переносится на changed args/project/descriptor/instance/policy.

## Policy Reload

Valid stable replacement reload-ится между calls.

Invalid/missing/unstable replacement:

```text
policy degraded
LKG reads may continue
risky classes fail closed
blocked work not replayed
```

Valid file восстанавливает будущие calls, не прошлые.

## Profiles

```text
Read-only
Inspect
API Reflection
Unity Docs
Profiler Readonly
Profiler Recording
Package Management
Test Validation
Safe Writes
Runtime Debug
Allow All
```

Profile меняет policy. `Apply + Enable Flags` также меняет config и требует restart.

Read-only — smoke test. Allow All — только trusted local project и не trust-ит extensions автоматически.

## Package Trust

```json
{
  "trusted_packages": ["com.example.reviewed-tools"],
  "untrusted_package_default": "ask"
}
```

Package не self-trust-ится. Trusted package всё равно проходит deny/features/schema/paths/approval/limits/audit.

## Editing Policy

```text
Unity MCP Window -> Permissions
CLI apply-profile --dry-run
manual user policy edit + validation
```

Не разрешайте silent wildcard broadening как side effect Unity task.

## Temporary Auto-Allow

`approvals.auto_allow_ask` auto-allow-ит только exact `ask`. Не обходит deny/features/schema/trust/project/PathGuard.

Default off. Preferred interactive path — native elicitation на capable connection. OpenCode v2 реализует формы; реальный frontend/human acceptance проверяется отдельно.

## Связанные Документы

- [Права и подтверждения](../guides/permissions.md)
- [Configuration](./configuration.md)
- [Security](./security.md)
- [Extension governance](../extensions/governance.md)
