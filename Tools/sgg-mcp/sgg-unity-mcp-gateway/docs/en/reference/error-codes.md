[English](./error-codes.md) | [Русский](../../ru/reference/error-codes.md)

# Error Codes And Recovery


Gateway errors use stable codes and bounded safe details. Respond to the code and `next_valid_actions`; do not parse free-form text as an execution contract.

## Routing And Catalog

| Code | Meaning | Safe response |
| --- | --- | --- |
| `project_offline` | Selected project/instance is unavailable or stale. | Call `sgg_unity_projects`, select a current live identity, inspect Unity state. |
| `project_not_found` | Selector has no current match. | Discover projects; do not invent an id. |
| `duplicate_project_id` / ambiguous selector | Multiple records match. | Choose an exact instance id or canonical identity from discovery. |
| `catalog_stale` | Descriptor/catalog changed. | Describe the project/command again. |
| `command_not_found` | Command is absent from the current catalog. | Describe catalog; verify package, compilation, feature, and version. |
| `feature_disabled` | Command family is implemented but hidden. | Enable only the required feature outside MCP, restart Gateway, describe again. |

## Schema And Input

| Code | Meaning | Safe response |
| --- | --- | --- |
| `invalid_json` | `args_json` or another JSON string could not be parsed. | Execute `schema_lookup`; construct new valid JSON. |
| `args_json_must_be_object` | Parsed value is not an object. | Use an object matching the descriptor. |
| `items_json_must_be_array` | Batch items JSON is not an array. | Read the top-level batch schema. |
| `missing_required_field` | Required field is absent. | Read the current descriptor. |
| `invalid_field_value` | Field violates command constraints. | Use `schema_lookup`; do not guess. |
| `schema_validation_failed` | One or more arguments violate schema. | Execute `error.details.schema_lookup`, then create a new corrected call. |
| `schema_unavailable` | Current schema cannot be safely supplied. | Refresh catalog/project state; do not call blindly. |

Schema recovery is discovery-only. It never authorizes or automatically retries the failed command.

## Policy And Approval

| Code | Meaning | Safe response |
| --- | --- | --- |
| `permission_denied` | Current policy is deny. | Do not bypass or disguise the operation. Review policy outside MCP only when intended. |
| `confirmation_required` | Current policy is ask and no valid exact approval is active. | On a capable connection, respond to the native prompt. Otherwise review the pending id, approve once, and retry the exact call. |
| `policy_degraded` | Configured replacement policy is invalid/missing/unstable. | Restore a valid policy. Risky commands remain fail-closed. |
| approval expired/invalidated | Pending context no longer matches or TTL elapsed. | Re-run the original call to produce a fresh current request after reviewing state. |

## Paths And Storage

| Code | Meaning | Safe response |
| --- | --- | --- |
| `absolute_path_rejected` | Command requires a Unity-relative/project-relative path. | Remove drive/UNC/WSL/leading-root syntax. |
| `path_traversal_rejected` | Path contains unsafe traversal. | Remove `..` and use a valid bounded target. |
| `path_not_allowed` | Path is outside the command's allowed roots. | Use `Assets/...`, `Packages/...`, or the documented fixed artifact root. |
| output/artifact bound error | Requested content exceeds limits or invalid root. | Narrow the read; use metadata and bounded slices. |

## Unity And Bridge Lifecycle

| Code | Meaning | Safe response |
| --- | --- | --- |
| `bridge_unreachable` | Gateway cannot reach current bridge endpoint. | Inspect project discovery, compile/reload/modal state; do not auto-repeat risky work. |
| `bridge_timeout` / `command_timeout` | Response did not complete in the bound. | Reads may have bounded recovery; risky delivery is uncertain and must not be auto-replayed. |
| `bridge_protocol_error` | Response violated the bridge protocol. | Record diagnostics; do not reinterpret an empty/malformed body as success. |
| `unity_busy` | Unity state blocks safe execution. | Wait for compile/reload/modal/save state to resolve, then re-discover. |
| `compilation_failed` | Explicit compile/validation completed with compiler failures. | Read bounded compiler diagnostics; fix project; validate again. |
| `unity_exception` | Unity handler failed with a normalized exception. | Read bounded details and diagnostics; do not expose raw exception/host data. |

## Jobs And Operations

| Code | Meaning | Safe response |
| --- | --- | --- |
| job/operation not found | Id is missing, expired, or from another context. | Use the exact returned id and list/status commands. |
| job cancellation unsupported | Command did not advertise real cancellation. | Keep polling or let operation finish; do not claim cancellation. |
| operation busy/conflict | Another bounded operation owns the resource. | Inspect status and avoid starting conflicting mutations. |
| timeout with uncertain side effect | Delivery may have occurred. | Inspect state/artifacts before any explicit repeat. |

## Network And Transport

| Code | Meaning | Safe response |
| --- | --- | --- |
| authentication/token error | Client token does not match or is missing where required. | Fix client secret configuration; never print the token. |
| origin/non-loopback rejection | Deployment safety requirements are not met. | Prefer loopback/stdio shim or configure token, origins, firewall intentionally. |
| rate/session capacity error | Local transport bound was reached. | Reduce clients/request rate, close idle sessions, inspect health. |

## Diagnostic Report Shape

When reporting a failure, include:

```text
project selector used
command id
top-level MCP tool
exact error code
current/historical evidence age
Unity readiness state
whether schema_lookup was executed
whether the operation may have been delivered
checks not run
one safe next action
```

Do not include raw arguments when they may contain proprietary data, token values, approval bindings, or absolute local paths.

## Related Docs

- [Diagnostics](../guides/diagnostics.md)
- [Troubleshooting](../troubleshooting.md)
- [MCP tools](./mcp-tools.md)
- [Policy](./policy.md)
