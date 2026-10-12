[English](./opencode-v2-client-follow-up.md) | [Русский](../../ru/maintainers/opencode-v2-client-follow-up.md)

# OpenCode v2: separate client follow-up

Status: **proposal, not implemented**. The owner authorized Gateway changes in `sgg-unity-mcp-gateway-dev` only. Do not patch OpenCode, its installed binary, shared service, user configuration or OpenChamber. The owner confirms that the MCP connection already works with OpenCode v2/OpenChamber v2; this is not a connection-failure report or permission to patch a client.

## Exact baseline

OpenCode `v2.0.25`, upstream commit [`b44eea9e204024db2b480a136ece29d790d5f593`](https://github.com/anomalyco/opencode/tree/b44eea9e204024db2b480a136ece29d790d5f593), and the public [plugin API](https://opencode.ai/v2/docs/build/plugins). OpenCode v2 and MCP protocol `2026-07-28` are independent versions; a legacy transport remains valid. Source/contract evidence is not live, visual or human acceptance. Validate Web/PWA, terminal and OpenChamber separately.

## Proposed client-only work

1. **Direct Web cards.** [`GenericTool:398–415`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/session-ui/src/components/basic-tool.tsx#L398-L415) currently reads `tool/status/input`, not our presentation metadata. Pass the actual tool-part metadata through a typed prop, validate the shipped versioned `sggPresentation` contract and render its bounded safe labels for running/completed/error states. Unknown formats retain the generic fallback. Do not rebuild labels from raw args, code, paths, tokens or opaque IDs. Keep canonical details separate.
2. **Default Code Mode inner rows.** [`ExecuteCall/progressHooks`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/core/src/codemode/tool.ts#L24-L35) stores `tool/status/input` only. Extend its typed row/progress contract with optional validated presentation; carry each inner call's progress/result/error into its own stable-identity row and render it in Web and terminal. Preserve start order, concurrency, cancellation and generic fallback. Do not parse model JavaScript, use a global current call or require `codemode:false` for labels. Review the actual Code Mode contracts before implementation.
3. **Optional structured error improvement.** [`mcp.ts:78–85`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/core/src/tool/mcp.ts#L78-L85) converts `isError` to text-only `ToolFailure`; success retains structured output. Gateway-side bounded canonical error text addresses recovery loss without a client patch. A separately authorized upstream change could preserve structured errors and safe metadata in typed failure/model/UI round-trips. Never convert an error to success; do not remove the compatibility projection before comparative direct/shim/Code Mode evidence.

None of these changes may alter tool names/inputs/results, permissions, approval authority or the session database.

## Acceptance first, not an assumed form-routing patch

OpenCode `2.0.25` already implements native form elicitation. [`mcp/index.ts:231–294`](https://github.com/anomalyco/opencode/blob/b44eea9e204024db2b480a136ece29d790d5f593/packages/core/src/mcp/index.ts#L231-L294) creates location-scoped forms with sentinel session `global`. `ai.opencode/sessionID` is opaque correlation, never authorization. Verify actual UI visibility, human approve/false/cancel, expiry, disconnect and concurrent forms on each transport/frontend. If this fails, record an exact redacted reproduction before proposing a separately authorized routing fix. No self-approval, policy exception, popup opt-in or automatic operation replay.

## Future acceptance gates

- Explicit client-source ownership/scope approval; no installed-client monkey patches.
- Visible direct and parallel inner Code Mode labels during and after calls, including errors/confirmation.
- Invalid/unknown metadata fallback and privacy fixtures for args/code/paths/tokens/raw errors/IDs.
- Unchanged canonical results/recovery, exact operation IDs and no reconnect replay.
- Real human form acceptance/refusal/cancellation on the actual transports.
- Separate versioned OpenCode Web, terminal and OpenChamber evidence, not metadata-only tests.

A Gateway release may finish its integration slice; overall P0 visual/human UX acceptance remains open until these gates pass.
