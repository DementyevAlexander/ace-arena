[English](../../en/guides/multi-project.md) | [Русский](./multi-project.md)

# Маршрутизация Нескольких Проектов


В SGG Unity MCP Gateway нет global active Unity project. Target project явно задаётся в каждом Unity command call.

## Зачем Нужен Explicit Routing

При нескольких Editors implicit active session меняется из-за focus, reconnect, domain reload или другого client. Explicit routing делает target видимым в request и audit.

```text
ПЛОХО
вызвать scene.hierarchy в случайно активном Editor

ХОРОШО
sgg_unity_call(
  project="tactics",
  command="scene.hierarchy",
  args_json="..."
)
```

## Поиск Проектов

Вызовите:

```text
sgg_unity_projects
```

Project record может содержать:

- public project id;
- display name;
- instance id;
- status/readiness;
- Unity version;
- package version;
- canonical project root identity;
- deterministic aliases для Windows, WSL, Unity-style или POSIX views.

Host-path aliases помогают выбрать проект, но не дают permission на прямой доступ к host path.

## Selectors

`project` может разрешаться через поддерживаемые public identities:

```text
project id
unique display name
instance id
canonical project root
known alias path
```

Для обычных calls используйте stable project id. Exact instance id нужен при нескольких live Editors с одним id или при stale/duplicate records.

## Duplicate Project Id

Если несколько live records разрешаются в одну identity, Gateway fail-closed и не выбирает случайный Editor.

Recovery:

```text
1. sgg_unity_projects
2. посмотреть instance ids и status
3. закрыть лишний Editor либо
4. выбрать exact intended instance
5. заново describe catalog
```

Approval одного instance нельзя переносить на другой.

## Offline Или Stale Project

При `project_offline`, `project_stale` или typed lifecycle error:

```text
1. sgg_unity_projects
2. дождаться compile/domain reload Unity
3. посмотреть current candidate instance
4. compile.status по fresh selector
5. risky command повторять только новым explicit request
```

Gateway может выполнять bounded recovery для safe read/status/wait. Write, execute, destructive и network commands после uncertain delivery автоматически не replay-ятся.

## Catalog Конкретного Проекта

Проекты могут различаться по:

```text
Unity versions
Bridge package versions
installed Unity packages
extension command manifests
feature visibility
descriptor revisions
```

Всегда discover-ьте commands выбранного проекта:

```text
sgg_unity_describe(project="<project-id>", selector="commands")
```

При необходимости schema:

```text
sgg_unity_describe(project="<project-id>", selector="command:<command-id>")
```

Command из project A может отсутствовать в project B.

## Per-Project Policy

Policy rules привязаны к resolved project identity. В Unity Permissions проверяйте `Project Id` перед profile или edit rules.

Не следует:

- автоматически копировать broad allow во все проекты;
- считать trusted package одного проекта trusted везде;
- использовать pending approval другого проекта;
- выбирать project по неоднозначному name.

## Batches

`sgg_unity_batch` подходит для bounded related calls.

Top-level scheduling:

```text
sequential
parallel_by_project
```

Каждый item содержит explicit:

```text
project
command
args_json или legacy args
item mode
```

`parallel_by_project` может выполнять разные project queues параллельно. Внутри одного проекта calls serialized.

Не batch-ите, когда:

- следующий шаг требует reasoning по предыдущему result;
- нужны отдельные destructive approvals;
- command может вызвать domain reload;
- long operation должна быть job;
- project identity может измениться между steps.

## Пример: Чтение Трёх Проектов

Conceptual items:

```json
{
  "mode": "parallel_by_project",
  "items": [
    {
      "project": "game",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    },
    {
      "project": "tools",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    },
    {
      "project": "benchmark",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    }
  ]
}
```

Точные fields и bounds берите из current MCP tool schema.

## Windows И WSL Identity

CanonicalPath согласует эквивалентные lexical roots Windows/WSL. Он не:

- авторизует arbitrary absolute paths;
- обходит PathGuard;
- превращает WSL path в Unity command payload;
- скрывает collision двух разных проектов.

Agent payload paths остаются `Assets/...` или `Packages/...`.

## Audit Correlation

Optional `checkpoint_id` и `plan_id` связывают calls в audit. Они не меняют selected project, не grant-ят permission, не выполняют plan и не авторизуют rollback.

## Handoff Evidence

При передаче работы записывайте:

```text
MCP availability проверена в current session
selected public project id или exact instance
current Unity/package version при необходимости
catalog revision/hash при необходимости
compile.status result
typed error и next action при failure
```

Не полагайтесь на старую запись о том, что project online.

## Связанные Документы

- [Основные сценарии](./workflows.md)
- [MCP tools](../reference/mcp-tools.md)
- [Windows и WSL](../connect/windows-wsl.md)
- [Коды ошибок](../reference/error-codes.md)
