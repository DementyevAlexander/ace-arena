[English](../../en/connect/unity-ai-assistant.md) | [Русский](./unity-ai-assistant.md)

# Подключение Unity AI Assistant


SGG Unity MCP Gateway можно добавить в Unity AI Assistant как внешний MCP server через project-local `UserSettings/mcp.json`. Локальный SGG workflow не требует Unity Assistant chat, Unity Cloud, paid credits, subscription state или official Unity relay.

## Baseline И Optional Integration

**Работает без official package**

```text
Gateway и Bridge installation
project discovery
command discovery
compile и Console status
asset/script/scene/prefab commands
local policy и approvals
plans, checkpoints, skills, diagnostics и artifacts при включённых features
```

**Добавляется с `com.unity.ai.assistant`**

```text
Unity Assistant UI
package-provided skill sources
project-local MCP config UI
optional official relay compatibility diagnostics
```

Official package не обязателен для локального SGG baseline.

## Configuration File

В Unity AI Assistant MCP `2.8` откройте:

```text
Edit -> Project Settings -> AI -> Assistant Extensions -> Open Config File
```

Project-local файл:

```text
<UnityProject>/UserSettings/mcp.json
```

Не коммитьте сюда реальный token.

## Предпочтительный Config-Based Shim

```json
{
  "enabled": true,
  "path": "",
  "mcpServers": {
    "sgg-unity-mcp": {
      "type": "stdio",
      "command": "sgg-unity-mcp-gateway",
      "args": [
        "stdio-shim",
        "--config",
        "%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json"
      ]
    }
  }
}
```

Если CLI не находится в `PATH`, используйте generated form из Unity MCP Window: он может указать точный путь к installed portable runtime.

## Форма С Явным URL

```json
{
  "enabled": true,
  "path": "",
  "mcpServers": {
    "sgg-unity-mcp": {
      "type": "stdio",
      "command": "sgg-unity-mcp-gateway",
      "args": [
        "stdio-shim",
        "--url",
        "http://127.0.0.1:27182/mcp",
        "--token-env",
        "SGG_UNITY_MCP_TOKEN"
      ],
      "env": {
        "SGG_UNITY_MCP_TOKEN": "<set-locally>"
      }
    }
  }
}
```

Предпочтительны process environment или поддерживаемый local secret mechanism.

## Имена Tools

Unity Assistant может prettify labels, но точные top-level tool ids:

```text
sgg_unity_projects
sgg_unity_describe
sgg_unity_call
sgg_unity_batch
sgg_unity_job
sgg_unity_logs
```

Unity capabilities вроде `scene.hierarchy` или `asset.search` остаются command ids внутри `sgg_unity_call`. Не создавайте PascalCase aliases или top-level копии `Unity_*`.

## Optional Official Relay

Локальный Gateway может показывать diagnostic state через `unity.relay.status`, когда соответствующая command family включена.

Настроенный relay adapter может импортировать allowlisted official tools как catalog entries:

```text
official.<toolName>
```

Эти entries:

- вызываются через `sgg_unity_call`;
- не добавляют top-level MCP tools;
- проходят SGG policy и approval;
- могут проверять Unity-relative path fields;
- выключены по умолчанию;
- не делают local SGG commands зависимыми от Unity Cloud или relay.

Missing, paywalled или approval-blocked relay — diagnostic information, а не поломка local Gateway baseline.

## Источники Skills

При включённых skill commands Gateway может обнаруживать:

```text
.mcp/skills/<skill>/SKILL.md
Assets/**/SKILL.md
Packages/**/AIAssistantSkills/**/SKILL.md
%APPDATA%/Unity/AIAssistantSkills/**/SKILL.md   opt-in
built-in example skills
```

Skills — только prompt/resources. Они не grant-ят policy, не включают commands, не approve-ят requests и не настраивают внешние MCP servers.

Перед copy reviewed skill в `.mcp/skills/` используйте `skill.import` с `dry_run:true`.

## Проверка

После reload configuration Unity Assistant:

```text
1. вызвать sgg_unity_projects
2. выбрать explicit project
3. прочитать command catalog
4. вызвать compile.status
```

Если client не отображает native MCP elicitation, `ask`-gated calls используют pending-approval fallback. Security model Gateway не зависит от одного UI.

## Примеры Локальной Совместимости

| Концепция Unity Assistant | Local SGG command |
| --- | --- |
| List/find assets | `asset.list`, `asset.search` |
| Read/find text | `asset.text.read`, `asset.text.find`, `asset.text.search` |
| Script hash/validation | `script.hash`, `script.validate` |
| Bounded script edits | `script.text_edits.apply` |
| Project settings | `project.settings.summary`, `project.settings.inspect` |
| Package information | `package.list`, `package.info` |
| Console | `logs.console.query`, `logs.console.read`, `logs.console.tail` |
| Scene inspection | `scene.loaded`, `scene.hierarchy`, `scene.search`, `scene.inspect_object` |
| Editor metadata | `editor.state.get`, `editor.windows.list`, `editor.menu.list` |

Доступность всегда определяется через `sgg_unity_describe` выбранного проекта.

## Связанные Документы

- [Generic stdio clients](./generic-stdio.md)
- [Command catalog](../reference/commands.md)
- [Права и подтверждения](../guides/permissions.md)
- [Безопасность](../reference/security.md)
