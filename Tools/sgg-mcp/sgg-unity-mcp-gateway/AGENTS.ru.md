# Agent Guide — SGG Unity MCP Gateway

[English](./AGENTS.md) | [Русский](./AGENTS.ru.md)

Machine-facing правила для coding agents. Human docs: [docs/ru/README.md](./docs/ru/README.md).

## Routing

- Перед Unity work вызвать `sgg_unity_projects`, если нет current unambiguous selector от пользователя.
- Global active project не существует.
- Не invent-ить `set_active`, `switch_project` и подобные tools.
- Каждый call и batch item содержит explicit `project`.
- Использовать id/instance/canonical root/exact alias из discovery.
- На ambiguous/duplicate не guess-ить.

## Discovery

```text
1. sgg_unity_projects
2. sgg_unity_describe(project, selector="commands")
3. sgg_unity_describe(project, selector="command:<id>")
4. sgg_unity_call / sgg_unity_batch
5. sgg_unity_job
6. sgg_unity_logs / diagnostics
```

- Не invent-ить command ids.
- Planned/documented command unavailable до появления в current catalog.
- `catalog_stale` -> describe again.
- Schema error -> execute `error.details.schema_lookup.metadata_lookup` как named top-level MCP tool, затем new corrected call.
- Metadata lookup не передаётся в `sgg_unity_call.command`.

## Call Shape

OpenCode v2 по умолчанию включает MCP Code Mode: ищите Gateway tools в catalog и вызывайте через `execute`. Отсутствие в provider-native tool list само по себе не доказывает connection failure. Native config использует `mcp.servers` и `plugins`; file/artifact status не доказывает active load или видимые подписи.

```json
{
  "project": "game",
  "command": "compile.status",
  "args_json": "{}",
  "mode": "wait"
}
```

- `args_json` — object по current schema.
- `project`, `command`, `mode`, `request_id`, `checkpoint_id`, `plan_id` остаются top-level.
- Все counts/timeouts/frames/logs/search/artifact reads bounded.

## Paths

- Unity-relative `Assets/...` или allowed read-only `Packages/...`.
- No Windows/WSL absolute, UNC, traversal, NUL, arbitrary output, кроме `build.run.output_root`, когда это поле есть в live schema. Exception принимает только существующий absolute Windows directory на fixed local drive вне project и требует exact human confirmation для real build.
- Host aliases/canonical root — identity, не filesystem permission.
- Prefer artifact/resource commands.

## Safety/Approval

- Не bypass-ить `permission_denied`, `feature_disabled`, `policy_degraded`, `confirmation_required`.
- Inspect/preflight/risk report/dry run до mutation.
- Native approval требует advertised и rendered MCP form elicitation. OpenCode v2 реализует формы; обновлённый legacy shim relay-ит их capability-aware. Реальный frontend/human acceptance требует отдельных доказательств. Accept/Decline/Cancel выбирает Human; opaque session metadata не является authority.
- Без elicitation показать exact pending workflow; agent не self-approve-ит.
- Exact stale-registry cleanup может использовать отдельное временное no-elicitation compatibility exception; оно не разрешает другие `ask` decisions и не превращает chat text в approval.
- После human approval повторить unchanged original call.
- Hash/request/checkpoint/plan ids не authorization.
- `auto_allow_ask` только по explicit choice пользователя.
- Владелец может отдельно включить `approvals.auto_allow_known_unity_popups` в MCP Window. Если running Gateway подтверждает enabled, поддерживаемый exact built-in ответ на попап не требует нового вопроса; deny/features, exact target checks, dry-run/precondition, confirm arguments и запрет replay сохраняются. Это не generic click permission, не разрешение завершать процессы и не право агента включать галочку самому. Если всё же вернулся `confirmation_required`, нужен обычный human workflow.
- Risky commands не auto-retry-ить после uncertain timeout/stale/protocol failure.

## Semantic Unity

- Semantic hierarchy/search/inspect до raw YAML/grep.
- References/dependencies/risk reports до move/delete/refactor.
- Property schema до write.
- Scene save только по explicit request.
- Report exact changes/validation.

## Compile/Tests/Packages

- Для Unity evidence использовать Unity MCP commands.
- `.csproj`/`dotnet` не заменяют Unity Test Runner.
- После changes compile status и explicit refresh/validation.
- Package operations poll по returned id; не invent progress/cancel.
- `test.list`/preflight до `test.run`; execution ask-gated.

## Jobs/Artifacts

- `mode:"job"` только при support.
- Exact returned id, bounded polling.
- Cancel только при `supports_cancel`.
- Metadata + bounded artifact reads.
- Prune/delete/restore destructive, dry-run first.

## Dynamic C#/Network

- Prefer typed commands.
- Dynamic C# только trusted project и при необходимости.
- No process/network/arbitrary paths/secrets/control bypass.
- Network commands только при explicit feature/policy/user intent.

## Diagnostics/Handoff

Перед “MCP unavailable”:

```text
1. current-session projects
2. safe compile.status if live
3. exact selector/command/code/Unity state/evidence age
4. schema_lookup if applicable
5. diagnostics.report_problem when useful
```

Не копировать tokens/credentials/approval stores/absolute paths/unredacted proprietary content.

## PowerShell Из WSL

`powershell.exe -Command` из bash оборачивать целиком в single quotes, чтобы bash не expanded PowerShell `$` variables.
