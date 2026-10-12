[English](./canvas-to-uitk.md) | [Русский](../../ru/guides/canvas-to-uitk.md)

# Canvas/uGUI To UI Toolkit Migration


This workflow creates a reviewable side-by-side UI Toolkit implementation from existing Canvas/uGUI data. It does not replace the legacy UI automatically.

## Availability Rule

Only commands returned by `sgg_unity_describe` for the selected project are available.

```text
sgg_unity_describe(project="game", selector="commands:ugui")
sgg_unity_describe(project="game", selector="commands:uitk")
```

Do not call roadmap command ids that are absent from the catalog.

## Current Implemented Path

```text
1. Discover ugui.* and uitk.* commands.
2. Scan the source with uitk.scan or ugui.canvas.scan.
3. Read ugui.canvas.report and focused layout/event/reference reports.
4. Run uitk.plan with persist=false first.
5. Run uitk.generate with mode=create_only and dry_run=true.
6. Review files_to_create and the isolated Assets/... output root.
7. Approve and repeat non-dry-run generation when intended.
8. Run uitk.validate using generation_id or the migration map.
9. Run uitk.preview with dry_run=true.
10. Provide already captured old/new PNG artifacts only when visual comparison is needed.
11. Review the generated bundle manually before integrating it into product UI.
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

Feature visibility and policy permission are independent. Generation and preview writes should remain ask-gated.

## Command Roles

### `uitk.scan`

A read-only migration entry point over the source uGUI scan. It returns a source scan resource and follow-up actions.

### `uitk.plan`

Builds a conservative migration plan from the cached scan/report. With `persist:false`, it returns the plan inline. With `persist:true`, it may create a bounded plan artifact under `Library/SGGUnityMcp/job-artifacts/`.

It does not generate assets or apply migration.

### `uitk.generate`

Creates a side-by-side scaffold under an isolated `Assets/...` output root.

Required posture:

```text
mode: create_only
dry_run: true first
overwrite: false by default
legacy_modified: false
files_to_modify: []
```

The source Canvas scenes and prefabs remain unchanged.

### `uitk.validate`

Validates the generated UXML, USS, binder, PanelSettings, UIDocument prefab, and migration map as one bundle. Use the same-process `generation_id` or the persisted migration map after a reload.

### `uitk.preview`

Creates bounded preview manifests and optional pixel-diff artifacts for existing source/generated PNG files. It can identify old uGUI and new UI Toolkit screenshot targets, but it does not automatically capture screenshots.

## Recommended Output Layout

```text
Assets/UI_Toolkit/Generated/<Target>/
  <Target>.uxml
  <Target>.uss
  <Target>Binder.cs
  <Target>PanelSettings.asset
  <Target>UIDocument.prefab
  <Target>.migration-map.json
```

Treat the exact filenames as plan output, not a universal hardcoded contract.

## Planned-Only Work

Unless they appear in the current catalog, these remain unavailable:

```text
uitk.repair
uitk.apply
uitk.rollback
```

Automatic replacement of scenes/prefabs, automatic screenshot capture, and rollback execution must not be claimed as implemented merely because planning/generation commands exist.

## Safety Contract

```text
- explicit project on every call;
- semantic uGUI inspection before raw scene YAML;
- Unity-relative Assets/... output paths;
- side-by-side and create-only by default;
- dry run before generated writes;
- no legacy scene/prefab replacement;
- validation before visual preview or manual integration;
- ask-gated writes and explicit human review.
```

## Example Natural-Language Request

> Inspect the `MainMenu` Canvas, produce a side-by-side UI Toolkit migration plan and dry-run scaffold under `Assets/UI_Toolkit/Generated/MainMenu`. Do not modify the legacy scene or prefab. Report unsupported controls and manual tasks.

Expected agent behavior:

```text
discover -> scan -> focused reports -> plan -> generate dry-run -> stop for review
```

## Related Docs

- [Common workflows](./workflows.md)
- [Permissions and approvals](./permissions.md)
- [Jobs and artifacts](./jobs-and-artifacts.md)
- [Command catalog](../reference/commands.md)
