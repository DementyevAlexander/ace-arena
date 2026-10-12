[English](./validation.md) | [Русский](../../ru/maintainers/validation.md)

# Validation And Release Evidence


Validation evidence must describe what actually ran for one exact commit/package. A checklist or passing static suite is not proof that Unity compiled or executed tests.

## Evidence Categories

Keep these separate:

```text
Node/static checks
bundled runtime reproducibility
notices/SBOM checks
Unity compile matrix
Windows local smoke
WSL -> Windows smoke
Unity 6000.4 dedicated MCP smoke
Unity 6000.5 dedicated MCP smoke
Unity EditMode tests
Unity PlayMode tests or explicit applicability decision
clean package install smoke
package update smoke
```

A missing category is `not_run`, not implied pass.

## Static Checks

```bash
npm run check:fast
npm run check:static
```

`check:all` is a compatibility alias for the static profile. Static profiles do not launch Unity and must state that Unity validation was not run.

Typical static coverage:

```text
typecheck
Vitest
agent evals
version/changelog
docs and generated command docs
forbidden identifiers
Unity package structure
runtime bundle
notices/SBOM
workflow contract
command descriptors
policy
skills
manifest security
```

## Compact Response Measurement

MCP-PR-114 uses deterministic representative success fixtures and serialized MCP wrapper bytes. Approximate tokens are `ceil(bytes / 4)`; they are a comparison estimate, not tokenizer billing evidence.

| Workflow | Legacy full-text + full-structured bytes | Compact summary + compact-structured bytes | Approx compact tokens | Healthy calls | Reduction |
| --- | ---: | ---: | ---: | ---: | ---: |
| Compile status | 23446 | 605 | 152 | 1 | 97.4% |
| Targeted test start-to-terminal | 33750 | 1091 | 273 | 1 | 96.8% |
| Full test start-to-terminal | 33774 | 1103 | 276 | 1 | 96.7% |
| Bounded test discovery/preflight | 44200 | 3942 | 986 | 1 | 91.1% |

The source report measured a real successful test start/status pair at approximately 12-14 KB before MCP wrapper duplication. The deterministic fixtures protect the same verbose fields and verify more than the required 60% reduction, one healthy start-to-terminal call, and no complete JSON copy in text content. They are static protocol measurements; live Unity Test Runner acceptance remains a separate evidence category.

## Unity Compile

Use `check:unity-editor-required` for Unity package C# changes when an exact local Unity executable and disposable test project are available.

A compile pass should record:

```text
exact Unity version
full commit SHA
package version
test project identity/hash where required
exact command
exit code
log/artifact reference and SHA-256
compiler error/warning criteria
```

## Matrix

The repository matrix runner may include import/compile floors and active Unity 6 lines. The planned matrix is not a support claim until every required target has current pass evidence.

Do not silently skip missing editors when producing release evidence.

## Dedicated MCP Smoke

The dedicated smoke runner targets separately configured Unity `6000.4` and `6000.5` projects and verifies the real MCP path with exact project selectors, package versions, command discovery, compile state, and nonzero authoritative Unity Test Runner results.

It must not:

```text
start arbitrary Unity processes
bypass confirmation_required
accept zero executed tests
accept static source-contract tests as Unity execution
retry uncertain test.run delivery
claim the full release matrix ran
```

## Release Evidence

Use the repository's fail-closed release-evidence template/checker. Evidence should bind:

```text
full HEAD
package version
UTC timestamps
exact commands/actions
pass/fail/not_run
contained verification artifacts
SHA-256
clean git state
```

Do not edit an ignored evidence file into a passing shape without the actual run and artifacts.

## Public Validation Report

A public report should be short and factual:

| Category | Exact target | Result | Date | Evidence |
| --- | --- | --- | --- | --- |
| Static | commit/package | pass/fail/not_run | UTC | command + artifact |
| Unity compile | exact Unity version | pass/fail/not_run | UTC | log hash |
| MCP smoke | exact Unity/project | pass/fail/not_run | UTC | smoke JSON hash |
| Matrix | exact list | pass/fail/not_run | UTC | combined evidence |

Do not place `not recorded` placeholders in a prominent product path. Keep incomplete templates in maintainer-only documentation until populated.

## Documentation Claims

Before release, verify:

```text
[ ] README requirements match actual evidence.
[ ] Import floor is not called full support.
[ ] OpenCode/OpenChamber elicitation wording matches the exact shipped client capability and does not promote upstream development branches to released support.
[ ] Current package/runtime install path works.
[ ] Native and fallback approval flows were tested or marked not run.
[ ] Extension examples compile and discover, or are marked static-only.
[ ] Planned commands are not described as implemented.
[ ] Skipped matrix/client/platform checks are explicit.
```

## Related Docs

- [Manual checks](./manual-checks.md)
- [Keep Backups consent-reader validation](./keep-backups-consent-validation.md)
- [Unity tools Setup validation](./unity-tools-setup-validation.md)
- [Setup population and test history validation](./test-history-validation.md)
- [Versioning](./versioning.md)
- [Compatibility](../reference/compatibility.md)
- [Limitations](../limitations.md)
