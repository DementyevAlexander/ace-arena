[English](../../en/reference/commands.md) | [Русский](./commands.md)

# Command Catalog


Command — project-specific capability из `sgg_unity_describe`, вызываемая через `sgg_unity_call`. Это не top-level MCP tool.

## Discovery

```text
sgg_unity_projects
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="commands:scene")
sgg_unity_describe(project="game", selector="command:scene.hierarchy")
```

Exact descriptor authoritative для current project instance.

## Descriptor Information

```text
id
title
description
JSON schema
risk
idempotency
feature flag
timeout
output limits
job/cancel support
source
package id/version
manifest path
trust label
revision/catalog hash
```

Не infer-ите fields из похожей command другого package/project.

## Status Meanings

```text
Implemented
  present без feature flag, policy всё равно применяется

Feature-gated
  implemented, но hidden до feature enable

Absent
  unavailable в current catalog
```

Roadmap names нельзя вызывать до появления в catalog.

## Command Families

| Family | Purpose |
| --- | --- |
| `compile.*`, `logs.*` | Compile/Console diagnostics. |
| `asset.*`, `script.*` | Assets, refs, text/scripts, edits. |
| `scene.*`, `gameobject.*`, `component.*`, `prefab.*`, `material.*` | Semantic inspection и writes. |
| `package.*` | Package operations. |
| `test.*`, `build.*` | Validation/build/artifacts. |
| `profiler.*`, `runtime.*` | Profiler и Play Mode. |
| `ugui.*`, `uitk.*`, `ui.*`, `tmp.*` | UI workflows. |
| `plan.*`, `pipeline.*`, `checkpoint.*`, `artifact.*`, `skill.*` | Local workflow state/artifacts. |
| `diagnostics.*`, `policy.*` | Diagnostics/recovery/audit/policy. |
| package-defined | Commands installed assets/tools. |

Availability определяет catalog.

## Selected Family Contracts

Эти краткие contracts фиксируют поведение, которое не видно из generated inventory. Availability и live schema всё равно определяются через discovery.

