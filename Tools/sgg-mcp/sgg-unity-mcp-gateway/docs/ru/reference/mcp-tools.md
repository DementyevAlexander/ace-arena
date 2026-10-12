[English](../../en/reference/mcp-tools.md) | [Русский](./mcp-tools.md)

# MCP Tools


Gateway exposes восемь stable MCP tools. Все Unity/package capabilities — discoverable catalog commands, а не новые top-level tools.

## `sgg_unity_projects`

Показывает только live project/instance records из одного snapshot local bridge registry и aggregate counts `total`, `live`, `stale`, `unique_projects`. Bodies stale records доступны только через bounded stale-history tool.

```json
{}
```

Используйте:

- при first contact;
- после `project_offline`/stale errors;
- после compile/domain reload;
- перед multi-project work;
- при ambiguous selector.

Result может содержать id, display name, instance id, canonical root, aliases, Unity/package version, catalog hash и readiness. Host paths — identity aliases, не permission для filesystem access.

## `sgg_unity_stale_projects`

Читает retained stale heartbeat history без изменения routing или registry state. Результаты имеют deterministic newest-first order, default limit 8, hard limit 16 и snapshot-bound cursor pagination.

Optional filters: `project`, `status`, `last_heartbeat_from`, `last_heartbeat_to`. Cursor valid только для тех же filters и неизменившегося stale snapshot; иначе начните новый query без cursor.

## `sgg_unity_stale_registry_cleanup`

Preview или explicit удаление expired stale heartbeat files. Tool доступен только когда `registry.cleanup_after_ms` настроен выше `registry.stale_after_ms`. Default — `dry_run:true`; preview возвращает opaque `stale_record_id`, process-state evidence и `precondition_hash` для exact deletable selection.

Real cleanup всегда требует `dry_run:false`, `confirm_cleanup:true`, exact previewed `record_ids` и matching `precondition_hash`. На form-elicitation-capable connection также требуется отдельно принятый one-shot Gateway approval, привязанный к тем же exact arguments. На connection без form elicitation временная настройка `auto_allow_stale_registry_cleanup_without_elicitation` может разрешить только эту exact operation. Удаляются только canonical valid heartbeat files старше configured cleanup threshold, owner PID которых confirmed dead в local process namespace Gateway; непосредственно перед удалением повторно проверяются content и file identity. Cross-OS/namespace ownership считается uncertain. Malformed records, live-PID records, project content и arbitrary paths не удаляются. После timeout или uncertain delivery не retry-те cleanup автоматически; выполните новый dry-run.

## `sgg_unity_describe`

Читает project status, groups, catalog или current descriptor.

```text
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="commands:asset")
sgg_unity_describe(project="game", selector="command:asset.search")
```

Если uncertain args/risk/feature/job support — describe exact command. На stale catalog не invent-ите schema, а обновите describe.

## `sgg_unity_call`

Одна catalog command для одного explicit project.

```json
{
  "project": "game",
  "command": "compile.status",
  "args_json": "{}",
  "mode": "wait"
}
```

| Field | Назначение |
| --- | --- |
| `project` | Explicit selector из discovery. |
| `command` | Exact lowercase id. |
| `args_json` | JSON string-object по current schema. |
| `mode` | `wait` или `job`. |
| `request_id` | Optional correlation/idempotency context, не authorization. |
| `checkpoint_id` | Audit correlation, не approval/rollback. |
| `plan_id` | Audit correlation, не plan execution. |
| `wait_for_replacement_ms` | Optional pre-dispatch wait 1..3600000, только для `game.view.screenshot` в `wait` mode. |
| `wait_for_test_run_terminal_ms` | Optional end-to-end deadline 1..295000 для одного compact non-dry `test.run` в `wait` mode. Лимит резервирует 5000 ms внутри поддерживаемой caller boundary 300000 ms для нормализации и доставки ответа. |

Не помещайте top-level controls внутрь `args_json`.

