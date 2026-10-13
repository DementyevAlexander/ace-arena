export const PROBLEM_REPORT_SCHEMA_VERSION = 2;
export const PROBLEM_REPORT_MAX_BYTES = 256 * 1024;
export const PROBLEM_REPORT_MAX_REGISTRY_RECORDS = 16;

const MAX_STRING_CHARS = 64 * 1024;
const MAX_ARRAY_ITEMS = 128;
const MAX_DEPTH = 16;
const SECRET_KEY_PATTERN = /token|authorization|api[_-]?key|secret|password|cookie/i;
const PATH_KEY_PATTERN = /(?:^|_)(?:path|paths|root|roots|directory|directories|dir|dirs|file|files|artifact_ref|artifact_refs)$/i;
const SAFE_PROJECT_RELATIVE_PATH = /^(?:Assets|Library|Packages|ProjectSettings|UserSettings)\//;
const ESCAPED_JSON_SECRET_PATTERN = new RegExp(
  `("(?:${["token", "authorization", "apikey", "api_key", "api-key", "secret", "password", "cookie"].map(jsonEscapedWordPattern).join("|")})"\\s*:\\s*")(?:\\\\.|[^"\\\\])*("|$)`,
  "gi",
);

const PRUNABLE_PATHS = [
  ["snapshot", "audit_tail", "lines"],
  ["snapshot", "startup_log_diagnostics", "probes"],
  ["snapshot", "unity_crash_reporter", "recent_crash_artifacts"],
  ["snapshot", "unity_crash_reporter", "temp_roots"],
  ["snapshot", "unity_processes", "processes"],
  ["snapshot", "registry", "projects"],
  ["snapshot", "library_lock_diagnostics", "locks"],
  ["snapshot", "unity_modal"],
  ["snapshot", "recent_unity_errors"],
  ["snapshot", "bridge_reload_trace_tail", "lines"],
  ["doctor", "unity_crash_reporter", "recent_crash_artifacts"],
  ["doctor", "unity_crash_reporter", "temp_roots"],
  ["registry", "projects"],
] as const;

export type SerializedProblemReport = {
  report: Record<string, unknown>;
  text: string;
  bytes: number;
  truncated: boolean;
  truncatedSections: string[];
};

type RedactedProblemReportValue = { value: unknown; truncated: boolean };

export function serializeProblemReport(value: Record<string, unknown>): SerializedProblemReport {
  const redacted = redactValue(value, undefined, 0);
  const bounded = boundProblemReportValue(redacted.value);
  const report = bounded.value as Record<string, unknown>;
  report.schema_version = PROBLEM_REPORT_SCHEMA_VERSION;
  report.redaction_applied = true;
  report.path_redaction_applied = true;

  const truncatedSections: string[] = redacted.truncated || bounded.truncated ? ["structural_bounds"] : [];
  const refreshText = (): string => {
    const truncated = truncatedSections.length > 0;
    report.report_bounds = {
      max_bytes: PROBLEM_REPORT_MAX_BYTES,
      truncated,
      ...(truncated ? { truncated_sections: truncatedSections } : {}),
    };
    return stringifyReport(report);
  };
  const recordTruncation = (section: string): void => {
    if (!truncatedSections.includes(section)) truncatedSections.push(section);
  };

  let text = refreshText();
  for (const path of PRUNABLE_PATHS) {
    if (Buffer.byteLength(text, "utf8") <= PROBLEM_REPORT_MAX_BYTES) break;
    if (replaceAtPath(report, path)) {
      recordTruncation(path.join("."));
      text = refreshText();
    }
  }

  if (Buffer.byteLength(text, "utf8") > PROBLEM_REPORT_MAX_BYTES) {
    for (const section of ["doctor", "snapshot"] as const) {
      if (Buffer.byteLength(text, "utf8") <= PROBLEM_REPORT_MAX_BYTES) break;
      if (Object.prototype.hasOwnProperty.call(report, section)) {
        report[section] = truncationMarker(report[section]);
        recordTruncation(section);
        text = refreshText();
      }
    }
  }

  if (Buffer.byteLength(text, "utf8") > PROBLEM_REPORT_MAX_BYTES) {
    const fixedKeys = new Set(["schema_version", "redaction_applied", "path_redaction_applied", "report_bounds"]);
    const candidates = Object.keys(report)
      .filter((key) => !fixedKeys.has(key) && !isTruncationMarker(report[key]))
      .sort((left, right) => Buffer.byteLength(JSON.stringify(report[right]) ?? "", "utf8") - Buffer.byteLength(JSON.stringify(report[left]) ?? "", "utf8"));
    for (const key of candidates) {
      if (Buffer.byteLength(text, "utf8") <= PROBLEM_REPORT_MAX_BYTES) break;
      report[key] = truncationMarker(report[key]);
      recordTruncation(key);
      text = refreshText();
    }
  }

  if (Buffer.byteLength(text, "utf8") > PROBLEM_REPORT_MAX_BYTES) {
    throw new Error("problem_report_size_limit_exceeded");
  }
  const truncated = truncatedSections.length > 0;
  return { report, text, bytes: Buffer.byteLength(text, "utf8"), truncated, truncatedSections };
}

