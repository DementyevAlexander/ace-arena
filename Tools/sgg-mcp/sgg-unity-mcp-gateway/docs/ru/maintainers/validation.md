[English](../../en/maintainers/validation.md) | [Русский](./validation.md)

# Validation И Release Evidence


Validation evidence описывает actual runs для exact commit/package. Checklist/static pass не доказывает Unity compile/tests.

## Evidence Categories

```text
Node/static
runtime reproducibility
notices/SBOM
Unity compile matrix
Windows smoke
WSL smoke
6000.4 MCP smoke
6000.5 MCP smoke
EditMode
PlayMode/applicability
clean install
update smoke
```

Missing = `not_run`.

## Static Checks

```bash
npm run check:fast
npm run check:static
```

`check:all` compatibility alias. Static не запускает Unity.

## Compact Response Measurement

MCP-PR-114 использует deterministic representative success fixtures и serialized MCP wrapper bytes. Approximate tokens считаются как `ceil(bytes / 4)`; это comparison estimate, а не billing evidence tokenizer.

| Workflow | Legacy full-text + full-structured bytes | Compact summary + compact-structured bytes | Approx compact tokens | Healthy calls | Reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| Compile status | 23446 | 605 | 152 | 1 | 97.4% |
| Targeted test start-to-terminal | 33750 | 1091 | 273 | 1 | 96.8% |
| Full test start-to-terminal | 33774 | 1103 | 276 | 1 | 96.7% |
| Bounded test discovery/preflight | 44200 | 3942 | 986 | 1 | 91.1% |

Source report измерил real successful test start/status pair примерно в 12-14 KB до MCP wrapper duplication. Deterministic fixtures защищают те же verbose fields и проверяют сокращение больше требуемых 60%, один healthy start-to-terminal call и отсутствие полной JSON-копии в text content. Это static protocol measurement; live Unity Test Runner acceptance остаётся отдельной evidence category.

## Unity Compile

`check:unity-editor-required` для package C# changes. Record exact Unity/full SHA/package/project identity/command/exit/log hash/criteria.

## Matrix

Planned matrix не support claim без current pass каждого target. Не silent-skip-ите missing Editors в release evidence.

## Dedicated MCP Smoke

Отдельные `6000.4` и `6000.5` projects, real MCP path, exact selectors/version/discovery/compile и nonzero authoritative Unity Test Runner.

Не:

```text
start arbitrary Unity
bypass approval
accept zero tests
accept static tests as Unity execution
retry uncertain test.run
claim full matrix
```

## Release Evidence

Fail-closed template/checker bind-ит full HEAD, package, UTC, commands/actions, result, artifacts/SHA, clean git.

Не превращайте template в pass без actual run.

## Public Report

| Category | Target | Result | Date | Evidence |
| --- | --- | --- | --- | --- |
| Static | commit/package | pass/fail/not_run | UTC | command/artifact |
| Unity compile | exact version | pass/fail/not_run | UTC | log hash |
| MCP smoke | exact project | pass/fail/not_run | UTC | JSON hash |
| Matrix | exact list | pass/fail/not_run | UTC | evidence |

Incomplete placeholders не должны быть prominent product path.

## Documentation Claims

```text
[ ] README matches evidence.
[ ] Import floor != full support.
[ ] OpenCode/OpenChamber elicitation wording соответствует exact shipped client capability и не выдаёт upstream development branches за released support.
[ ] Install path works.
[ ] Approval flows tested/not run.
[ ] Extension examples compile/discover или static-only.
[ ] Planned != implemented.
[ ] Skips explicit.
```

## Связанные Документы

- [Manual checks](./manual-checks.md)
- [Keep Backups — проверка чтения согласия](./keep-backups-consent-validation.md)
- [Unity tools Setup validation](./unity-tools-setup-validation.md)
- [Проверка начального заполнения Setup и истории тестов](./test-history-validation.md)
- [Versioning](./versioning.md)
- [Compatibility](../reference/compatibility.md)
- [Limitations](../limitations.md)
