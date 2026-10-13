[English](./mcp-tools.md) | [Русский](../../ru/reference/mcp-tools.md)

# MCP Tools


The Gateway exposes eight stable MCP tools. All Unity/package capabilities are discovered catalog commands rather than additional top-level tools.

## `sgg_unity_projects`

Lists only live project/instance records from one local bridge registry snapshot, plus aggregate `total`, `live`, `stale`, and `unique_projects` counts. Stale record bodies are available only through the bounded stale-history tool.

Input:

```json
{}
```

Use it:

- at first contact;
- after `project_offline` or stale-instance errors;
- after Unity compilation/domain reload;
- before multi-project work;
- when a selector is ambiguous.

The result can include project id, display name, instance id, canonical project root, host aliases, Unity/package version, catalog hash, and readiness. Treat returned host paths as identity aliases, not permission to access arbitrary filesystem paths.

## `sgg_unity_stale_projects`

Reads retained stale heartbeat history without changing routing or registry state. Results use deterministic newest-first ordering, a default limit of 8, a hard limit of 16, and snapshot-bound cursor pagination.

Optional filters are `project`, `status`, `last_heartbeat_from`, and `last_heartbeat_to`. A cursor is valid only for the same filters and unchanged stale snapshot; otherwise start a fresh query without the cursor.

## `sgg_unity_stale_registry_cleanup`

Previews or explicitly removes expired stale heartbeat files. The tool is available only when `registry.cleanup_after_ms` is configured above `registry.stale_after_ms`. It defaults to `dry_run:true`; the preview returns opaque `stale_record_id` values, process-state evidence, and a `precondition_hash` for the exact deletable selection.

Real cleanup always requires `dry_run:false`, `confirm_cleanup:true`, exact previewed `record_ids`, and the matching `precondition_hash`. On a form-elicitation-capable connection it also requires a separately accepted one-shot Gateway approval bound to those exact arguments. On a connection without form elicitation, the temporary `auto_allow_stale_registry_cleanup_without_elicitation` setting can authorize only this exact operation. Cleanup deletes only canonical valid heartbeat files older than the configured cleanup threshold whose owner PID is confirmed dead in the Gateway's local process namespace, then re-reads and revalidates each file and its file identity immediately before deletion. Cross-OS/namespace ownership is treated as uncertain. It never deletes malformed records, live-PID records, project content, or arbitrary paths. Do not automatically retry a cleanup after timeout or uncertain delivery; run a fresh dry-run instead.

## `sgg_unity_describe`

Reads project status, command groups, the project catalog, or one current command descriptor.

Common calls:

```text
sgg_unity_describe(project="game", selector="commands")
sgg_unity_describe(project="game", selector="commands:asset")
sgg_unity_describe(project="game", selector="command:asset.search")
```

Always describe the exact command when its arguments, risk, feature, or job support are uncertain.

A stale catalog is not a reason to invent an argument shape. Describe again.

## `sgg_unity_call`

Invokes one catalog command for one explicit project.

```json
{
  "project": "game",
  "command": "compile.status",
  "args_json": "{}",
  "mode": "wait"
}
```

Core fields:

| Field | Purpose |
| --- | --- |
| `project` | Explicit selector from project discovery. |
| `command` | Exact lowercase catalog command id. |
| `args_json` | JSON string that parses to an object matching the current descriptor. |
| `mode` | `wait` or `job` when supported. |
| `request_id` | Optional bounded caller correlation/idempotency context. Not authorization. |
| `checkpoint_id` | Optional audit correlation. Not approval or automatic rollback. |
| `plan_id` | Optional audit correlation. Does not execute a plan. |
| `wait_for_replacement_ms` | Optional 1..3600000 pre-dispatch wait, supported only by `game.view.screenshot` in `wait` mode. |
| `wait_for_test_run_terminal_ms` | Optional 1..295000 end-to-end deadline for one compact, non-dry `test.run` in `wait` mode. The limit reserves 5000 ms inside the supported 300000 ms caller boundary for normalization and response delivery. |

Do not place top-level control fields inside `args_json`.

