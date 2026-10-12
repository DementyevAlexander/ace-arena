[English](./troubleshooting.md) | [Русский](../ru/troubleshooting.md)

# Troubleshooting


Start with the symptom. Use the Unity window first, then MCP diagnostics, then CLI recovery. Do not broaden network exposure or policy merely to make an error disappear.

## Fast Symptom Table

| Symptom | First action |
| --- | --- |
| Setup Health is red | `Setup -> Refresh Health`, then `Copy Log`. |
| MCP client cannot connect | Verify the selected connection mode and token placeholder/secret source. |
| `sgg_unity_projects` returns no projects | Check Bridge status, Unity compilation, and current registry health. |
| Project is stale/offline | Wait for Unity reload/compile, inspect modal state, call projects again. |
| Project selector is ambiguous | Select exact `instance_id` or canonical identity from discovery. |
| Command is missing | Describe catalog; check package install/compile and feature flag. |
| `feature_disabled` | Enable only the required feature, restart Gateway, describe again. |
| `permission_denied` | Stop; review project policy outside MCP. |
| `confirmation_required` | Use a native prompt only when the active connection advertises and renders elicitation; otherwise use the documented fallback. |
| Native prompt does not appear | Verify the active connection advertises and renders MCP form elicitation over a nested-request-capable transport; otherwise use fallback. |
| Schema error | Execute `error.details.schema_lookup`, then create a new corrected call. |
| WSL connection refused | Use Windows stdio shim or intentional secured remote HTTP. |
| Unity appears frozen | Inspect modal/startup/crash diagnostics and Editor UI. |
| Package operation appears stuck | Read `package.operation.status/list`; do not start conflicting mutation. |
| Path rejected | Use the command's Unity-relative/fixed-root contract. |

## Setup Health Is Red

In Unity:

```text
SGG/Unity MCP/MCP Window -> Setup
```

Run:

```text
Refresh Health
Copy Log
```

Then inspect `Status` for:

```text
Unity Bridge active/ready
Project Id
Gateway process and health
MCP URL
package/runtime version or hash mismatch
token present/missing state
package refresh/reload state
```

Use `Install Runtime`, `Start Gateway`, and `Refresh Health` when the bundled runtime is missing or stale. Do not install a separate Gateway per project.

## MCP Client Cannot Connect

Check:

```text
Gateway process is running
client URL/path matches /mcp
token comes from SGG_UNITY_MCP_TOKEN when configured
header name is X-SGG-Unity-MCP-Token
stdio shim points to an already running HTTP Gateway
WSL mode matches the chosen topology
```

Default local endpoint:

```text
http://127.0.0.1:27182/mcp
```

Do not browse the MCP endpoint as if it were a normal web page and infer health from the result. Use the client connection or the `/health` endpoint/window.

Offline check:

```powershell
sgg-unity-mcp-gateway doctor --json --config "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\gateway.json" --policy "%LOCALAPPDATA%\SGG\UnityMcpGateway\config\policy.json"
```

## No Projects Listed

Call:

```text
sgg_unity_projects
```

Check:

- the Unity package is installed in the target project;
- Unity finished compiling the bridge Editor assembly;
- CanonicalPath setup is complete;
- the Unity MCP Window shows the expected Project Id;
- the bridge heartbeat is fresh;
- Gateway and Unity use the same user-local registry root;
- a startup modal is not blocking Unity main-thread initialization.

An empty current registry does not prove the package is absent. Report only what current evidence shows.

## Project Stale, Offline, Or Rotated

Compilation, package changes, domain reload, and Editor restart can replace a bridge instance.

Safe sequence:

```text
1. sgg_unity_projects
2. choose a current ready instance
3. sgg_unity_describe(project, selector="commands")
4. repeat only the still-intended operation as a new explicit call
```

Read/status commands may use bounded recovery. Write, execute, destructive, and network commands are not automatically replayed after uncertain delivery.

If a write timed out, inspect the project before repeating it.

## Duplicate Or Ambiguous Project

When two Editors publish the same project id/display name, the Gateway fails closed.

Use one of:

```text
instance_id
canonical_project_root
an exact reported alias
```

Close duplicate Editors when they are accidental. Never add an implicit active-project fallback.

## Command Missing Or Catalog Stale

```text
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="command:<id>")
```

A missing command can mean:

```text
package not installed
Unity compile failed
feature flag disabled
package/runtime version differs
extension manifest invalid or unsupported path
command removed/renamed
planned command not implemented
```

On `catalog_stale`, describe again after Unity reload.

## Feature And Policy Errors

```text
feature_disabled
  command family is hidden; enable only the required flag and restart

permission_denied
  current policy denies; do not bypass

confirmation_required
  current policy asks for one exact human approval

policy_degraded
  restore valid policy; risky commands fail closed
```

Policy allow does not enable features. Feature enable does not grant permission.

## OpenCode Native Elicitation

