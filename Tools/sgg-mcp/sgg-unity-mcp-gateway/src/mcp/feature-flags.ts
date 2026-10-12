import type { CommandDescriptor } from "../bridge/bridge-client.js";

const GAME_VIEW_FOCUS_CAPTURE_FEATURE = "game_view_focus_capture";
const GAME_VIEW_FOCUS_CAPTURE_DEPENDENCIES = new Set([
  "editor.windows.list",
  "game.view.screenshot.status",
  "game.view.screenshot.cancel",
]);

export function isCommandFeatureEnabled(command: Pick<CommandDescriptor, "id" | "feature_flag">, features: Record<string, boolean> | undefined): boolean {
  if (!command.feature_flag || features?.[command.feature_flag] === true) return true;
  return features?.[GAME_VIEW_FOCUS_CAPTURE_FEATURE] === true && GAME_VIEW_FOCUS_CAPTURE_DEPENDENCIES.has(command.id);
}
