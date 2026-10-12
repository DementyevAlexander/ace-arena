[English](./diagnostics.md) | [Русский](../../ru/guides/diagnostics.md)

# Diagnostics


Use diagnostics in layers. Start with the Unity window, then MCP commands, then the local CLI only when the client or Gateway cannot complete the normal path.

## Diagnostic Order

```text
1. Setup -> Refresh Health
2. Setup -> Copy Log
3. Status -> project id, bridge heartbeat, Gateway health, package/runtime versions
4. Commands -> discover the exact command and schema
5. Permissions -> feature visibility and effective policy
6. diagnostics.* -> bounded redacted evidence
7. doctor/report-problem CLI -> offline fallback
```

Do not start by collecting entire log directories. Prefer the smallest redacted result that proves the failure.

## Gateway And Bridge Status

Useful read-only commands when `features.sgg_mcp_diagnostics` and policy allow them:

```text
diagnostics.status
diagnostics.command_statistics
diagnostics.audit.summary
diagnostics.audit.tail
diagnostics.problem.list
diagnostics.problem.read
diagnostics.problem.triage
diagnostics.recovery.suggest
```

A bounded problem report can be created with:

```json
{
  "project": "game",
  "command": "diagnostics.report_problem",
  "args_json": "{\"summary\":\"compile.status cannot reach the Unity bridge\",\"last_command\":\"compile.status\",\"last_error_code\":\"project_offline\",\"include_snapshot\":true}",
  "mode": "wait"
}
```

Problem reports live in a fixed Gateway-controlled directory. Commands do not accept arbitrary report paths. Reports redact tokens, authorization headers, cookies, passwords, secret-like values, and host paths.

## Schema Recovery

For these argument/schema errors:

```text
schema_validation_failed
invalid_field_value
schema_unavailable
args_json_must_be_object
items_json_must_be_array
invalid_json
missing_required_field
```

inspect:

```text
error.details.schema_lookup
```

The lookup is an executable metadata action, not a suggested retry of the failed mutation.

### Safe Project And Command

```json
{
  "action_kind": "mcp_tool",
  "tool": "sgg_unity_describe",
  "arguments": {
    "project": "game",
    "selector": "command:asset.import"
  }
}
```

### Unknown Command

The lookup uses the project catalog:

```json
{
  "tool": "sgg_unity_describe",
  "arguments": {
    "project": "game",
    "selector": "commands"
  }
}
```

### Project Could Not Be Resolved Safely

```json
{
  "tool": "sgg_unity_projects",
  "arguments": {}
}
```

Path-like or unresolved selectors are not repeated as resolved project ids. After reading the descriptor, construct a new explicit corrected call. Schema recovery never auto-retries `sgg_unity_call`, does not return authentication material, and does not authorize the original request.

## Unity Modal And Startup Blockers

When enabled, diagnostics can report bounded evidence for:

```text
diagnostics.unity_startup.status
diagnostics.unity_modal.status
diagnostics.unity_recovery_prompt.status
diagnostics.unity_crash_reporter.status
```

Read-only detection does not click arbitrary Unity dialogs. Narrow response commands such as recovery-prompt response or crash-reporter close remain destructive-risk, default to dry-run where supported, target only verified windows/processes, and require policy/approval.

When Unity is blocked by a modal or main-thread freeze, heartbeat data may be stale even though the process exists. Do not report the project as healthy until fresh bridge evidence confirms recovery.

## Compile And Reload Diagnostics

Recommended sequence:

```text
1. sgg_unity_projects
2. compile.status
3. compile.errors.recent or compile.warnings.recent when discoverable
4. logs.console.query/read/tail
5. diagnostics.unity_modal.status when Unity appears blocked
6. diagnostics.report_problem if the failure is recurring or handoff-worthy
```

`compile.refresh_and_wait` and `validation.after_edit` are explicit write/validation operations. Do not replace them with arbitrary filesystem touching or repeated compilation requests.

Package/runtime file changes may report `package_refresh_required` or `stale_package_resolution`. Review dirty-scene risk, then call an explicit refresh/validation command. The documentation does not promise an automatic `AssetDatabase.Refresh`.

## Audit

Audit is evidence, not authorization.

```text
diagnostics.command_statistics
  per-command call counts, UTF-8 request bytes, paired full/compact response bytes, and redacted failure causes

diagnostics.audit.summary
  aggregate outcomes and state

diagnostics.audit.tail
  bounded recent redacted records
```

`diagnostics.command_statistics` aggregates the retained local audit window without replaying commands. Full and compact sizes are serialized from the same logical result, while `delivered_response_bytes` records the mode actually returned. Internal terminal-wait polling is excluded from user command volume; batch items are counted under their command ids and the batch envelope remains a separate tool-level aggregate.

Statistics retain typed error code/stage, a fixed redacted explanation, byte counts, timestamps, and audit correlation ids. They do not retain raw arguments, request/response bodies, tokens, approval bindings, stack traces, host paths, or Unity project content. Coverage fields state whether retained files or bytes were omitted; the module is local-only and sends no telemetry.

`checkpoint_id`, `plan_id`, and request ids correlate work. They do not approve commands, run plans, or trigger rollback.

## Offline CLI Fallback

When the MCP client cannot reach the Gateway:

```powershell
sgg-unity-mcp-gateway doctor --json --config "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\gateway.json" --policy "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\policy.json"
```

Create a bounded redacted report without MCP:

```powershell
sgg-unity-mcp-gateway report-problem --project game --summary "MCP client cannot connect" --include-doctor --config "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\gateway.json" --policy "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\policy.json"
```

Do not attach raw config, token environment values, full approval storage, or unredacted local logs to public reports.

## Handoff Evidence

When another person or agent needs to continue, record:

```text
mcp_availability_checked: current_session
active_project_selector: exact selector used
sgg_unity_projects: ok/error plus bounded status
command: compile.status or another exact command id
compile_status_result: ok or exact error code
schema_lookup: executed/not needed/unavailable
unity_state: ready/compiling/reloading/modal/offline/unknown
evidence_age: current, not historical
checks_not_run: explicit list
next_action: one concrete safe action
```

Do not write `MCP skipped` or “MCP unavailable” from historical notes alone. Verify the current session first.

## Related Docs

- [Troubleshooting](../troubleshooting.md)
- [Error codes](../reference/error-codes.md)
- [Permissions and approvals](./permissions.md)
- [Jobs and artifacts](./jobs-and-artifacts.md)
