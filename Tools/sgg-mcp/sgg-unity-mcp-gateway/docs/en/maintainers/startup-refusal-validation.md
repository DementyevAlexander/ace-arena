# Startup and preflight refusal validation

[English](./startup-refusal-validation.md) | [Русский](../../ru/maintainers/startup-refusal-validation.md)

Gateway `2026.10.12-2`, 2026-10-12. Development-source fixes and isolated validation, not working-host installation or native interaction acceptance.

## Bounded changes

- Publication refusals distinguish prepare admission/queue, command deadline, precommit denial and the existing five-second handshake timeout. Expired permission cannot be granted by a delayed parent. The command deadline, durability and immutable late-commit/issuance rules are unchanged.
- Shared-startup failure timing includes phase elapsed time, the descriptor/inherited-caller minimum and binding reserve. Discovery elapsed evidence now includes metadata latency. Terminal waiting never extends admission.
- Keep Backups refuses incomplete User32 coverage even when its target row is complete. Fixed, bounded scanner stage markers improve localization without changing the scanner budget or authorizing interaction. Binding refusal witnesses distinguish unchecked comparisons from mismatches and expose only root fingerprints.
- A delivered trusted quit refusal must match the reserved operation, caller hash and exact owner before shutdown reconciliation can be skipped. Bridge issuance remains distinct from quit lifecycle/exit issuance. Missing, malformed or uncertain evidence keeps conservative reconciliation; Play close and retained-native-custody release remain unsupported.

## Evidence and limits

- Targeted Node consumers: **1179/1179 passed**. Final full Node: **3795 total / 3789 passed / 6 platform-skipped / 0 failed**, 174 files; agent evals **15/15**, source static gates **15/15**. Only Markdown changed after the frozen functional input set; no assertion, timeout or production guard was relaxed.
- Windows Node **22.16.0** actual bundled worker: **3855 retained bindings / 7710 frozen files**, all hashes and mtimes unchanged; prepare **2817 ms**, issuance **2465 ms**, one guard invocation and the bounded refusal witness verified. No Unity invocation.
- Temp Unity **6000.6.5f1**: exact pure classes **32/32 + 102/102 passed**. Full EditMode `test-20261012-084005-cf54cae9`: **1590 total / 1570 passed / 20 ignored / 0 failed**, authoritative terminal counts; one delayed status read after 120 seconds, no replay.
- One explicit refresh returned `compile_freshness_unverified` after losing post-reload lifecycle continuity. It was not repeated. A separate read returned `ready_clean`, zero compile errors; execution of the new pure class proves its loaded test code, not the original refresh outcome or general source/domain/DLL provenance.
- **1607 protected raw/config/policy files remain byte/mtime-identical.** The concurrent shared history is not claimed unchanged: six old files became absent and four files belong to two unrelated Weather operations. This task's test operations use a separate private store. No external changes are restored, classified as successful cleanup, or used as historical outcome proof.

No historical execution is repeated to obtain evidence. Private planning changes are not included in source publication.

The original publication abort cause is not established by these fixes. The inspected reserved and issued historical test operations still have unknown outcomes; later independent passing runs cannot replace them. A reservation is a deliberate bounded placeholder, not an issuance marker.

Current-source/compiler-generation acceptance, physical popup/backups acceptance and working runtime/package deployment remain separate. Retained native custody needs its owning package's explicit close/ACK contract; process exit cannot prove GPU completion or quality success.

Gateway-side OpenCode v2 integration is already released. Stock Web/default Code Mode presentation requires a separately authorized client-source change; no installed OpenCode, user configuration or client renderer is patched here.
