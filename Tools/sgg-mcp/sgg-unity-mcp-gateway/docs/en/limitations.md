[English](./limitations.md) | [Русский](../ru/limitations.md)

# Limitations


These limitations are part of the product contract. They should remain visible and be updated when the implementation changes.

## Product And Deployment

- This is not an official Unity product.
- The baseline is local-first; there is no hosted or managed runner.
- OAuth, JWT, SSO, organization identity, and multi-user tenant isolation are not implemented as baseline features.
- The normal host is Windows. Other hosts are not claimed without separate validation.
- Non-loopback access is advanced local deployment, not an internet service configuration.

## Unity And Versions

- Unity `6000.4+` is the recommended baseline, while the package metadata floor remains `2022.3`; import floor is not proof of full workflow support.
- Exact support claims require current recorded validation evidence.
- Open projects with different package versions may not all be compatible with one installed runtime. Use version/hash health diagnostics.
- Unity modals, compilation, domain reload, main-thread stalls, or crashes can temporarily make projects stale/offline.
- The Gateway cannot make Unity's main thread responsive while Unity itself is blocked.

## Commands And Discovery

- A documented family may be feature-gated or absent in a particular project.
- The catalog is project/version specific and can change after reload.
- Planned command ids are not callable until discovered.
- Some Unity APIs do not expose reliable cancellation, progress percentages, or complete runtime data; commands report unsupported/unavailable instead of inventing values.
- Inline output is bounded; large data requires artifacts and multiple reads.

## Approvals

- Native elicitation depends on the MCP client advertising and rendering the capability.
- Native approval depends on advertised and rendered MCP form elicitation. OpenCode v2 implements forms and the updated legacy shim relays them capability-aware; actual frontend/human acceptance remains a separate gate.
- Approval is one-shot and exact; it is intentionally inconvenient for broad repeated writes.
- `auto_allow_ask` weakens interactive review for all ask decisions in that trusted session and is not a recommended default.
- Approval does not guarantee the Unity operation itself succeeds after acceptance.

## Files, Paths, And Checkpoints

- CanonicalPath is identity normalization, not a sandbox.
- Windows same-user processes remain outside the guarantees of a pure Node/Unity path boundary.
- Checkpoints snapshot bounded selected `Assets/...` files and `.meta` companions, not complete project state.
- Checkpoints do not capture every in-memory Editor state, external file, package cache, Library database, scene state, or runtime side effect.
- Restore writes files and requires explicit Unity refresh/compile validation afterward.
- Artifact and workflow commands use fixed roots. The only caller-selected output exception is `build.run.output_root`, which is restricted to an existing absolute fixed-local-drive Windows directory outside the project; the Gateway owns the operation directory and target-specific leaf, and a real build requires exact human confirmation.

## Dynamic Execution

- Dynamic C# runs inside the trusted Unity Editor process and is not sandboxed.
- It can hang or destabilize Unity if misused.
- The Gateway cannot prove arbitrary generated C# is safe.
- Normal typed commands should be preferred.

## Profiler And Runtime Debugging

- Read-only profiler commands inspect data Unity exposes; availability varies by version, platform, Play Mode state, and recording state.
- A frame is authoritative only when its recorded range, thread data, iterator traversal, and post-read range revalidation succeed. `authoritative_bounded_empty` means the configured thread/marker bound prevents a full absence claim.
- Per-sample `durationMS` and depth are available through the supported iterator; aggregate call count and self time may be `null` rather than inferred.
- Recording/capture commands are separately gated and do not imply full parity with the Unity Profiler UI.
- Runtime state-machine sampling must be bounded and is not a general gameplay automation engine.
- Screenshots can expose proprietary or sensitive UI; capture commands require careful target selection and policy.

## UI Migration

- The current Canvas/uGUI to UI Toolkit path is scan/plan/create-only generation/validation/preview.
- It does not automatically replace legacy scenes or prefabs.
- Preview compares already available screenshot artifacts; it does not guarantee automatic screenshot capture.
- Repair, apply, rollback, and full behavioral parity remain unavailable until current descriptors prove otherwise.
- Generated UI requires human design, accessibility, navigation, localization, and device review.

## Extensions

- An extension manifest is not trusted by default.
- Package commands execute inside Unity Editor and can be dangerous if a reviewed package is malicious or incorrectly classified.
- Static checks do not prove Unity compilation, behavior, or security.
- The Gateway cannot guarantee compatibility with every third-party package or custom Editor architecture.

## Diagnostics And Audit

- Redaction reduces leakage risk but is not a substitute for reviewing what data a command is asked to collect.
- In-memory audit without a configured durable directory is not durable across restart.
- Logs and artifacts have retention/size bounds and may not preserve old evidence indefinitely.
- Failure after a risky operation reaches Unity can be operationally uncertain; the Gateway deliberately avoids automatic replay.

## Related Docs

- [Compatibility](./reference/compatibility.md)
- [Security](./reference/security.md)
- [Troubleshooting](./troubleshooting.md)
- [Validation](./maintainers/validation.md)
