import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "AddressablesReadonlyCommands.cs");
const metaPath = `${commandPath}.meta`;
const contractsPath = path.join(process.cwd(), "src", "mcp", "contracts.ts");
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const securityDocsPath = path.join(process.cwd(), "docs", "en", "reference", "security.md");

describe("Addressables read-only command source-contract", () => {
  it("declares read-only feature-gated Addressables commands", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const meta = fs.readFileSync(metaPath, "utf8");

    expect(source).toContain('"addressables.status"');
    expect(source).toContain('"addressables.groups.list"');
    expect(source).toContain('"addressables.entries.search"');
    expect(source).toContain('"addressables.labels.report"');
    expect(source).toContain('"addressables.dependencies.report"');
    expect(source).toContain('"addressables.build.preflight"');
    expect(source).toContain('const string FeatureFlag = "addressables_readonly"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(6);
    expect(source.match(/Idempotency = "safe"/g)).toHaveLength(6);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(6);
    expect(source).toContain('ResultShape = "object_details"');
    expect(source).toContain('ResultShape = "bounded_list"');
    expect(source).toContain('SupportsJob = true');
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("bounds status, group list, and entry search schemas", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("StatusSchemaJson");
    expect(source).toContain("GroupsListSchemaJson");
    expect(source).toContain("EntriesSearchSchemaJson");
    expect(source).toContain("LabelsReportSchemaJson");
    expect(source).toContain("DependenciesReportSchemaJson");
    expect(source).toContain("BuildPreflightSchemaJson");
    expect(source).toContain("StatusOutputLimitJson");
    expect(source).toContain("GroupsListOutputLimitJson");
    expect(source).toContain("EntriesSearchOutputLimitJson");
    expect(source).toContain("LabelsReportOutputLimitJson");
    expect(source).toContain("DependenciesReportOutputLimitJson");
    expect(source).toContain("BuildPreflightOutputLimitJson");
    expect(source).toContain('\\"max_scan_assets\\"');
    expect(source).toContain('\\"max_scan_groups\\"');
    expect(source).toContain('\\"max_entries_per_group\\"');
    expect(source).toContain('\\"max_entry_samples_per_label\\"');
    expect(source).toContain('\\"max_dependencies_per_entry\\"');
    expect(source).toContain('\\"max_results\\"');
    expect(source).toContain('const int MaxEntryResults = 1000');
    expect(source).toContain('const int MaxEntriesPerGroup = 10000');
    expect(source).toContain('const int MaxEntrySamplesPerLabel = 50');
    expect(source).toContain('const int MaxDependenciesPerEntry = 500');
  });

  it("uses serialized Addressables inspection without compile-time package references", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('FindAddressablesPackage');
    expect(source).toContain('PackageManagerPackageInfo.GetAllRegisteredPackages()');
    expect(source).toContain('AddressablesEditorApiLoaded');
    expect(source).toContain('AssetDatabase.FindAssets("t:" + typeName, searchRoots)');
    expect(source).toContain('CandidateAssetsByType("AddressableAssetGroup"');
    expect(source).toContain('new SerializedObject(asset)');
    expect(source).toContain('FindFirstProperty(serializedObject, "m_Entries", "entries")');
    expect(source).toContain('FindFirstRelativeProperty(entry, "m_SerializedLabels", "m_Labels", "labels")');
    expect(source).toContain('AssetDatabase.GUIDToAssetPath(entryGuid)');
    expect(source).toContain('\\"report_mode\\":\\"addressables_serialized_labels\\"');
    expect(source).toContain('\\"report_mode\\":\\"addressables_entries_assetdatabase_dependencies\\"');
    expect(source).toContain('\\"separate_from_assetdatabase_dependencies\\":true');
    expect(source).toContain('AssetDatabase.GetDependencies(assetPath, Recursive)');
    expect(source).toContain('\\"preflight_mode\\":\\"read_only_serialized_addressables\\"');
    expect(source).toContain('\\"build_started\\":false');
    expect(source).toContain('\\"remote_operations_started\\":false');
    expect(source).toContain('\\"network_operations_started\\":false');
    expect(source).toContain("BuildPreflightState");
    expect(source).toContain('AddressablesReportArtifactRoot');
    expect(source).toContain('ProjectPaths.ProjectRoot');
    expect(source).toContain('WriteAddressablesReportArtifact');
    expect(source).not.toContain('using UnityEditor.AddressableAssets');
    expect(source).not.toContain('using UnityEngine.AddressableAssets');
    expect(source).not.toContain('AddressableAssetSettingsDefaultObject');
  });

  it("does not run content builds, network package APIs, or asset mutations", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).not.toContain("BuildPlayerContent");
    expect(source).not.toContain("BuildScript");
    expect(source).not.toContain("Client.Search");
    expect(source).not.toContain("Client.Add");
    expect(source).not.toContain("Client.Remove");
    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.Refresh");
    expect(source).not.toContain("AssetDatabase.CreateAsset");
    expect(source).not.toContain("AssetDatabase.MoveAsset");
    expect(source).not.toContain("AssetDatabase.DeleteAsset");
    expect(source).not.toContain("AssetDatabase.SaveAssets");
    expect(source).not.toContain("EditorUtility.DisplayDialog");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
  });

  it("documents and exposes the Addressables readonly feature intentionally", () => {
    const contracts = fs.readFileSync(contractsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const securityDocs = fs.readFileSync(securityDocsPath, "utf8");

    expect(contracts).toContain('"addressables"');
    expect(cli).toContain('"addressables.": "addressables_readonly"');
    expect(gatewayExample).toContain('"addressables_readonly": false');
    expect(policyExample).toContain('"addressables.status"');
    expect(policyExample).toContain('"addressables.groups.list"');
    expect(policyExample).toContain('"addressables.entries.search"');
    expect(policyExample).toContain('"addressables.labels.report"');
    expect(policyExample).toContain('"addressables.dependencies.report"');
    expect(policyExample).toContain('"addressables.build.preflight"');
    expect(policyProfiles).toContain('"addressables_readonly"');
    expect(docs).toContain("Feature-gated Addressables read-only commands");
    expect(docs).toContain("addressables.entries.search");
    expect(docs).toContain("addressables.labels.report");
    expect(docs).toContain("addressables.dependencies.report");
    expect(docs).toContain("addressables.build.preflight");
    expect(docsRu).toContain("addressables.entries.search");
    expect(docsRu).toContain("addressables.labels.report");
    expect(docsRu).toContain("addressables.dependencies.report");
    expect(docsRu).toContain("addressables.build.preflight");
    expect(securityDocs).toContain("features.addressables_readonly");
  });
});
