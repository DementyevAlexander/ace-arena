[English](../../en/guides/canvas-to-uitk.md) | [Русский](./canvas-to-uitk.md)

# Миграция Canvas/uGUI В UI Toolkit


Этот workflow создаёт reviewable side-by-side UI Toolkit implementation из существующего Canvas/uGUI. Legacy UI автоматически не заменяется.

## Availability Rule

Доступны только commands из `sgg_unity_describe` выбранного проекта.

```text
sgg_unity_describe(project="game", selector="commands:ugui")
sgg_unity_describe(project="game", selector="commands:uitk")
```

Не вызывайте roadmap command ids, которых нет в catalog.

## Текущий Implemented Path

```text
1. Discover ugui.* и uitk.* commands.
2. Scan source через uitk.scan или ugui.canvas.scan.
3. Прочитать ugui.canvas.report и focused layout/event/reference reports.
4. Запустить uitk.plan сначала с persist=false.
5. Запустить uitk.generate с mode=create_only и dry_run=true.
6. Проверить files_to_create и isolated Assets/... output root.
7. После approval повторить non-dry-run generation.
8. Запустить uitk.validate с generation_id или migration map.
9. Запустить uitk.preview с dry_run=true.
10. Передавать уже снятые old/new PNG только для visual comparison.
11. Вручную проверить bundle перед интеграцией в product UI.
```

## Feature Families

| Capability | Typical feature flag |
| --- | --- |
| Baseline uGUI inspection | `ugui_inspection` |
| uGUI bounds/visual inspection | `ugui_visual_inspection` |
| UI Toolkit inspection | `uitk_inspection` |
| Generated bundle validation | `uitk_validation` |
| Focused create-only UI Toolkit writes | `uitk_safe_writes` |
| Migration planning and generation | `uitk_migration` |
| Preview artifact generation | `uitk_migration_preview` |

Feature visibility и policy permission независимы. Generation и preview writes лучше оставлять ask-gated.

## Роли Commands

### `uitk.scan`

Read-only migration entry point поверх source uGUI scan. Возвращает scan resource и follow-up actions.

### `uitk.plan`

Строит conservative migration plan из cached scan/report. `persist:false` возвращает inline plan. `persist:true` может создать bounded artifact в `Library/SGGUnityMcp/job-artifacts/`.

Assets не генерирует и migration не применяет.

### `uitk.generate`

Создаёт side-by-side scaffold в isolated `Assets/...` root.

Безопасная позиция:

```text
mode: create_only
dry_run: true first
overwrite: false by default
legacy_modified: false
files_to_modify: []
```

Source Canvas scenes и prefabs не меняются.

### `uitk.validate`

Проверяет generated UXML, USS, binder, PanelSettings, UIDocument prefab и migration map как единый bundle. Используйте same-process `generation_id` или persisted migration map после reload.

### `uitk.preview`

Создаёт bounded preview manifests и optional pixel-diff artifacts для существующих source/generated PNG. Выбирает old uGUI и new UI Toolkit screenshot targets, но сам screenshots не снимает.

## Рекомендуемый Output Layout

```text
Assets/UI_Toolkit/Generated/<Target>/
  <Target>.uxml
  <Target>.uss
  <Target>Binder.cs
  <Target>PanelSettings.asset
  <Target>UIDocument.prefab
  <Target>.migration-map.json
```

Точные filenames берите из plan, а не считайте universal hardcoded contract.

## Planned-Only Work

Пока commands не появились в current catalog, недоступны:

```text
uitk.repair
uitk.apply
uitk.rollback
```

Automatic replacement scenes/prefabs, automatic screenshot capture и rollback execution нельзя считать implemented только из-за наличия planning/generation commands.

## Safety Contract

```text
- explicit project в каждом call;
- semantic uGUI inspection до raw scene YAML;
- Unity-relative Assets/... output paths;
- side-by-side и create-only по умолчанию;
- dry run перед generated writes;
- без replacement legacy scenes/prefabs;
- validation до visual preview или manual integration;
- ask-gated writes и explicit human review.
```

## Пример Запроса Пользователя

> Проверь Canvas `MainMenu`, подготовь side-by-side план миграции в UI Toolkit и dry-run scaffold в `Assets/UI_Toolkit/Generated/MainMenu`. Legacy scene и prefab не изменяй. Отдельно перечисли unsupported controls и manual tasks.

Expected flow:

```text
discover -> scan -> focused reports -> plan -> generate dry-run -> stop for review
```

## Связанные Документы

- [Основные сценарии](./workflows.md)
- [Права и подтверждения](./permissions.md)
- [Jobs и artifacts](./jobs-and-artifacts.md)
- [Command catalog](../reference/commands.md)
