const MAX_EVIDENCE_INTEGER = 2_147_483_647;
const COUNT_KEYS = ["total", "passed", "failed", "skipped", "pass_count", "fail_count", "skip_count", "inconclusive_count", "authoritative_leaf_total"];

/** Extracted Bridge DTO validator; terminal strings alone are not execution evidence. */
export function isValidObservedTestRunDto(data: Record<string, unknown>): boolean {
  const pending = new Set(["running", "execution_may_have_started", "awaiting_filter_revalidation_after_reload"]);
  const terminal = new Set(["completed", "failed", "cancelled", "timeout", "pre_execute_refused", "callback_lost_after_playmode_exit",
    "test_operation_replaced_after_reload", "test_execution_receipt_unavailable_after_reload", "test_filter_drift_after_reload",
    "test_filter_identity_unavailable_after_reload", "test_operation_result_identity_unavailable", "test_operation_result_identity_mismatch"]);
  if (typeof data.status !== "string" || !pending.has(data.status) && !terminal.has(data.status)) return false;
  if (Object.hasOwn(data, "terminal") && data.terminal !== terminal.has(data.status)) return false;
  if (Object.hasOwn(data, "success") && typeof data.success !== "boolean") return false;
  for (const key of ["operation_created", "execute_dispatch_committed", "test_runner_execute_invocation_attempted", "test_runner_execute_invoked"])
    if (Object.hasOwn(data, key) && typeof data[key] !== "boolean") return false;
  if (Object.hasOwn(data, "execute_dispatch_count") && !boundedInteger(data.execute_dispatch_count)) return false;
  if (Object.hasOwn(data, "terminal_counts_authoritative") && typeof data.terminal_counts_authoritative !== "boolean") return false;
  if (Object.hasOwn(data, "counts") && !isRecord(data.counts)) return false;
  const nested = isRecord(data.counts) ? data.counts : {};
  if (Object.hasOwn(data, "terminal_count_source") && data.terminal_count_source !== "unity_test_runner" && data.terminal_count_source !== "unavailable") return false;
  if (Object.hasOwn(nested, "source") && nested.source !== "unity_test_runner" && nested.source !== "unavailable") return false;
  if (Object.hasOwn(data, "terminal_count_source") && Object.hasOwn(nested, "source") && data.terminal_count_source !== nested.source) return false;
  if (Object.hasOwn(nested, "authoritative") && typeof nested.authoritative !== "boolean") return false;
  if (typeof data.terminal_counts_authoritative === "boolean" && typeof nested.authoritative === "boolean" && data.terminal_counts_authoritative !== nested.authoritative) return false;
  for (const source of [data, nested]) for (const key of COUNT_KEYS) if (Object.hasOwn(source, key) && !boundedInteger(source[key])) return false;
  for (const key of COUNT_KEYS) if (Object.hasOwn(data, key) && Object.hasOwn(nested, key) && data[key] !== nested[key]) return false;
  const counts = testRunCountEvidence(data);
  if (counts.authoritative !== true) return data.success !== true;
  if (!terminal.has(data.status) || counts.source !== "unity_test_runner") return false;
  const pass = counts.pass_count, fail = counts.fail_count, skip = counts.skip_count, inconclusive = counts.inconclusive_count, leaf = counts.authoritative_leaf_total;
  if ([pass, fail, skip, inconclusive, leaf].some(value => typeof value !== "number")) return false;
  const sum = (pass as number) + (fail as number) + (skip as number) + (inconclusive as number);
  if (sum !== leaf || sum > MAX_EVIDENCE_INTEGER) return false;
  for (const [key, expected] of [["total", leaf], ["passed", pass], ["failed", fail], ["skipped", (skip as number) + (inconclusive as number)]] as const)
    if (Object.hasOwn(counts, key) && counts[key] !== expected) return false;
  return data.success !== true || data.status === "completed" && sum > 0 && fail === 0 && inconclusive === 0;
}

export function hasAuthoritativeClosedTestRunOutcome(data: Record<string, unknown>): boolean {
  // Cancellation is a requested/observed outcome, not proof that TestRunner execution ceased.
  if (typeof data.status !== "string" || !["completed", "failed"].includes(data.status) || !isValidObservedTestRunDto(data)) return false;
  const counts = testRunCountEvidence(data);
  return counts.authoritative === true && counts.source === "unity_test_runner";
}
function testRunCountEvidence(data: Record<string, unknown>): Record<string, unknown> {
  const counts: Record<string, unknown> = isRecord(data.counts) ? { ...data.counts } : {};
  if (Object.hasOwn(data, "terminal_counts_authoritative")) counts.authoritative = data.terminal_counts_authoritative;
  if (Object.hasOwn(data, "terminal_count_source")) counts.source = data.terminal_count_source;
  for (const key of COUNT_KEYS) if (Object.hasOwn(data, key)) counts[key] = data[key];
  return counts;
}
function boundedInteger(value: unknown): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= MAX_EVIDENCE_INTEGER; }
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
