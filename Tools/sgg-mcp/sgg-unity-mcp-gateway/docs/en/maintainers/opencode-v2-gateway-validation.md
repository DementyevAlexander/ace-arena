[English](./opencode-v2-gateway-validation.md) | [Русский](../../ru/maintainers/opencode-v2-gateway-validation.md)

# OpenCode v2 Gateway integration validation

Gateway build `2026.10.10-6`, presentation plugin `2.0.0`, upstream OpenCode `2.0.25` / `b44eea9e204024db2b480a136ece29d790d5f593`. This report covers the Gateway-side P0 slice, not complete client UX acceptance or the full Unity release matrix.

## Executed evidence

- Focused source review: GO after fixing hidden-layer disable shadowing, supported legacy timeout inheritance, and native optional-field/protocol validation. Authority, approval TTL, opt-ins and no-replay behavior are unchanged.
- Initial joint Node targets: 326/326. Review regression/selector consumers: 138/138. Transport consumers were checked separately; obsolete error-summary assertions now require exact canonical JSON within the unchanged selector byte bounds, not a relaxed limit.
- Final full Node on the frozen Linux snapshot: **3603 total, 3595 passed, 8 skipped, zero failed**, 170 files; started `2026-10-10T21:53:46.640Z`, completed in 456.89 seconds. All 15 agent evals and runtime-compliance cases executed successfully; final source hashes stayed unchanged. The eight platform skips are six native-Windows cases and two WSL-mount cases; those exact two mount cases additionally passed in the development clone. Native Windows execution is not claimed.
- Runtime build and all 15 non-test static constituents passed, including typecheck, docs, version/changelog, artifact ownership/reproducibility, notices/SBOM and manifest-security. Together with the final full Node/evals result, every static-profile constituent passed; this is separate command evidence, not a full release-matrix GO.
- Isolated Unity `6000.6.2f1`, development package only: final compact compile `9d74e8e52f58495285f91d8429431f54` returned `ready_clean`, zero errors.
- Final `McpFtueConfigTests`: 108/108, `test-20261010-205020-2dd75ff6`. `OpenCodeConfigPathResolverTests`: 27/27; `OpenCodePresentationSetupManagerTests`: 17/17. Consumers `GatewayHealthParserTests`: 41/41; `Harden09A1WindowStateTests`: 25/25. The existing window class remains 20 ignored, not an executed UI pass.
- Final full EditMode `test-20261010-205203-92fd0c55`: **1399 total, 1379 passed, 20 ignored, zero failed**, authoritative `unity_test_runner` counts. Successful artifacts were not reread or rewritten.
- Real released OpenCode host with temporary HOME/XDG directories, private authenticated server and a copied installed plugin outside repository dependency resolution: exact `sgg-unity.presentation` active, local legacy MCP shim connected, active after reload, idempotent install returned `noop`, removed plugin absent after reload, reinstall active again. Native config was read by the host. Cold inventories before location activation were not counted as loading evidence; location activation and bounded waits preceded the passing checks.

The installed-host lifecycle used the exact bundled plugin manifest SHA-256 `fb36e673195d9275a4dd46d8acf42477a0cff49694d918cba1127f1f64d66824`. Boundary verification retained 1539 frozen raw reports and triage bytes/timestamps, original isolated config/policy bytes, a clean deployment clone, and unchanged PR224 publication source/tests.

Final Node JSON report SHA-256: `bf2738a00429b3f14e18c66d8312c0cae150dfc8d62d29c67a15052cff2b904e`. The frozen non-Markdown source manifest digest is `8f16d6b587dda97332d643680b29f2494d855e8eaaceffc5251d7bc10d46e788` (SHA-256 of sorted `path + NUL + file SHA-256`, joined with LF). Only the bilingual validation report changed after this full run; documentation checks were repeated. Private logs/configs and raw reports are not published.

## Retained preliminary failures

The additional mounted-tree SDK/dispatcher run passed 532 tests and timed out one approval fixture at about 5363 ms against its unchanged 5000 ms limit. That exact unchanged fixture passed separately in the Linux snapshot. A preliminary two-worker mounted-tree full attempt reported ten failed tests and exceeded its 900-second process deadline; its source was still changing, so it is not final-commit evidence. A mounted-tree eval also timed out once. The failures are retained, not reclassified as pass or covered by the previous release's waiver. Final verification used the immutable snapshot with one worker; assertions and timeouts were not loosened. Environment/timing sensitivity is not a proven attribution for every preliminary failure.

The first one-worker Linux full attempt also exceeded 900 seconds and reported failures in dispatcher, HTTP, popup, audit and historical source tests. The snapshot lacked Git history, which invalidated three historical source checks; linked external dependencies also prevent treating the skipped runtime-compliance tests as pass. Git history and physical Linux dependencies were subsequently supplied. No final Node/full-static pass or release publication is claimed by these incomplete attempts.

After supplying Git history and physical Linux dependencies, the affected ten-file run completed: 839 tests, 821 passed, 18 failed, none skipped, frozen source unchanged. Seventeen failures were 5000 ms timeouts; one mandatory-human case returned fail-closed `approval_unavailable` instead of the expected pending confirmation. That exact unchanged case passed alone in 387 ms, which is not a replacement for the failed suite. The other previously failing HTTP, popup, audit, compile-convergence and runtime-compliance cases passed in this run. The idle-environment repeat passed 838/839, leaving only the HTTP fixture's compressed 30/60 ms real-network timing race.

That fixture now advances a logical clock after the real HTTP request arrives while observing the unchanged native timeout and exact serialized deadline: 40 seconds must succeed beyond the old 30-second cap, and 60/60.001 seconds must fail at/after the later deadline. The runtime is unchanged; no test timeout was increased. The updated HTTP suite passed 76/76 and focused review returned GO before the successful final full run above.

## Explicitly not claimed

- Web/PWA, terminal and OpenChamber visual labels, including inner default Code Mode rows.
- Real human form Accept/Decline/Cancel or concurrent frontend routing. Public SDK relay fixtures test transport behavior, not human acceptance.
- A model session exercising direct/Code Mode tools inside the isolated OpenCode host. Connected inventory is connection evidence only.
- Windows PowerShell shim live client execution in this iteration; command parity is tested, while the actual isolated host used the Linux shim.
- Other Unity versions, PlayMode, clean install/update matrix or `check:release` full-matrix GO.
- Deployment, installed client patches, shared Gateway restart or changes to working configs, policies or reported operations.

The separate [client follow-up](./opencode-v2-client-follow-up.md) remains a proposal. Gateway source/artifact integration can be delivered independently; overall P0 visual/human acceptance remains open. PR219 and Missing Signature follow-up are not implemented by this release.
