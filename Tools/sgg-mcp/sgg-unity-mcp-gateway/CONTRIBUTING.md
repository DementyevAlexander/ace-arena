# Contributing

Russian localization: `CONTRIBUTING.ru.md`.

Read `AGENTS.md`, `README.md`, `docs/en/reference/security.md`, and `docs/en/maintainers/manual-checks.md` before changing gateway behavior.

## Public Repository Rules

- Keep docs, examples, issues, and PRs generic and user-facing.
- Do not include secrets, local tokens, private paths, private repository names, customer/project identifiers, or unpublished implementation notes.
- Do not document the Gateway as an internet-facing service, hosted runner, official Unity product, or enterprise identity/auth system.
- Keep the stable top-level MCP tool surface limited to `sgg_unity_projects`, `sgg_unity_stale_projects`, `sgg_unity_stale_registry_cleanup`, `sgg_unity_describe`, `sgg_unity_call`, `sgg_unity_batch`, `sgg_unity_job`, and `sgg_unity_logs`.
- Document Unity capabilities as command catalog entries invoked through `sgg_unity_call.command` with `args_json`, not as new top-level MCP tools.
- Keep Unity calls explicit: no global active project, and every Unity command call must pass `project` except project discovery.
- Use Unity-relative paths such as `Assets/...` and `Packages/...` in agent guidance; do not ask users to pass arbitrary absolute asset paths.
- Treat approvals as outside MCP. Agents cannot approve their own commands, and `checkpoint_id` or `plan_id` values are audit correlation metadata only.

## Pull Requests

Public contributions should normally go through a pull request. Maintainers may use direct commits for controlled release-preparation work when project policy explicitly allows it. Keep PRs focused on one behavior, documentation, release, or repository-maintenance change.

Use the PR template checklist and include the local verification command you ran. If a change affects Unity command behavior, update tests and command docs in the same PR.

For an installable change, create exactly one calendar build and a completed changelog release with user-facing `Added`, `Changed`, and `Fixed` sections. Do not use `Unreleased` or expose internal implementation journals in the current release. Private-repository documentation-only changes do not require a version bump. See `docs/en/maintainers/versioning.md`.

## CI Policy

Public pull requests run a safe GitHub Actions subset: install dependencies, typecheck, unit tests, docs checks, generated command-doc freshness, version checks, forbidden identifier checks, command descriptor checks, manifest security, changelog, and workflow contract checks.

Unity Editor validation, gateway-runtime rebuild verification, release-readiness, and release packaging remain manual/local gates. Do not add automatic `push`, `pull_request_target`, `schedule`, or `workflow_run` triggers unless the project policy changes. The root npm package stays marked `private` until npm publication metadata and package contents are reviewed manually.

## Branch Naming

Use lowercase ASCII branch names in this form:

```text
<type>/<short-kebab-topic>
```

Allowed types:

```text
feature
fix
docs
l10n
test
refactor
chore
security
release
```

Examples:

```text
feature/asset-risk-report
fix/project-alias-resolution
docs/security-disclosure
l10n/russian-docs
chore/release-readiness-workflow
```

Rules:

- Use lowercase letters, digits, and hyphens in the topic.
- Keep exactly one slash between the type and topic.
- Use `l10n` for documentation translation or localization-only changes.
- Do not put secrets, hostnames, private directory names, customer names, or tokens in branch names.
- Do not use `main`, `master`, `release`, or tag-like names such as `v1.2.3` for PR branches.

## Local Checks

Install dependencies with:

```bash
npm ci
```

Run the fast profile for daily work and the complete static profile before readiness work:

```bash
npm run check:fast
npm run check:static
```

`check:all` is a compatibility alias for `check:static`; it does not run or validate Unity compilation.

When gateway runtime source or config changes, run:

```bash
npm run build:gateway-runtime
npm run check:gateway-runtime
```

If Unity is installed and a test project is available, run the optional editor validation with `UNITY_EXE` and `SGG_UNITY_TEST_PROJECT` set:

```bash
npm run check:unity-editor
```
