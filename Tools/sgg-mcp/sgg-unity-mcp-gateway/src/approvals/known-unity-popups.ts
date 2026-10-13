import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { GatewayResult } from "../mcp/results.js";

// Private in-process guard provenance, never a JSON/error-code lookalike.
// Only a pre-send permission failure may preserve an unattempted plan.
export class KnownPopupPermissionChanged extends Error {
  constructor(readonly result: GatewayResult) { super("Known popup permission changed before interaction"); }
}

// Explicit owner opt-in only: no prefixes, arbitrary native interaction,
// process termination, builds, or commands from extension packages.
export const KNOWN_UNITY_POPUP_RESPONSE_COMMAND_IDS = Object.freeze([
  "editor.save_prompt.respond",
  "diagnostics.unity_external_scene_reload.respond",
  "diagnostics.unity_project_downgrade.continue",
  "diagnostics.unity_scoped_registry_import.close",
  "diagnostics.unity_safe_mode.enter",
  "diagnostics.unity_scene_backup.keep_backups",
]);

export function isKnownUnityPopupResponse(command: CommandDescriptor): boolean {
  return KNOWN_UNITY_POPUP_RESPONSE_COMMAND_IDS.includes(command.id)
    && command.source === "builtin"
    && command.trust === "builtin"
    && command.package_id === "com.sungeargames.unity-mcp";
}
