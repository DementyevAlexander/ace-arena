[English](../../en/reference/architecture.md) | [Русский](./architecture.md)

# Архитектура


SGG Unity MCP Gateway — local control plane между MCP clients и одним или несколькими Unity Editor projects.

## Topology

```text
MCP client
  OpenCode / Cursor / Unity AI Assistant / generic client
                         │
             direct HTTP / direct stdio / stdio shim
                         ▼
              SGG Unity MCP Gateway
         routing / catalog / policy / jobs / audit
              │             │             │
              ▼             ▼             ▼
        Unity Bridge A  Unity Bridge B  Unity Bridge C
        Project A       Project B       Project C
```

Gateway — MCP server. Unity package — Editor-only bridge, не второй MCP server.

## Dual-Era MCP Transport

Одни и те же восемь tools обслуживаются в двух protocol eras:

```text
/mcp request
  modern 2026-07-28 -> stateless SDK 2 handler, fresh server per request, MRTR
  legacy MCP 2025   -> существующий SDK 1 sessionful/stateless compatibility handler

direct --stdio      -> SDK 2 выбирает modern или legacy на connection
stdio-shim          -> legacy-only compatibility adapter к HTTP Gateway
```

Modern requests не создают protocol session и не используют `Mcp-Session-Id`. Roots observer подключается только к legacy instances. Jobs, operation ids, queues, persisted approvals, policy, registry и audit остаются shared application state между per-request modern server instances.

## Stable MCP Surface

Восемь top-level tools:

```text
sgg_unity_projects
sgg_unity_stale_projects
sgg_unity_stale_registry_cleanup
sgg_unity_describe
sgg_unity_call
sgg_unity_batch
sgg_unity_job
sgg_unity_logs
```

Unity operations вроде `scene.hierarchy`, `asset.search`, `test.run` и package-contributed commands остаются catalog entries. Capabilities растут без постоянного раздувания model tool context.

## Explicit Project Routing

Global active project нет.

Каждый call resolve-ит supplied selector в один current project/instance. Selector берётся из discovery: public project id, instance id, canonical root, unambiguous display name или reported alias.

После resolution очереди, jobs, policy, cache, approval binding и audit используют resolved identity. Ambiguous/duplicate identity fail-ит вместо silent selection.

## Project Discovery

Unity bridge публикует bounded heartbeat и command catalog metadata в user-local registry. Gateway читает его и показывает live/stale/reloading/offline state.

Bridge отвечает за Unity main-thread execution. Gateway — за transport, routing, policy, approvals, jobs, virtual commands, audit и client compatibility.

## Command Catalog

Catalog project-specific, потому что отличаются:

- packages и versions;
- feature flags;
- package manifests;
- Unity/editor capabilities;
- descriptors после compile/domain reload.

JIT discovery:

```text
projects -> describe commands -> describe exact command -> call
```

Catalog hash/revision позволяет ловить stale descriptor. На `catalog_stale` снова describe, а не повтор old schema.

## Invocation Pipeline

```text
1. Parse top-level input.
2. Resolve explicit project/instance.
3. Resolve current descriptor.
4. Verify feature visibility.
5. Validate args_json.
6. Resolve package trust.
7. Evaluate policy/risk.
8. При ask — modern MRTR, legacy form elicitation или pending approval.
9. Revalidate после approval.
10. Admit в project queue или создать job.
11. Invoke virtual logic/relay/Unity Bridge.
12. Normalize и bound result.
13. Write redacted audit outcome.
```

Deny, invalid schema, stale project, failed revalidation или hidden feature останавливают execution до mutation.

## Concurrency

Commands одного Unity project сериализуются. `parallel_by_project` позволяет independent queues разных проектов.

## Wait И Job Modes

`mode:"wait"` возвращает bounded result в исходном call.

`mode:"job"` создаёт Gateway-local job только для commands с поддержкой. Poll — через `sgg_unity_job`. Large output должен быть artifact-backed.

## Path Identity И Safety

CanonicalPath даёт deterministic lexical identity Windows/WSL aliases. Это не filesystem sandbox.

Security обеспечивают:

```text
command schema
Unity-relative Assets/... и Packages/... rules
fixed Gateway storage roots
CanonicalFS where used
Unity-side PathGuard
package trust и policy
```

Agent payload не должен содержать arbitrary absolute paths.

## Approval Architecture

MCP self-approval tool нет.

В modern MCP `2026-07-28` Gateway возвращает `input_required` с HMAC-signed opaque correlation state; клиент заполняет форму и повторяет original call. Legacy clients могут использовать form elicitation. В обеих eras Accept приводит к полной revalidation и не более чем одному exact execution; request state не является доказательством consent.

Без elicitation возвращается `confirmation_required` и pending identity для human review вне MCP. Agent явно повторяет exact call.

## Extension Architecture

Unity package добавляет commands через:

```text
[McpCommand] Editor handler
Editor/Mcp/mcp.commands.json manifest-only handler
```

Bridge публикует descriptors, Gateway применяет trust/schema/policy/approval/output/audit. Package не может сам стать trusted и не добавляет top-level tool.

## Local-First Boundary

```text
trusted local developer machine
Gateway на Windows loopback
Unity Editors на Windows
OpenCode в WSL через Windows stdio shim
user-controlled LocalAppData config/policy/approvals/audit/runtime
```

Не заявляются hosted execution, multi-user tenant isolation, OAuth/JWT/SSO или sandbox против другого process того же OS user.

## Связанные Документы

- [MCP tools](./mcp-tools.md)
- [Command catalog](./commands.md)
- [Configuration](./configuration.md)
- [Policy](./policy.md)
- [Security](./security.md)
