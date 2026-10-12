[English](../../en/connect/cursor.md) | [Русский](./cursor.md)

# Подключение Cursor


Cursor подключается к Gateway через stdio shim или через намеренно настроенный remote HTTP MCP endpoint.

## Рекомендуемый Локальный Путь

1. Запустите Unity и Gateway из `SGG/Unity MCP/MCP Window`.
2. Откройте `Setup -> MCP Connection`.
3. Скопируйте Cursor/stdio config, generated установленным package.
4. Проверьте command и environment placeholders.
5. Сохраните в MCP configuration location вашей версии Cursor.
6. Reload-ните MCP servers Cursor.

Stdio shim — адаптер к уже работающему HTTP Gateway, а не второй Unity MCP server.

Общая форма:

```json
{
  "mcpServers": {
    "sgg-unity-mcp": {
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

Unity window может сгенерировать более точный command к installed runtime. Используйте generated form, если CLI не находится в `PATH`.

## Форма С Явным URL

```json
{
  "mcpServers": {
    "sgg-unity-mcp": {
      "command": "sgg-unity-mcp-gateway",
      "args": [
        "stdio-shim",
        "--url",
        "http://127.0.0.1:27182/mcp",
        "--token-env",
        "SGG_UNITY_MCP_TOKEN"
      ],
      "env": {
        "SGG_UNITY_MCP_TOKEN": "<set-in-a-local-secret-store-or-process-environment>"
      }
    }
  }
}
```

Не коммитьте реальный local token. Используйте process environment или client secret store.

## Remote HTTP

Native remote MCP entry подходит только когда текущая версия Cursor поддерживает нужный transport и Gateway listener настроен намеренно.

Non-loopback access требует token authentication, allowed origins и local firewall controls. Unity Bridge наружу не открывается; клиент подключается только к Gateway endpoint.

## Проверка

Выполните:

```text
sgg_unity_projects
sgg_unity_describe(project="<project-id>", selector="commands")
sgg_unity_call(project="<project-id>", command="compile.status", args_json="{}", mode="wait")
```

Если Cursor не advertises MCP elicitation, `ask`-gated calls возвращают `confirmation_required`. Используйте pending-approval fallback из [Права и подтверждения](../guides/permissions.md); не добавляйте self-approval MCP tool.

## Точные Имена Tools

Top-level names:

```text
sgg_unity_projects
sgg_unity_describe
sgg_unity_call
sgg_unity_batch
sgg_unity_job
sgg_unity_logs
```

Unity commands вроде `scene.hierarchy` передаются внутри `sgg_unity_call.command`, а не становятся дополнительными top-level tools.

## Связанные Документы

- [Generic stdio clients](./generic-stdio.md)
- [Права и подтверждения](../guides/permissions.md)
- [Диагностика проблем](../troubleshooting.md)