After one dispatched Play Mode transition, read-only `editor.status` verification may re-resolve the heartbeat and re-describe only two exact `/commands` catalog-v1 response-shape rejections: generation/hash mismatch with unchanged trusted project/root/process/package/connection provenance. The original deadline/attempt budget and all descriptor/schema/policy/target-correlation gates remain. Rejected metadata authorizes no status invocation or transition replay. Additive Gateway-owned `catalog_metadata_read_retry_count` counts actual subsequent describes; last reason/convergence appear only after eligible lag. Matching metadata is not proof of transition completion. Persistent lag keeps the exact operation recovery receipt with `verification_status:"catalog_stale"`; use its exact status lookup and do not re-enter automatically. These bounded controls do not establish historical failure causes or extend the wait.

The converged route rechecks trusted owner/package provenance even if the original hash returns. Status helpers must be feature-enabled and policy-allowed before preparation and immediately before issuance; `deny`/`ask` never causes implicit approval or fallback. `verification_read_request_count` counts actual transport issuance, not blocked preparation. Both success and scene-blocker projections discard lookalike Unity metadata diagnostics before adding Gateway-owned values. Legacy no-lag provenance behavior is unchanged; these checks do not authorize a transition repeat.

Wrapper waits are revision-bound command descriptor capabilities. The current descriptor must advertise the exact field, `wait` mode, accepted range, and deadline scope; unsupported commands, modes, arguments, missing contracts, and values outside the descriptor range fail before command dispatch.

The current `test.run` descriptor advertises a bounded **60000 ms admission/start budget**, including discovery and durable Gateway binding/reservation/issuance publication. A shorter inherited caller or selected wrapper deadline still wins. `max_seconds` bounds Unity test tracking after admission, not Gateway startup; `wait_for_test_run_terminal_ms` does not extend the descriptor startup cap, and `mcp.request_timeout_ms` is not a command-timeout override. Owner/policy/feature/deadline checks and no-replay rules remain mandatory. This budget does not guarantee admission for arbitrarily large or slow retained stores and does not authorize history cleanup or another trial. See [validation evidence](../maintainers/test-run-admission-validation.md).

Successful exact-bound persisted `test.run.status` reads add `status_observation` in full and compact responses: contract `sgg.unity_mcp.test_run_status_observation.v1`, Gateway `observed_at_utc` and `artifact_snapshot_age_ms` from the validated stable opened handle's mtime, not the final pathname or artifact JSON. Age is floored to milliseconds and is nullable for unavailable/invalid/backward clocks or future/negative file timestamps. It describes the filesystem snapshot only—not Runner update age, progress, run wall time or continued execution. Retained Runner `duration_ms`, counts, Success, owner and no-replay evidence are unchanged. Compact/internal server-wait projection preserves that same observation without resampling. Live/error/unbound/legacy payloads cannot supply this Gateway-owned field. Read convergence remains bounded by caller/local deadline, at most 1500ms scheduled wait and 32 attempts even with a frozen clock; invalid supplied deadlines do not create a fresh convergence budget. No new test request, status repair, cancellation or fallback authority is added.

Compile responses with source contract `sgg.unity_mcp.compile_readiness_qualification.v1` carry `readiness_qualification` through full/compact, nested validation and bounded recovered/error projections. It qualifies existing script-compiler/Console CS-error evidence plus editor/package lifecycle, explicit-barrier and background-progress gates—not blanket asset validation. `shader_asset_readiness:"unknown"`, `console_history_complete:false` and `console_history_durable:false` remain true even with zero captured errors or settled native progress. Ordinary shader-shaped Console errors without the `error CS` marker are not compiler-count evidence; a marker-containing message is a textual classifier hit, not a shader inventory. `ready_clean` alone does not prove an exact explicit compilation: inspect `compile_barrier` in full mode for operation/generation/finished/reload correlation or no-barrier-required state. Qualification itself grants no readiness or admission and proves no newly loaded test DLLs/current repaired source. Missing legacy/malformed qualification stays absent; statuses, counts, retained failure receipts, background failure evidence, gates and deadlines are unchanged. No shader/asset scan, Console backfill, automatic refresh or replay is performed.

