const EVIDENCE_FIELDS = ["non_passing_evidence_status", "non_passing_tests_truncated", "non_passing_tests"] as const;
const EVIDENCE_STATUSES = new Set(["complete", "partial", "truncated", "unavailable"]);
const NON_PASSING_HEADS = new Set(["Failed", "Failure", "Error", "Inconclusive", "Skipped", "Ignored"]);
const MAX_SOURCE_ENTRIES = 64;
const MAX_DIAGNOSTICS = 4;

// Additive display projection only. Source coverage and display omissions are
// separate witnesses; neither changes aggregate outcomes or permits execution.
export function compactNonPassingEvidence(data: Record<string, unknown>): Record<string, unknown> {
  if (!EVIDENCE_FIELDS.some((field) => Object.hasOwn(data, field))) return {};

  const sourceStatus = ownValue(data, "non_passing_evidence_status");
  const status = typeof sourceStatus === "string" && sourceStatus.length <= 11 && EVIDENCE_STATUSES.has(sourceStatus) ? sourceStatus : "unavailable";
  const sourceTruncated = ownValue(data, "non_passing_tests_truncated");
  const sourceEntries = ownValue(data, "non_passing_tests");
  // Server waiting may receive an already projected status from its internal
  // read, then project again at the outer delivery boundary. Preserve only
  // validated display fields; do not invent exact identities from clipped text.
  if (!Object.hasOwn(data, "non_passing_tests") && ownValue(data, "response_mode") === "compact"
    && Object.hasOwn(data, "non_passing_diagnostics")) {
    return reprojectDisplay(data, status, sourceStatus, sourceTruncated);
  }
  let displayTruncated = status !== sourceStatus || status === "truncated" || sourceTruncated === true
    || typeof sourceTruncated !== "boolean" || !Array.isArray(sourceEntries);
  const diagnostics: Array<Record<string, unknown>> = [];
  // Keep only the keys of retained diagnostics. Rejected fifth records cannot
  // create an unbounded auxiliary deduplication collection.
  const retainedKeys = new Set<string>();
  if (Array.isArray(sourceEntries)) {
    if (sourceEntries.length > MAX_SOURCE_ENTRIES) displayTruncated = true;
    for (let index = 0; index < Math.min(sourceEntries.length, MAX_SOURCE_ENTRIES); index++) {
      const row: unknown = sourceEntries[index];
      if (!isRecord(row)) { displayTruncated = true; continue; }
      const full = exactOptionalText(row, "full_name", 2048);
      const unique = exactOptionalText(row, "unique_name", 2048);
      const assembly = exactOptionalText(row, "assembly", 256, false);
      const rawStatus = ownValue(row, "raw_status");
      if (full === undefined || unique === undefined || assembly === undefined
        || (!full.trim() && !unique.trim()) || typeof rawStatus !== "string"
        || rawStatus.length === 0 || rawStatus.length > 120 || !validUtf16(rawStatus)
        || !NON_PASSING_HEADS.has(rawStatus.split(":", 1)[0]!)) {
        displayTruncated = true;
        continue;
      }
      const key = JSON.stringify([assembly, full, unique, rawStatus]);
      if (retainedKeys.has(key)) continue;
      if (diagnostics.length >= MAX_DIAGNOSTICS) { displayTruncated = true; continue; }
      const preferred = full.trim() ? full : unique;
      const test = clippedText(preferred, 160);
      const diagnostic: Record<string, unknown> = { test: test.value, raw_status: rawStatus };
      displayTruncated ||= test.truncated;
      if (full.length > 0 && !full.trim()) displayTruncated = true;
      if (unique.length > 0 && !unique.trim()) displayTruncated = true;
      if (unique.trim() && unique !== preferred) {
        const text = clippedText(unique, 160);
        diagnostic.unique_name = text.value;
        displayTruncated ||= text.truncated;
      }
      if (assembly.length > 0) {
        const text = clippedText(assembly, 160);
        diagnostic.assembly = text.value;
        displayTruncated ||= text.truncated;
      }
      if (Object.hasOwn(row, "message")) {
        const message = ownValue(row, "message");
        if (typeof message === "string") {
          const text = clippedText(message, 240);
          if (text.value !== undefined) diagnostic.message = text.value;
          displayTruncated ||= text.truncated;
        } else displayTruncated = true;
      }
      retainedKeys.add(key);
      diagnostics.push(diagnostic);
    }
  }

  const compact: Record<string, unknown> = {
    non_passing_evidence_status: status,
    non_passing_diagnostics: diagnostics,
    non_passing_diagnostics_truncated: displayTruncated,
  };
  if (typeof sourceTruncated === "boolean") compact.non_passing_tests_truncated = sourceTruncated;
  return compact;
}

