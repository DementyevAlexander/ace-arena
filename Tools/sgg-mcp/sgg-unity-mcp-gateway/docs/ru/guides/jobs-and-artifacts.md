[English](../../en/guides/jobs-and-artifacts.md) | [Русский](./jobs-and-artifacts.md)

# Jobs И Artifacts


Jobs и artifacts нужны, чтобы long operations не блокировали client, а большие результаты оставались bounded.

## Когда Нужен Job

`mode:"job"` используется только если discovered descriptor advertises job support.

Типичные workloads:

```text
builds
test runs
long profiler operations
large scans or reports
bounded temporal runtime debugging
package/domain workflows с job support
```

Короткий read должен оставаться `mode:"wait"`.

## Job Flow

```text
1. discover command и schema
2. вызвать с mode:"job"
3. сохранить job id
4. poll через sgg_unity_job
5. после terminal прочитать artifact refs
6. cancel только при supports_cancel
```

Не придумывайте job support из-за того, что operation кажется медленной.

## Polling

Polling bounded:

```text
maximum attempts
reasonable interval
overall deadline
terminal-state detection
никаких recursive/unbounded loops
```

Следуйте `next_valid_actions` и status fields command.

Client timeout не доказывает, что Unity не получила write/execute request. Risky call после uncertain delivery автоматически не replay-ится.

Для operation-correlated `test.run` используйте explicit compact server wait, когда нужен один end-to-end call: задайте `response_mode:"compact"` в command args и `wait_for_test_run_terminal_ms` в `sgg_unity_call`. Gateway отправляет один run и опрашивает только его exact status identity. После bounded timeout продолжайте по возвращённому operation id; не отправляйте новый run, пока delivery не разрешён.

## Cancellation

Cancellation доступна только при `supports_cancel`.

При этом:

- cancellation — request, а не гарантия undo всех side effects;
- terminal result может опередить cancel;
- не cancel-ьте чужой job;
- записывайте exact job id и project;
- после cancel смотрите final state и artifacts.

## Artifact Model

Большие результаты лежат под fixed project-local SGG roots и возвращаются как bounded refs.

Типичные roots:

```text
Library/SGGUnityMcp/job-artifacts/
Library/SGGUnityMcp/screenshots/
Library/SGGUnityMcp/checkpoints/
Library/SGGUnityMcp/plans/
Library/SGGUnityMcp/pipelines/
CaptureArtifacts/
```

Exact root зависит от command family.

В catalog могут быть:

```text
artifact.list
artifact.metadata
artifact.read или job.artifact.read
artifact.prune
```

Используйте discovered schema.

## Чтение Artifacts

Предпочитайте:

```text
artifact metadata
bounded page/range reads
specific artifact ref из command result
```

Не используйте:

```text
arbitrary filesystem reads
absolute output paths
unbounded log dumps
угаданные artifact paths
inline binary content
```

Artifact refs — data handles, а не authorization tokens.

## Pruning

Prune destructive.

Flow:

```text
1. artifact.list
2. artifact.metadata
3. artifact.prune с dry_run:true
4. review candidates и limits
5. approval точного non-dry-run request
```

То же относится к checkpoint и package-operation pruning.

## Build И Test Artifacts

```text
start bounded run
poll terminal status
read failure_summary
follow next_valid_actions
read returned artifact_ref / artifact_refs
```

Не заменяйте Unity Test Runner evidence на `dotnet test` generated `.csproj`, если задача — Unity package tests.

## Screenshots

Screenshot commands пишут в fixed roots. Сначала dry run или target inspection. Не снимайте sensitive Editor windows.

Screenshot artifact может содержать proprietary project content. Не публикуйте без явного запроса пользователя.

## Plans И Pipelines

Plans/pipelines могут хранить local state под `Library/SGGUnityMcp/` или versioned definitions в `.mcp/`.

Они не:

- auto-run-ят steps;
- approve-ят commands;
- делают recorded evidence истинным;
- roll back-ят side effects при cancel.

## Checkpoints

Checkpoints snapshot-ят bounded `Assets/...` files и `.meta`, а не всё состояние проекта.

Restore flow:

```text
checkpoint.diff
human approval
checkpoint.restore
assets.refresh
compile.status
```

После restore проверьте scene/Editor state.

## Security Rules

- no arbitrary artifact root;
- no absolute agent path;
- no symlink/reparse escape;
- bounded file count/bytes;
- secrets redacted;
- prune/restore destructive и policy-gated.

## Связанные Документы

- [Основные сценарии](./workflows.md)
- [Diagnostics](./diagnostics.md)
- [Безопасность](../reference/security.md)
- [Коды ошибок](../reference/error-codes.md)
