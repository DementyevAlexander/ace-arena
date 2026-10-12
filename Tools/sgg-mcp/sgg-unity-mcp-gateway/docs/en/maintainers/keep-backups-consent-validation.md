[English](./keep-backups-consent-validation.md) | [Русский](../../ru/maintainers/keep-backups-consent-validation.md)

# Keep Backups consent-reader validation

Gateway build `2026.10.11-1`, 2026-10-11. This is a narrow Gateway source/artifact fix, not physical popup acceptance or a full Unity release-matrix claim.

## Observed failure and fix

`problem_20261011T152654_95d237dcaa08` reports an exact Keep Backups dry-run followed by a non-dry-run `confirmation_required` refusal. No click is proven by that report. Fresh Gateway diagnostics showed the known-popup opt-in disabled with `source_status: unstable`. A read-only probe used the exact running Gateway executable, native Windows Node `22.16.0`, and its exact config pathname: the saved opt-in was `true`, path/descriptor inode, size, mtime and ctime matched and remained stable, but `lstat.dev=0` while `fstat.dev` was a nonzero volume id. The old reader rejected this native asymmetry. This explains the reproduced current consent-read defect; it does not attribute every historical PR219 refusal or prove the config's state at a past report instant.

The reader now reuses the existing platform-specific `sameFileObjectIdentity` rule. Nonzero inode, metadata, bounded contents, regular-file checks and strict config parsing remain required. Path-to-path and handle-to-handle device ids must independently remain stable. Replacement, mutation, uncertain identity and invalid/missing config revoke consent; no cached last-known-true fallback exists. No setting, feature, policy, approval TTL, response allowlist or popup interaction authority was enabled or broadened.

## Executed evidence

- Focused independent source review: **GO**, before the final full suites. The optional additional pathname-device-loss regression was added and passed in the final targeted/full Node runs.
- Final targeted Node: **187/187**, four files (`known-unity-popup-settings`, `scene-backup-dispatcher`, `known-popup-live-responses`, `gateway-context`). This includes live OFF/ON/OFF, both Windows device-asymmetry directions, real platform identity rules, non-Windows refusal, zero inode, volume/inode/metadata changes and existing revocation/pre-send/no-replay consumers.
- Native emitted fixture `tests/fixtures/known-popup-settings-native.ts`, bundled from the fixed source with a separately bundled pre-fix manager from `92b55ce5`: exact native Windows Node `22.16.0` reproduced path/handle device asymmetry and the old false/unstable result. The fixed reader passed ON/OFF/ON; malformed, duplicate-key, non-boolean, oversized and missing config controls failed closed. This fixture used only a fresh private temp config and the non-secret repository example; it did not access the owner's config, approve anything or invoke a popup response. The separate diagnostic probe above was read-only.
- Final full Node on the frozen Linux snapshot: **3619 total, 3611 passed, 8 platform-skipped, zero failed**, 170 files, 527.93 seconds. Physical Linux dependencies and Git history were supplied; source hashes remained unchanged. The two skipped WSL-mount cases additionally passed in the development clone; the remaining six native Windows storage/artifact cases were not run. The narrow native consent fixture is separate evidence, not a pass of those six cases.
- Agent evals **15/15**. Runtime build and all 15 non-test static constituents passed as individual commands, including typecheck, version, docs, notices/SBOM, bundled-runtime reproducibility and manifest-security. This is not a `check:release` full-matrix GO.
- Isolated `sgg-unity-mcp-gateway-temp`, package `2026.10.11-1`, Unity `6000.6.5f1`: compact compile `4072277cdc164c52a305d6a472f1afbb` returned **ready_clean, zero errors**, post-reload stability verified, no refresh replay.
- Unity target `McpFtueConfigTests`, `test-20261011-161952-822e6b47`: **108/108**, authoritative `unity_test_runner` counts; one delayed status read, no test replay.
- Final full EditMode `test-20261011-162225-31845212`: **1399 total, 1379 passed, 20 ignored, zero failed**, authoritative `unity_test_runner` counts; one delayed status read, no test replay. The existing setup-window tests were ignored because an MCP window was already open; they are not an executed UI pass. Successful artifacts were not reread or rewritten.

Final Node JSON report SHA-256: `99b085c16dca0f3d44755ca96278e3b28898176cdc3ccc9f288b61a218f636ec`. Frozen non-Markdown source manifest SHA-256: `332e41c2233dc75d1ded212f293fdb393f9482251702d836ce4373a6f15d1fdf` (sorted `path + NUL + file SHA-256`, joined with LF). After the successful full run, only Markdown evidence/changelog documentation was changed; documentation checks were repeated. Private logs/configs, raw reports and triage are not published.

## Retained failures and recovery

- The first typecheck rejected an optional `lstat` test-helper result; the fixture was corrected before final verification. The first static pass rejected a missing Added changelog section; it was supplied and the exact gate rerun. These are retained preliminary failures, not passing attempts.
- A native Windows Vitest attempt failed before collecting tests because `.js` source imports did not resolve to `.ts` in that environment. It is **not** native test-suite pass evidence. The separately emitted native fixture above exercised the unchanged real Node filesystem APIs and both baseline/fixed readers instead.
- The first full Node run reported **3610 passed, 1 failed, 8 skipped**. The unchanged audit child-process append/rotation case threw `Storage file identity changed` while reading a concurrent lock owner. Audit/storage runtime, its fixture, assertions and timeouts were not changed. The exact unchanged audit suite passed **17/17**, followed by the successful full frozen run above. No previous-release waiver was applied; the root cause of that preliminary audit race is not claimed fixed here.
- Initial isolated Unity startup was blocked by **Project Upgrade Required** (saved `6000.6.2f1`, opened `6000.6.5f1`); discovery had no fresh temp heartbeat and `compile.status` returned `project_stale` without dispatch. The owner separately chose manual Continue for temp. Fresh readiness and compile evidence followed; the agent did not click Continue/Quit or implement this popup. The owner-requested upgrade-popup roadmap entry is separate from this reader fix.

## Boundaries and remaining acceptance

1552 frozen raw report/triage files and all four original shared/private config/policy files retained exact hashes, sizes and timestamps. The deployment clone remained clean; the working Gateway runtime, installed OpenCode/OpenChamber, working configs and popup opt-ins were not updated. Source changes were confined to the development clone; Unity validation used only temp. Deployment belongs to the owner.

No real Keep Backups click, backup-preservation check, human Accept/Decline/Cancel or client visual acceptance is claimed. Other Unity versions, PlayMode, clean install/update, broader native Windows client/storage matrix and historical PR219 scanner/binding causes remain separate gates. Missing Signature and Project Upgrade Required response handlers remain roadmap-only.