export function redactProblemReport<T>(value: T): T {
  return redactValue(value, undefined, 0).value as T;
}

export function summarizeProblemCrashReporter(value: unknown): Record<string, unknown> {
  if (!isRecord(value)) return { status: "unavailable" };
  const recent = isRecord(value.recent_crash_artifacts) ? value.recent_crash_artifacts : {};
  const crashDirectories = arrayValue(recent.crash_directories);
  const bugReportJson = arrayValue(recent.bug_report_json);
  const bugReportTempDirectories = arrayValue(recent.bug_report_temp_directories);
  return {
    schema_version: value.schema_version,
    supported: value.supported,
    unity_crashed: value.unity_crashed,
    blocking_agent_work: value.blocking_agent_work,
    active_reporters_count: value.active_reporters_count,
    active_reporters: arrayValue(value.active_reporters).slice(0, 8),
    recent_crash_artifacts: {
      max_age_ms: recent.max_age_ms,
      crash_directories_count: crashDirectories.length,
      bug_report_json_count: bugReportJson.length,
      bug_report_temp_directories_count: bugReportTempDirectories.length,
    },
    errors: arrayValue(value.errors).slice(0, 8),
    next_valid_actions: arrayValue(value.next_valid_actions).slice(0, 16),
    detailed_artifacts_omitted: true,
  };
}

function redactValue(value: unknown, parentKey: string | undefined, depth: number): RedactedProblemReportValue {
  if (depth > MAX_DEPTH) return { value: { truncated: true, reason: "max_depth" }, truncated: true };
  if (Array.isArray(value)) {
    const items = value.map((item) => redactValue(item, parentKey, depth + 1));
    return { value: items.map((item) => item.value), truncated: items.some((item) => item.truncated) };
  }
  if (typeof value === "string") return redactString(value, parentKey, depth);
  if (!isRecord(value)) return { value, truncated: false };
  const redacted: Record<string, unknown> = {};
  let truncated = false;
  for (const [key, child] of Object.entries(value)) {
    const normalizedKey = normalizeKey(key);
    if (SECRET_KEY_PATTERN.test(normalizedKey)) {
      redacted[key] = "[REDACTED]";
      continue;
    }
    const redactedChild = redactValue(child, normalizedKey, depth + 1);
    redacted[key] = redactedChild.value;
    truncated ||= redactedChild.truncated;
  }
  return { value: redacted, truncated };
}

function redactString(value: string, parentKey: string | undefined, depth: number): RedactedProblemReportValue {
  if (parentKey && PATH_KEY_PATTERN.test(parentKey) && isUnsafePath(value)) return { value: "[REDACTED_PATH]", truncated: false };
  const trimmed = value.trim();
  if ((trimmed.startsWith("{") && trimmed.endsWith("}")) || (trimmed.startsWith("[") && trimmed.endsWith("]"))) {
    try {
      const redacted = redactValue(JSON.parse(trimmed), parentKey, depth + 1);
      return { value: JSON.stringify(redacted.value), truncated: redacted.truncated };
    } catch {
      // Fall through to bounded text redaction for malformed diagnostic JSON.
    }
  }
  return { value: redactText(value), truncated: false };
}

