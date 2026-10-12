[English](./commands.md) | [Русский](../../ru/reference/commands.md)

# Command Catalog


A command is a project-specific capability discovered through `sgg_unity_describe` and invoked through `sgg_unity_call`. It is not a top-level MCP tool.

## Discovery

```text
sgg_unity_projects
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="commands:scene")
sgg_unity_describe(project="game", selector="command:scene.hierarchy")
```

The exact descriptor is authoritative for the current project instance.

## Descriptor Information

A descriptor can include:

```text
id
title
description
JSON argument schema
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

Do not infer missing fields from a similarly named command in another package or project.

## Status Meanings

```text
Implemented
  present without a feature flag and subject to policy

Feature-gated
  implemented but hidden until its matching feature is enabled

Absent
  not available in this project/catalog; it may be unsupported, uninstalled, planned, or from another version
```

Roadmap names are not callable until returned by the catalog.

## Command Families

Representative families include:

| Family | Typical purpose |
| --- | --- |
| `compile.*`, `logs.*` | Compilation and Console diagnostics. |
| `asset.*`, `script.*` | Asset index, references, text/script inspection, controlled edits. |
| `scene.*`, `gameobject.*`, `component.*`, `prefab.*`, `material.*` | Semantic inspection and reviewed Editor writes. |
| `package.*` | Package reads and controlled operations. |
| `test.*`, `build.*` | Bounded validation/build operations and artifacts. |
| `profiler.*`, `runtime.*` | Read-only profiler state, recording gates, Play Mode diagnostics. |
| `ugui.*`, `uitk.*`, `ui.*`, `tmp.*` | UI inspection, validation, migration, and package-specific UI workflows. |
| `plan.*`, `pipeline.*`, `checkpoint.*`, `artifact.*`, `skill.*` | Local workflow metadata and bounded project-local artifacts. |
| `diagnostics.*`, `policy.*` | Redacted diagnostics, recovery, audit, and policy explanation. |
| package-defined families | Asset/tool-specific commands contributed by installed packages. |

Use the catalog, not this list, to decide availability.

## Selected Family Contracts

These concise contracts cover behavior that is not obvious from the generated inventory. Availability and the live schema still come from discovery.

- Asset index commands `asset.list`, `asset.info`, `asset.serialized.inspect`, `asset.semantic_index.status`, and `asset.semantic_search` are cloudless local reads and do no network access. `asset.serialized.inspect` accepts only `Assets/` or `Packages/`; use `project.settings.inspect` with an allowlisted settings name for persisted `ProjectSettings`, including `QualitySettings`. The listing indexed assets/resources path defaults to `Assets/`. `asset.text.read`, `asset.text.find`, and `asset.text.search` default to literal substring search, with bounded regex mode for project-wide text search.
- Safe asset relocation recipe: enable `features.asset_readonly` and `features.asset_safe_writes`, inspect with `script.read`, `script.hash`, `script.validate`, `script.capabilities`, or `script.text_edits.spec`, then use `dry_run:true`, `precondition_guid`/`precondition_sha256`, and review `changed_assets` before `asset.folder.create`, `asset.move`, `asset.delete`, `script.text_edits.apply`, or `script.write`.
- `asset.relocation.preflight` and dependency queries can report `script_inherits_type`, `script_implements_interface`, `script_uses_type`, `script_declares_field_type`, `script_declares_property_type`, `script_declares_method_return_type`, and `script_declares_method_parameter_type`.
- Asset reports default to `"write_artifact":false`; `write_artifact:true` stores bounded output under `Library/SGGUnityMcp/job-artifacts/asset-reports/` with `readable_with:"job.artifact.read"` and `next_valid_actions`.
- `diagnostics.bridge_reload_trace args_json` reads bounded state from `Library/SGGUnityMcp/bridge-reload-trace.log`.
- Console reads use `logs.console.read` or `logs.console.query` with `severity`, `contains`, `since`, `filter_text`, and `include_stack_trace`; `source_available:false` is explicit and reading does not clear the Unity Console. Console results include `buffer.epoch`, `initialized_at_utc`, `reset_loss`, and `history_complete:false`: an empty list is authoritative only for the current observation epoch, not for log history before initialization/domain reload. The package does not backfill from `Editor.log`.
- Build/test automation uses feature `build_test_automation`: `build.settings`, `build.profile.list`, `build.run`, `build.run.status`, `test.framework.status`, `test.list`, `test.run`, `test.run.status`, and `test.run.cancel`. `build.run` defaults to `BuildArtifacts/SGGUnityMcp`; optional `output_root` accepts only an existing absolute fixed-local-drive Windows directory outside the project, appends the reserved operation id and target-specific leaf, and forces exact human confirmation for a real external build. It publishes a durable operation receipt; poll its exact operation with `build.run.status`. `test.list` reads the mode-scoped authoritative Unity TestRunner catalog and supports assembly, query, cursor, exact totals, parameterized identities, and a bounded discovery generation. Before a filtered non-dry `test.run`, the Gateway polls that read-only catalog within the original startup deadline, binds the ready generation to one process/heartbeat/catalog identity, and lets Unity resolve exact runnable full names in one assembly. Exact full/unique/suite selection is a deliberate opt-in for NUnit `Explicit` candidates even though discovery keeps them `runnable:false`; substring, broad/default and other non-runnable states remain excluded. Pending, incomplete, unavailable, mismatched, absent, or ambiguous discovery never invokes the TestRunner; a stable selector may follow one proven same-process pre-dispatch replacement, while an exact instance selector never moves.
- `compile.refresh_and_wait` optionally accepts up to 64 unique `expected_script_paths` under `Assets/` ending in `.cs`. Strict readiness then verifies a non-empty AssetDatabase GUID and a loaded `MonoScript` for only those caller-declared paths, including across replacement read verification. Omitting the field preserves ordinary compile readiness; the command never scans all project files or repairs malformed metadata.
- `runtime.quality_settings.inspect` reads a fixed bounded snapshot of effective `UnityEngine.QualitySettings` values while playing. Compare it with persisted serialized data from `project.settings.inspect {"name":"QualitySettings"}`; neither command writes settings or uses arbitrary C#.
- Feature-gated Addressables read-only commands include `addressables.entries.search`, `addressables.labels.report`, `addressables.dependencies.report`, and `addressables.build.preflight`.
- Feature-gated gameplay data read-only commands include `scriptable_object.inspect`, `scriptable_object.schema`, `animation.clip.inspect`, `animation.controller.inspect`, `animation.controller.graph`, `animation.bindings.report`, `timeline.asset.inspect`, `gameplay.data.references.report`, and `gameplay.data.validation.report`.
- Feature-gated UI text read-only commands include `ui.text.inventory` and `ui.text.references.report`. Feature-gated cross-UI read-only commands include `ui.screen.inventory`, `ui.screen.inspect`, and `ui.interaction.map`.
- Read-only Frame Debugger commands are `frame_debugger.status` and `frame_debugger.events`.
- Profiler frame reads (`profiler.frame.summary`, `profiler.frame.markers`, `profiler.marker.search`, and `profiler.report.create`) report the current recorded frame range before reading. `selection_state` distinguishes `invalid`, `unsettled`, `unavailable`, and `authoritative`; `result_state` distinguishes nonempty, exact empty, and bounded/truncated empty results. Unknown thread counts and unsupported aggregate `calls`/`self_ms` values are `null`, never fabricated zero. Iterator or range-revalidation failure is non-authoritative, and this state survives report artifact reads. Recording remains separately ask-gated and is never started or replayed by a read command.
- `game.view.screenshot` remains the authoritative final Game View composition. Bounded requested dimensions use Unity 6 public rendering-resolution selection, exact Game View/`Screen` convergence, and pre-commit restoration with typed apply, restore, and no-fallback evidence. `game.view.focus` is a separate dry-run-first write for one exact `UnityEditor.GameView` selected by the current `windows_hash`; capture never invokes it automatically or uses native desktop focus.
- `editor.window.screenshot` captures content-only pixels for an already-focused exact EditorWindow, including a selected `UnityEditor.GameView` panel. It requires the current `windows_hash` and exact `HostView.actualView` binding, uses `unity_internal_capture_editor_window`, supports bounded `output_path` and artifact-free `dry_run`, and returns PNG signature/dimension, byte-count, SHA-256, selected-window, and privacy metadata. `include_editor_chrome:true`, an unavailable Unity internal API, focus/identity/hash changes, or any unproven target returns typed `capture_target_unavailable` without an artifact; it never focuses, uses HWND, falls back to the desktop, or claims authoritative final Game View composition. It never substitutes Unity's main window or desktop pixels. `game.view.screenshot` remains the authoritative final composition. `desktop.region.screenshot` remains an explicit coordinate-region capture. `runtime.event_capture.cancel` performs cooperative session cleanup.
- Semantic inspection includes `scene.active`, `scene.build_settings`, `scene.loaded`, `scene.hierarchy.active`, `scene.visible_objects`, `gameobject.builtin_assets`, `gameobject.bounds`, `asset.components.list`, `component.types.search`, and `component.type.inspect`, with bounds such as `max_results` and `include_obsolete`.
- Feature `serialize_reference_index` exposes `serializereference.types.search`, `serializereference.usages.find`, and `serializereference.graph`.
- TMP inspection includes `tmp.font_asset.list`, `tmp.font_asset.inspect`, `tmp.font_asset.unicode_set.extract`, `tmp.unicode_set.collect`, `tmp.unicode_set.normalize`, `tmp.font_asset.coverage.check`, `tmp.font_asset.static.plan`, `tmp.font_asset.report`, `tmp.font_asset.validate`, and `tmp.font_asset.resource.read`. `TMP_FontAsset` reports distinguish dynamic/static atlas population mode.
- TMP writes `tmp.font_asset.static.create`, `tmp.font_asset.static.bake`, `tmp.font_asset.static.convert`, `tmp.font_asset.references.swap`, and `tmp.font_asset.fallbacks.set` require `asset_safe_writes`, a `precondition_hash`, and explicit review of `missing_count`.
- `package.info` is the installed-only local parity command for Unity AI Assistant `Unity.PackageManager.GetData`; `package.install.git` is network-risk. Unity's PackageManager `Request` type does not provide a stable cancel API, so status exposes `operation_diagnostics` and `next_valid_actions` instead.

## Generated Inventory

The machine-generated inventory is retained at:

- [Generated command inventory](../../commands.generated.md)

Refresh it in the Gateway repository with:

```bash
npm run generate:command-docs
```

Verify freshness with:

```bash
npm run check:command-docs
```

The generated table is an inventory, not a replacement for per-command schema discovery.

## `args_json`

Command-specific arguments are passed as a JSON string that must parse to an object:

```json
{
  "project": "game",
  "command": "asset.search",
  "args_json": "{\"query\":\"Player\",\"max_results\":20}",
  "mode": "wait"
}
```

Rules:

```text
- describe before guessing fields;
- object only, not array/string/null;
- no duplicate top-level control fields inside args_json;
- obey min/max and additionalProperties:false;
- use Unity-relative paths where required;
- keep bounds small;
- use dry_run/preconditions where supported.
```

## Feature Flags

Feature flags control visibility, not permission.

```text
flag off -> command hidden
flag on  -> descriptor can be evaluated by policy
```

After changing Gateway feature configuration, restart the Gateway and describe again. A policy change alone does not reveal hidden commands.

## Risk

```text
read
write
execute
destructive
network
```

The descriptor risk participates in policy defaults and approval. A command can also have more specific project patterns. Do not use a lower-risk command as a disguised path to perform a higher-risk side effect.

## Source And Trust

Command sources include:

```text
builtin
attribute
manifest
```

Package trust labels include:

```text
builtin
trusted_package
untrusted_package
```

Source/trust metadata does not bypass policy. See [Extension governance](../extensions/governance.md).

## Schema Errors

For schema-class errors, execute the trusted `error.details.schema_lookup` metadata action. It points to either:

```text
sgg_unity_describe for the exact command/catalog
sgg_unity_projects when a safe project could not be resolved
```

Then construct a new corrected call. Do not automatically retry the original mutation.

## Output And Artifacts

Inline results are bounded. Commands that produce large reports should return artifact references. Inspect metadata first, then read bounded slices. Never infer a host filesystem path from an artifact id.

## Planned Versus Implemented

Public docs may discuss planned command families, but agents must apply this rule:

```text
not returned by sgg_unity_describe -> unavailable now
```

For example, a migration plan can be implemented while apply/rollback remains planned-only.

## Related Docs

- [MCP tools](./mcp-tools.md)
- [Configuration](./configuration.md)
- [Policy](./policy.md)
- [Error codes](./error-codes.md)
- [Generated inventory](../../commands.generated.md)
