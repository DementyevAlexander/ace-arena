[English](../en/quick-start.md) | [Русский](./quick-start.md)

# Быстрый старт


Этот путь доводит один локальный Unity-проект от установки package до первого успешного read-only MCP-вызова.

## Цель

Настройка завершена, когда выполнены все четыре условия:

```text
[ ] Unity MCP Window показывает активный Bridge.
[ ] Gateway Health зелёный.
[ ] sgg_unity_projects видит открытый проект.
[ ] compile.status возвращает read-only результат для явно выбранного проекта.
```

## Требования

- Unity Editor на Windows.
- Unity-проект, который может компилировать Editor assemblies.
- Доступ к pinned tag репозитория или локальному checkout package.
- OpenCode, Cursor, Unity AI Assistant или другой MCP-клиент.
- Для обычного Setup системная установка Node.js не требуется.

Для разработки рекомендуется Unity `6000.4+`. В manifest package пока указан Unity `2022.3` как import/compile floor; это само по себе не означает полную release support.

## 1. Установите Unity Package

Pinned Git UPM dependency:

```json
{
  "dependencies": {
    "com.sungeargames.unity-mcp": "https://github.com/romanilyin/sgg-unity-mcp-gateway.git?path=/unity/Packages/com.sungeargames.unity-mcp#2026.10.12-2"
  }
}
```

Для локального checkout откройте Unity Package Manager, выберите **Add package from disk...** и укажите:

```text
<gateway-repository>/unity/Packages/com.sungeargames.unity-mcp/package.json
```

Оставьте Unity открытым и дождитесь окончания компиляции package.

## 2. Выполните обязательный Setup

Откройте:

```text
SGG/Unity MCP/MCP Window
```

На странице `Setup`:

```text
1. Установите или настройте CanonicalPath, если Setup сообщает, что provider отсутствует.
2. Install Portable Node.
3. Install Runtime.
4. Start Gateway.
5. Refresh Health.
```

CanonicalPath требуется для детерминированной project identity и path-bound Bridge commands. В зависимости от версии package кнопка может находиться в группе dependencies или optional tools; отсутствующий required provider нужно считать обязательным шагом Setup.

Обычный flow устанавливает файлы в:

```text
%LOCALAPPDATA%/SGG/UnityMcpGateway/
```

Системный `PATH` не меняется.

Если Health не стал зелёным, сначала используйте `Copy Log` на этой же странице, а не меняйте network и policy вслепую.

## 3. Создайте Config И Выберите Первый Profile

В `Setup -> Permission Setup`:

```text
1. Create Config/Policy Files.
2. При желании примените Read-only для первого smoke test.
3. Перезапустите Gateway, если Setup менял feature/config state.
```

Read-only намеренно ограничен. Его достаточно для project discovery, compile status, bounded logs и нескольких безопасных status reads. Позже используйте `Permissions -> Apply + Enable Flags`, когда задаче нужны Inspect, Safe Writes, Package Management, Runtime Debug или другой profile.

## 4. Подключите Один MCP-Клиент

Используйте copy helper в `Setup -> MCP Connection`.

Рекомендуемые пути:

- OpenCode на Windows или в WSL: [Настройка OpenCode](./connect/opencode.md).
- Cursor: [Настройка Cursor](./connect/cursor.md).
- Unity AI Assistant: [Настройка Unity AI Assistant](./connect/unity-ai-assistant.md).
- Любой stdio-only client: [Generic stdio setup](./connect/generic-stdio.md).

Для OpenCode в WSL на той же машине выбирайте **WSL via Windows stdio shim**. Gateway остаётся на Windows loopback, поэтому не нужен non-loopback listener или firewall rule.

## 5. Выполните Первые Вызовы

Сначала найдите проекты:

```text
sgg_unity_projects
```

Выберите живой `project` id и получите catalog этого проекта:

```text
sgg_unity_describe(project="<project-id>", selector="commands")
```

Прочитайте compile state:

```json
{
  "project": "<project-id>",
  "command": "compile.status",
  "args_json": "{}",
  "mode": "wait"
}
```

Ожидаемое поведение:

- assets проекта не изменяются;
- результат показывает текущий compile/readiness state;
- недоступный проект возвращает typed error, а не переключает агента на другой Editor;
- агент не использует неявный active project.

## 6. Прочитайте Ограниченный Фрагмент Console

Сначала проверьте наличие нужной log-команды в catalog. Типичный вызов:

```json
{
  "project": "<project-id>",
  "command": "logs.console.read",
  "args_json": "{\"limit\":50,\"types\":\"error,warning\",\"include_stack_trace\":false}",
  "mode": "wait"
}
```

Ограничивайте число записей и объём stack traces.

## Native Approval Prompts

Первый smoke test read-only и не должен требовать approval.

Для дальнейших `ask`-gated commands:

- Advertised/rendered MCP form connection отображает native request. OpenCode v2 реализует формы, обновлённый shim relay-ит их capability-aware. Реальный frontend/human acceptance — отдельный gate.
- Пользователь явно принимает, отклоняет или отменяет один точный запрос.
- Перед выполнением Gateway заново проверяет project, command, feature, schema и policy state.
- Clients без elicitation получают `confirmation_required` и используют fallback behavior; для exact stale-registry cleanup есть отдельное configured narrow compatibility exception.

Подробнее: [Права и подтверждения](./guides/permissions.md).

## Если Что-то Не Работает

Проверяйте в таком порядке:

```text
1. Setup -> Refresh Health
2. Setup -> Copy Log
3. Status -> Project Id и Bridge state
4. Повторный sgg_unity_projects
5. Typed error code и next_valid_actions
6. Документ Troubleshooting
```

Не включайте все feature flags и `Allow All` только ради первого read-only smoke test.

## Следующие Шаги

- [Основные сценарии](./guides/workflows.md)
- [Права и подтверждения](./guides/permissions.md)
- [Маршрутизация нескольких проектов](./guides/multi-project.md)
- [Диагностика проблем](./troubleshooting.md)
