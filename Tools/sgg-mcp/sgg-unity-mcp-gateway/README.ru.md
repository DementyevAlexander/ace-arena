<div align="center">

# SGG Unity MCP Gateway

**Один локальный MCP-шлюз для безопасной работы AI-агентов сразу с несколькими Unity-проектами.**

[English](./README.md) |
[Русский](./README.ru.md)

—

[Установка](./docs/ru/installation.md) |
[Быстрый старт](./docs/ru/quick-start.md) |
[OpenCode](./docs/ru/connect/opencode.md) |
[Сценарии работы](./docs/ru/guides/workflows.md) |
[Поддержка MCP в своём ассете](./docs/ru/extensions/quick-start.md) |
[Справочник](./docs/ru/reference/commands.md) |
[Диагностика проблем](./docs/ru/troubleshooting.md) |
[История изменений](./CHANGELOG.md)

<p>
  <a href="./docs/ru/installation.md"><img src="./docs/assets/readme/cards/unity.svg" alt="Unity 6" height="48"></a>
  <a href="./docs/ru/guides/multi-project.md"><img src="./docs/assets/readme/cards/multi-project.svg" alt="Несколько проектов" height="48"></a>
  <a href="./docs/ru/connect/opencode.md"><img src="./docs/assets/readme/cards/opencode.svg" alt="OpenCode" height="48"></a>
  <a href="./docs/ru/connect/windows-wsl.md"><img src="./docs/assets/readme/cards/windows-wsl.svg" alt="Windows и WSL" height="48"></a>
  <a href="./docs/ru/guides/permissions.md"><img src="./docs/assets/readme/cards/safety.svg" alt="Права и подтверждения" height="48"></a>
  <a href="./docs/ru/extensions/quick-start.md"><img src="./docs/assets/readme/cards/extensions.svg" alt="Расширения для ассетов" height="48"></a>
</p>

<p>
  <a href="./docs/ru/reference/architecture.md"><img src="./docs/assets/readme/hero-architecture.svg" alt="Архитектура SGG Unity MCP Gateway" width="960"></a>
</p>

</div>

SGG Unity MCP Gateway подключает OpenCode, Cursor, Unity AI Assistant и другие MCP-клиенты к нескольким локальным проектам Unity Editor через один Gateway. Каждый Unity-вызов явно указывает проект, возможности открываются по мере необходимости, а рискованные команды остаются под контролем feature flags, локальной policy и подтверждения пользователя.

## Какие проблемы решает Gateway

| Типичная проблема Unity MCP | Подход SGG |
| --- | --- |
| Неявный active Editor становится неоднозначным, когда открыто несколько Unity-проектов. | Каждый Unity-вызов содержит явный `project`; один Gateway маршрутизирует запросы в несколько живых Editor bridges. |
| Десятки или сотни top-level tools постоянно занимают контекст модели. | Публичный MCP surface состоит из восьми стабильных tools; Unity-возможности читаются из command catalog конкретного проекта. |
| Windows, WSL и Unity по-разному записывают путь к одному проекту. | CanonicalPath даёт детерминированную lexical identity для routing и cache keys; Unity-side границей доступа остаётся PathGuard. |
| Агенту нельзя выдавать постоянные права на write, execute, network и destructive операции. | Feature flags управляют видимостью; policy выбирает `allow`, `ask` или `deny`; approval привязан к одному точному запросу. |
| Автору ассета нужен agent API, но не хочется поддерживать отдельный MCP server. | Unity-пакет добавляет команды через C# handlers или `mcp.commands.json`, а агент продолжает использовать те же восемь tools. |

## Как это устроено

```text
OpenCode / Cursor / Unity AI Assistant / другой MCP-клиент
                              │
                     восемь MCP tools
                              ▼
                   SGG Unity MCP Gateway
                     │        │        │
                     ▼        ▼        ▼
                 Project A  Project B  Project C
                 Unity      Unity      Unity
                 Bridge     Bridge     Bridge
```

Основные правила:

```text
1. Глобального active Unity project нет.
2. Каждый Unity command call содержит явный project.
3. Возможности проекта открываются через sgg_unity_describe.
4. Unity/package commands — записи каталога, вызываемые через sgg_unity_call.
5. В payload используются пути Assets/... и Packages/..., а не произвольные absolute paths.
6. Один локальный Gateway обслуживает все открытые проекты.
```

