# Agent Guide — SGG Unity MCP Gateway

[English](./AGENTS.md) | [Русский](./AGENTS.ru.md)

This file defines the machine-facing rules for coding agents that use the Gateway. Human documentation starts at [docs/en/README.md](./docs/en/README.md).

## Required Routing Rules

- Call `sgg_unity_projects` before Unity work unless the user already supplied a current unambiguous project selector.
- Never assume a global active Unity project.
- Never invent `set_active`, `switch_project`, or similar session-switching tools.
- Every Unity command call and every batch item must include an explicit `project`.
- Use a current project id, instance id, canonical root, or exact alias returned by discovery.
- On duplicate/ambiguous selection, ask the user or select the exact returned instance. Do not guess.

## Required Discovery Rules

```text
1. sgg_unity_projects
2. sgg_unity_describe(project, selector="commands")
3. sgg_unity_describe(project, selector="command:<id>") when schema/risk/support is needed
4. sgg_unity_call / sgg_unity_batch
5. sgg_unity_job for returned jobs
6. sgg_unity_logs or diagnostics for transport/lifecycle failures
```

- Do not invent command ids.
- A roadmap/documented command is unavailable until it appears in the selected project's current catalog.
- On `catalog_stale`, describe again.
- On schema-class errors, execute `error.details.schema_lookup.metadata_lookup` as the top-level MCP tool it names, then construct a new corrected call.
- Never pass a metadata lookup name as `sgg_unity_call.command`.

## Call Shape

OpenCode v2 enables MCP Code Mode by default. Discover Gateway tools in its catalog and call through `execute`; absence from the provider-native tool list alone does not prove failure. Native config uses `mcp.servers` and `plugins`; file/artifact status does not prove active loading or visible labels.

```json
{
  "project": "game",
  "command": "compile.status",
  "args_json": "{}",
  "mode": "wait"
}
```

- `args_json` must parse to an object matching the current command schema.
- Keep command-specific fields inside `args_json`; keep `project`, `command`, `mode`, `request_id`, `checkpoint_id`, and `plan_id` at the top level.
- Keep counts, timeouts, frame ranges, log lines, search results, and artifact reads bounded.

## Path Rules

- Use Unity-relative paths required by the descriptor, normally `Assets/...` or read-only `Packages/...`.
- Do not pass Windows absolute paths, WSL absolute paths, UNC paths, traversal, NUL, or arbitrary output paths, except `build.run.output_root` when its live schema advertises that field. That exception accepts only an existing absolute fixed-local-drive Windows directory outside the project and forces exact human confirmation for a real build.
- Do not treat `canonical_project_root` or host aliases as permission to access the filesystem.
- Use fixed artifact/resource commands rather than direct filesystem reads when available.

## Safety And Approval

- Never bypass `permission_denied`, `feature_disabled`, `policy_degraded`, or `confirmation_required`.
- Prefer inspect/preflight/risk-report/dry-run commands before mutation.
- Native approval requires advertised and rendered MCP form elicitation. OpenCode v2 implements forms; the updated legacy shim relays them capability-aware. Actual frontend/human acceptance requires separate evidence. Accept/Decline/Cancel belongs to the human; opaque session metadata is not authority.
- For clients without elicitation, present the exact pending approval workflow to the user. The agent must not approve itself.
- Exact stale-registry cleanup may use the separately configured temporary no-elicitation compatibility exception; it never authorizes other `ask` decisions or turns chat text into approval.
- After a human pending approval, retry the original call unchanged; approval does not execute the command.
- `request_id`, argument hashes, `checkpoint_id`, and `plan_id` are not authorization.
- Do not enable `auto_allow_ask` unless the user explicitly chooses that trusted-session compatibility mode.
- An owner may separately opt into `approvals.auto_allow_known_unity_popups` in the MCP Window. When the running Gateway reports it enabled, supported exact built-in popup responses may proceed without another human question; keep deny/feature gates, exact target checks, dry-run/precondition and confirm arguments, and no replay. This is not generic click permission, process termination, or authority to enable the setting yourself. A returned `confirmation_required` still requires the normal human workflow.
- Do not automatically retry write, execute, destructive, or network commands after uncertain timeout, stale instance rotation, or protocol failure.

## Semantic Unity Rules

When available:

- Use `scene.hierarchy`, `prefab.hierarchy`, `asset.search`, and focused inspect commands before raw YAML or broad grep.
- Use `asset.references`, dependencies, and risk reports before move/delete/refactor.
- Use component/property schemas before writes.
- Leave scenes dirty unless the user explicitly requests `scene.save`.
- Report exact changed objects/files and validation state.

## Compile, Tests, And Packages

- Use Unity MCP commands for Unity compilation/test/package evidence when the task requires Unity evidence.
- Do not substitute generated `.csproj` or `dotnet` compilation for Unity Test Runner evidence.
- Use `compile.status` after source/package changes and explicit refresh/validation commands when required.
- Package mutations return operation ids; poll bounded status and do not invent progress/cancellation.
- Use `test.list`/preflight before `test.run`; keep test/build execution ask-gated unless the user intentionally configured otherwise.

## Jobs And Artifacts

- Use `mode:"job"` only when the descriptor supports jobs.
- Poll the exact returned id with `sgg_unity_job`; do not run unbounded loops.
- Request cancellation only when `supports_cancel` is true.
- Prefer artifact metadata and bounded reads over large inline output.
- Treat prune/delete/restore as destructive and dry-run/preview first where supported.

## Dynamic C# And Network

- Prefer typed catalog commands over `editor.execute_csharp.*`.
- Use dynamic C# only in a trusted project when normal commands cannot perform the task.
- Do not spawn processes, access network, touch arbitrary paths, collect secrets, or disable controls from dynamic code.
- Do not call network-risk commands unless the feature and policy explicitly allow the intended request.

## Diagnostics And Handoff

Before saying MCP is skipped/unavailable:

```text
1. Call sgg_unity_projects in the current session.
2. If a project is live, run a safe compile.status smoke test.
3. Record exact selector, command, error code, current Unity state, and evidence age.
4. Execute schema_lookup when applicable.
5. Use diagnostics.report_problem when available and useful.
```

Do not copy tokens, credentials, raw approval stores, absolute paths, or unredacted proprietary content into reports.

## PowerShell From WSL

When launching `powershell.exe -Command` from bash/WSL, wrap the whole PowerShell script in single quotes so bash does not expand PowerShell `$` variables first.