После единственного отправленного перехода Play Mode проверка через read-only `editor.status` может заново получить heartbeat и описание команды только при двух точных отклонениях `/commands`: несовпадении поколения или хеша каталога в ответе схемы catalog-v1. Обязательны прежние доверенные project/root/process/package/connection; исходный срок и число попыток, проверки descriptor/schema/policy и связь с целевым состоянием не меняются. Отклонённые метаданные не разрешают вызов статуса или повтор перехода. Поле Gateway `catalog_metadata_read_retry_count` считает фактические последующие чтения описания; причина и признак сходимости добавляются только после допустимого расхождения. Совпадение метаданных ещё не доказывает завершение перехода. При сохраняющемся расхождении исходный receipt и точный lookup операции остаются, а `verification_status` равен `catalog_stale`; используйте этот lookup и не входите в Play повторно автоматически. Исправление не увеличивает срок ожидания и не доказывает причины исторических отказов.

После сходимости владелец и доверенный пакет проверяются повторно, даже если вернулся исходный хеш. Статусная команда должна быть включена и разрешена policy как при подготовке, так и непосредственно перед отправкой; `deny`/`ask` не запускают автоматическое согласование или обходной путь. `verification_read_request_count` считает фактические отправки, а не заблокированные подготовки. И успешный ответ, и ответ о блокирующем окне очищаются от подставных полей метаданных до добавления значений Gateway. Прежняя проверка происхождения без отставания метаданных не меняется; повтор перехода не разрешается.

Wrapper waits являются revision-bound capabilities command descriptor. Current descriptor должен объявлять exact field, `wait` mode, accepted range и deadline scope; unsupported commands, modes, arguments, missing contracts и значения вне descriptor range отклоняются до command dispatch.

Текущий descriptor `test.run` объявляет ограниченный **60000 ms budget для admission/start**, включая discovery и durable Gateway binding/reservation/issuance publication. Более короткий inherited caller или выбранный wrapper deadline по-прежнему побеждает. `max_seconds` ограничивает Unity test tracking после admission, не запуск Gateway; `wait_for_test_run_terminal_ms` не увеличивает descriptor startup cap, а `mcp.request_timeout_ms` не переопределяет command timeout. Owner/policy/feature/deadline checks и запрет replay остаются обязательными. Budget не гарантирует admission при произвольно большой или медленной retained history и не разрешает её очистку либо новый trial. См. [проверки исправления](../maintainers/test-run-admission-validation.md).

`wait_for_replacement_ms` включается явно и покрывает только replacement discovery и pre-dispatch revalidation, пока Unity заменяет bridge во время domain reload. Gateway ждёт внутри одной project queue, до replacement deadline заново проверяет canonical project identity, re-described command catalog, policy, schema и approval, затем запускает свежий descriptor command timeout и отправляет не более одного screenshot request. После начала или неопределённого результата transport delivery команда никогда не replay-ится. Без этого поля transitional lifecycle по-прежнему сразу возвращает ошибку.

`wait_for_test_run_terminal_ms` требует descriptor-backed `"response_mode":"compact"` внутри `args_json`. Для filtered run Gateway сначала выполняет bounded read-only polling `test.list` в startup-части того же absolute deadline и связывает discovery generation. Затем он отправляет ровно одну test operation, освобождает launch queue и выполняет только bounded чтения `test.run.status`. Healthy run возвращает один compact terminal response. Pending discovery создаёт ноль test requests; timeout или uncertain launch возвращает доступную exact workflow/operation evidence и никогда не повторяет `test.run`.

Успешные exact-bound persisted чтения `test.run.status` добавляют `status_observation` в full/compact: contract `sgg.unity_mcp.test_run_status_observation.v1`, Gateway `observed_at_utc` и `artifact_snapshot_age_ms` из mtime проверенного stable opened handle, не final pathname и не artifact JSON. Возраст округляется вниз до миллисекунд и nullable при unavailable/invalid/backward clock либо future/negative file timestamp. Это только filesystem snapshot, не Runner update age/progress/run wall time/continued execution. Retained Runner `duration_ms`, counts, Success, owner и no-replay evidence не меняются. Compact/internal server-wait projection сохраняет тот же observation без нового clock sample. Live/error/unbound/legacy payloads не могут задать Gateway-owned field. Read convergence ограничен caller/local deadline, максимум 1500ms scheduled wait и 32 попытками даже при frozen clock; invalid supplied deadline не создаёт свежий budget. Новый test request, status repair, cancellation или fallback authority не добавлены.