function reprojectDisplay(data: Record<string, unknown>, status: string, sourceStatus: unknown, sourceTruncated: unknown): Record<string, unknown> {
  const rows = ownValue(data, "non_passing_diagnostics");
  const priorDisplayTruncated = ownValue(data, "non_passing_diagnostics_truncated");
  let truncated = priorDisplayTruncated !== false || sourceTruncated === true || typeof sourceTruncated !== "boolean"
    || status !== sourceStatus || status === "truncated" || !Array.isArray(rows);
  const diagnostics: Array<Record<string, unknown>> = [];
  if (Array.isArray(rows)) {
    if (rows.length > MAX_DIAGNOSTICS) truncated = true;
    for (let index = 0; index < Math.min(rows.length, MAX_DIAGNOSTICS); index++) {
      const row: unknown = rows[index];
      if (!isRecord(row)) { truncated = true; continue; }
      const test = ownValue(row, "test");
      const rawStatus = ownValue(row, "raw_status");
      if (typeof test !== "string" || test.length > 160 || !test.trim() || !validUtf16(test)
        || typeof rawStatus !== "string" || rawStatus.length > 120 || !validUtf16(rawStatus)
        || !NON_PASSING_HEADS.has(rawStatus.split(":", 1)[0]!)) { truncated = true; continue; }
      const diagnostic: Record<string, unknown> = { test, raw_status: rawStatus };
      let invalidIdentity = false;
      for (const field of ["unique_name", "assembly"] as const) {
        if (!Object.hasOwn(row, field)) continue;
        const value = ownValue(row, field);
        if (typeof value !== "string" || value.length > 160 || !validUtf16(value)) { invalidIdentity = true; break; }
        diagnostic[field] = value;
      }
      if (invalidIdentity) { truncated = true; continue; }
      if (Object.hasOwn(row, "message")) {
        const value = ownValue(row, "message");
        if (typeof value === "string") {
          const text = clippedText(value, 240);
          if (text.value !== undefined) diagnostic.message = text.value;
          truncated ||= text.truncated;
        } else truncated = true;
      }
      // Distinct exact source identities can share the same display prefix.
      // Never deduplicate this already-projected presentation by clipped text.
      diagnostics.push(diagnostic);
    }
  }
  const compact: Record<string, unknown> = {
    non_passing_evidence_status: status,
    non_passing_diagnostics: diagnostics,
    non_passing_diagnostics_truncated: truncated,
  };
  if (typeof sourceTruncated === "boolean") compact.non_passing_tests_truncated = sourceTruncated;
  return compact;
}

function ownValue(record: Record<string, unknown>, field: string): unknown {
  return Object.hasOwn(record, field) ? record[field] : undefined;
}

function exactOptionalText(record: Record<string, unknown>, field: string, limit: number, undefinedIsAbsent = true): string | undefined {
  const value = ownValue(record, field);
  if (value === undefined) return undefinedIsAbsent || !Object.hasOwn(record, field) ? "" : undefined;
  return typeof value === "string" && value.length <= limit && validUtf16(value) ? value : undefined;
}

function clippedText(value: string, limit: number): { value: string | undefined; truncated: boolean } {
  let end = Math.min(value.length, limit);
  if (end < value.length && end > 0 && isHighSurrogate(value.charCodeAt(end - 1))) end--;
  const prefix = value.slice(0, end);
  // Only inspect the bounded retained prefix of diagnostic text. A discarded
  // tail cannot expand projection work or silently repair an exact identity.
  if (!validUtf16(prefix)) return { value: undefined, truncated: true };
  return { value: prefix, truncated: end < value.length };
}

function validUtf16(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (isHighSurrogate(code)) {
      const next = value.charCodeAt(++index);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
  }
  return true;
}

function isHighSurrogate(code: number): boolean { return code >= 0xd800 && code <= 0xdbff; }
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
