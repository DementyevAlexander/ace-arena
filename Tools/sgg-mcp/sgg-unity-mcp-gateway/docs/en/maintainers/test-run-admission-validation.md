[English](./test-run-admission-validation.md) | [Русский](../../ru/maintainers/test-run-admission-validation.md)

# Test-run admission budget validation

Gateway `2026.10.12-1`, 2026-10-12 UTC. Development-source and isolated validation evidence; not working-host installation or full release-matrix acceptance.

## Scope and qualification

Report `problem_20261011T234916_4bab7d40b1cd` records `test-20261011-234559-f2a8214a` as **not dispatched**: durable binding/reservation published, no request issuance observed, reason `binding_publication_crossed_deadline`. The old descriptor allowed 15000 ms for startup, including discovery and durable publication. Neither terminal waiting nor test `max_seconds` extends that cap.

The supported descriptor now advertises **60000 ms**. A shorter caller/wrapper deadline still wins. Storage formats, capacity, retention, custody, pre-commit and pre-fetch owner/policy/feature/deadline checks, original issuance timestamps, uncertainty and no-replay rules are unchanged. No configuration/catalog patch, transport bypass, history purge or new job route is used. This changes the Unity package descriptor; updating only the Gateway executable is insufficient.

Storage inspection establishes per-publication retained metadata scans, not the exact cause of the reported native latency. No performance guarantee is made for arbitrarily large/slow stores. The separate later Foliage operation `test-20261011-235459-97783b90` executed and failed; it is not acceptance or recovery of the earlier non-dispatched request. Neither operation was repeated by this task.

## Executed evidence

- Primary focused implementation/integration review completed. Independent read-only storage diagnosis is not an independent final-fix review claim.
- Affected Node targets: **538/538**, five files. Added controlled 20-second preparation/25-second issuance cases use the shipped descriptor; shorter deadlines, changed owner, denied policy and disabled feature still refuse safely. Original bindings/reservations remain byte/mtime exact on pre-issuance refusal; one attempt and its original timestamp remain exact after issuance.
- Native Windows Node `22.16.0`, actual bundled worker: **3855 retained bindings / 7710 protected files**, hashes and mtimes unchanged. Preparation **3313 ms**, issuance after lock release **2545 ms**. Lock wait, terminal receipt, pre-commit denial and invalid worker-role refusal passed; Unity was not invoked. These are synthetic fixture timings, not a reproduction of the historical latency.
- Final full Node `22.22.3`: **3755 total / 3747 passed / 8 skipped / 0 failed**, 174 files, **470.78 seconds**, byte-verified Linux snapshot and no source drift. Evals **15/15**. Fourteen static constituents passed initially; the required changelog Added section was then restored and final static checks passed **15/15**.
- Temp Unity `6000.6.5f1`, loaded package `2026.10.12-1`, commands hash `1f52daf5bb7dd2de`: actual trusted `test.run` descriptor reports **60000 ms**, revision `sha256:61ceb66d98e9174ce49d9d7f1a8558e55411969a9e2dfc0ebf0667ed5d8b629d`.
- Independent compact readiness `f5525329dfbb48f1a55f24596a487a14`: **ready_clean / 0 errors**. Target `UnityMcpPureHelperTests`, operation `test-20261012-001402-c7f75c9e`: **102/102**, authoritative counts, one status read after 15 seconds.
- Full EditMode `test-20261012-001436-750b60dd`: **1558 total / 1538 passed / 20 ignored / 0 failed**, authoritative persisted exact-owner result, one status read after 120 seconds, no replay. The existing open-window fixture exclusion was preserved; ignored tests are not passed tests.

Full Node report SHA-256: `b314e6ee925095d2e8fb2bfdaa87c7f9b5e6b15ca0152b18bd00c3c27d4fdc16`. Frozen non-Markdown manifest: **788 files**, SHA-256 `62226cf03e0e3333ba2f9ab5666da6b23fa397039ca799c181713b36db4f2e17` (sorted path + NUL + file SHA-256, joined with LF). Only Markdown evidence/navigation/changelog changed after full suites; functional inputs remain identical. Bundled CLI SHA-256 remains `1e9489047ded5138959894b5c310a0b1e54b7b9dfb5951f66b9021a9403f0f42`; package/runtime metadata and SBOM were rebuilt.

## Retained failures and boundaries

Initial targeted run: **305 passed / 2 failed**, both new fixtures. One replaced a captured policy object rather than revoking its existing execute field; one supplied an incomplete response DTO, correctly rejected by production validation. Fixtures were corrected with existing policy/artifact contracts, without weaker guards. Two private launcher fixtures refused before process start due to a non-normalized comparison target; partial owned directories were retained.

One refresh was issued. Its shell wrapper exceeded 180 seconds; owned audit later recorded `compile_freshness_unverified` / `catalog_stale` at 193293 ms after reload, with no repeated write. The independent readiness and executed target above qualify current loaded state, not successful freshness verification of the initial refresh. No old waiver was applied.

All **1597** protected raw report/triage/original configuration/policy files retained hashes, sizes and mtimes. One additionally watched Foliage evidence Markdown file changed outside this task, documenting the separate later failure; the boundary checker retained exit 1 for that external change, which was neither overwritten nor reverted. Deployment clone remained clean and unchanged at `bf752cd7441fafb908a5575de5dbf61bec5dc5a0`. Shared history was not cleared; only exact-identity owned validation Gateway PID75356 was stopped.

Source changes are dev-only; Unity validation is temp-only. Working clients/runtime/configs, Foliage trial execution, physical UI, installed runtime/DLL parity, other Unity versions, PlayMode and full release matrix remain unverified/not run. Historical missing operations and broader report families are not causally closed. Accumulated private planning changes are not published; working installation belongs to the owner.
