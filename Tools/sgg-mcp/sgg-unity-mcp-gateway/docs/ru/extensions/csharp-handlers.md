[English](../../en/extensions/csharp-handlers.md) | [Русский](./csharp-handlers.md)

# C# Command Handlers


Compile-time C# integration нужна, когда package намеренно зависит от SGG Unity MCP Editor API и использует typed bridge context или result helpers.

## Assembly Boundary

Handler должен находиться в Editor-only assembly. Ссылайтесь на bridge Editor assembly явно, а runtime assemblies package оставляйте независимыми.

Пример:

```csharp
using SGG.UnityMCP.Editor.Commands;

namespace Example.UnityMcpModule.Editor
{
    public static class ExampleCommands
    {
        [McpCommand("example.status", Risk = "read", Idempotency = "safe")]
        public static CommandExecutionResult Status(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(
                "{\"ok\":true,\"project\":" +
                JsonUtil.String(context.Project) +
                "}");
        }
    }
}
```

Exact source of truth — public API установленной package version.

## Supported High-Level Shapes

```text
static CommandExecutionResult Handler()
static CommandExecutionResult Handler(McpCommandContext context)
```

Attribute задаёт command identity и metadata. Metadata-only manifest нужен только для package-level информации; не добавляйте второй manifest `handler` с тем же id.

## Context Rules

`McpCommandContext` содержит resolved invocation context, а не разрешение на arbitrary state.

Хорошие use cases:

- resolved project identity;
- validated arguments через supported APIs;
- bounded bridge helpers;
- request correlation metadata;
- project-local artifact conventions.

Не делайте:

- новый active-project singleton;
- arbitrary absolute path reads;
- external process из read command;
- hidden network access;
- обход PathGuard/policy;
- собственный approval mechanism.

## Result Design

Command result должен быть:

```text
structured
bounded
deterministic where possible
explicit about changed state
explicit about warnings and next actions
free of secrets and raw host paths
```

Для write полезны поля:

```text
changed
created_paths
changed_objects
warnings
validation
next_valid_actions
dry_run
```

Не возвращайте success до завершения Unity operation, если command явно не вернула asynchronous operation/job id.

## Read Command Pattern

```csharp
[McpCommand(
    "example.settings.inspect",
    Risk = "read",
    Idempotency = "safe",
    Description = "Read bounded example package settings without changing assets")]
public static CommandExecutionResult Inspect(McpCommandContext context)
{
    // Validate supported selectors and return a bounded JSON object.
}
```

Read command не должна скрыто rebuild/import/save, менять selection, входить в Play Mode или создавать files, если side effect не заявлен в contract/risk.

## Write Command Pattern

Reviewable write обычно включает:

```text
read-only inspect/preflight command
dry_run=true
strict target selectors
precondition hash/value when practical
overwrite=false by default
ask-gated policy recommendation
post-write validation and next actions
```

```csharp
[McpCommand(
    "example.asset.generate",
    Risk = "write",
    Idempotency = "idempotent",
    Description = "Create one generated asset under a Unity-relative Assets/... output root")]
```

Reject absolute paths, traversal, package writes без explicit support, existing destination без reviewed overwrite и output вне documented root.

## Long Operations

Advertise job support только при реальной интеграции с job contract. Большие reports должны быть artifact-backed.

Документируйте:

- реальность cancellation;
- progress semantics или их отсутствие;
- timeout/max work;
- artifact family/storage root;
- terminal status;
- может ли retry задублировать side effects.

## Domain Reload И Unity State

Compilation, package changes и domain reload могут менять bridge instance. Не храните unsafe static references across reload. Gateway должен заново resolve-ить state; risky operation не повторяется автоматически после uncertain delivery/timeout.

## Errors

Используйте typed stable codes и bounded safe details. Не возвращайте raw exception text, request body, credentials, absolute paths или arbitrary Unity object dumps.

Ошибочные arguments лучше отклонять descriptor/schema validation до handler. Тогда агент выполнит `schema_lookup`.

## Tests

```text
[ ] descriptor discovery;
[ ] strict schema acceptance/rejection;
[ ] read command без mutation;
[ ] dry-run/real write parity;
[ ] path traversal/absolute path rejection;
[ ] output bounds;
[ ] Unity exception normalization;
[ ] package trust и policy behavior;
[ ] domain reload/stale instance, если relevant;
[ ] runtime package работает без Gateway.
```

## Связанные Документы

- [Extension quick start](./quick-start.md)
- [Command manifests](./command-manifest.md)
- [Extension governance](./governance.md)
- [Extension testing](./testing.md)
