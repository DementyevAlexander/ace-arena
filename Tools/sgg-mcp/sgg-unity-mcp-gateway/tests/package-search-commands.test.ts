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
const builtInCommandsPath = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp", "Editor", "Commands", "BuiltInCommands.cs");
const metadataInspectorPath = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp", "Editor", "Bridge", "LocalPackageMetadataInspector.cs");

describe("Unity package registry command source-contract", () => {
  it("declares installed-only package.info for Unity PackageManager data parity", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");
    const start = source.indexOf('"package.info"');
    const end = source.indexOf('[McpCommand(\n            "package.install"', start);
    const section = source.slice(start, end);

    expect(section).toContain('"package.info"');
    expect(section).toContain("Unity package metadata and sample status without registry/network access");
    expect(section).toContain('Risk = "read"');
    expect(section).toContain('Idempotency = "safe"');
    expect(section).toContain('Capability = Capability');
    expect(section).toContain('ResultShape = "package_info"');
    expect(section).toContain('Tags = new string[] { "package", "unity-ai-assistant" }');
    expect(section).toContain("PackageInfoListJson(packageName, maxResults, includeSamples)");
    expect(section).not.toContain("Client.Search");
    expect(section).not.toContain("Client.SearchAll");
    expect(section).not.toContain("Client.Add");
    expect(section).not.toContain("Client.Remove");
  });

  it("bounds package.info query results and sample metadata", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('const string InfoSchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('\\"package_name\\"');
    expect(source).toContain('\\"maxLength\\":128');
    expect(source).toContain('\\"include_samples\\"');
    expect(source).toContain('\\"max_results\\"');
    expect(source).toContain('\\"maximum\\":200');
    expect(source).toContain('const string InfoOutputLimitJson = "{\\"max_results\\":200,\\"max_samples_per_package\\":25,\\"max_description_chars\\":1000}"');
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(argsJson, "max_results", DefaultInfoMaxResults), 1, HardInfoMaxResults)');
    expect(source).toContain("IsSafePackageInfoQuery(packageName)");
    expect(source).toContain("PackageManagerPackageInfo.GetAllRegisteredPackages()");
    expect(source).toContain("FindPackageSamples(package.name, package.version)");
    expect(source).toContain('const string sampleTypeName = "UnityEditor.PackageManager.UI.Sample"');
    expect(source).toContain('GetMethod(\n                "FindByPackage"');
    expect(source).not.toContain("using UnityEditor.PackageManager.UI");
    expect(source).not.toContain("Sample.FindByPackage");
    expect(source).toContain("samples_truncated");
    expect(source).toContain("Unity.PackageManager.GetData");
    expect(source).not.toContain("Client.SearchAll()");
    expect(source).not.toContain("Sample.Import");
  });

  it("distinguishes cached UPM versions from bounded local package manifest metadata", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");
    const builtInSource = fs.readFileSync(builtInCommandsPath, "utf8");
    const inspector = fs.readFileSync(metadataInspectorPath, "utf8");

    expect(source).toContain("LocalPackageMetadataInspector.InspectPackages(returnedPackages, matches.Count > count)");
    expect(source).toContain("LocalPackageMetadataInspector.AppendPackageJsonFields");
    expect(builtInSource).toContain("LocalPackageMetadataInspector.InspectPackages(packages, false)");
    expect(builtInSource).toContain("LocalPackageMetadataInspector.AppendPackageJsonFields");
    expect(inspector).toContain("const int MaxManifestBytes = 64 * 1024");
    expect(inspector).toContain("const int MaxTotalManifestBytes = 512 * 1024");
    expect(inspector).toContain("const int MaxInspectedLocalPackages = 64");
    expect(inspector).toContain('Path.Combine(root, "package.json")');
    expect(inspector).toContain('PackageSource.Local.ToString()');
    expect(inspector).toContain('PackageSource.Embedded.ToString()');
    expect(inspector).toContain("FileAttributes.ReparsePoint");
    expect(inspector).toContain("DriveType.Network");
    expect(inspector).toContain('"stale_package_resolution"');
    expect(inspector).toContain('builder.Append(",\\"version_source\\":\\"unity_package_manager\\"")');
    expect(inspector).toContain('builder.Append(",\\"local_manifest_version\\":")');
    expect(inspector).not.toContain("Client.Resolve");
  });

  it("declares package.search, package.update and package.install.git as Unity command catalog entries", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('"package.install.git"');
    expect(source).toContain("public static CommandExecutionResult PackageInstallGit(McpCommandContext context)");
    expect(source).toContain("RequireHttpsGitPackageSpec(context.ArgsJson, out var packageSpec, out var packageName)");
    expect(source).toContain('RegisterOperation("install_git", packageName, packageSpec, request');
    expect(source).toContain('"package.search"');
    expect(source).toContain("public static CommandExecutionResult PackageSearch(McpCommandContext context)");
    expect(source).toContain("Client.Search(query)");
    expect(source).toContain('RegisterOperation("search", query, query, request');
    expect(source).toContain('"package.update"');
    expect(source).toContain("public static CommandExecutionResult PackageUpdate(McpCommandContext context)");
    expect(source).toContain("Client.Add(packageSpec)");
    expect(source).toContain('RegisterOperation("update", packageName, packageSpec, request');
  });

  it("marks package.search as network risk, safe, and feature-gated", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('Risk = "network"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('FeatureFlag = "package_registry_network"');
    expect(source).toContain("ResultShape = ResultShape");
  });

  it("requires a bounded query and bounded max_results", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('const string SearchSchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('\\"required\\":[\\"query\\"]');
    expect(source).toContain('\\"minLength\\":2');
    expect(source).toContain('\\"maxLength\\":128');
    expect(source).toContain('\\"minimum\\":1');
    expect(source).toContain('\\"maximum\\":100');
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(argsJson, "max_results", DefaultSearchMaxResults), 1, HardSearchMaxResults)');
  });

  it("requires explicit installed package update versions and rejects URL-like specs", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('const string UpdateSchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('\\"required\\":[\\"package_name\\",\\"version\\"]');
    expect(source).toContain("RequireRegistryPackageVersion(context.ArgsJson, out var packageName, out var version, out var packageSpec)");
    expect(source).toContain("TryGetInstalledPackage(packageName, out var installedPackage)");
    expect(source).toContain('string.Equals(installedPackage.source.ToString(), "Registry", StringComparison.Ordinal)');
    expect(source).toContain("packageSpec = packageName + \"@\" + version");
    expect(source).toContain("IsSafePackageVersion(version)");
    expect(source).toContain('lower.Contains("://")');
    expect(source).toContain('lower.StartsWith("git+", StringComparison.Ordinal)');
    expect(source).toContain('lower.EndsWith(".git", StringComparison.Ordinal)');
  });

  it("requires bounded HTTPS Git package specs without weakening local package install", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('const string InstallGitSchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('\\"required\\":[\\"git_url\\"]');
    expect(source).toContain("MaxPackageSpecLength = 1024");
    expect(source).toContain('packageSpec.StartsWith("git+https://", StringComparison.Ordinal)');
    expect(source).toContain('uriCandidate.StartsWith("https://", StringComparison.Ordinal)');
    expect(source).toContain("!string.IsNullOrEmpty(uri.UserInfo)");
    expect(source).toContain('uri.AbsolutePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('query.StartsWith("?path=/", StringComparison.Ordinal)');
    expect(source).toContain("IsSafeGitRevision(uri.Fragment.Substring(1))");
    expect(source).toContain("GitPackageNameFromUri(uri)");
  });

  it("rejects empty, URL, path, and git-like registry search queries", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain('CommandExecutionResult.Error("schema_validation_failed", "Argument query is required")');
    expect(source).toContain("IsSafeRegistrySearchQuery(query)");
    expect(source).toContain('lower.Contains("://")');
    expect(source).toContain('lower.StartsWith("git+", StringComparison.Ordinal)');
    expect(source).toContain('lower.EndsWith(".git", StringComparison.Ordinal)');
    expect(source).toContain("character == '/'");
    expect(source).toContain("character == '\\\\'");
    expect(source).toContain("character == ':'");
    expect(source).not.toContain("Client.Add(query)");
  });

  it("returns bounded search results with truncation metadata", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain("SearchResultsJson(request.Result, maxResults)");
    expect(source).toContain("builder.Append(\"{\\\"max_results\\\":\").Append(maxResults)");
    expect(source).toContain("builder.Append(\",\\\"truncated\\\":\").Append(JsonUtil.Bool(total > count))");
    expect(source).toContain("builder.Append(\",\\\"results\\\":[\")");
    expect(source).toContain("PackageInfoJson(packages[index])");
    expect(source).toContain('\\"display_name\\"');
    expect(source).toContain('\\"source\\"');
  });

  it("does not weaken package.install local-folder-only validation", () => {
    const source = fs.readFileSync(packageCommandsPath, "utf8");

    expect(source).toContain("RequireLocalPackageSpec(context.ArgsJson, out var packageSpec, out var packageName)");
    expect(source).toContain('normalized.StartsWith("/", StringComparison.Ordinal) || IsDriveAbsolute(normalized)');
    expect(source).toContain("normalized.IndexOf(':') >= 0");
    expect(source).toContain("HasTraversal(normalized)");
    expect(source).toContain('CommandExecutionResult.Error("path_traversal_rejected", "package_path must stay under Packages/")');
    expect(source).toContain('packageSpec = "file:" + normalized');
  });

  it("documents and configures package registry network search explicitly", () => {
    const gatewayConfig = fs.readFileSync(path.join(root, "config", "gateway.example.json"), "utf8");
    const policyConfig = fs.readFileSync(path.join(root, "config", "policy.example.json"), "utf8");
    const docs = fs.readFileSync(path.join(root, "docs", "en", "reference", "commands.md"), "utf8");
    const ruDocs = fs.readFileSync(path.join(root, "docs", "ru", "reference", "commands.md"), "utf8");
    const generatedDocs = fs.readFileSync(path.join(root, "docs", "commands.generated.md"), "utf8");
    const generatedRuDocs = fs.readFileSync(path.join(root, "docs", "ru", "commands.generated.ru.md"), "utf8");

    expect(gatewayConfig).toContain('"package_registry_network": false');
    expect(policyConfig).toContain('"package.install.git"');
    expect(policyConfig).toContain('"package.info"');
    expect(policyConfig).toContain('"package.search"');
    expect(policyConfig).toContain('"package.update"');
    expect(generatedDocs).toContain("`package.install.git` | Feature-gated | `package_registry_network`");
    expect(generatedDocs).toContain("`package.info` | Implemented | none");
    expect(docs).toContain("`package.info` is the installed-only local parity command for Unity AI Assistant `Unity.PackageManager.GetData`");
    expect(generatedDocs).toContain("`package.search` | Feature-gated | `package_registry_network`");
    expect(generatedDocs).toContain("`package.update` | Feature-gated | `package_registry_network`");
    expect(docs).toContain("`package.install.git` is network-risk");
    expect(generatedRuDocs).toContain("`package.install.git` | Feature-gated | `package_registry_network`");
    expect(generatedRuDocs).toContain("`package.info` | Implemented | none");
    expect(ruDocs).toContain("`package.info` — installed-only local parity command для Unity AI Assistant `Unity.PackageManager.GetData`");
    expect(generatedRuDocs).toContain("`package.search` | Feature-gated | `package_registry_network`");
    expect(generatedRuDocs).toContain("`package.update` | Feature-gated | `package_registry_network`");
    expect(ruDocs).toContain("`package.install.git` имеет network risk");
  });
});
