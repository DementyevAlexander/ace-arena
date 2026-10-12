[English](../../en/reference/configuration.md) | [Русский](./configuration.md)

# Configuration


Setup window создаёт user-editable config:

```text
%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json
%LOCALAPPDATA%/SGG/UnityMcpGateway/config/policy.json
```

Schema examples — `config/gateway.example.json` и `config/policy.example.json`. Не коммитьте tokens, approval keys, user paths или production policy.

## Normal Setup

```text
SGG/Unity MCP/MCP Window -> Setup
```

Window ставит portable Node, копирует bundled runtime, создаёт config/policy, запускает Gateway, даёт safe client snippets и health.

Для normal workflows предпочитайте UI ручному JSON editing.

## MCP Transport

| Field | Назначение |
| --- | --- |
| `host` | Bind address, default loopback. |
| `port` | Gateway port, docs default `27182`. |
| `token_env` | Token environment variable. |
| `allowed_origins` | Exact browser Origin allowlist. |
| `allow_non_loopback` | Explicit non-loopback opt-in. |
| `max_body_bytes` | Request size bound. |
| `request_timeout_ms` | Request bound. |
| `shutdown_timeout_ms` | Shutdown bound. |
| `sessions` | Stateful session caps/TTL. |
| `rate_limit` | Per-client bounds/trusted proxy mode. |

Non-loopback требует token, allowed origins и scoped firewall/network controls.

## Token

```text
Environment variable: SGG_UNITY_MCP_TOKEN
Client header:        X-SGG-Unity-MCP-Token
```

Copy helpers используют placeholders. Храните secret в process environment/client secret store, не в committed files/screenshots/issues.

## Registry

```text
instances_dir
stale_after_ms
cleanup_after_ms
scan_max_files
cache_max_entries
cache_ttl_ms
invalid_diagnostics_max
```

`cleanup_after_ms` — read-side expiration. Не удаляйте registry broadly при активных Unity/Gateway processes.

## Logging И Audit

```json
{
  "logging": {
    "level": "info",
    "audit_enabled": true,
    "audit_dir": "%LOCALAPPDATA%/SGG/UnityMcpGateway/audit"
  }
}
```

Operational logs и audit имеют разные роли. Audit хранит admission/outcome evidence. Всё bounded/redacted.

## Approvals

```json
{
  "approvals": {
    "dir": "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals",
    "hmac_key_file": "%LOCALAPPDATA%/SGG/UnityMcpGateway/secrets/approval-hmac-v1.key",
    "default_ttl_ms": 300000,
    "auto_allow_ask": false,
    "auto_allow_known_unity_popups": false,
    "auto_allow_stale_registry_cleanup_without_elicitation": true
  }
}
```

Не коммитьте approval key. `auto_allow_ask` — broad compatibility workaround для trusted local sessions, а не основной approval path. `auto_allow_stale_registry_cleanup_without_elicitation` — временный узкий default только для exact precondition-bound cleanup dead records у clients без form elicitation; capable clients по-прежнему требуют native prompt. Значение `false` возвращает one-shot approval для каждого real cleanup.

## Cache

`cache.asset_index_root` — Gateway-local root с project-specific identity. Не направляйте в source-controlled assets/shared untrusted locations.

## Feature Flags

Примеры:

```text
semantic_scene_inspection
asset_index
asset_readonly
asset_safe_writes
editor_safe_writes
build_test_automation
runtime_state_debugging
unity_api_reflection
unity_docs_local
profiler_readonly
profiler_recording
ugui_inspection
uitk_inspection
uitk_migration
sgg_mcp_diagnostics
sgg_mcp_plans
sgg_mcp_pipelines
sgg_mcp_skills
sgg_mcp_checkpoints
sgg_mcp_artifacts
sgg_mcp_policy
```

Включайте только нужные. Изменение features и остальных config settings требует restart; valid policy reload-ится между calls. Единственное live-исключение для Gateway config на поддерживающем runtime — сохранённый владельцем boolean `approvals.auto_allow_known_unity_popups`.

## Official Relay Adapter

`official_relay` optional и off по умолчанию. Allowlisted tools появляются как `official.<toolName>` catalog commands и всё равно проходят SGG policy/approval/path checks. Local SGG commands от relay не зависят.

## Skills

`skills.unity_user_skills_root` — optional source. Skill — prompt/resource data, не executable authority.

## Reload Behavior

```text
valid policy replacement -> live reload
invalid policy -> degraded; risky fail closed
saved approvals.auto_allow_known_unity_popups boolean -> live со следующего запроса на поддерживающем file-backed runtime
feature/other config change -> restart Gateway
package/bridge change -> Unity reload; refresh catalog
```

Blocked/uncertain risky command не auto-replay-ится.

Единственная live Gateway config setting — сохранённый владельцем boolean `approvals.auto_allow_known_unity_popups`, default `false`. В runtime с поддержкой этого режима исходный config file перечитывается перед запросами и проверяется снова перед отправкой реального built-in popup ответа, включая отзыв разрешения. Чтение должно быть стабильным и ограниченным 1 MiB; полный config строго валидируется, duplicate JSON keys запрещены. Отсутствующий, недоступный, невалидный, слишком большой или нестабильный config выключает opt-in вместо сохранения прежнего `true`. Остальные settings, включая features, `auto_allow_ask`, approval keys/directories, token и network configuration, остаются startup-bound и требуют restart.

Проверяйте действующее `popup_response_approval.enabled` в `diagnostics.status` или `diagnostics.unity_modal.status`, а не только сохранённую галочку. File-backed contexts могут также возвращать `application_mode:live_on_request`, `source_status`, `saved_enabled` и `restart_required:false`. UI не включает настройку по собственной инициативе; сохранение выбранного владельцем значения не отправляет ответ попапу и не повторяет command. Предупреждение владельцу, точные поддерживаемые ответы и command safeguards описаны в [Permissions](../guides/permissions.md#ответы-на-известные-окна-без-нового-вопроса).

## Advanced Manual Start

```powershell
$env:SGG_UNITY_MCP_TOKEN = "replace-with-local-token"
npm ci
npm start -- --config config/gateway.example.json --policy config/policy.example.json
```

Это development path. Для users — bundled runtime через Unity window.

## Связанные Документы

- [Installation](../installation.md)
- [Policy](./policy.md)
- [Security](./security.md)
- [Windows и WSL](../connect/windows-wsl.md)
