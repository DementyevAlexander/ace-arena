// integrations/opencode-sgg-unity-presentation/src/sanitize.ts
var ANSI_OSC_PATTERN = /(?:\u001B\]|\u009D)[\s\S]*?(?:\u0007|\u001B\\|\u009C)/gu;
var ANSI_CSI_PATTERN = /(?:\u001B\[|\u009B)[0-?]*[ -/]*[@-~]/gu;
var ANSI_ESCAPE_PATTERN = /\u001B[@-_]/gu;
var BIDI_CONTROL_PATTERN = /[\u061C\u200E\u200F\u202A-\u202E\u2066-\u2069]/gu;
var CONTROL_PATTERN = /[\u0000-\u001F\u007F-\u009F]/gu;
var LINE_BREAK_PATTERN = /[\r\n\t]+/gu;
var WHITESPACE_PATTERN = /\s+/gu;
var PRESENTATION_TEXT_MAX_LENGTH = 160;
function removeInvalidSurrogates(value) {
  let result = "";
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code >= 55296 && code <= 56319) {
      const next = value.charCodeAt(index + 1);
      if (next >= 56320 && next <= 57343) {
        result += value[index] ?? "";
        result += value[index + 1] ?? "";
        index += 1;
      }
      continue;
    }
    if (code >= 56320 && code <= 57343) continue;
    result += value[index] ?? "";
  }
  return result;
}
function sanitizePresentationText(value, maxLength = PRESENTATION_TEXT_MAX_LENGTH) {
  const boundedLength = Number.isSafeInteger(maxLength) && maxLength > 0 ? Math.min(maxLength, PRESENTATION_TEXT_MAX_LENGTH) : PRESENTATION_TEXT_MAX_LENGTH;
  const normalized = removeInvalidSurrogates(value).replace(ANSI_OSC_PATTERN, "").replace(ANSI_CSI_PATTERN, "").replace(ANSI_ESCAPE_PATTERN, "").replace(LINE_BREAK_PATTERN, " ").replace(BIDI_CONTROL_PATTERN, "").replace(CONTROL_PATTERN, "").replace(WHITESPACE_PATTERN, " ").trim();
  return Array.from(normalized).slice(0, boundedLength).join("");
}

// integrations/opencode-sgg-unity-presentation/src/format.ts
var TOOL_IDS = {
  projects: "sgg-unity-mcp_sgg_unity_projects",
  describe: "sgg-unity-mcp_sgg_unity_describe",
  call: "sgg-unity-mcp_sgg_unity_call",
  batch: "sgg-unity-mcp_sgg_unity_batch",
  job: "sgg-unity-mcp_sgg_unity_job",
  logs: "sgg-unity-mcp_sgg_unity_logs",
  staleProjects: "sgg-unity-mcp_sgg_unity_stale_projects",
  staleCleanup: "sgg-unity-mcp_sgg_unity_stale_registry_cleanup"
};
var SGG_UNITY_PRESENTATION_TOOL_IDS = Object.freeze(Object.values(TOOL_IDS));
var COMMAND_LABELS = /* @__PURE__ */ new Map([
  ["compile.refresh_and_wait", "Refreshing and compiling Unity"],
  ["compile.wait_ready", "Waiting for Unity to become ready"],
  ["compile.status", "Checking Unity compile status"],
  ["test.run", "Starting Unity tests"],
  ["test.run.status", "Checking Unity test status"],
  ["editor.quit", "Requesting Unity Editor shutdown"]
]);
function asInputRecord(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) return void 0;
  return value;
}
function batchLabel(args) {
  if (!args) return "Running a Unity command batch";
  const items = args.items;
  if (!Array.isArray(items)) return "Running a Unity command batch";
  const count = Math.min(items.length, 999);
  const countText = items.length > 999 ? "999+" : String(count);
  return `Running a Unity command batch (${countText} ${count === 1 ? "command" : "commands"})`;
}
function jobLabel(args) {
  if (args?.action === "cancel") return "Requesting Unity job cancellation";
  return "Checking a Unity job";
}
function callLabel(args) {
  const command = args?.command;
  if (typeof command !== "string") return "Running a Unity command";
  return COMMAND_LABELS.get(command) ?? "Running a Unity command";
}
function createPresentationSnapshot(tool, rawArgs) {
  if (typeof tool !== "string") return void 0;
  const args = asInputRecord(rawArgs);
  let label;
  switch (tool) {
    case TOOL_IDS.projects:
      label = "Finding live Unity projects";
      break;
    case TOOL_IDS.describe:
      label = "Reading Unity command metadata";
      break;
    case TOOL_IDS.call:
      label = callLabel(args);
      break;
    case TOOL_IDS.batch:
      label = batchLabel(args);
      break;
    case TOOL_IDS.job:
      label = jobLabel(args);
      break;
    case TOOL_IDS.logs:
      label = "Reading Unity logs";
      break;
    case TOOL_IDS.staleProjects:
      label = "Inspecting stale Unity project records";
      break;
    case TOOL_IDS.staleCleanup:
      label = "Previewing or cleaning stale Unity records";
      break;
    default:
      return void 0;
  }
  const safeLabel = sanitizePresentationText(label);
  if (!safeLabel) return void 0;
  return Object.freeze({ schemaVersion: 1, tool, label: safeLabel });
}

// integrations/opencode-sgg-unity-presentation/src/server.ts
var PLUGIN_ID = "sgg-unity.presentation";
function isMetadataRecord(value) {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}
function addPresentationMetadata(output, snapshot) {
  try {
    let metadata = output.metadata;
    if (metadata === void 0 || metadata === null) {
      metadata = {};
      output.metadata = metadata;
    }
    if (!isMetadataRecord(metadata)) return;
    metadata.sggPresentation = Object.freeze({
      schemaVersion: snapshot.schemaVersion,
      label: snapshot.label,
      outcome: "completed"
    });
    metadata.description = snapshot.label;
  } catch {
  }
}
function createPresentationHooks() {
  return {
    async "tool.execute.after"(input, output) {
      try {
        const snapshot = createPresentationSnapshot(input.tool, input.args);
        if (!snapshot) return;
        addPresentationMetadata(output, snapshot);
      } catch {
      }
    }
  };
}
var plugin = {
  id: PLUGIN_ID,
  async server() {
    return createPresentationHooks();
  }
};
var server_default = plugin;
export {
  createPresentationHooks,
  server_default as default
};
