// integrations/opencode-sgg-unity-presentation/src/server.ts
import { Plugin } from "@opencode/plugin";

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
var DEFAULT_SERVER_PREFIXES = Object.freeze(["sgg-unity-mcp"]);
function presentationToolAllowlist(serverPrefixes = DEFAULT_SERVER_PREFIXES) {
  if (!Array.isArray(serverPrefixes) || serverPrefixes.length === 0 || serverPrefixes.length > 8 || serverPrefixes.some((prefix) => typeof prefix !== "string" || !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(prefix)) || new Set(serverPrefixes).size !== serverPrefixes.length) {
    throw new Error("SGG presentation serverPrefixes must contain 1–8 unique exact MCP server prefixes");
  }
  return new Map(serverPrefixes.flatMap((prefix) => Object.values(TOOL_IDS).map((tool) => [
    `${prefix}_${tool.slice("sgg-unity-mcp_".length)}`,
    tool
  ])));
}
var DEFAULT_TOOL_ALLOWLIST = presentationToolAllowlist();
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
function createPresentationSnapshot(tool, rawArgs, allowlist = DEFAULT_TOOL_ALLOWLIST) {
  if (typeof tool !== "string") return void 0;
  const canonicalTool = allowlist.get(tool);
  if (!canonicalTool) return void 0;
  const args = asInputRecord(rawArgs);
  let label;
  switch (canonicalTool) {
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
function isMetadataRecord(value) {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}
function presentationMetadata(snapshot, outcome) {
  return Object.freeze({
    schemaVersion: snapshot.schemaVersion,
    label: snapshot.label,
    outcome
  });
}
function addPresentationMetadata(target, snapshot, outcome) {
  try {
    const descriptor = Object.getOwnPropertyDescriptor(target, "metadata");
    if (descriptor && !Object.hasOwn(descriptor, "value")) return;
    const metadata = descriptor?.value;
    const presentation = presentationMetadata(snapshot, outcome);
    if (metadata === void 0) {
      Object.defineProperty(target, "metadata", {
        value: { sggPresentation: presentation },
        enumerable: true,
        configurable: true,
        writable: true
      });
    } else if (isMetadataRecord(metadata) && !Object.hasOwn(metadata, "sggPresentation")) {
      Object.defineProperty(metadata, "sggPresentation", {
        value: presentation,
        enumerable: true,
        configurable: true,
        writable: true
      });
    }
  } catch {
  }
}
function wrapExecutor(original, tool, allowlist) {
  return async function(input, context) {
    try {
      const snapshot = createPresentationSnapshot(tool, input, allowlist);
      if (snapshot) await context.progress({ sggPresentation: presentationMetadata(snapshot, "running") });
    } catch {
    }
    return Reflect.apply(original, this, [input, context]);
  };
}
var server_default = Plugin.define({
  id: "sgg-unity.presentation",
  async setup(ctx) {
    const allowlist = presentationToolAllowlist(ctx.options.serverPrefixes);
    await ctx.tool.hook("execute.after", (event) => {
      try {
        const snapshot = createPresentationSnapshot(event.tool, event.input, allowlist);
        if (!snapshot) return;
        if (event.status === "completed") addPresentationMetadata(event.result, snapshot, "completed");
        if (event.status === "error") addPresentationMetadata(event.error, snapshot, "error");
      } catch {
      }
    });
    await ctx.tool.transform((editor) => {
      for (const tool of editor.list()) {
        if (!allowlist.has(tool.id)) continue;
        editor.update(tool.id, (draft) => {
          draft.execute = wrapExecutor(draft.execute, tool.id, allowlist);
        });
      }
    });
  }
});
export {
  server_default as default
};