When the caller explicitly selects terminal waiting and the trusted current `test.run` descriptor advertises `wrapper_wait.wait_for_test_run_terminal_ms.compile_readiness_contract:"sgg.unity_mcp.test_run_compile_readiness.v1"`, the Gateway first issues one bounded, policy-allowed read-only `compile.wait_ready`. Metadata, native readiness convergence and discovery share the original startup deadline; the existing 5000 ms helper-delivery and 4000 ms durable-binding reserves are retained, not new phase budgets. Strict source evidence requires current inventory and consistent explicit readiness flags, while allowing source-shaped idle/superseded barriers and exact unobserved background evidence. Blank/missing unavailable reasons never become `not_scanned`. Owner/process/package/catalog/compilation, feature and effective policy are frozen/rechecked before sole test issuance; already accepted approval is not consumed twice. Missing/unsupported helper evidence, unsupported explicitly selected contract or deadline exhaustion issues zero test requests and no automatic refresh, replay or fallback. Ordinary async calls and legacy descriptors without the new contract keep their existing route. This is selected-route admission, not proof of newly loaded test DLLs, all-route readiness or historical failure causes.

`wait_for_replacement_ms` is opt-in and covers only replacement discovery and pre-dispatch revalidation while Unity replaces its bridge during a domain reload. The Gateway waits under one project queue, revalidates canonical project identity, the re-described command catalog, policy, schema, and approval before the replacement deadline, then starts a fresh descriptor command timeout and issues at most one screenshot request. It never replays after transport delivery starts or becomes uncertain. Without this field, transitional bridge lifecycle states fail immediately as before.

`wait_for_test_run_terminal_ms` requires descriptor-backed `"response_mode":"compact"` inside `args_json`. For a filtered run, the Gateway first performs bounded read-only `test.list` polling under the startup portion of the same absolute deadline and binds its discovery generation. It then dispatches exactly one test operation, releases the launch queue, and performs only bounded `test.run.status` reads. A healthy run returns one compact terminal response. Pending discovery issues zero test requests; timeout or uncertain launch returns the exact available workflow/operation evidence and never repeats `test.run`.

For direct/sequential terminal-wait timeouts and Unity timeout actions, poll `test.run.status` first with the returned exact operation ID; inspect that operation's artifact only if available, or report unresolved evidence. A wait timeout or `test.run.cancel` tracking-cleanup receipt does not prove Runner shutdown. Do not automatically cancel, replay or start another run until execution state is resolved. This guidance adds no programmatic launch block or artifact probe, and leaves timeout receipts, counts, explicit batch sequencing and permission/schema gates unchanged. `test.run.cancel` remains write/idempotent and its schema has no `confirm` field. Separate XML completion is not acceptance without exact operation/owner/source/time/leaf correlation.

Current descriptors expose `response_mode:"full"|"compact"` for `compile.status`, `compile.wait_ready`, `compile.refresh_and_wait`, `test.list`, `test.run`, and `test.run.status`. Omitted or `full` retains the complete structured result. Explicit `compact` keeps status/result, operation identity, authoritative counts, bounded discovery-generation evidence, duration, short diagnostics, and artifact identity while omitting success-only duplicate previews, fallback plans, hashes, and nested result copies.

When additive leaf evidence is available, compact test responses also include up to four `non_passing_diagnostics` with exact raw statuses and bounded identity/message text. `non_passing_evidence_status` and `non_passing_tests_truncated` describe source capture; `non_passing_diagnostics_truncated` separately marks compact display omissions, malformed entries or shortened text. Display truncation does not downgrade source coverage or change counts/Success. Legacy responses without these fields retain their old shape; absence is not proof of complete-zero evidence. Use the same operation's full status for retained details, never rerun tests to reconstruct lost reasons.

For dynamic C#, `max_output_chars` limits captured console text in UTF-16 code units (default 4000), not returned values or whole-response size. Both full and compact retain independent serialization bounds: each string allows at most 256 escaped JSON-content UTF-8 bytes, excluding enclosing quotes; the serialized result remains bounded to 65,536 UTF-8 bytes including annotations. A truncated string reports `reason:"max_string_json_content_bytes"` and numeric `applied_limit:256`. Existing character counts use UTF-16 code units; string byte counters describe unescaped text, while `result_summary.utf8_bytes` measures the complete serialized result. Console/result truncation flags remain separate. Exact `editor.execute_csharp.readonly.status` retrieves the retained projected result without executing again; it cannot recover discarded tails.

