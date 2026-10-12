[English](../en/comparison.md) | [Русский](./comparison.md)

# Сравнение И Позиционирование


SGG Unity MCP Gateway оптимизирован как local control plane для external coding agents и нескольких Unity projects. Это не universal replacement всех Unity AI/automation/Editor workflows.

## Основной Фокус

```text
explicit project routing
small stable MCP surface
JIT project catalogs
one Gateway for multiple Editors
Windows/WSL identity
local policy/exact approvals
package agent APIs
bounded jobs/artifacts/diagnostics/audit
```

## Unity AI Assistant / Official Unity MCP

Unity AI Assistant — official in-Editor experience для Unity UI/services/generators/integrations.

SGG:

- external clients OpenCode/Cursor;
- local-first без Assistant chat/Cloud/credits/relay;
- explicit project;
- command catalog;
- может подключаться к Unity AI Assistant как optional client;
- optional relay tools остаются catalog entries под SGG policy.

Подходы complementary.

## Broad Unity MCP Toolsets

Broad open-source packages хороши для immediate breadth/direct tools.

SGG выбирает:

```text
eight tools
explicit project
per-project discovery
external Gateway
feature/policy/approval
package trust
bounded output/jobs
```

Tradeoff: describe + `args_json` вместо отдельной top-level function на каждую operation.

## Editor-Embedded Servers

Embedded server проще для одного project. SGG даёт one endpoint/multi-project/shared policy-audit-jobs, но требует external process, version health и project selector.

## Unrestricted Automation

Raw shell/C#/filesystem быстрее, но SGG добавляет schemas/risk/flags/policy/approval/dry-run/artifacts/audit/no risky replay. Это ceremony ради reviewability.

## Cloud/Enterprise

Не заявляются managed runners, remote hosting, org identity, OAuth/JWT/SSO, multi-tenant isolation, central policy или hosted audit.

## Separate MCP Server Per Asset

SGG позволяет package добавить manifest/[McpCommand] в existing catalog и использовать same eight tools/policy/approval/audit.

## Good Fit

- external MCP client;
- несколько projects;
- важен context cost;
- explicit routing;
- approvals/audit;
- asset agent APIs;
- Windows/WSL.

## May Not Fit

- нужен hosted/enterprise сегодня;
- нужен implicit active Editor;
- unrestricted authority by default;
- отдельный top-level tool на всё;
- Unity Editor integration не нужна.

## Связанные Документы

- [Architecture](./reference/architecture.md)
- [Compatibility](./reference/compatibility.md)
- [Limitations](./limitations.md)
- [Extension quick start](./extensions/quick-start.md)
