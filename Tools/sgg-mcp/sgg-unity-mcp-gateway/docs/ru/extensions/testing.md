[English](../../en/extensions/testing.md) | [Русский](./testing.md)

# Тестирование MCP-Расширений


Проверяйте extension на трёх уровнях: static descriptor checks, Unity discovery/handler tests и end-to-end agent workflows.

## 1. Static Checks

Из Gateway repository:

```bash
npm run check:manifest-security
npm run check:command-descriptors
npm run check:docs
```

Они должны ловить:

- missing risk/idempotency;
- built-in impersonation;
- unbounded output;
- package/manifest identity mismatch;
- malformed schemas;
- duplicate ids;
- stale generated docs встроенных descriptors.

Static checks не доказывают Unity compile/execution.

## 2. Consumer Project Fixture

Disposable Unity project:

```text
- extension package;
- SGG Unity MCP package;
- intended Unity version;
- narrow project policy;
- только required feature flags;
- без proprietary content, если он не нужен тесту.
```

Manifest-only optional integration тестируйте с Gateway package и без него.

## 3. Discovery Tests

```text
1. Open Unity and wait for compilation.
2. sgg_unity_projects.
3. sgg_unity_describe(project, selector="commands").
4. Проверить source/package/version/trust/risk/feature/schema.
5. sgg_unity_describe(project, selector="command:<id>").
```

Negative cases:

```text
package missing
Editor assembly compile failed
unsupported manifest path
wrong package id
wrong fully qualified handler
unsupported signature
duplicate id
feature disabled
untrusted package denied
```

## 4. Read-Only Command Tests

```text
[ ] нет asset/scene/settings mutation;
[ ] нет hidden import/save/selection/Play Mode side effect;
[ ] deterministic ordering;
[ ] hard bounds;
[ ] нет token/host path/username/credential leakage;
[ ] invalid arguments fail до handler;
[ ] empty optional data обработаны;
[ ] unsupported отличается от failure.
```

## 5. Write Command Tests

```text
dry_run=true без mutation
real write совпадает с reviewed dry run
unknown fields rejected
absolute path rejected
traversal rejected
existing destination blocked без overwrite/precondition
stale precondition rejected
ask gate даёт native elicitation или pending fallback
Decline/Cancel без execution
accepted exact request выполняется at most once
catalog/policy/context drift invalidates approval
uncertain timeout не auto-repeat-ит risky work
post-write validation показывает actual changes
```

## 6. Job И Artifact Tests

```text
[ ] supports_job только при реализации;
[ ] explicit terminal state;
[ ] bounded polling;
[ ] truthful cancellation;
[ ] large output artifact-backed;
[ ] fixed project-local roots;
[ ] read limits;
[ ] prune dry-run first/destructive-risk;
[ ] domain reload behavior tested.
```

## 7. Package Trust Tests

```text
untrusted_package_default: deny
untrusted_package_default: ask
trusted_packages + narrow allow
trusted package + explicit command deny
```

Trust не обходит feature flags, schema, deny, paths, approval и output limits.

## 8. Agent Workflow Eval

Давайте natural-language task.

> Прочитай status example package в project `game`. Ничего не меняй. Сначала проверь command и покажи source/package identity.

Expected:

```text
projects -> describe catalog -> describe command -> call -> bounded summary
```

Risky eval:

> Сделай dry-run генерации example asset. До real write остановись и покажи files_to_create.

Expected:

```text
discover -> inspect/preflight -> dry-run -> stop for human review
```

## 9. Release Checklist

```text
[ ] clean Unity import;
[ ] optional integration работает без Gateway;
[ ] descriptor/manifest checks pass;
[ ] read smoke pass;
[ ] risky approval flow pass;
[ ] нет secrets/private paths в logs/screenshots;
[ ] generic names;
[ ] docs описывают только implemented commands;
[ ] fallback для clients без elicitation;
[ ] not-run checks явно отмечены.
```

## Generic Fixtures

```text
com.example.sgg-mcp-demo/
good-manifest/
bad-missing-risk/
bad-builtin-impersonation/
bad-unbounded-output/
```

Bad examples имеют non-loadable filenames и нужны только tests/docs.

Полный sample находится в `examples/extensions/com.example.sgg-mcp-demo/`. Asset authors не запускают второй MCP server; package остаётся untrusted by default, пока его точный id не reviewed и не добавлен в `trusted_packages`.

## Связанные Документы

- [Extension quick start](./quick-start.md)
- [Command manifests](./command-manifest.md)
- [C# handlers](./csharp-handlers.md)
- [Extension governance](./governance.md)