- `asset.list`, `asset.info`, `asset.serialized.inspect`, `asset.semantic_index.status` и `asset.semantic_search` — cloudless local reads и не используют network access. `asset.serialized.inspect` принимает только `Assets/` или `Packages/`; для persisted `ProjectSettings`, включая `QualitySettings`, используйте `project.settings.inspect` с allowlisted settings name. listing indexed assets/resources по умолчанию берет `Assets/`. `asset.text.read`, `asset.text.find` и `asset.text.search` по умолчанию делают literal substring search; доступен bounded regex mode для project-wide text search.
- Safe asset relocation recipe: включите `features.asset_readonly` и `features.asset_safe_writes`, используйте `script.read`, `script.hash`, `script.validate`, `script.capabilities` или `script.text_edits.spec`, затем `dry_run:true`, `precondition_guid`/`precondition_sha256` и review `changed_assets` перед `asset.folder.create`, `asset.move`, `asset.delete`, `script.text_edits.apply` или `script.write`.
- `asset.relocation.preflight` и dependency queries могут возвращать `script_inherits_type`, `script_implements_interface`, `script_uses_type`, `script_declares_field_type`, `script_declares_property_type`, `script_declares_method_return_type` и `script_declares_method_parameter_type`.
- Asset reports используют `"write_artifact":false` по умолчанию; `write_artifact:true` сохраняет bounded output в `Library/SGGUnityMcp/job-artifacts/asset-reports/` с `readable_with:"job.artifact.read"` и `next_valid_actions`.
- `diagnostics.bridge_reload_trace args_json` читает bounded state из `Library/SGGUnityMcp/bridge-reload-trace.log`.
- Console reads используют `logs.console.read` или `logs.console.query` с `severity`, `contains`, `since`, `filter_text` и `include_stack_trace`; `source_available:false` явный, чтение не очищает Unity Console. Результат содержит `buffer.epoch`, `initialized_at_utc`, `reset_loss` и `history_complete:false`: пустой список authoritative только для текущей observation epoch, а не для истории до initialization/domain reload. Package не восстанавливает записи из `Editor.log`.
- Build/test automation использует feature `build_test_automation`: `build.settings`, `build.profile.list`, `build.run`, `build.run.status`, `test.framework.status`, `test.list`, `test.run`, `test.run.status` и `test.run.cancel`. `build.run` по умолчанию резервирует output под `BuildArtifacts/SGGUnityMcp`; optional `output_root` принимает только существующий absolute Windows directory на fixed local drive вне project, добавляет reserved operation id и target-specific leaf и требует exact human confirmation для real external build. Command публикует durable operation receipt; poll точного operation выполняется через `build.run.status`. `test.list` читает mode-scoped authoritative Unity TestRunner catalog и поддерживает assembly, query, cursor, exact totals, parameterized identities и bounded discovery generation. Перед filtered non-dry `test.run` Gateway опрашивает этот read-only catalog в пределах исходного startup deadline, связывает ready generation с одной process/heartbeat/catalog identity и позволяет Unity разрешить exact runnable full names в одной assembly. Exact full/unique/suite selection является осознанным opt-in для NUnit `Explicit`, хотя discovery сохраняет `runnable:false`; substring, broad/default и остальные non-runnable states остаются исключены. Pending, incomplete, unavailable, mismatched, absent или ambiguous discovery никогда не вызывает `Execute`; stable selector может перейти только на один доказанный same-process pre-dispatch replacement, а exact instance selector не переносится.
- `compile.refresh_and_wait` опционально принимает до 64 уникальных `expected_script_paths` под `Assets/` с расширением `.cs`. Strict readiness проверяет non-empty AssetDatabase GUID и загруженный `MonoScript` только для объявленных caller paths, включая replacement read verification. Без поля сохраняется обычный compile-readiness contract; command не сканирует весь проект и не исправляет malformed metadata.
- `runtime.quality_settings.inspect` читает фиксированный bounded snapshot эффективных `UnityEngine.QualitySettings` в Play Mode. Сравнивайте его с persisted serialized data из `project.settings.inspect {"name":"QualitySettings"}`; оба command read-only и не используют arbitrary C#.
- Feature-gated Addressables read-only commands: `addressables.entries.search`, `addressables.labels.report`, `addressables.dependencies.report`, `addressables.build.preflight`.
- Feature-gated gameplay data read-only commands: `scriptable_object.inspect`, `scriptable_object.schema`, `animation.clip.inspect`, `animation.controller.inspect`, `animation.controller.graph`, `animation.bindings.report`, `timeline.asset.inspect`, `gameplay.data.references.report`, `gameplay.data.validation.report`.
- Feature-gated UI text read-only commands: `ui.text.inventory`, `ui.text.references.report`. Feature-gated cross-UI read-only commands: `ui.screen.inventory`, `ui.screen.inspect`, `ui.interaction.map`.
- Read-only Frame Debugger commands: `frame_debugger.status`, `frame_debugger.events`.
- Profiler frame reads (`profiler.frame.summary`, `profiler.frame.markers`, `profiler.marker.search` и `profiler.report.create`) сначала возвращают текущий recorded frame range. `selection_state` различает `invalid`, `unsettled`, `unavailable` и `authoritative`; `result_state` различает nonempty, exact empty и bounded/truncated empty результаты. Неизвестный thread count и недоступные aggregate `calls`/`self_ms` возвращаются как `null`, а не как выдуманный zero. Iterator failure или потеря frame при range revalidation остаётся non-authoritative и сохраняется при чтении report artifact. Recording отдельно ask-gated и никогда не запускается/повторяется read-командой.
- `game.view.screenshot` остаётся authoritative final Game View composition. Bounded requested dimensions используют public Unity 6 rendering-resolution selection, exact convergence Game View/`Screen` и pre-commit restoration с typed apply, restore и no-fallback evidence. `game.view.focus` — отдельная dry-run-first write-команда для одного exact `UnityEditor.GameView`, выбранного по текущему `windows_hash`; capture никогда не вызывает её автоматически и не использует native desktop focus.
- `editor.window.screenshot` захватывает только content pixels уже сфокусированного exact EditorWindow, включая выбранную панель `UnityEditor.GameView`. Требуются текущий `windows_hash` и точная привязка `HostView.actualView`; используется `unity_internal_capture_editor_window`, поддерживаются bounded `output_path` и `dry_run` без artifact, а metadata включает PNG signature/dimensions, bytes, SHA-256, selected window и privacy warning. `include_editor_chrome:true`, недоступный Unity internal API, изменение focus/identity/hash или любой недоказанный target возвращают typed `capture_target_unavailable` без artifact; команда не меняет focus, не использует HWND, desktop fallback и не заявляет authoritative final Game View composition. `game.view.screenshot` остаётся authoritative final composition. `desktop.region.screenshot` остаётся явным capture по координатам. `runtime.event_capture.cancel` выполняет cooperative session cleanup.
- Semantic inspection: `scene.active`, `scene.build_settings`, `scene.loaded`, `scene.hierarchy.active`, `scene.visible_objects`, `gameobject.builtin_assets`, `gameobject.bounds`, `asset.components.list`, `component.types.search`, `component.type.inspect`; bounds включают `max_results` и `include_obsolete`.
- Feature `serialize_reference_index` открывает `serializereference.types.search`, `serializereference.usages.find`, `serializereference.graph`.
- TMP inspection: `tmp.font_asset.list`, `tmp.font_asset.inspect`, `tmp.font_asset.unicode_set.extract`, `tmp.unicode_set.collect`, `tmp.unicode_set.normalize`, `tmp.font_asset.coverage.check`, `tmp.font_asset.static.plan`, `tmp.font_asset.report`, `tmp.font_asset.validate`, `tmp.font_asset.resource.read`. Reports `TMP_FontAsset` различают dynamic/static atlas population mode.
- TMP writes `tmp.font_asset.static.create`, `tmp.font_asset.static.bake`, `tmp.font_asset.static.convert`, `tmp.font_asset.references.swap`, `tmp.font_asset.fallbacks.set` требуют `asset_safe_writes`, `precondition_hash` и review `missing_count`.
- `package.info` — installed-only local parity command для Unity AI Assistant `Unity.PackageManager.GetData`; `package.install.git` имеет network risk. Unity PackageManager `Request` не предоставляет stable cancel API, поэтому status возвращает `operation_diagnostics` и `next_valid_actions`.

