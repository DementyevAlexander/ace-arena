import { describe, expect, it } from "vitest";
import { isKnownUnityPopupResponse, KNOWN_UNITY_POPUP_RESPONSE_COMMAND_IDS } from "../src/approvals/known-unity-popups.js";
import { compileStatusCommand } from "../src/bridge/bridge-client.js";

const builtin = { ...compileStatusCommand, source: "builtin" as const, trust: "builtin" as const, package_id: "com.sungeargames.unity-mcp" };

describe("owner-preapproved known Unity popup scope", () => {
  it.each(KNOWN_UNITY_POPUP_RESPONSE_COMMAND_IDS)("recognizes only the built-in exact response %s", (id) => {
    expect(isKnownUnityPopupResponse({ ...builtin, id })).toBe(true);
  });

  it.each([
    "asset.delete", "build.run", "editor.execute_csharp", "diagnostics.unity_crash_reporter.close",
    "diagnostics.unity_process.terminate", "diagnostics.unity_recovery_prompt.respond",
    "diagnostics.unity_safe_mode.ignore", "diagnostics.unity_unknown.respond",
    "diagnostics.unity_external_scene_reload.respond.extra", "editor.save_prompt.respond.extra",
  ])("does not treat process actions, generic recovery, prefixes or ordinary writes as popup replies: %s", (id) => {
    expect(isKnownUnityPopupResponse({ ...builtin, id })).toBe(false);
  });

  it.each([
    { source: undefined }, { source: "manifest" as const }, { source: "attribute" as const },
    { trust: undefined }, { trust: "trusted_package" as const }, { trust: "untrusted_package" as const },
    { package_id: undefined }, { package_id: "com.other.package" },
  ])("requires server-owned built-in provenance, not a same-name extension: %j", (override) => {
    expect(isKnownUnityPopupResponse({ ...builtin, id: "editor.save_prompt.respond", ...override })).toBe(false);
  });
});