function redactText(value: string): string {
  return value
    .replace(/(authorization\s*[:=]\s*)(bearer\s+)?[^\s,;}]+/gi, "$1[REDACTED]")
    .replace(/\bbearer\s+[^\s,;}]+/gi, "Bearer [REDACTED]")
    .replace(/((?:token|api[_-]?key|password|secret|cookie)\s*[:=]\s*)[^\s,;}]+/gi, "$1[REDACTED]")
    .replace(ESCAPED_JSON_SECRET_PATTERN, "$1[REDACTED]$2")
    .replace(/\bfile:\/\/[^"'\r\n,;}\[]+/gi, "[REDACTED_PATH]")
    .replace(/\\\\[^\\\s"',;}]+\\[^"'\r\n,;}\[]+/g, "[REDACTED_PATH]")
    .replace(/\b[A-Za-z]:[\\/][^"'\r\n,;}\[]+/g, "[REDACTED_PATH]")
    .replace(/(^|[\s"',:])\/(?:mnt\/[A-Za-z]\/|home\/|Users\/|tmp\/|private\/tmp\/|var\/folders\/|Volumes\/)[^"'\r\n,;}\[]+/g, "$1[REDACTED_PATH]")
    .replace(/(^|[\s"',:])\/(?!\/)(?:[^/\s"',:;}]+\/)+[^\s"',:;}\[]+/g, "$1[REDACTED_PATH]");
}

function isUnsafePath(value: string): boolean {
  if (value === "[REDACTED_PATH]" || value.startsWith("gateway://") || SAFE_PROJECT_RELATIVE_PATH.test(value.replace(/\\/g, "/"))) return false;
  if (/^file:\/\//i.test(value)) return true;
  return /^(?:[A-Za-z]:[\\/]|\\\\|\/(?:mnt\/|home\/|Users\/|tmp\/|private\/tmp\/|var\/folders\/|Volumes\/))/.test(value)
    || ((value.includes("/") || value.includes("\\")) && !/^[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(value));
}

function boundProblemReportValue(value: unknown, depth = 0): { value: unknown; truncated: boolean } {
  if (depth > MAX_DEPTH) return { value: { truncated: true, reason: "max_depth" }, truncated: true };
  if (typeof value === "string") {
    return value.length <= MAX_STRING_CHARS
      ? { value, truncated: false }
      : { value: `${value.slice(0, MAX_STRING_CHARS)}[TRUNCATED]`, truncated: true };
  }
  if (Array.isArray(value)) {
    const boundedItems = value.slice(0, MAX_ARRAY_ITEMS).map((item) => boundProblemReportValue(item, depth + 1));
    const items = boundedItems.map((item) => item.value);
    const arrayTruncated = value.length > MAX_ARRAY_ITEMS;
    if (arrayTruncated) items.push({ truncated: true, omitted_items: value.length - MAX_ARRAY_ITEMS });
    return { value: items, truncated: arrayTruncated || boundedItems.some((item) => item.truncated) };
  }
  if (!isRecord(value)) return { value, truncated: false };
  const entries = Object.entries(value);
  const boundedEntries = entries.slice(0, MAX_ARRAY_ITEMS).map(([key, child]) => [key, boundProblemReportValue(child, depth + 1)] as const);
  const result = Object.fromEntries(boundedEntries.map(([key, child]) => [key, child.value]));
  const objectTruncated = entries.length > MAX_ARRAY_ITEMS;
  if (objectTruncated) result.__truncated__ = { truncated: true, omitted_properties: entries.length - MAX_ARRAY_ITEMS };
  return { value: result, truncated: objectTruncated || boundedEntries.some(([, child]) => child.truncated) };
}

function replaceAtPath(root: Record<string, unknown>, path: readonly string[]): boolean {
  let current: Record<string, unknown> = root;
  for (let index = 0; index < path.length - 1; index += 1) {
    const child = current[path[index] ?? ""];
    if (!isRecord(child)) return false;
    current = child;
  }
  const key = path[path.length - 1];
  if (!key || !Object.prototype.hasOwnProperty.call(current, key)) return false;
  current[key] = truncationMarker(current[key]);
  return true;
}

function truncationMarker(value: unknown): Record<string, unknown> {
  return {
    truncated: true,
    reason: "report_size_limit",
    ...(Array.isArray(value) ? { original_count: value.length } : {}),
  };
}

function isTruncationMarker(value: unknown): boolean {
  return isRecord(value) && value.truncated === true && value.reason === "report_size_limit";
}

function stringifyReport(report: Record<string, unknown>): string {
  return `${JSON.stringify(report, null, 2)}\n`;
}

function normalizeKey(key: string): string {
  return key.replace(/([a-z0-9])([A-Z])/g, "$1_$2").toLowerCase();
}

function jsonEscapedWordPattern(value: string): string {
  return [...value].map((character) => {
    const literal = character.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    return `(?:${literal}|\\\\u${character.charCodeAt(0).toString(16).padStart(4, "0")})`;
  }).join("");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function arrayValue(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}