## Generated Inventory

- [Generated command inventory](../../commands.generated.md)

```bash
npm run generate:command-docs
npm run check:command-docs
```

Inventory не заменяет per-command schema discovery.

## `args_json`

```json
{
  "project": "game",
  "command": "asset.search",
  "args_json": "{\"query\":\"Player\",\"max_results\":20}",
  "mode": "wait"
}
```

```text
- describe до guessing;
- только object;
- top-level controls не класть внутрь;
- obey schema bounds;
- Unity-relative paths;
- small limits;
- dry_run/preconditions.
```

## Feature Flags

```text
flag off -> command hidden
flag on  -> descriptor оценивается policy
```

После изменения feature config — restart Gateway и describe again. Policy не раскрывает hidden feature. Только `approvals.auto_allow_known_unity_popups` перечитывается без restart; это не меняет feature/catalog visibility.

## Risk

```text
read
write
execute
destructive
network
```

Risk участвует в defaults и approval. Нельзя выполнять high-risk side effect через disguised read command.

## Source И Trust

```text
source: builtin / attribute / manifest
trust: builtin / trusted_package / untrusted_package
```

Trust не bypass-ит policy. См. [Extension governance](../extensions/governance.md).

## Schema Errors

Выполните trusted `error.details.schema_lookup`:

```text
sgg_unity_describe exact command/catalog
sgg_unity_projects если project нельзя safe resolve-ить
```

Затем создайте новый corrected call. Original mutation не auto-retry-ится.

## Output И Artifacts

Inline results bounded. Large reports возвращают artifact refs. Сначала metadata, затем bounded reads. Artifact id не является host path.

## Planned Versus Implemented

```text
not returned by sgg_unity_describe -> unavailable now
```

Planning command может быть implemented, пока apply/rollback planned-only.

## Связанные Документы

- [MCP tools](./mcp-tools.md)
- [Configuration](./configuration.md)
- [Policy](./policy.md)
- [Error codes](./error-codes.md)
- [Generated inventory](../../commands.generated.md)