## Быстрый старт

1. Установите Unity package по pinned Git UPM tag или из локального checkout.
2. Откройте `SGG/Unity MCP/MCP Window`, выполните обязательные шаги Setup и добейтесь зелёного Gateway Health.
3. Скопируйте конфигурацию для OpenCode, Cursor, Unity AI Assistant или другого MCP-клиента.
4. Выполните безопасную первую последовательность:

```text
sgg_unity_projects
sgg_unity_describe(project="<project-id>", selector="commands")
sgg_unity_call(
  project="<project-id>",
  command="compile.status",
  args_json="{}",
  mode="wait"
)
```

Форма pinned Git UPM dependency:

```json
{
  "dependencies": {
    "com.sungeargames.unity-mcp": "https://github.com/romanilyin/sgg-unity-mcp-gateway.git?path=/unity/Packages/com.sungeargames.unity-mcp#2026.10.12-2"
  }
}
```

Во время pre-release разработки можно использовать локальную file dependency. Установка CanonicalPath, обновление runtime, удаление и режим запуска из repository описаны в разделе [Установка](./docs/ru/installation.md).

CanonicalPath — обязательная зависимость проекта, которую host project устанавливает из публичного scoped registry.

## Основные сценарии

- **Диагностика компиляции и Console** — чтение compile state и ограниченных Editor logs без изменения проекта.
- **Анализ сцен, prefab, assets и ссылок** — semantic commands до чтения raw Unity YAML.
- **Контролируемые правки** — read-only risk report или dry run перед точной write-командой, которую дополнительно проверяют policy и approval.
- **Тесты и билды** — bounded operation, polling статуса и чтение больших результатов через project-local artifacts.
- **Работа с несколькими проектами** — явный project/instance в каждом вызове и параллельные очереди разных проектов.
- **Команды установленных пакетов** — asset package публикует свой command set в каталоге конкретного проекта.

Готовые последовательности и примеры пользовательских запросов находятся в [Сценариях работы](./docs/ru/guides/workflows.md).

## Agent API для ваших Unity-ассетов

Unity asset или tool package может предоставить API для агента без второго MCP server.

```text
com.example.my-asset
  ├─ Editor/Mcp/mcp.commands.json
  │     регистрация без зависимости от SGG API
  └─ [McpCommand] C# Editor handlers
        глубокая интеграция с bridge API
                 │
                 ▼
       command catalog проекта
                 │
                 ▼
      sgg_unity_call + args_json
```

Команды пакета не превращаются в новые top-level MCP tools. Gateway применяет к ним те же schema validation, output limits, feature flags, package trust, policy, approval и audit.

Начните с документа [Как добавить поддержку MCP в свой ассет](./docs/ru/extensions/quick-start.md).

## Безопасность по умолчанию

Базовый deployment локальный:

- Gateway по умолчанию слушает `127.0.0.1`;
- local token поддерживается и обязателен перед намеренным non-loopback доступом;
- команды имеют риск `read`, `write`, `execute`, `destructive` или `network`;
- feature flags делают command family видимой, а policy отдельно выбирает `allow`, `ask` или `deny`;
- агент не может approve-ить собственный `ask`-gated запрос;
- clients с advertised и rendered MCP form elicitation показывают native Gateway prompt;
- OpenCode v2 реализует формы, обновлённый shim relay-ит их capability-aware. Реальный OpenCode/OpenChamber frontend/human acceptance требует отдельных доказательств;
- dynamic C# execution — продвинутая локальная автоматизация, а не sandbox.

Перед широкими write/execute/network/destructive правами прочитайте [Права и подтверждения](./docs/ru/guides/permissions.md) и [Безопасность](./docs/ru/reference/security.md).

## Требования и текущая поддержка

