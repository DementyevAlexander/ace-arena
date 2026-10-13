import { sanitizePresentationText } from "./sanitize.js";

const TOOL_IDS = {
  projects: "sgg-unity-mcp_sgg_unity_projects",
  describe: "sgg-unity-mcp_sgg_unity_describe",
  call: "sgg-unity-mcp_sgg_unity_call",
  batch: "sgg-unity-mcp_sgg_unity_batch",
  job: "sgg-unity-mcp_sgg_unity_job",
  logs: "sgg-unity-mcp_sgg_unity_logs",
  staleProjects: "sgg-unity-mcp_sgg_unity_stale_projects",
  staleCleanup: "sgg-unity-mcp_sgg_unity_stale_registry_cleanup",
} as const;

export const SGG_UNITY_PRESENTATION_TOOL_IDS = Object.freeze(Object.values(TOOL_IDS));

const DEFAULT_SERVER_PREFIXES = Object.freeze(["sgg-unity-mcp"]);

export function presentationToolAllowlist(serverPrefixes: unknown = DEFAULT_SERVER_PREFIXES): ReadonlyMap<string, string> {
  if (!Array.isArray(serverPrefixes) || serverPrefixes.length === 0 || serverPrefixes.length > 8
    || serverPrefixes.some((prefix) => typeof prefix !== "string" || !/^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/.test(prefix))
    || new Set(serverPrefixes).size !== serverPrefixes.length) {
    throw new Error("SGG presentation serverPrefixes must contain 1–8 unique exact MCP server prefixes");
  }
  return new Map(serverPrefixes.flatMap((prefix) => Object.values(TOOL_IDS).map((tool) => [
    `${prefix}_${tool.slice("sgg-unity-mcp_".length)}`, tool,
  ] as const)));
}

const DEFAULT_TOOL_ALLOWLIST = presentationToolAllowlist();

const COMMAND_LABELS = new Map<string, string>([
  ["compile.refresh_and_wait", "Refreshing and compiling Unity"],
  ["compile.wait_ready", "Waiting for Unity to become ready"],
  ["compile.status", "Checking Unity compile status"],
  ["test.run", "Starting Unity tests"],
  ["test.run.status", "Checking Unity test status"],
  ["editor.quit", "Requesting Unity Editor shutdown"],
]);

type InputRecord = Record<string, unknown>;

export type PresentationSnapshot = Readonly<{
  schemaVersion: 1;
  tool: string;
  label: string;
}>;

function asInputRecord(value: unknown): InputRecord | undefined {
  if (!value || typeof value !== "object" || Array.isArray(value)) return undefined;
  return value as InputRecord;
}

function batchLabel(args: InputRecord | undefined): string {
  if (!args) return "Running a Unity command batch";
  const items = args.items;
  if (!Array.isArray(items)) return "Running a Unity command batch";
  const count = Math.min(items.length, 999);
  const countText = items.length > 999 ? "999+" : String(count);
  return `Running a Unity command batch (${countText} ${count === 1 ? "command" : "commands"})`;
}

function jobLabel(args: InputRecord | undefined): string {
  if (args?.action === "cancel") return "Requesting Unity job cancellation";
  return "Checking a Unity job";
}

function callLabel(args: InputRecord | undefined): string {
  const command = args?.command;
  if (typeof command !== "string") return "Running a Unity command";
  return COMMAND_LABELS.get(command) ?? "Running a Unity command";
}

export function createPresentationSnapshot(tool: unknown, rawArgs: unknown, allowlist = DEFAULT_TOOL_ALLOWLIST): PresentationSnapshot | undefined {
  if (typeof tool !== "string") return undefined;
  const canonicalTool = allowlist.get(tool);
  if (!canonicalTool) return undefined;
  const args = asInputRecord(rawArgs);
  let label: string | undefined;

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
      return undefined;
  }

  const safeLabel = sanitizePresentationText(label);
  if (!safeLabel) return undefined;
  return Object.freeze({ schemaVersion: 1, tool, label: safeLabel });
}
