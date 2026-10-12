[English](../../en/extensions/quick-start.md) | [Русский](./quick-start.md)

# Добавление MCP Support В Unity-Ассет


Добавьте agent-facing command в reusable Unity package без второго MCP server и без обязательной runtime-зависимости package от Gateway.

Для первой интеграции рекомендуется dependency-free command manifest.

## Что Получится

```text
Packages/com.example.unity-mcp-module/
  package.json
  Editor/
    Example.UnityMcpModule.Editor.asmdef
    ExampleCommands.cs
    Mcp/
      mcp.commands.json
```

Package остаётся рабочим без SGG Unity MCP Gateway. Если Gateway установлен, bridge обнаруживает manifest и добавляет command в catalog этого проекта.

## 1. Создайте Editor Assembly

`Editor/Example.UnityMcpModule.Editor.asmdef`:

```json
{
  "name": "Example.UnityMcpModule.Editor",
  "references": [],
  "includePlatforms": [
    "Editor"
  ],
  "autoReferenced": true
}
```

Для manifest-only path ссылка на SGG assembly не нужна.

## 2. Добавьте Static Handler

`Editor/ExampleCommands.cs`:

```csharp
namespace Example.UnityMcpModule.Editor
{
    public static class ExampleCommands
    {
        public static string Status()
        {
            return "{\"ok\":true}";
        }
    }
}
```

Возвращаемая строка должна быть valid JSON для command `data` payload.

## 3. Добавьте Manifest

`Editor/Mcp/mcp.commands.json`:

```json
{
  "schema_version": 1,
  "package": "com.example.unity-mcp-module",
  "commands": [
    {
      "id": "example.status",
      "title": "Example status",
      "description": "Read example module status",
      "handler": "Example.UnityMcpModule.Editor.ExampleCommands.Status",
      "risk": "read",
      "idempotency": "safe",
      "timeout_ms": 10000,
      "schema": {
        "type": "object",
        "properties": {},
        "additionalProperties": false
      },
      "output_limit": {
        "max_chars": 2048
      }
    }
  ]
}
```

Manifest package id должен совпадать с Unity package identity.

## 4. Дождитесь Unity Compile

Откройте consumer project и дождитесь компиляции Editor assembly. Package command появляется только там, где package установлен и успешно loaded.

## 5. Найдите Command

```text
sgg_unity_projects
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="command:example.status")
```

Не считайте, что package commands существуют во всех проектах.

## 6. Вызовите Command

```json
{
  "project": "game",
  "command": "example.status",
  "args_json": "{}",
  "mode": "wait"
}
```

Command остаётся catalog entry и не добавляет седьмой top-level MCP tool.

## Handler Signatures

Manifest-only handlers:

```text
static string Handler()
static string Handler(string argsJson)
static string Handler(string argsJson, string settingsJson)
```

Используйте `argsJson` только со строгой JSON Schema. Ограничивайте output и возвращайте structured JSON вместо free-form Console text.

## Когда Нужен `[McpCommand]`

Compile-time bridge integration подходит, когда package намеренно ссылается на SGG Editor API и нужны:

- `McpCommandContext`;
- typed `CommandExecutionResult`;
- direct bridge helpers;
- richer job/artifact integration;
- descriptor metadata рядом с handler.

См. [C# handlers](./csharp-handlers.md).

## Trust И Policy

Manifest сам по себе не делает package trusted. Gateway разрешает package identity по Unity metadata и применяет:

```text
feature visibility
package trust
command schema
risk and policy
approval при action ask
output limits
audit
```

Перед write, execute, destructive или network commands прочитайте [Extension governance](./governance.md).

## Done Criteria

```text
[ ] Package работает без Gateway package.
[ ] Editor assembly не попадает в runtime.
[ ] Manifest package id совпадает с package.json.
[ ] Handler name fully qualified.
[ ] Schema запрещает unknown fields.
[ ] Risk и idempotency точные.
[ ] Text output имеет hard limit.
[ ] Command discoverable только в нужном project.
[ ] Read-only command возвращает valid JSON.
[ ] Local manifest checks проходят.
```

## Дальше

- [Command manifests](./command-manifest.md)
- [C# handlers](./csharp-handlers.md)
- [Extension governance](./governance.md)
- [Extension testing](./testing.md)
