[English](./test-history-validation.md) | [Русский](../../ru/maintainers/test-history-validation.md)

# Setup population and test history validation

Gateway `2026.10.11-3`, 2026-10-11 UTC. This is development-source, fixture and isolated-runtime evidence, not working-host deployment, physical UI acceptance or full release-matrix GO.

## Delivered scope

Required Setup rows populate before optional freshness checks. Independent read-only sections report failed/unchecked state explicitly. Fixture suppression and teardown belong to the isolated window, not the owner's open window. The original screenshot's complete root cause is not established.

Gateway Test History has separate byte/record bars, saved restart-bound settings and confirmed fingerprint-bound cleanup. Independent defaults are **209715200 bytes (200 MiB)** and **192000 records**; retention remains 30 days, per-record size 16 KiB and publisher leases 64. Unknown usage is unavailable, not zero. Saving settings preserves unrelated fields and does not restart Gateway or change permissions.

Cleanup previews at most 256 records. Only exact validated terminal execution evidence or a bound no-execution receipt without contradictory artifacts is eligible. Active, unknown, cancelled/uncertain, reserved, foreign, invalid or changing metadata remains. Directory/file custody, full snapshot/content checks at commit, shared settings-writer locking and one ten-second command deadline are enforced. Unity result artifacts and reports are never removed.

## Executed evidence

- Independent C# focused review accepted; prior backend safety findings were fixed and checked by the primary after the worker's provider error. Final focused integration review and regression execution completed without new agents; no independent final-fix review is claimed.
- Latest affected Node targets: **103/103**, four files. Earlier 19-file report: **904 passed, 6 skipped, zero failed**; its shell wrapper exceeded 300 seconds although the completed Vitest report was successful, so wrapper success is not claimed. Signal-bound child-fixture consumers separately passed **105**, with **4 skipped**.
- Final full Node: **3748 total, 3740 passed, 8 skipped, zero failed**, 174 files, 494.29 seconds; Node `22.22.3`, byte-verified Linux snapshot with physical dependencies, no source drift. Original test deadlines/assertions and within-test concurrency retained.
- All **15** non-test static constituents passed, including version, docs, package, reproducible final runtime, notices/SBOM and security; final-tree agent evals **15/15**.
- Native Windows Node `22.16.0`: six storage/admin consumer files **185 total, 176 passed, 9 skipped, zero failed**. After the last host-path guard, the exact admin class passed **78 total, 77 passed, 1 skipped, zero failed**. The broader Windows matrix was not run.
- Final emitted CLI SHA-256 `1e9489047ded5138959894b5c310a0b1e54b7b9dfb5951f66b9021a9403f0f42`: actual Windows `status`, confirmed `configure`, `preview-cleanup` and `cleanup` passed on new synthetic isolated metadata. Missing confirmation and stale fingerprints were refused. Two terminal bindings removed; reserved/foreign records and raw-report bytes retained. Independent settings changes and default restoration passed; shared history/configs were not mutated.
- Temp Unity `6000.6.5f1`, package `2026.10.11-3`: compact compile `6e25dd438a824214930f1c6258b00120` returned **ready_clean, zero errors**, post-reload stability verified, no refresh replay. Exact EditMode targets: history **22**, initial population **4**, shared parser **96**, tool view **34**, state restoration **41**: **201/201**.
- Full EditMode `test-20261011-231226-f1bf95f9`: **1557 total, 1537 passed, 20 ignored, zero failed**, authoritative `unity_test_runner` counts, persisted exact-owner artifact; one status read after 120 seconds, no replay. Subsequent changes were Node/admin path validation, tests and generated CLI/SBOM; Unity C# inputs were unchanged, so the full EditMode run was not repeated.

Full Node report SHA-256: `0a80a1c01635a84e979adde0d2e31564ff3b3754917d9425431ca0cdb4890927`. Frozen non-Markdown manifest SHA-256: `5f723e9b8e7b95e5d7b24992e8e3c80dca3fed3902d10ee87985386d7c9707f0` (788 files; sorted `path + NUL + file SHA-256`, joined with LF). Only Markdown evidence/navigation changed after final suites; documentation checks were repeated. Private raw evidence/configs are not published.

## Retained failures and recovery

Initial C# fixtures had one missing namespace and four behavioral failures; real-panel lifecycle and literal absent-JSON checks fixed them without weaker assertions. Backend reviews found cancellation/evidence, writer locking, maintenance side effects, custody, bounded scanning, identity, contradiction, deadline and deletion-content issues; final regressions preserve uncertain/changing files.

Initial native runs failed on the Linux dependency tree's missing Windows resolver; dependencies were installed only inside the disposable native fixture. A 200 ms child-exit timer expired during native owner observation; explicit release signaling now proves both live preservation and confirmed-exit quarantine. Coherent Windows transition-gate successors are reobserved, never retired using stale dead-owner evidence.

The first full Node run retained **3732 passed, 4 failed, 8 skipped**: two external diagnostic timeouts, a forbidden direct path resolver and an obsolete global-refresh source contract. The narrow fixture uses a complete bounded empty native scan without changing deadlines/dispatcher behavior; config validation uses CanonicalPath with four invalid-path regressions; the source contract asserts per-window suppression. A missing changelog section and smoke-harness import/accounting errors were also retained and corrected. No old waiver was applied; historical timeout causes are not declared solved.

## Boundaries and open acceptance

All **1596** frozen raw/triage/original config/policy files retained hashes, sizes and timestamps. Deployment clone stayed clean at `78bd6e572f0b2503e0b92809cea69a992a419101`. Source edits only in dev; Unity validation only in temp. Existing shared history was never cleared. Only exact-identity task-owned validation Gateways were stopped. Working clients/tools/configs and original authorization were not changed; deployment belongs to the owner.

Physical EN/RU bars/buttons/confirmation/cancel, working-host install/update, installed runtime/DLL parity, other Unity versions, PlayMode and full release matrix remain **not_run/unverified**. Missing Signature and Project Upgrade Required remain roadmap-only. Historical missing operations, retained-running outcomes and PR219/224/228/241 are not causally closed or replayed. Local accumulated private planning changes are not published.