| Область | Текущий контракт |
| --- | --- |
| Unity host | Нормальный поддерживаемый вариант — Unity Editor и Gateway на Windows. |
| Версия Unity | Для разработки рекомендуется Unity `6000.4+`. В package metadata сохранён `2022.3` как import/compile floor; публичную поддержку определяет фактическая release validation. |
| Render pipeline | Архитектура Gateway не зависит от Built-in, URP или HDRP. У команд отдельных пакетов могут быть свои требования. |
| MCP-клиенты | Основной документированный путь — OpenCode. Также описаны Cursor, Unity AI Assistant и generic stdio/remote HTTP clients. |
| MCP protocol | Direct HTTP и direct stdio поддерживают modern `2026-07-28` и legacy MCP 2025. HTTP-to-stdio shim остаётся legacy-only. |
| Elicitation в OpenCode v2 | Формы реализованы; authoritative — advertised/rendered capability connection и реальный human UI acceptance. OpenChamber требует собственного evidence. |
| Node.js | Обычный Setup устанавливает собственный portable Node. Системный Node не требуется. |
| WSL | OpenCode может работать в WSL; для одной машины рекомендуется Windows stdio shim. |
| Deployment | Local-first. Hosted runner, cloud project hosting, OAuth, JWT, SSO и enterprise identity не заявлены. |

## Документация

**Начало работы**

- [Каноническая русская документация](./docs/ru/README.md)
- [Быстрый старт](./docs/ru/quick-start.md)
- [Установка](./docs/ru/installation.md)
- [Диагностика проблем](./docs/ru/troubleshooting.md)

**Подключение**

- [OpenCode](./docs/ru/connect/opencode.md)
- [Cursor](./docs/ru/connect/cursor.md)
- [Unity AI Assistant](./docs/ru/connect/unity-ai-assistant.md)
- [Generic stdio clients](./docs/ru/connect/generic-stdio.md)
- [Windows и WSL](./docs/ru/connect/windows-wsl.md)

**Использование**

- [Основные сценарии](./docs/ru/guides/workflows.md)
- [Права и подтверждения](./docs/ru/guides/permissions.md)
- [Маршрутизация нескольких проектов](./docs/ru/guides/multi-project.md)
- [Jobs и artifacts](./docs/ru/guides/jobs-and-artifacts.md)
- [Diagnostics](./docs/ru/guides/diagnostics.md)
- [Миграция Canvas/uGUI в UI Toolkit](./docs/ru/guides/canvas-to-uitk.md)

**Расширение ассетов**

- [Быстрый старт для extension author](./docs/ru/extensions/quick-start.md)
- [Command manifests](./docs/ru/extensions/command-manifest.md)
- [C# handlers](./docs/ru/extensions/csharp-handlers.md)
- [Trust и governance](./docs/ru/extensions/governance.md)
- [Тестирование extensions](./docs/ru/extensions/testing.md)

**Справочник**

- [Архитектура](./docs/ru/reference/architecture.md)
- [MCP tools](./docs/ru/reference/mcp-tools.md)
- [Command catalog](./docs/ru/reference/commands.md)
- [Конфигурация](./docs/ru/reference/configuration.md)
- [Policy](./docs/ru/reference/policy.md)
- [Коды ошибок](./docs/ru/reference/error-codes.md)
- [Совместимость](./docs/ru/reference/compatibility.md)
- [Безопасность](./docs/ru/reference/security.md)
- [FAQ по лицензии](./docs/ru/reference/license.md)
- [Сравнение](./docs/ru/comparison.md)
- [Ограничения](./docs/ru/limitations.md)

## Известные ограничения

- Это не официальный продукт Unity.
- Базовый deployment local-first; hosted или managed runner отсутствует.
- Открытые проекты могут использовать разные версии package/runtime. Нужно смотреть Setup Health и version diagnostics, а не предполагать автоматическую совместимость.
- Checkpoints покрывают ограниченный набор project-local файлов, а не всё состояние проекта и Editor.
- Profiler и UI migration capabilities зависят от command family; сначала проверяйте актуальный catalog.
- Native elicitation зависит от того, объявляет ли MCP-клиент capability и умеет ли отрисовать форму. Pending approval остаётся compatibility path.

## Версионирование

Installable builds используют calendar build version в SemVer-compatible форме:

```text
YYYY.M.D-N
```

Подробнее: [Версионирование](./docs/ru/maintainers/versioning.md).

## Лицензия

Проект лицензирован по **Stinger Royalty-Free EULA 1.0**.

- Основной русский текст: [LICENSE.ru.md](./LICENSE.ru.md)
- Английский справочный перевод: [LICENSE.md](./LICENSE.md)
- Уведомления: [NOTICE.md](./NOTICE.md)
- Практическое объяснение: [FAQ по лицензии](./docs/ru/reference/license.md)
