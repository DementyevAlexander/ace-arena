import { describe, expect, it } from "vitest";

import { PROBLEM_REPORT_MAX_BYTES, redactProblemReport, serializeProblemReport, summarizeProblemCrashReporter } from "../src/diagnostics/problem-report.js";

describe("problem report serialization", () => {
  it("redacts host paths and secrets in structured and JSON-string evidence", () => {
    const windowsPath = "C:\\Users\\Private User\\Project\\Editor.log";
    const wslPath = "/home/private user/project/audit.ndjson";
    const genericPosixPath = "/opt/private-project/logs/editor.log";
    const report = serializeProblemReport({
      report_id: "problem_test",
      configPath: windowsPath,
      recent_artifact_refs: [windowsPath, "Library/SGGUnityMcp/job-artifacts/test.json", "gateway://problem-reports/test.json"],
      startup_log_diagnostics: { probes: [{ path: wslPath }] },
      audit_tail: { lines: [JSON.stringify({ path: windowsPath, nested: { fs_path: wslPath }, authorization: "Bearer secret-value" })] },
      message: `failed at ${genericPosixPath}`,
      file_uri_message: "failed at file:///home/private user/project/Editor.log",
      authority_file_uri_message: "failed at file://localhost/home/private user/project/Editor.log",
      bearer_message: "Bearer standalone-secret",
      endpoint: "http://127.0.0.1:27182/mcp",
    });

    expect(report.report).toMatchObject({
      schema_version: 2,
      configPath: "[REDACTED_PATH]",
      recent_artifact_refs: ["[REDACTED_PATH]", "Library/SGGUnityMcp/job-artifacts/test.json", "gateway://problem-reports/test.json"],
      startup_log_diagnostics: { probes: [{ path: "[REDACTED_PATH]" }] },
      redaction_applied: true,
      path_redaction_applied: true,
    });
    for (const privateValue of [windowsPath, wslPath, genericPosixPath, "Private User", "private user", "secret-value", "standalone-secret", "file:///home", "file://localhost"]) {
      expect(report.text).not.toContain(privateValue);
    }
    expect(report.text).toContain("[REDACTED_PATH]");
    expect(report.text).toContain("[REDACTED]");
    expect(report.text).toContain("http://127.0.0.1:27182/mcp");
  });

  it("redacts escaped secret keys before bounding oversized JSON strings", () => {
    const encodedSecret = "escaped-json-secret";
    const oversizedJson = `{"to\\u006ben":"${encodedSecret}","padding":"${"x".repeat(70 * 1024)}"}`;
    const report = serializeProblemReport({ report_id: "problem_escaped_json", evidence: oversizedJson });

    expect(report.text).not.toContain(encodedSecret);
    expect(report.text).toContain("[REDACTED]");
    expect(report.report.report_bounds).toMatchObject({ truncated: true, truncated_sections: ["structural_bounds"] });
  });

  it("redacts escaped secret keys in malformed JSON text", () => {
    const report = serializeProblemReport({
      complete_value: "{\"to\\u006ben\":\"malformed-json-secret\",",
      unclosed_value: "{\"to\\u006ben\":\"unfinished-json-secret",
      camel_api_key: "{\"api\\u004bey\":\"unfinished-api-key-secret",
    });

    expect(report.text).not.toContain("malformed-json-secret");
    expect(report.text).not.toContain("unfinished-json-secret");
    expect(report.text).not.toContain("unfinished-api-key-secret");
    expect(report.text).toContain("[REDACTED]");
  });

  it("reports max-depth truncation nested inside JSON-string evidence", () => {
    let nested: Record<string, unknown> = { value: "leaf" };
    for (let index = 0; index < 20; index += 1) nested = { nested };
    const report = serializeProblemReport({ evidence: JSON.stringify(nested) });

    expect(report.truncated).toBe(true);
    expect(report.report.report_bounds).toMatchObject({ truncated: true, truncated_sections: ["structural_bounds"] });
  });

  it("does not treat caller-provided max-depth marker text as serializer truncation", () => {
    const report = serializeProblemReport({ evidence: "{\"truncated\":true,\"reason\":\"max_depth\"}" });

    expect(report.truncated).toBe(false);
    expect(report.report.report_bounds).toMatchObject({ truncated: false });
  });

  it("bounds oversized reports with explicit section truncation", () => {
    const report = serializeProblemReport({
      report_id: "problem_large",
      snapshot: {
        audit_tail: { lines: Array.from({ length: 128 }, (_, index) => `${index}:${"x".repeat(8192)}`) },
        bridge_reload_trace_tail: { lines: Array.from({ length: 128 }, (_, index) => `${index}:${"y".repeat(8192)}`) },
      },
    });

    expect(report.bytes).toBeLessThanOrEqual(PROBLEM_REPORT_MAX_BYTES);
    expect(report.truncated).toBe(true);
    expect(report.report.report_bounds).toMatchObject({ max_bytes: PROBLEM_REPORT_MAX_BYTES, truncated: true });
    expect(report.truncatedSections.length).toBeGreaterThan(0);
  });

  it("falls back to bounded top-level sections when no named section is prunable", () => {
    const report = serializeProblemReport(Object.fromEntries(Array.from({ length: 8 }, (_, index) => [`evidence_${index}`, "x".repeat(64 * 1024)])));

    expect(report.bytes).toBeLessThanOrEqual(PROBLEM_REPORT_MAX_BYTES);
    expect(report.truncated).toBe(true);
    expect(report.truncatedSections).toEqual(expect.arrayContaining(["evidence_0"]));
  });

  it("summarizes crash artifacts without persisting their paths", () => {
    const summary = summarizeProblemCrashReporter({
      schema_version: 1,
      supported: true,
      unity_crashed: true,
      blocking_agent_work: false,
      active_reporters_count: 0,
      active_reporters: [],
      recent_crash_artifacts: {
        max_age_ms: 1000,
        crash_directories: [{ path: "C:/private/crash" }],
        bug_report_json: [{ path: "/tmp/private/report.json" }],
        bug_report_temp_directories: [],
      },
      temp_roots: [{ path: "C:/private/temp" }],
      errors: [],
      next_valid_actions: ["inspect_unity_editor_crash_artifacts"],
    });

    expect(summary).toMatchObject({
      recent_crash_artifacts: { crash_directories_count: 1, bug_report_json_count: 1, bug_report_temp_directories_count: 0 },
      detailed_artifacts_omitted: true,
    });
    expect(JSON.stringify(summary)).not.toContain("C:/private");
    expect(JSON.stringify(summary)).not.toContain("/tmp/private");
  });

  it("redacts a standalone problem-report value without serialization", () => {
    expect(redactProblemReport({ root: "C:/private/project", artifact_ref: "Library/output.json" })).toEqual({
      root: "[REDACTED_PATH]",
      artifact_ref: "Library/output.json",
    });
  });
});
