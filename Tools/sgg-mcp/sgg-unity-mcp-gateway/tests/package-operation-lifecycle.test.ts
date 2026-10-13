import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageCommandsPath = path.join(
  root,
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Editor",
  "Commands",
  "PackageManagementCommands.cs",
);

describe("Unity package operation lifecycle source-contract", () => {
  it("declares list and prune as Unity command catalog entries", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('"package.operation.list"');
    expect(source).toContain("public static CommandExecutionResult PackageOperationList(McpCommandContext context)");
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('"package.operation.prune"');
    expect(source).toContain("public static CommandExecutionResult PackageOperationPrune(McpCommandContext context)");
    expect(source).toContain('Risk = "destructive"');
    expect(source).toContain('Idempotency = "unsafe"');
  });

  it("keeps lifecycle schemas bounded and pathless", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain("OperationListSchemaJson");
    expect(source).toContain('\\"max_results\\":{\\"type\\":\\"integer\\",\\"minimum\\":1,\\"maximum\\":100}');
    expect(source).toContain('\\"include_completed\\":{\\"type\\":\\"boolean\\"}');
    expect(source).toContain('\\"include_running\\":{\\"type\\":\\"boolean\\"}');
    expect(source).toContain("OperationPruneSchemaJson");
    expect(source).toContain('\\"older_than_days\\":{\\"type\\":\\"integer\\",\\"minimum\\":0,\\"maximum\\":365}');
    expect(source).toContain('\\"max_delete\\":{\\"type\\":\\"integer\\",\\"minimum\\":1,\\"maximum\\":100}');
    expect(source).toContain('\\"dry_run\\":{\\"type\\":\\"boolean\\"}');
    expect(source).not.toContain('\\"root\\"');
    expect(source).not.toContain('\\"path\\"');
  });

  it("uses the fixed package operation root and skips unsafe filesystem entries", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('Path.Combine(ProjectPaths.ProjectRoot, "Library", "SGGUnityMcp", "package-operations")');
    expect(source).toContain('directory.GetFiles("*.json", SearchOption.TopDirectoryOnly)');
    expect(source).toContain("IsSafeOperationId(operationId)");
    expect(source).toContain("FileAttributes.ReparsePoint");
    expect(source).toContain("file.Length > 65536");
    expect(source).toContain("return TryReadSafeFile(file, out json)");
    expect(source).toContain("!pair.Value.Request.IsCompleted");
  });

  it("returns lifecycle diagnostics and next actions for live and persisted operations", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain("PackageOperationLongRunningMs = 10 * 60 * 1000");
    expect(source).toContain("PackageOperationStalePersistedMs = 2 * 60 * 1000");
    expect(source).toContain('AppendOperationDiagnosticsFields(builder, status, isCompleted, StartedUtc, DateTime.UtcNow, "memory")');
    expect(source).toContain("PersistedOperationStatusJson(persistedJson, file.CreationTimeUtc, file.LastWriteTimeUtc)");
    expect(source).toContain("OperationDiagnosticsJson(classification, source)");
    expect(source).toContain("PackageOperationNextActionsJson(classification, isCompleted)");
    expect(source).toContain('builder.Append(",\\"progress_supported\\":false")');
    expect(source).toContain('builder.Append(",\\"unity_package_manager_cancel_supported\\":false")');
    expect(source).toContain('"long_running"');
    expect(source).toContain('"persisted_running_after_reload"');
    expect(source).toContain('"stale_persisted_running"');
    expect(source).toContain("diagnostics.report_problem_if_stuck_persists");
    expect(source).toContain("IsInstallCompletionKind(kind)");
    expect(source).toContain('string.Equals(kind, "install_git", StringComparison.Ordinal)');
    expect(source).toContain('string.Equals(kind, "update", StringComparison.Ordinal)');
    expect(source).toContain("InstalledPackageSatisfiesOperation(kind, packageSpec, installedPackage)");
    expect(source).toContain("PackageSpecMatchesInstalledPackageId(packageSpec, installedPackage.packageId)");
    expect(source).toContain("inferred_from_current_package_list");
  });

  it("classifies UPM PackageCache EPERM rename failures from bounded logs", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain("UpmLogTailBytes = 64 * 1024");
    expect(source).toContain("UpmRenameEpermRegex");
    expect(source).toContain("UpmFailedRenameEpermRegex");
    expect(source).toContain("FindUpmPackageCacheRenameFailure()");
    expect(source).toContain("ReadFileTail(file, UpmLogTailBytes)");
    expect(source).toContain("FileShare.ReadWrite | FileShare.Delete");
    expect(source).toContain("upm_log_diagnostics");
    expect(source).toContain("upm_packagecache_rename_eperm");
    expect(source).toContain("error_code");
    expect(source).toContain("source_path");
    expect(source).toContain("target_path");
    expect(source).toContain("JsonStringOrNull(ProjectRelativeOrNull");
    expect(source).toContain("destructive_cleanup_allowed");
    expect(source).toContain("do_not_delete_packagecache_without_explicit_confirmation");
    expect(source).toContain("inspect_process_locks_for_packagecache_paths");
    expect(source).toContain("UpmFailureMatchesOperation(upmRenameFailure, packageName, packageSpec)");
    expect(source).toContain('PersistedOperationJson(operationId, kind, packageName, packageSpec, "failed", true');
    expect(source).toContain('return string.Equals(status, "failed", StringComparison.Ordinal) ? "failed" : "completed";');
  });

  it("prunes only completed persisted records with dry-run and max-delete bounds", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "older_than_days", 0), 0, 365)');
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_delete", 1), 1, HardOperationMaxResults)');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(source).toContain("files.Sort((left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc))");
    expect(source).toContain('JsonUtil.ExtractBool(json, "is_completed", false)');
    expect(source).toContain("File.Delete(candidates[index].FullName)");
    expect(source).toContain(',\\"would_delete\\":');
    expect(source).toContain(',\\"deleted\\":');
  });

  it("documents policy, docs, and unsupported cancellation", () => {
    const policyConfig = fs.readFileSync(path.join(root, "config", "policy.example.json"), "utf8");
    const policyProfiles = fs.readFileSync(path.join(root, "src", "policy", "policy-profiles.ts"), "utf8");
    const docs = fs.readFileSync(path.join(root, "docs", "en", "reference", "commands.md"), "utf8");
    const ruDocs = fs.readFileSync(path.join(root, "docs", "ru", "reference", "commands.md"), "utf8");
    const generatedDocs = fs.readFileSync(path.join(root, "docs", "commands.generated.md"), "utf8");
    const generatedRuDocs = fs.readFileSync(path.join(root, "docs", "ru", "commands.generated.ru.md"), "utf8");
    const troubleshooting = fs.readFileSync(path.join(root, "docs", "en", "troubleshooting.md"), "utf8");

    expect(policyConfig).toContain('"package.operation.list"');
    expect(policyConfig).toContain('"package.operation.prune"');
    expect(policyProfiles).toContain('"package.operation.list"');
    expect(policyProfiles).toContain('"package.operation.prune"');
    expect(generatedDocs).toContain("`package.operation.list` | Implemented | none");
    expect(generatedDocs).toContain("`package.operation.prune` | Implemented | none");
    expect(docs).toContain("Unity's PackageManager `Request` type does not provide a stable cancel API");
    expect(generatedRuDocs).toContain("`package.operation.list` | Implemented | none");
    expect(generatedRuDocs).toContain("`package.operation.prune` | Implemented | none");
    expect(ruDocs).toContain("stable cancel API");
    expect(troubleshooting).toContain("PackageManager operation cancellation is unsupported");
    expect(troubleshooting).toContain("stale_persisted_running");
    expect(docs).toContain("operation_diagnostics");
    expect(docs).toContain("next_valid_actions");
    expect(troubleshooting).toContain("PackageManager cancellation is unsupported");
  });
});
