[English](../../en/connect/generic-stdio.md) | [Русский](./generic-stdio.md)

# Подключение Generic Stdio Client


Используйте direct `--stdio`, когда клиент должен сам запускать Gateway process. Этот режим поддерживает modern MCP `2026-07-28`, legacy MCP 2025 и modern multi-round approvals. `stdio-shim` нужен только как адаптер stdio-only клиента к уже запущенному HTTP Gateway.

| Режим | Protocol posture | Lifecycle Gateway |
| --- | --- | --- |
| `sgg-unity-mcp-gateway --stdio` | Modern `2026-07-28` + legacy 2025 | Клиент запускает Gateway process. |
| `sgg-unity-mcp-gateway stdio-shim ...` | Только legacy 2025 | HTTP Gateway уже должен работать. |

## Direct Stdio

```text
sgg-unity-mcp-gateway --stdio --config "%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json"
```

Это полнофункциональное direct stdio connection. Соединение договаривается об одной protocol era и закрепляет один server instance на весь lifetime. Modern calls используют stateless request envelopes и MRTR; legacy calls сохраняют initialize, Roots и form elicitation compatibility.

## Архитектура

```text
stdio-only MCP client
        │
        │ stdin/stdout
        ▼
sgg-unity-mcp-gateway stdio-shim
        │
        │ local HTTP MCP
        ▼
уже запущенный SGG Unity MCP Gateway
        │
        ▼
Unity Bridge
```

Shim:

- не подключается к Unity напрямую;
- сам не выполняет project discovery;
- не запускает второй Gateway;
- показывает только восемь стабильных SGG MCP tools;
- остаётся legacy compatibility transport и не ретранслирует modern MRTR.

## Сначала Запустите Gateway

Используйте normal Setup flow в Unity MCP Window и проверьте Health.

Default endpoint:

```text
http://127.0.0.1:27182/mcp
```

## Shim Через Config

Предпочтительный вариант, если клиент может запустить installed CLI:

```text
sgg-unity-mcp-gateway stdio-shim --config "%LOCALAPPDATA%/SGG/UnityMcpGateway/config/gateway.json"
```

Generic JSON:

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

Shim получает endpoint и имя token environment variable из того же Gateway config.

## Shim С Явным URL

```text
sgg-unity-mcp-gateway stdio-shim --url http://127.0.0.1:27182/mcp --token-env SGG_UNITY_MCP_TOKEN
```

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
        "SGG_UNITY_MCP_TOKEN": "<local-placeholder>"
      }
    }
  }
}
```

Используйте client secret store или process environment. Не коммитьте реальный token.

## Проверка

Клиент должен показать ровно:

```text
sgg_unity_projects
sgg_unity_describe
sgg_unity_call
sgg_unity_batch
sgg_unity_job
sgg_unity_logs
```

После этого выполните read-only smoke test из [Быстрого старта](../quick-start.md).

## Approvals

Direct `--stdio` поддерживает modern MRTR approvals и legacy form elicitation. Обновлённый `stdio-shim` relay-ит legacy forms только при outer `elicitation.form`; это не modern MRTR proxy. Non-capable/cancelled/failed exchanges используют `confirmation_required` fallback, не implicit approval или replay.

## Диагностика

- Startup error обычно означает, что HTTP Gateway не запущен либо config/URL не совпадает.
- Token error означает, что shim process не получил тот же `SGG_UNITY_MCP_TOKEN`.
- Пустой project list означает, что transport работает, но Unity Bridge registry пуст или stale.
- Не подключайте клиент к Unity Bridge port; MCP client всегда подключается к Gateway.

## Связанные Документы

- [Установка](../installation.md)
- [MCP tools](../reference/mcp-tools.md)
- [Безопасность](../reference/security.md)
