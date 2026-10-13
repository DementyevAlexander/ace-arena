[English](../../en/extensions/command-manifest.md) | [Русский](./command-manifest.md)

# Command Manifests


Command manifest регистрирует Editor commands без compile dependency на SGG bridge API. Handler всё равно работает внутри trusted local Unity Editor process, поэтому metadata и policy должны быть conservative.

## Supported Locations

```text
Packages/<package-id>/Editor/Mcp/mcp.commands.json
Packages/<package-id>/mcp.commands.json
```

Legacy candidates в `Assets/Scripts/**/mcp.commands.json` могут отображаться в diagnostics, но package commands из них не регистрируются.

## Top-Level Shape

```json
{
  "schema_version": 1,
  "package": "com.example.tools",
  "commands": []
}
```

| Field | Contract |
| --- | --- |
| `schema_version` | Required manifest schema version. Для current format — `1`. |
| `package` | Required installed Unity package id. Должен совпадать с package identity. |
| `commands` | Bounded array command descriptors. |

## Descriptor Fields

| Field | Required | Назначение |
| --- | --- | --- |
| `id` | yes | Lowercase dot-notation id, например `example.status`. |
| `title` | recommended | Короткая human-facing label. |
| `description` | yes | Чёткое agent-facing поведение и non-goals. |
| `handler` | manifest-only | Fully qualified static method. Для attributed handler с тем же id пропускается. |
| `risk` | yes | `read`, `write`, `execute`, `destructive` или `network`. |
| `idempotency` | yes | Точное repeat behavior. |
| `timeout_ms` | yes | Bounded expected timeout. |
| `schema` | yes | JSON object schema для `args_json`. |
| `output_limit` | yes для textual output | Maximum inline output, например `max_chars`. |
| `feature_flag` | optional | Local feature family. |
| `supports_job` | optional | Job support только при реальной поддержке handler. |
| `supports_cancel` | optional | Только если cancellation реализован. |

При развитии формата exact source of truth — current examples и checker.

## Schema Guidance

Предпочитайте strict object schemas:

```json
{
  "type": "object",
  "properties": {
    "asset_path": {
      "type": "string",
      "description": "Unity-relative Assets/... path"
    },
    "max_results": {
      "type": "integer",
      "minimum": 1,
      "maximum": 100,
      "default": 20
    }
  },
  "required": ["asset_path"],
  "additionalProperties": false
}
```

Правила:

```text
- reject unknown fields;
- bounded counts и time ranges;
- описывать Unity-relative path constraints;
- не принимать arbitrary absolute paths;
- не принимать raw shell commands;
- не принимать credentials;
- dry_run для reviewable writes;
- overwrite=false или explicit preconditions для replacement;
- safe defaults.
```

## Risk Classification

| Risk | Когда использовать |
| --- | --- |
| `read` | Inspection без intended project/external side effect. |
| `write` | Создание или изменение project-local state без destructive replacement. |
| `execute` | Запуск кода, tests, builds, menu actions или active operations. |
| `destructive` | Delete, overwrite, restore, prune, close, terminate или риск потери state. |
| `network` | Network boundary. |

Выбирайте наивысший material risk. Write не становится read только потому, что его основной output диагностический.

## Output Limits И Artifacts

Inline result должен быть небольшим. Для большого output:

```text
1. Сохранить bounded artifact в approved project-local SGGUnityMcp root.
2. Вернуть artifact_ref и compact summary.
3. Сначала читать metadata.
4. Читать bounded slices через job.artifact.read или relevant artifact command.
```

Не принимайте arbitrary output path от модели.

## Manifest-Only Handler Contract

```text
static string Handler()
static string Handler(string argsJson)
static string Handler(string argsJson, string settingsJson)
```

Result должен быть valid JSON. Exception становится command failure; по возможности возвращайте bounded domain errors через supported JSON shape.

## Attribute И Manifest

Для `[McpCommand]` handler manifest может добавить package metadata без `handler`.

Не регистрируйте один id дважды:

```text
[McpCommand] + manifest handler с тем же id -> invalid/ambiguous
attribute handler + metadata-only descriptor -> supported
manifest-only handler + no attribute -> supported
```

## Security Review

```text
[ ] command id не impersonate-ит built-ins;
[ ] package id совпадает с installed identity;
[ ] risk и idempotency присутствуют;
[ ] schema strict и bounded;
[ ] output bounded или artifact-backed;
[ ] проверено untrusted package behavior;
[ ] risky path требует explicit policy;
[ ] нет self-trust/self-approval fields;
[ ] generic fields не скрывают arbitrary filesystem/process/shell/credential access.
```

## Validation

Из Gateway repository:

```bash
npm run check:manifest-security
npm run check:command-descriptors
```

Generic good и intentionally invalid fixtures лежат в `examples/extensions/`. `mcp.commands.bad.json` — только documentation/test fixture, не loadable manifest.

## Связанные Документы

- [Extension quick start](./quick-start.md)
- [C# handlers](./csharp-handlers.md)
- [Extension governance](./governance.md)
- [Extension testing](./testing.md)
