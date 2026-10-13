[English](./manual-checks.md) | [Русский](../../ru/maintainers/manual-checks.md)

# Manual Checks


This page is a readable index. Repository scripts define the exact commands, environment schema, and fail-closed release evidence.

## Daily Documentation Work

```bash
npm run check:docs
npm run check:command-docs
npm run check:forbidden-identifiers
```

Review:

```text
[ ] English/Russian canonical pages are paired.
[ ] Local Markdown/image links resolve.
[ ] Language switchers are present.
[ ] Generated command inventory is fresh.
[ ] No internal-only identifiers or secrets appear.
[ ] README and both documentation indexes link to current paths.
```

## Static Profile

```bash
npm run check:static
```

Use before documentation handoff and release preparation. It does not run Unity.

## Runtime Bundle

When Gateway code/runtime payload changed:

```bash
npm run build:gateway-runtime
npm run check:gateway-runtime
npm run check:notices
```

Documentation-only replacement should not rebuild runtime unless its generated payload intentionally embeds documentation or metadata changed.

## Unity Compile

For package C# changes:

```bash
npm run check:unity-editor-required
```

Use an exact disposable validation project and record its Unity version. A skipped Editor run is not evidence.

## Unity Matrix

```bash
npm run check:unity-editor-matrix
```

Run the required matrix manually with explicit required-mode environment configuration. Record missing targets as not run/fail according to release policy.

## Dedicated Unity MCP Smoke

```bash
npm run check:unity-smoke
```

This requires exact separately configured Unity `6000.4` and `6000.5` projects, selectors, test filters/assemblies, and reviewed positive expected leaf counts.

Required version environment entry:

```text
<!-- sgg-current-version-claim:manual.en.smoke.environment -->
SGG_UNITY_SMOKE_PACKAGE_VERSION             exact current package version (2026.10.12-2)
```

Command prefix:

```bash
<!-- sgg-current-version-claim:manual.en.smoke.command -->
SGG_UNITY_SMOKE_PACKAGE_VERSION='2026.10.12-2' \
npm run check:unity-smoke
```

The runner uses the real MCP client/transport and must not bypass ask gates. Native elicitation may require a human response; dedicated automated release fixtures should use an explicitly reviewed policy/approval arrangement that preserves the security contract.

## Capable-Client Approval Smoke

Manual flow:

```text
1. Connect a client build that actually advertises and renders MCP form elicitation through a transport that carries nested requests.
2. Verify projects and compile.status.
3. Select an ask-gated harmless/dry-run-capable command.
4. Confirm native MCP form appears with server identity and Accept/Decline/Cancel.
5. Decline: verify no execution.
6. Repeat and Accept: verify exact call executes at most once.
7. Change catalog/policy/context while prompt is open: verify old acceptance is invalidated.
8. Record fallback behavior when elicitation is disabled/unavailable.
```

Do not use `auto_allow_ask` for this test.

## Extension Example Smoke

```text
1. Install generic example package in a disposable project.
2. Verify package compiles with and without Gateway when optional integration is claimed.
3. Describe example command and trust/source metadata.
4. Run read-only command.
5. Dry-run risky command.
6. Verify untrusted default and trusted package policy cases.
7. Run manifest security check.
```

## Documentation FTUE Smoke

Use a clean user profile or disposable machine/VM where practical.

```text
[ ] New user understands the product in 30 seconds.
[ ] Git UPM/local pre-release install succeeds.
[ ] Setup installs portable runtime without system PATH changes.
[ ] Health becomes green.
[ ] One client config can be copied without a real secret.
[ ] First projects/describe/compile.status flow succeeds.
[ ] WSL stdio path works when tested.
[ ] On a clean Windows profile, Unity CLI detection reports missing, the install confirmation names the beta Unity CDN source and user/PATH effects, and cancel performs no write.
[ ] Confirmed Windows CLI installation reaches `%LOCALAPPDATA%/Unity/bin/unity.exe`, validates that exact executable, and does not accept an older `unity.exe` from `PATH` as install success.
[ ] Closing MCP Window or triggering assembly reload during Windows CLI installation cancels the bounded process; a fresh detect is required before any repeat.
[ ] CLI, Pipeline and Skill each own their install/check/update buttons. Missing is yellow, latest checked blue, known update red, and unknown/failed check purple; unknown state never authorizes Install.
[ ] Pipeline and Skill manual buttons show available package/commit versions before installation. Checking another block preserves same-context display metadata without background queries or update authority.
[ ] Skill shows installed and available official commits, uses the supported Unity latest snapshot without manual owner content review, and never invents CLI compatibility from commit/prose.
[ ] Skill installation refuses an appeared file/directory or changed ancestor; it does not merge or delete foreign content. Updates preserve the previous backup and failed replacements, and refuse rollback into a foreign target.
[ ] Use a genuinely disposable Windows user/HKCU/profile or VM for CLI beta install/update: UNITY_CLI_HOME alone does not isolate PATH/Hub receipt side effects. Verify exact post-action version and preserve working installations.
[ ] WSL explanation and explicit diagnostics distinguish a wrapper to the exact Windows CLI from a separate Linux CLI/environment.
[ ] Troubleshooting recovers one intentional connection failure.
[ ] Extension quick start produces a discoverable command.
```

## Release Gate

```bash
npm run generate:release-evidence-template
npm run check:release
```

The checker verifies evidence; it does not run the matrix or smoke tests for you.

## Related Docs

- [Validation](./validation.md)
- [Versioning](./versioning.md)
- [Troubleshooting](../troubleshooting.md)
