[English](./jobs-and-artifacts.md) | [Русский](../../ru/guides/jobs-and-artifacts.md)

# Jobs And Artifacts


Use jobs and artifacts to keep long operations non-blocking and large output bounded.

## When To Use A Job

Use `mode:"job"` only when the discovered command descriptor advertises job support.

Typical job workloads:

```text
builds
test runs
long profiler operations
large scans or reports
bounded temporal runtime debugging
package/domain workflows that expose job support
```

A normal short read should stay in `mode:"wait"`.

## Job Flow

```text
1. discover the command and schema
2. call it with mode:"job"
3. store the returned job id
4. poll with sgg_unity_job
5. read artifact refs when terminal
6. request cancellation only when supports_cancel is true
```

Do not invent job support because an operation seems slow.

## Polling

Polling must be bounded:

```text
maximum attempts
reasonable interval
overall deadline
terminal-state detection
no recursive/unbounded loops
```

Respect `next_valid_actions` and command-specific status fields.

A client timeout does not prove that Unity never received a write/execute request. Do not automatically replay a risky call after uncertain delivery.

For an operation-correlated `test.run`, prefer explicit compact server waiting when one end-to-end call is useful: set `response_mode:"compact"` in command arguments and `wait_for_test_run_terminal_ms` on `sgg_unity_call`. The Gateway sends one run and polls only its exact status identity. If the bounded deadline expires, continue with the returned operation id; never submit another run until delivery is resolved.

## Cancellation

Cancellation is available only when the command advertises `supports_cancel`.

Even then:

- cancellation is a request, not a guarantee that all domain side effects are undone;
- a terminal result may win a race with cancellation;
- do not cancel another user's job;
- record the exact job id and project;
- inspect final state and artifacts after cancellation.

## Artifact Model

Large results are stored under fixed project-local SGG roots and returned as bounded references.

Typical roots:

```text
Library/SGGUnityMcp/job-artifacts/
Library/SGGUnityMcp/screenshots/
Library/SGGUnityMcp/checkpoints/
Library/SGGUnityMcp/plans/
Library/SGGUnityMcp/pipelines/
CaptureArtifacts/
```

Exact roots depend on the command family.

Commands may expose:

```text
artifact.list
artifact.metadata
artifact.read or job.artifact.read
artifact.prune
```

Use the discovered catalog and schema.

## Reading Artifacts

Prefer:

```text
artifact metadata
bounded page/range reads
specific artifact ref returned by a command
```

Avoid:

```text
arbitrary filesystem reads
absolute output paths
unbounded log dumps
guessing an artifact path
copying binary content inline
```

Artifact references are data handles, not authorization tokens.

## Pruning

Prune operations are destructive.

Recommended flow:

```text
1. artifact.list
2. artifact.metadata
3. artifact.prune with dry_run:true
4. review exact candidates and limits
5. approve the exact non-dry-run request
```

The same principle applies to checkpoint and package-operation pruning.

## Build And Test Artifacts

For builds/tests:

```text
start bounded run
poll terminal status
read failure_summary
follow next_valid_actions
read returned artifact_ref / artifact_refs
```

Do not replace Unity Test Runner evidence with `dotnet test` against generated `.csproj` files when the task is to validate Unity package tests.

## Screenshots

Screenshot commands may write under fixed screenshot/artifact roots. Use dry run or target inspection first and avoid sensitive Editor windows.

A screenshot artifact can contain proprietary project content. Keep it local unless the user explicitly asks to share it.

## Plans And Pipelines

Plans and pipelines may store local state under `Library/SGGUnityMcp/` or versioned definitions under `.mcp/`.

They do not:

- execute steps automatically;
- approve commands;
- make recorded evidence true;
- roll back domain side effects on cancel.

## Checkpoints

Checkpoints snapshot bounded `Assets/...` files and `.meta` companions, not complete project state.

Restore flow:

```text
checkpoint.diff
human approval
checkpoint.restore
assets.refresh
compile.status
```

After restore, inspect scene/Editor state before continuing.

## Security Rules

- no arbitrary artifact root;
- no absolute agent path;
- no symlink/reparse escape;
- bounded file count and bytes;
- secrets redacted from diagnostics;
- prune/restore remain destructive and policy-gated.

## Related Docs

- [Common workflows](./workflows.md)
- [Diagnostics](./diagnostics.md)
- [Security](../reference/security.md)
- [Error codes](../reference/error-codes.md)