Compile responses с source contract `sgg.unity_mcp.compile_readiness_qualification.v1` сохраняют `readiness_qualification` в full/compact, nested validation и bounded recovered/error projections. Это qualification существующего script compiler/Console CS-error evidence плюс editor/package lifecycle, explicit barrier и background-progress gates, не blanket asset validation. `shader_asset_readiness:"unknown"`, `console_history_complete:false` и `console_history_durable:false` сохраняются даже при нуле captured errors или settled native progress. Shader-shaped Console errors без `error CS` не являются compiler-count evidence; наличие marker — textual classifier hit, не shader inventory. Один `ready_clean` не доказывает exact explicit compilation: проверьте `compile_barrier` в full mode для operation/generation/finished/reload correlation либо no-barrier-required state. Qualification сама не даёт readiness/admission и не доказывает newly loaded test DLLs/current repaired source. Missing legacy/malformed qualification остаётся absent; statuses, counts, retained failed receipts, background failure evidence, gates и deadlines не меняются. Shader/asset scan, Console backfill, automatic refresh или replay не выполняются.

Когда caller явно выбирает terminal waiting, а trusted current descriptor `test.run` объявляет `wrapper_wait.wait_for_test_run_terminal_ms.compile_readiness_contract:"sgg.unity_mcp.test_run_compile_readiness.v1"`, Gateway сначала отправляет один bounded policy-allowed read-only `compile.wait_ready`. Metadata, native readiness convergence и discovery используют исходный startup deadline; существующие reserves 5000 ms helper-delivery и 4000 ms durable-binding сохраняются, не создавая новых phase budgets. Strict source evidence требует current inventory и consistent explicit readiness flags, но допускает source-shaped idle/superseded barriers и exact unobserved background evidence. Blank/missing unavailable reasons не превращаются в `not_scanned`. Owner/process/package/catalog/compilation, feature и effective policy замораживаются/перепроверяются до единственной test issuance; уже accepted approval не потребляется повторно. Missing/unsupported helper evidence, unsupported явно выбранный contract или deadline exhaustion создают ноль test requests без automatic refresh/replay/fallback. Ordinary async и legacy descriptors без нового contract сохраняют прежний route. Это selected-route admission, не proof новых loaded test DLLs, all-route readiness или historical failure causes.

Для direct/sequential terminal-wait timeouts и Unity timeout actions сначала читайте `test.run.status` с возвращённым exact operation ID; artifact этой операции проверяйте только при наличии, иначе сообщайте unresolved evidence. Wait timeout или tracking-cleanup receipt от `test.run.cancel` не доказывает остановку Runner. Не делайте automatic cancel/replay и не начинайте другой run до разрешения execution state. Это guidance, без нового programmatic launch block/artifact probe; timeout receipts, counts, explicit batch sequencing и permission/schema gates не меняются. `test.run.cancel` сохраняет write/idempotent, schema не содержит `confirm`. Отдельный XML completion не даёт acceptance без exact operation/owner/source/time/leaf correlation.

Current descriptors поддерживают `response_mode:"full"|"compact"` для `compile.status`, `compile.wait_ready`, `compile.refresh_and_wait`, `test.list`, `test.run` и `test.run.status`. Отсутствующий или `full` mode сохраняет полный structured result. Явный `compact` оставляет status/result, operation identity, authoritative counts, bounded discovery-generation evidence, duration, короткие diagnostics и artifact identity, но убирает success-only duplicate previews, fallback plans, hashes и nested result copies.

При наличии additive leaf evidence compact test responses также содержат до четырёх `non_passing_diagnostics` с неизменными raw statuses и ограниченным текстом identity/message. `non_passing_evidence_status` и `non_passing_tests_truncated` описывают source capture; отдельный `non_passing_diagnostics_truncated` отмечает пропуски compact display, malformed entries или сокращённый текст. Display truncation не понижает source coverage и не меняет counts/Success. Legacy responses без этих полей сохраняют прежнюю форму; отсутствие не доказывает complete-zero evidence. Сохранённые детали читайте через full status той же операции, никогда не повторяйте тесты для восстановления утраченных причин.