Dedicated read-only Frame Debugger commands preserve their legacy type lookup order, then try the exact `UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility` and `FrameDebuggerEventData` names. This does not broaden generic reflection, feature/policy permissions or supported member signatures. Unsupported layouts remain unavailable. Lookup never enables, steps, selects or captures the debugger; metadata compatibility alone does not prove that event data is currently available.

Read-only C# admission no longer deletes retained results by age. It conservatively counts matching JSON files without reading/classifying them and refuses a new operation at 64 observed files after completed enumeration; observing a 257th match exceeds the 256-match scan budget and fails closed. Faulted enumeration reports only the observed lower bound. The additive `capacity_observation` is not an atomic snapshot or a count of validated receipts: state/age classification is not performed, and automatic release eligibility is zero because release is disabled by contract, not because terminals were acknowledged. Root/binding/duplicate failures do not acquire an enumeration witness. The exact `Library/SGGUnityMcp/job-artifacts/dynamic-csharp-readonly` subtree remains readable/listable but reports `prunable:false` and is protected at generic prune selection and deletion boundaries; near-prefix siblings retain existing policy. This provides no quota relief, archive, fallback or safe-release command. Age/terminal/response-write completion is not consumer acknowledgement. Exact status may still repair a missing cached terminal receipt, so the admission threshold is not a universal physical-file ceiling. Cache, owner/hash validation, publication cleanup and permission/schema gates are unchanged.

For every mode, `structuredContent` is the sole canonical `GatewayResult`. MCP text `content` is only a bounded human summary that points to `structuredContent`; it is not a second JSON serialization.

## `sgg_unity_batch`

Runs a bounded list of call-like items.

Top-level scheduling modes:

```text
sequential
parallel_by_project
```

Example:

```json
{
  "mode": "parallel_by_project",
  "items": [
    {
      "project": "game-a",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    },
    {
      "project": "game-b",
      "command": "compile.status",
      "args_json": "{}",
      "mode": "wait"
    }
  ]
}
```

Use batch when operations are known and independent or have a fixed reviewed order. Do not batch a destructive command with dependent reasoning steps or mix work that needs separate approvals.

Each item still has an explicit project. Commands in one project remain serialized; different project queues may run concurrently.

## `sgg_unity_job`

Polls or requests cancellation of Gateway-local jobs.

Use the job id returned by a `mode:"job"` call or batch item. Do not invent job ids or poll unboundedly.

Typical lifecycle:

```text
queued -> running -> succeeded
                  -> failed
                  -> cancelled when real cancellation is supported
```

A cancel request is meaningful only when the descriptor advertises `supports_cancel`. Otherwise report that cancellation is unsupported rather than pretending the Unity operation stopped.

## `sgg_unity_logs`

Reads bounded Gateway, bridge, or Unity-related log tails through the Gateway logging contract.

Use it for transport/lifecycle diagnostics. For Unity Console entries, prefer project catalog commands such as `logs.console.query`, `logs.console.read`, or `logs.console.tail` when available. Console payloads expose a bounded `buffer` epoch and reset-loss metadata; `history_complete:false` means an empty current buffer must not be treated as complete pre-initialization or pre-reload history.

Never request unlimited logs. Keep line/byte bounds small and avoid copying secrets or proprietary content into reports.

## First Safe Sequence

```text
1. sgg_unity_projects
2. select one live project id
3. sgg_unity_describe(project, selector="commands")
4. sgg_unity_describe(project, selector="command:compile.status")
5. sgg_unity_call(project, command="compile.status", args_json="{}", mode="wait")
```

## Error Discipline

```text
catalog_stale          -> describe again
project_offline        -> projects, then choose current live selector
feature_disabled       -> enable only required feature outside MCP and restart
permission_denied      -> do not bypass policy
confirmation_required  -> native elicitation or exact pending approval
schema errors          -> execute error.details.schema_lookup
job not found          -> use the returned id and verify retention/status
```

## Related Docs

- [Architecture](./architecture.md)
- [Command catalog](./commands.md)
- [Common workflows](../guides/workflows.md)
- [Error codes](./error-codes.md)
