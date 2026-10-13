export const COMPILE_READINESS_QUALIFICATION_CONTRACT = "sgg.unity_mcp.compile_readiness_qualification.v1";

const qualification = Object.freeze({
  schema_version: 1,
  contract: COMPILE_READINESS_QUALIFICATION_CONTRACT,
  readiness_scope: "editor_package_lifecycle_script_evidence_and_background_progress",
  compile_diagnostics_scope: "script_compiler_and_console_cs_error_marker",
  shader_asset_readiness: "unknown",
  console_history_complete: false,
  console_history_durable: false,
  explicit_compilation_evidence: "inspect_compile_barrier_not_inferred_from_ready_clean",
});

/** Source-authored scope qualification only, never a readiness/admission input.
 * Missing legacy metadata stays missing; no positive evidence is synthesized. */
export function safeCompileReadinessQualification(value: unknown): Readonly<Record<string, unknown>> | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  const prototype = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) return undefined;
  const keys = Reflect.ownKeys(value);
  const expected = Object.entries(qualification);
  if (keys.length !== expected.length) return undefined;
  for (const [key, literal] of expected) {
    const property = Object.getOwnPropertyDescriptor(value, key);
    if (!property?.enumerable || !("value" in property) || property.value !== literal) return undefined;
  }
  return qualification;
}