The Gateway can produce a native MCP form prompt only when the active connection advertises and renders elicitation. OpenCode v2 implements forms; verify actual location/frontend display and human acceptance separately. A working MCP connection or installed plugin alone is not that evidence.

If it does not appear:

1. Verify the client connection advertises `elicitation.form`.
2. Verify the UI renders `elicitation/create` and nested requests reach it; the updated Windows stdio shim relays forms only for an outer `elicitation.form` connection. Check the installed runtime version.
3. Check the tool result for `confirmation_required` and `approval_workflow`.
4. Use the pending fallback instead of `auto_allow_ask`.
5. Do not add an MCP self-approval tool.

Fallback:

```powershell
sgg-unity-mcp-gateway approval pending --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --project game
sgg-unity-mcp-gateway approval approve --dir "%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals" --pending-id <pending-id>
```

Retry the exact original call after approval.

## Schema Errors

For schema-class errors, inspect:

```text
error.details.schema_lookup
```

Execute its `metadata_lookup` as a top-level MCP tool call. It will point to the exact descriptor/catalog or to `sgg_unity_projects` when the project could not be resolved safely.

Do not pass a metadata operation name as the Unity `command`. After reading the descriptor, construct a new corrected `sgg_unity_call`.

## Windows And WSL

Recommended same-machine path:

```text
OpenCode in WSL
 -> powershell.exe
 -> Windows stdio shim
 -> Windows loopback Gateway
 -> Unity Bridge
```

If direct WSL HTTP to Windows loopback fails, that is expected in common NAT topology. Use the stdio shim.

Remote HTTP requires:

```text
Windows host IP
allow_non_loopback=true
token
allowed_origins
scoped firewall rule
```

Do not expose the Gateway broadly to fix a localhost namespace issue.

## Unity Modal Or Startup Blocker

Use the Unity Editor UI first. When diagnostics are enabled:

```text
diagnostics.unity_startup.status
diagnostics.unity_modal.status
diagnostics.unity_recovery_prompt.status
diagnostics.unity_crash_reporter.status
```

Read-only diagnostics do not click arbitrary dialogs. Narrow response commands remain policy/approval gated and should use dry-run first.

A stale heartbeat plus a live Unity process can still mean Unity is blocked by a modal or main-thread freeze.

## Compile And Refresh

Use:

```text
compile.status
compile.errors.recent / compile.warnings.recent when available
logs.console.query/read/tail
```

After reviewed package/source changes, use explicit `assets.refresh`, `compile.refresh_and_wait`, or `validation.after_edit` according to the discovered schema and policy.

The package may report `package_refresh_required` or `stale_package_resolution`; the bridge does not promise an automatic `AssetDatabase.Refresh`.

## Package Operations

Package mutations are asynchronous and return an operation id.

```text
package.operation.status
package.operation.list
```

Interpret states carefully:

```text
running
  keep polling within bounds

long_running
  inspect Package Manager; do not start conflicting mutation

persisted_running_after_reload
  verify package.list and compile.status; operation may have completed

stale_persisted_running
  stale evidence; report diagnostics if recurring
```

PackageManager operation cancellation is unsupported because Unity Package Manager does not expose a stable general cancellation/progress API for every operation. PackageManager cancellation is unsupported across reload recovery as well; do not invent percentages or cancellation.

Use `package.operation.prune` with `dry_run:true` before destructive cleanup.

## Jobs And Artifacts

Use the exact returned job/operation/artifact id. Poll at a bounded interval and stop at terminal state.

If an artifact is large:

```text
artifact.list
artifact.metadata
job.artifact.read with limits
```

Do not derive an absolute path or read the filesystem directly.

## Path Rejected

Typical corrections:

```text
C:\Game\Assets\A.prefab      -> Assets/A.prefab
/mnt/c/Game/Assets/A.prefab  -> Assets/A.prefab
../Assets/A.prefab           -> reject traversal; provide Assets/A.prefab
```

Some commands accept only `Assets/...`; others allow read-only `Packages/...`; artifact commands use fixed roots. Read the exact schema.

## Diagnostics Report

When the failure is reproducible:

```json
{
  "project": "game",
  "command": "diagnostics.report_problem",
  "args_json": "{\"summary\":\"Describe the observed failure without secrets\",\"last_command\":\"compile.status\",\"last_error_code\":\"bridge_unreachable\",\"include_snapshot\":true}",
  "mode": "wait"
}
```

If MCP is unreachable, use CLI `report-problem --include-doctor`.

## What To Include In A Report

```text
Gateway/package version
Unity exact version
client and connection mode
project selector used
exact command and error code
current Unity state
steps to reproduce
what was expected/observed
checks run and checks not run
bounded redacted artifacts
```

Never include tokens, auth headers, private keys, full approval stores, raw credentials, or unredacted proprietary project content.

## Related Docs

- [Diagnostics](./guides/diagnostics.md)
- [Error codes](./reference/error-codes.md)
- [OpenCode](./connect/opencode.md)
- [Windows and WSL](./connect/windows-wsl.md)
- [Permissions and approvals](./guides/permissions.md)
