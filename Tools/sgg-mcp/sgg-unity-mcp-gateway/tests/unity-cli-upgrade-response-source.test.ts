import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const source = fs.readFileSync(path.join(packageRoot, "Editor", "Setup", "UnityCliUpdateCheck.cs"), "utf8");
const tests = fs.readFileSync(path.join(packageRoot, "Tests", "Editor", "UnityCliUpgradeResponseTests.cs"), "utf8");

// Source-contract checks only. Behavioral proof remains the exact Unity class.
describe("evidenced beta.9 upgrade response source contract", () => {
  it("accepts only the evidenced canonical command and help alias alongside legacy upgrade", () => {
    expect(source).toContain('string.Equals(envelope.command, "self-update", StringComparison.Ordinal)');
    expect(source).toContain('string.Equals(envelope.command, "upgrade", StringComparison.Ordinal)');
    expect(source).toContain('"Usage: unity self-update|upgrade [options]"');
    expect(source).toContain('new[] { "--target <version>", "-y, --yes" }');
    expect(source).toContain('new[] { "upgrade", "--check", "--channel", UnityCliUpdateCheck.CliChannel, "--format", "json", "--non-interactive", "--no-banner" }');
  });

  it("uses a bounded duplicate-safe typed schema rather than permissive Unity JSON for CLI metadata", () => {
    expect(source).toContain("envelope = new CliCheckResponseReader(output).Read();");
    expect(source).not.toContain("JsonUtility.FromJson<CliCheckEnvelope>");
    expect(source).toContain("new HashSet<string>(StringComparer.Ordinal)");
    expect(source).toContain("!names.Add(name)");
    expect(source).toContain("if (m_index != m_json.Length) throw new FormatException();");
    expect(source).toContain("if (items.Count >= MaxMetadataEntries) throw new FormatException();");
    expect(source).toContain("builder.Length >= MaxMetadataEntryCharacters");
    expect(tests).toContain("ResponseRejectsUnverifiedSchemaDuplicateKeysAndWrongTypes");
  });

  it("selects requested installed components without changing authorization TTL or freshness guards", () => {
    expect(source).toContain("Start(string projectRoot, UnityToolUpdateComponents requestedComponents)");
    expect(source).toContain("return Start(projectRoot, UnityToolUpdateComponents.All);");
    expect(source).toContain("var components = installed.Components & requestedComponents;");
    expect(source).toContain("components &= installed.Components;");
    expect(source).toContain("All = WindowsCli | Pipeline | Skill");
    expect(source).toContain("CacheTtl = TimeSpan.FromMinutes(15)");
    expect(source).toContain("if (!selected || status == null)");
    const recordComponent = source.slice(source.indexOf("internal static UnityToolFreshnessRecord RecordComponent("));
    const missingGate = "if (!installed && !(availabilityDisplayOnly && !status.AuthoritativeForMutation";
    expect(recordComponent).toContain(missingGate);
    expect(recordComponent.indexOf("if (!selected || status == null)")).toBeLessThan(recordComponent.indexOf(missingGate));
    expect(recordComponent).toContain("status.CheckState == UnityCliUpdateCheck.CheckStateChecked");
    expect(tests).toContain("SequentialMissingComponentChecksRetainBothAvailabilityRowsWithoutExtraProcesses");
    expect(tests).toContain("MissingAvailabilityCannotPersistAnAuthoritativeSnapshot");
    expect(tests).toContain("PipelineOnlyChecksLocalCliPrerequisitesButNoCliOrSkillRemoteMetadata");
    expect(tests).toContain("UnselectedFreshnessRecordIsPreservedWithoutMutationAuthority");
  });

  it("keeps explicit missing availability separate, display-only and absence-proven", () => {
    expect(source).toContain("StartAvailabilityCheck(string projectRoot, UnityToolUpdateComponents requestedComponents)");
    expect(source).toContain("CreateInitialAvailabilitySnapshotForTests(");
    expect(source).toContain("snapshot.PipelineAvailabilityDisplayOnly = !installed.PipelineInstalled;");
    expect(source).toContain('BridgeProtocol.IsValidUniqueObjectJson(json)');
    expect(source).toContain("!PipelineJsonEvidenceParser.TryReadStringPath(json");
    expect(source).toContain('"pipeline_local_state_unverified"');
    expect(source).toContain('"skill_local_state_unverified"');
    expect(source).toContain("if (m_status.PipelineAvailabilityDisplayOnly) m_status.Pipeline.Update.AuthoritativeForMutation = false;");
    expect(source).toContain("if (m_status.SkillAvailabilityDisplayOnly) m_status.Skill.Update.AuthoritativeForMutation = false;");
    expect(tests).toContain("ExplicitMissingPipelineAvailabilityUsesOnlyTrustedCliPipelineReadStages");
    expect(tests).toContain("ExplicitMissingSkillAvailabilityIsOneNetworkReadAndNeverMutationAuthority");
    expect(tests).toContain("ExplicitAvailabilityDoesNotWidenNormalOrAutomaticMissingComponentSelection");
  });
});
