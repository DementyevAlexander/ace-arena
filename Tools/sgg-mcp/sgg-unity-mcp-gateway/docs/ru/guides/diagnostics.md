[English](../../en/guides/diagnostics.md) | [Русский](./diagnostics.md)

# Диагностика


Используйте диагностику послойно. Сначала Unity window, затем MCP commands и только потом local CLI, если client или Gateway не могут завершить normal path.

## Порядок Диагностики

```text
1. Setup -> Refresh Health
2. Setup -> Copy Log
3. Status -> project id, bridge heartbeat, Gateway health, package/runtime versions
4. Commands -> точная command и schema
5. Permissions -> feature visibility и effective policy
6. diagnostics.* -> bounded redacted evidence
7. doctor/report-problem CLI -> offline fallback
```

Не начинайте со сбора целых папок логов. Берите минимальный redacted результат, который доказывает проблему.

## Gateway И Bridge Status

Полезные read-only commands при включённом `features.sgg_mcp_diagnostics` и разрешающей policy:

```text
diagnostics.status
diagnostics.command_statistics
diagnostics.audit.summary
diagnostics.audit.tail
diagnostics.problem.list
diagnostics.problem.read
diagnostics.problem.triage
diagnostics.recovery.suggest
```

Bounded problem report:

```json
{
  "project": "game",
  "command": "diagnostics.report_problem",
  "args_json": "{\"summary\":\"compile.status не может обратиться к Unity bridge\",\"last_command\":\"compile.status\",\"last_error_code\":\"project_offline\",\"include_snapshot\":true}",
  "mode": "wait"
}
```

Problem reports хранятся в фиксированной Gateway-controlled папке. Commands не принимают произвольные report paths. Tokens, authorization headers, cookies, passwords, secret-like values и host paths редактируются.

## Восстановление После Schema Errors

Для ошибок:

```text
schema_validation_failed
invalid_field_value
schema_unavailable
args_json_must_be_object
items_json_must_be_array
invalid_json
missing_required_field
```

смотрите:

```text
error.details.schema_lookup
```

Это executable metadata action, а не автоматический retry failed mutation.

### Safe Project И Command

```json
{
  "action_kind": "mcp_tool",
  "tool": "sgg_unity_describe",
  "arguments": {
    "project": "game",
    "selector": "command:asset.import"
  }
}
```

### Unknown Command

```json
{
  "tool": "sgg_unity_describe",
  "arguments": {
    "project": "game",
    "selector": "commands"
  }
}
```

### Project Нельзя Безопасно Resolve-ить

```json
{
  "tool": "sgg_unity_projects",
  "arguments": {}
}
```

Path-like и unresolved selectors не повторяются как resolved project ids. После descriptor создайте новый explicit corrected call. Schema recovery не retry-ит `sgg_unity_call`, не возвращает authentication material и не авторизует исходный request.

## Unity Modal И Startup Blockers

При включённой диагностике доступны bounded evidence:

```text
diagnostics.unity_startup.status
diagnostics.unity_modal.status
diagnostics.unity_recovery_prompt.status
diagnostics.unity_crash_reporter.status
```

Read-only detection не нажимает произвольные Unity dialogs. Узкие response commands для recovery prompt или crash reporter остаются destructive-risk, используют dry-run по умолчанию, где он поддерживается, работают только с verified windows/processes и требуют policy/approval.

Если Unity заблокирован modal dialog или main-thread freeze, heartbeat может быть stale при живом процессе. Не объявляйте проект healthy до fresh bridge evidence.

## Compile И Reload Diagnostics

```text
1. sgg_unity_projects
2. compile.status
3. compile.errors.recent или compile.warnings.recent, если discoverable
4. logs.console.query/read/tail
5. diagnostics.unity_modal.status, если Unity выглядит заблокированным
6. diagnostics.report_problem для повторяющейся проблемы или handoff
```

`compile.refresh_and_wait` и `validation.after_edit` — explicit write/validation operations. Не заменяйте их произвольным touching файлов или циклами compilation requests.

Package/runtime changes могут вернуть `package_refresh_required` или `stale_package_resolution`. Проверьте dirty-scene risk, затем вызовите explicit refresh/validation command. Документация не обещает automatic `AssetDatabase.Refresh`.

## Audit

Audit — evidence, не authorization.

```text
diagnostics.command_statistics
  per-command call counts, UTF-8 request bytes, paired full/compact response bytes и redacted failure causes

diagnostics.audit.summary
  aggregate outcomes and state

diagnostics.audit.tail
  bounded recent redacted records
```

`diagnostics.command_statistics` агрегирует retained local audit window без повторного выполнения commands. Размеры full и compact сериализуются из одного logical result, а `delivered_response_bytes` показывает реально возвращённый mode. Internal terminal-wait polling не увеличивает user command volume; batch items учитываются по command ids, а batch envelope остаётся отдельным tool-level aggregate.

Статистика хранит typed error code/stage, фиксированное redacted explanation, byte counts, timestamps и audit correlation ids. Raw arguments, request/response bodies, tokens, approval bindings, stack traces, host paths и Unity project content не сохраняются. Coverage fields явно показывают пропущенные retained files или bytes; модуль остаётся local-only и не отправляет telemetry.

`checkpoint_id`, `plan_id` и request ids только коррелируют работу. Они не approve-ят commands, не выполняют plans и не запускают rollback.

## Offline CLI Fallback

Если MCP client не видит Gateway:

```powershell
sgg-unity-mcp-gateway doctor --json --config "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\gateway.json" --policy "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\policy.json"
```

Bounded redacted report без MCP:

```powershell
sgg-unity-mcp-gateway report-problem --project game --summary "MCP client cannot connect" --include-doctor --config "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\gateway.json" --policy "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\policy.json"
```

Не прикладывайте raw config, token environment values, полное approval storage или unredacted logs к публичным reports.

## Handoff Evidence

```text
mcp_availability_checked: current_session
active_project_selector: точный selector
sgg_unity_projects: ok/error плюс bounded status
command: compile.status или другой точный command id
compile_status_result: ok или exact error code
schema_lookup: executed/not needed/unavailable
unity_state: ready/compiling/reloading/modal/offline/unknown
evidence_age: current, not historical
checks_not_run: явный список
next_action: одно конкретное safe действие
```

Не пиши `MCP skipped` или «MCP unavailable» только по старым notes. Сначала проверь current session.

## Связанные Документы

- [Диагностика проблем](../troubleshooting.md)
- [Error codes](../reference/error-codes.md)
- [Права и подтверждения](./permissions.md)
- [Jobs и artifacts](./jobs-and-artifacts.md)
