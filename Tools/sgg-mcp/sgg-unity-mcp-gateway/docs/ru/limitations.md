[English](../en/limitations.md) | [Русский](./limitations.md)

# Ограничения


Эти ограничения — часть product contract.

## Product/Deployment

- Не официальный Unity product.
- Local-first, без hosted runner.
- Нет baseline OAuth/JWT/SSO/org identity/multi-user isolation.
- Normal host Windows; другие hosts без отдельной validation не заявляются.
- Non-loopback — advanced local deployment, не internet service.

## Unity/Versions

- Recommended `6000.4+`, metadata floor `2022.3`; floor не доказывает full support.
- Exact claims только по current evidence.
- Разные package versions open projects могут не быть compatible с одним runtime.
- Modals/compile/reload/stall/crash делают projects stale.
- Gateway не может оживить blocked Unity main thread.

## Commands/Discovery

- Family может быть hidden/absent.
- Catalog project/version specific.
- Planned ids не callable.
- Unity APIs иногда не дают cancellation/progress/full data; нельзя invent values.
- Inline output bounded.

## Approvals

- Native elicitation зависит от client capability/UI.
- Native approval зависит от advertised/rendered MCP form elicitation. OpenCode v2 реализует формы, обновлённый legacy shim relay-ит их capability-aware; реальный frontend/human acceptance остаётся отдельным gate.
- Approval exact one-shot.
- `auto_allow_ask` снижает review и не default.
- Accept не гарантирует success Unity operation.

## Files/Checkpoints

- CanonicalPath identity, не sandbox.
- Same-user Windows process вне полной гарантии.
- Checkpoints bounded `Assets/...` + `.meta`, не full project/editor state.
- Restore требует explicit refresh/compile validation.
- Commands используют fixed roots. Единственное caller-selected исключение — `build.run.output_root`: существующий absolute Windows directory на fixed local drive вне project; operation directory и target-specific leaf определяет Gateway, а real build требует exact human confirmation.

## Dynamic Execution

- C# внутри Unity, не sandbox.
- Может hang/destabilize.
- Safety arbitrary code не доказуема.
- Prefer typed commands.

## Profiler/Runtime

- Availability зависит от Unity/platform/state.
- Frame authoritative только после valid recorded range, thread data, iterator traversal и post-read range revalidation. `authoritative_bounded_empty` не доказывает полное отсутствие за пределами настроенных bounds.
- Per-sample `durationMS` и depth доступны через supported iterator; aggregate call count и self time могут быть `null`, а не inferred.
- Recording отдельно gated, не full Profiler UI parity.
- Sampling bounded, не gameplay automation engine.
- Screenshots могут leak proprietary UI.

## UI Migration

- Current path: scan/plan/create-only/validate/preview.
- Legacy scenes/prefabs не replace-ятся.
- Preview uses existing screenshots.
- Repair/apply/rollback unavailable пока нет descriptors.
- Human design/accessibility/navigation/localization/device review обязательны.

## Extensions

- Manifest untrusted.
- Commands исполняются в Editor и могут быть dangerous.
- Static checks не доказывают Unity behavior/security.
- Нет guarantee для всех third-party architectures.

## Diagnostics/Audit

- Redaction не заменяет data review.
- In-memory audit не durable.
- Retention/size bounds.
- Risky delivery может быть uncertain; no auto-replay.

## Связанные Документы

- [Compatibility](./reference/compatibility.md)
- [Security](./reference/security.md)
- [Troubleshooting](./troubleshooting.md)
- [Validation](./maintainers/validation.md)
