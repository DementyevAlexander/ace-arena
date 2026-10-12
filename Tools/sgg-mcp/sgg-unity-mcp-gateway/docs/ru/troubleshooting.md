[English](../en/troubleshooting.md) | [Русский](./troubleshooting.md)

# Диагностика Проблем


Начинайте с симптома. Сначала Unity window, затем MCP diagnostics, потом CLI. Не расширяйте network exposure или policy только ради исчезновения error.

## Быстрая Таблица

| Симптом | Первое действие |
| --- | --- |
| Setup Health red | `Refresh Health`, затем `Copy Log`. |
| Client не подключается | Проверить connection mode/token. |
| Нет projects | Bridge status/Unity compile/registry health. |
| Stale/offline | Дождаться reload, modal diagnostics, projects again. |
| Ambiguous selector | Exact `instance_id`/canonical identity. |
| Missing command | Catalog/package/compile/feature. |
| `feature_disabled` | Enable required feature, restart. |
| `permission_denied` | Stop, policy review outside MCP. |
| `confirmation_required` | Native prompt только при advertised и rendered elicitation активного connection; иначе documented fallback. |
| Prompt не появился | Проверить version/capability, использовать fallback. |
| Schema error | `error.details.schema_lookup`. |
| WSL refused | Windows stdio shim или secured remote HTTP. |
| Unity frozen | Modal/startup/crash diagnostics. |
| Package operation stuck | status/list, no conflicting mutation. |
| Path rejected | Unity-relative/fixed-root contract. |

## Setup Health Red

```text
SGG/Unity MCP/MCP Window -> Setup
Refresh Health
Copy Log
```

Проверить:

```text
Bridge active/ready
Project Id
Gateway health/MCP URL
package/runtime mismatch
token state
package refresh/reload state
```

При stale runtime: `Install Runtime`, `Start Gateway`, `Refresh Health`. Не ставьте Gateway на каждый project.

## Client Не Подключается

```text
Gateway running
URL/path /mcp
token from SGG_UNITY_MCP_TOKEN
header X-SGG-Unity-MCP-Token
stdio shim -> running HTTP Gateway
correct WSL mode
```

Default:

```text
http://127.0.0.1:27182/mcp
```

Offline:

```powershell
sgg-unity-mcp-gateway doctor --json --config "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\gateway.json" --policy "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\policy.json"
```

## No Projects

Проверьте package install, bridge compile, CanonicalPath setup, Project Id, fresh heartbeat, shared registry root и startup modal.

Empty current registry не доказывает absence package — сообщайте только evidence.

## Stale/Offline/Rotated

```text
1. sgg_unity_projects
2. current ready instance
3. describe catalog
4. fresh explicit repeat только если всё ещё нужно
```

Reads may recover; risky commands не auto-replay-ятся. После timeout сначала inspect project.

## Duplicate/Ambiguous

Используйте exact `instance_id`, canonical root или alias. Не добавляйте implicit active project.

## Missing/Stale Command

```text
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="command:<id>")
```

Причины: package missing, compile failed, feature off, version mismatch, invalid manifest, removed/renamed, planned-only.

## Feature/Policy

```text
feature_disabled      -> enable required flag + restart
permission_denied     -> no bypass
confirmation_required -> exact human approval
policy_degraded       -> valid policy; risky fail closed
```

Policy не включает feature, feature не grant-ит permission.

## OpenCode Native Elicitation

Gateway показывает native MCP form только при advertised/rendered elicitation. OpenCode v2 реализует формы; реальный location/frontend показ и human acceptance проверяются отдельно. Working MCP connection или установленный plugin этого не доказывают.

Если нет:

1. Проверить advertised `elicitation.form` у client connection.
2. Проверить UI `elicitation/create` и nested requests; обновлённый Windows shim relay-ит формы только при outer capability `elicitation.form`. Проверьте установленную версию runtime.
3. Найти `confirmation_required`/`approval_workflow`.
4. Использовать pending fallback, не `auto_allow_ask`.
5. Не добавлять self-approval tool.

```powershell
sgg-unity-mcp-gateway approval pending --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --project game
sgg-unity-mcp-gateway approval approve --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --pending-id <pending-id>
```

Затем exact retry.

## Schema Errors

Execute `error.details.schema_lookup.metadata_lookup` как top-level tool. Не передавайте metadata operation в Unity `command`. После descriptor — новый corrected call.

## Windows И WSL

Recommended:

```text
OpenCode WSL -> powershell.exe -> Windows stdio shim -> loopback Gateway -> Bridge
```

Direct WSL HTTP к Windows loopback часто fail-ит в NAT. Remote HTTP требует host IP, non-loopback, token, origins, firewall. Не broad-expose Gateway.

## Unity Modal/Startup

```text
diagnostics.unity_startup.status
diagnostics.unity_modal.status
diagnostics.unity_recovery_prompt.status
diagnostics.unity_crash_reporter.status
```

Read-only diagnostics не кликают arbitrary dialogs. Response commands gated/dry-run-first.

## Compile/Refresh

```text
compile.status
compile.errors.recent / warnings
logs.console.*
```

После changes explicit `assets.refresh`, `compile.refresh_and_wait` или `validation.after_edit` по schema/policy.

`package_refresh_required`/`stale_package_resolution` не означают automatic `AssetDatabase.Refresh`.

## Package Operations

```text
package.operation.status
package.operation.list
```

```text
running -> poll
long_running -> inspect UI, no conflict
persisted_running_after_reload -> package.list + compile.status
stale_persisted_running -> stale evidence/diagnostics
```

PackageManager operation cancellation is unsupported: Unity Package Manager не предоставляет stable general cancellation/progress API для каждой операции. PackageManager cancellation is unsupported и при reload recovery; не invent progress/cancel. `package.operation.prune` dry-run first.

## Jobs/Artifacts

Exact ids, bounded polling. Large artifact: list -> metadata -> bounded read. Не derive host path.

## Path Rejected

```text
C:\Game\Assets\A.prefab     -> Assets/A.prefab
/mnt/c/Game/Assets/A.prefab -> Assets/A.prefab
../Assets/A.prefab          -> Assets/A.prefab без traversal
```

Exact allowed roots — в schema.

## Problem Report

```json
{
  "project": "game",
  "command": "diagnostics.report_problem",
  "args_json": "{\"summary\":\"Observed failure without secrets\",\"last_command\":\"compile.status\",\"last_error_code\":\"bridge_unreachable\",\"include_snapshot\":true}",
  "mode": "wait"
}
```

MCP offline — CLI `report-problem --include-doctor`.

## Что Приложить

```text
Gateway/package version
Unity exact version
client/mode
project selector
command/error code
Unity state
repro
expected/observed
checks run/not run
bounded redacted artifacts
```

Никогда: tokens, auth headers, keys, approval stores, raw credentials, unredacted proprietary content.

## Связанные Документы

- [Diagnostics](./guides/diagnostics.md)
- [Error codes](./reference/error-codes.md)
- [OpenCode](./connect/opencode.md)
- [Windows и WSL](./connect/windows-wsl.md)
- [Права](./guides/permissions.md)
