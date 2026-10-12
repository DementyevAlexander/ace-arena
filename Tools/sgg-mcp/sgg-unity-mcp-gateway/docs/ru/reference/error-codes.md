[English](../../en/reference/error-codes.md) | [Русский](./error-codes.md)

# Error Codes И Recovery


Gateway errors имеют stable codes и bounded safe details. Реагируйте на code и `next_valid_actions`, не парсите free-form text как execution contract.

## Routing И Catalog

| Code | Значение | Действие |
| --- | --- | --- |
| `project_offline` | Project/instance unavailable/stale. | `sgg_unity_projects`, current live identity, Unity state. |
| `project_not_found` | Нет match. | Discover, не invent id. |
| duplicate/ambiguous | Несколько records. | Exact instance/canonical identity. |
| `catalog_stale` | Catalog changed. | Describe again. |
| `command_not_found` | Command absent. | Catalog/package/compile/feature/version. |
| `feature_disabled` | Family hidden. | Enable outside MCP, restart, describe. |

## Schema И Input

| Code | Значение | Действие |
| --- | --- | --- |
| `invalid_json` | JSON parse failure. | `schema_lookup`, новый JSON. |
| `args_json_must_be_object` | Не object. | Object по descriptor. |
| `items_json_must_be_array` | Batch items не array. | Read top-level schema. |
| `missing_required_field` | Missing field. | Current descriptor. |
| `invalid_field_value` | Constraint failure. | `schema_lookup`, не guess. |
| `schema_validation_failed` | Schema mismatch. | Execute lookup, new corrected call. |
| `schema_unavailable` | Schema нельзя safe предоставить. | Refresh state/catalog. |

Recovery discovery-only и не retry/authorize-ит command.

## Policy И Approval

| Code | Значение | Действие |
| --- | --- | --- |
| `permission_denied` | Deny. | Не обходить. Policy review вне MCP. |
| `confirmation_required` | Ask без valid exact approval. | На capable connection ответить native prompt; иначе использовать pending fallback. |
| `policy_degraded` | Invalid/missing/unstable policy. | Restore valid policy; risky fail closed. |
| approval invalidated/expired | Context/TTL changed. | После review повторить original call для fresh request. |

## Paths И Storage

| Code | Значение | Действие |
| --- | --- | --- |
| `absolute_path_rejected` | Нужен relative path. | Убрать drive/UNC/WSL root. |
| `path_traversal_rejected` | Unsafe `..`. | Valid bounded target. |
| `path_not_allowed` | Вне allowed roots. | `Assets/...`, `Packages/...` или fixed artifact root. |
| output bound | Too large/invalid root. | Narrow read, metadata+slices. |

## Unity И Bridge

| Code | Значение | Действие |
| --- | --- | --- |
| `bridge_unreachable` | Нет current bridge. | Discovery/compile/reload/modal; risky не auto-repeat. |
| `bridge_timeout` / `command_timeout` | Bound elapsed. | Reads may recover; risky delivery uncertain. |
| `bridge_protocol_error` | Malformed protocol. | Diagnostics; не считать success. |
| `unity_busy` | Unity state blocks. | Resolve compile/reload/modal/save. |
| `compilation_failed` | Compiler failure. | Read diagnostics, fix, validate. |
| `unity_exception` | Normalized handler exception. | Bounded details, no raw leak. |

## Jobs И Operations

| Code | Значение | Действие |
| --- | --- | --- |
| not found | Id expired/missing. | Exact returned id/list/status. |
| cancel unsupported | No real cancel. | Poll/finish, не claim cancel. |
| busy/conflict | Resource owned. | Inspect status, no conflicting mutation. |
| uncertain timeout | Side effect possible. | Inspect state before explicit repeat. |

## Network И Transport

| Code | Значение | Действие |
| --- | --- | --- |
| auth/token | Secret mismatch/missing. | Fix secret config, не print token. |
| origin/non-loopback | Safety requirements not met. | Loopback/stdio или intentional secure config. |
| rate/session capacity | Transport bound. | Reduce rate/clients, close idle, health. |

## Report Shape

```text
project selector
command id
top-level tool
exact error code
evidence age
Unity state
schema_lookup executed
possible delivery
checks not run
one safe next action
```

Не включайте raw proprietary args, tokens, approval bindings или absolute paths.

## Связанные Документы

- [Diagnostics](../guides/diagnostics.md)
- [Troubleshooting](../troubleshooting.md)
- [MCP tools](./mcp-tools.md)
- [Policy](./policy.md)