Для dynamic C# параметр `max_output_chars` ограничивает captured console text в UTF-16 code units (default 4000), а не возвращаемые значения или размер всего ответа. Full и compact сохраняют независимые serialization bounds: строка допускает до 256 UTF-8 bytes escaped JSON content без внешних кавычек; весь serialized result ограничен 65 536 UTF-8 bytes с учётом annotations. Truncated string сообщает `reason:"max_string_json_content_bytes"` и numeric `applied_limit:256`. Existing character counts измеряют UTF-16 code units; string byte counters относятся к unescaped text, а `result_summary.utf8_bytes` — ко всему serialized result. Console/result truncation flags остаются раздельными. Exact `editor.execute_csharp.readonly.status` читает сохранённый projected result без повторного исполнения и не восстанавливает отброшенные tails.

Dedicated read-only Frame Debugger commands сохраняют порядок legacy type lookup, затем проверяют exact names `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility` и `FrameDebuggerEventData`. Generic reflection, feature/policy permissions и допустимые member signatures не расширяются. Unsupported layouts остаются unavailable. Lookup не включает debugger, не делает step/select/capture; metadata compatibility сама по себе не доказывает наличие текущих event data.

Read-only C# admission больше не удаляет retained results по возрасту. Он консервативно считает matching JSON files без чтения/classification и отказывает новой operation при 64 observed files после complete enumeration; 257-й match превышает scan budget 256 и даёт fail-closed. Faulted enumeration сообщает только observed lower bound. Additive `capacity_observation` — не atomic snapshot и не число validated receipts: state/age classification не выполняется, automatic release eligibility равна нулю из-за disabled-by-contract release, а не из-за acknowledged terminals. Root/binding/duplicate failures не получают enumeration witness. Exact subtree `Library/SGGUnityMcp/job-artifacts/dynamic-csharp-readonly` остаётся readable/listable, но сообщает `prunable:false` и защищён на selection/deletion boundaries generic prune; near-prefix siblings сохраняют прежнюю policy. Это не quota relief, archive, fallback или safe-release command. Age/terminal/response-write completion не являются consumer acknowledgement. Exact status по-прежнему может восстановить отсутствующий cached terminal receipt, поэтому admission threshold — не universal physical-file ceiling. Cache, owner/hash validation, publication cleanup и permission/schema gates не меняются.

Во всех modes единственный canonical `GatewayResult` находится в `structuredContent`. MCP text `content` содержит только bounded human summary со ссылкой на `structuredContent`, а не вторую JSON serialization.

## `sgg_unity_batch`

Bounded список call-like items.

```text
sequential
parallel_by_project
```

```json
{
  "mode": "parallel_by_project",
  "items": [
    {"project":"game-a","command":"compile.status","args_json":"{}","mode":"wait"},
    {"project":"game-b","command":"compile.status","args_json":"{}","mode":"wait"}
  ]
}
```

Batch подходит для известных independent операций или fixed order. Не объединяйте destructive command с зависимым reasoning и отдельными approvals.

Каждый item имеет explicit project. В одном проекте команды сериализуются; разные queues могут идти concurrent.

## `sgg_unity_job`

Poll/cancel Gateway-local jobs.

Используйте returned job id. Не invent-ите ids и не делайте unbounded polling.

```text
queued -> running -> succeeded
                  -> failed
                  -> cancelled при реальной support
```

Cancellation valid только при `supports_cancel`.

## `sgg_unity_logs`

Bounded Gateway/bridge/Unity-related log tails. Для Unity Console лучше `logs.console.query/read/tail` из catalog. Console payload содержит bounded `buffer` epoch и reset-loss metadata; при `history_complete:false` пустой текущий buffer не доказывает полноту истории до initialization/domain reload.

Не запрашивайте unlimited logs и не копируйте secrets/proprietary content.

## Первый Safe Sequence

```text
1. sgg_unity_projects
2. выбрать live project id
3. sgg_unity_describe(project, selector="commands")
4. sgg_unity_describe(project, selector="command:compile.status")
5. sgg_unity_call(... compile.status ...)
```

## Error Discipline

```text
catalog_stale          -> describe again
project_offline        -> projects и current selector
feature_disabled       -> enable required feature вне MCP, restart
permission_denied      -> не обходить
confirmation_required  -> native elicitation или pending approval
schema errors          -> error.details.schema_lookup
job not found          -> проверить returned id/retention
```

## Связанные Документы

- [Architecture](./architecture.md)
- [Command catalog](./commands.md)
- [Workflows](../guides/workflows.md)
- [Error codes](./error-codes.md)
