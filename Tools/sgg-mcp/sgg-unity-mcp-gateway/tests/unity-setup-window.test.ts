import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const setupWindowPath = path.join(
  process.cwd(),
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Editor",
  "UI",
  "UnityMcpSetupWindow.cs",
);
const setupStatusPath = path.join(path.dirname(setupWindowPath), "UnityMcpSetupWindow.SetupStatus.cs");
const ftuePath = path.join(path.dirname(setupWindowPath), "UnityMcpSetupWindow.Ftue.cs");
const permissionsCommandsPath = path.join(path.dirname(setupWindowPath), "UnityMcpSetupWindow.PermissionsCommands.cs");
const permissionsCommandsRefreshPath = path.join(path.dirname(setupWindowPath), "UnityMcpSetupWindow.PermissionsCommands.Refresh.cs");
const plansSkillsPath = path.join(path.dirname(setupWindowPath), "UnityMcpSetupWindow.PlansSkills.cs");
const unityAiDiagnosticsPath = path.join(path.dirname(setupWindowPath), "UnityMcpSetupWindow.UnityAiDiagnostics.cs");
const unityCliPipelinePath = path.join(path.dirname(setupWindowPath), "UnityMcpSetupWindow.UnityCliPipeline.cs");

function readWindowSources(): string {
  return [setupWindowPath, setupStatusPath, ftuePath, permissionsCommandsPath, permissionsCommandsRefreshPath, plansSkillsPath, unityAiDiagnosticsPath, unityCliPipelinePath].map((file) => fs.readFileSync(file, "utf8")).join("\n");
}
const setupWindowFixturePath = path.join(
  process.cwd(),
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Tests",
  "Editor",
  "UnityMcpSetupWindowTests.cs",
);
const pureHelperFixturePath = path.join(
  process.cwd(),
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Tests",
  "Editor",
  "UnityMcpPureHelperTests.cs",
);
const harden08cFixturePath = path.join(
  process.cwd(),
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Tests",
  "Editor",
  "Harden08CStateRestorationTests.cs",
);
const harden09a1FixturePath = path.join(
  process.cwd(),
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Tests",
  "Editor",
  "Harden09A1WindowStateTests.cs",
);
const harden09a2FixturePath = path.join(
  process.cwd(),
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Tests",
  "Editor",
  "Harden09A2ViewModelTests.cs",
);

describe("Unity MCP setup window source-contract", () => {
  it("accepts CanonicalPath registry URLs with a trailing slash", () => {
    const source = readWindowSources();

    expect(source).toContain("RegistryUrlsMatch(JsonUtil.ExtractString(objectText, \"url\"), CanonicalPathRegistryUrl)");
    expect(source).toContain("NormalizeRegistryUrl(actual)");
    expect(source).toContain("TrimEnd('/')");
  });

  it("ships the real Unity EditMode fixture and required NUnit contracts", () => {
    const fixture = fs.readFileSync(setupWindowFixturePath, "utf8");

    expect(fixture).toContain("public sealed class UnityMcpSetupWindowTests");
    expect(fixture).toContain("[SetUp]");
    expect(fixture).toContain("[TearDown]");
    expect(fixture).toContain("[Test]");
    expect(fixture).toContain("OpenForCommandCreatesGuiReadyWindow");
    expect(fixture).toContain("RepeatedOpenForCommandPreservesSingleWindowInstance");
    expect(fixture).toContain("SelectTabForCommandChangesCurrentTabAndSelectsExactlyOneToggle");
    expect(fixture).toContain("SetupTabContainsChecklistAndSetupStateControls");
    expect(fixture).toContain("Assert.Ignore(\"Close the existing SGG Unity MCP window before running dedicated UnityMcpSetupWindowTests.\")");
    expect(fixture).toContain("readonly List<UnityMcpSetupWindow> m_testWindows");
    expect(fixture).toContain("if (!m_testWindows.Contains(window))");
    expect(fixture).not.toContain("CloseAllSetupWindows");
    expect(fixture).not.toContain("Resources.FindObjectsOfTypeAll<UnityMcpSetupWindow>");
    expect(fs.existsSync(`${setupWindowFixturePath}.meta`)).toBe(true);
  });

  it("keeps production automatic refresh and bounds the test seam to skipping it", () => {
    const source = readWindowSources();

    expect(source).toContain("internal static bool SkipAutomaticRefreshForTests { get; set; } = false;");
    expect(source).toContain("m_skipAutomaticRefreshForTests = SkipAutomaticRefreshForTests;");
    expect(source).toContain("m_isIsolatedTestFixture = SkipAutomaticRefreshForTests;");
    expect(source).toContain("if (!m_skipAutomaticRefreshForTests)\n            {\n                RefreshAll(true);\n            }");
    expect(source.match(/SkipAutomaticRefreshForTests/g)).toHaveLength(3);
    expect(source).toContain("m_unityToolFreshnessVisibilityHandled || m_skipAutomaticRefreshForTests");
    expect(source).not.toContain("RefreshAll(!SkipAutomaticRefreshForTests)");
    expect(source).not.toContain("AddButton(actions, \"Copy sgg_unity_call Snippet\", CopySelectedCommandCallSnippet);\n            RefreshCommands();");
    expect(source).not.toContain("DisableAutomaticHealthProbeForTests");
  });

  it("ships side-effect-free HARDEN-09A1 window and runtime-health state coverage", () => {
    const fixture = fs.readFileSync(harden09a1FixturePath, "utf8");
    const restorationFixture = fs.readFileSync(harden08cFixturePath, "utf8");

    expect(fixture).toContain("public sealed class Harden09A1WindowStateTests");
    expect(fixture).toContain("WindowStateUnityJsonRoundtripPreservesNormalizedSelection");
    expect(fixture).toContain("RuntimeHealthStaleRecordsRespectLiveCoverage");
    expect(fixture).toContain("RuntimeHealthPolicyHeartbeatAndUnavailableHealthRequireRefreshOnly");
    expect(fixture).not.toMatch(/File\.|Directory\.|Process\.|EditorWindow|CreateInstance/);
    expect(restorationFixture).toContain('\\"m_windowState\\":{\\"m_selectedTab\\":\\"');
    expect(restorationFixture).toContain("LegacyRootTabMigratesToNestedStateAndRecreatedGui");
    expect(restorationFixture).toContain("InvalidLegacyRootTabFallsBackToSetupAndClearsLegacyState");
    expect(restorationFixture).toContain("ExplicitNestedSelectionWinsOverMixedLegacyStateAndClearsLegacy");
    expect(fs.existsSync(`${harden09a1FixturePath}.meta`)).toBe(true);
  });

  it("ships side-effect-free EditMode coverage for diagnostics, policy summaries, and descriptor filters", () => {
    const source = readWindowSources();
    const fixture = fs.readFileSync(pureHelperFixturePath, "utf8");

    expect(source).toContain("internal static string FormatTokenPresence(string token)");
    expect(source).toContain("McpDiagnosticsCopyViewModel.Build(diagnostics)");
    expect(source).toContain("McpRuntimePolicyViewModel.BuildSummary(status)");
    expect(source).toContain("internal static bool MatchesCommandSearchAndRiskFilters(CommandDescriptor descriptor, string risk, string search)");
    expect(source).toContain("internal static bool MatchesCommandGroupFilter(CommandDescriptor descriptor, string group)");
    const filtersStart = source.indexOf("bool MatchesCommandFilters(");
    const filtersEnd = source.indexOf("internal static bool MatchesCommandSearchAndRiskFilters", filtersStart);
    const filtersBody = source.slice(filtersStart, filtersEnd);
    expect(filtersBody.indexOf("MatchesCommandSearchAndRiskFilters")).toBeLessThan(filtersBody.indexOf('feature == "none"'));
    expect(filtersBody.indexOf('feature == "none"')).toBeLessThan(filtersBody.indexOf("MatchesPolicyActionFilter"));
    expect(filtersBody.indexOf("MatchesPolicyActionFilter")).toBeLessThan(filtersBody.indexOf("MatchesCommandGroupFilter"));
    expect(fixture).toContain("public sealed class UnityMcpPureHelperTests");
    expect(fixture).toContain("RedactDiagnosticsTextRemovesHeaderAndTextSecrets");
    expect(fixture).toContain("RuntimePolicySummaryReportsDegradedLastKnownGoodGuidance");
    expect(fixture).toContain("MatchesCommandSearchAndRiskFiltersAcceptsIdTitleDescriptionAndExactRisk");
    expect(fixture).toContain("MatchesCommandGroupFilterAcceptsAllIdCapabilityPrefixExactAndTag");
    expect(fixture).not.toContain("Environment.SetEnvironmentVariable");
    expect(fixture).not.toContain("OpenForCommand");
    expect(fixture).not.toContain("File.");
    expect(fs.existsSync(`${pureHelperFixturePath}.meta`)).toBe(true);
  });

  it("ships side-effect-free HARDEN-09A2 policy timing and diagnostics-copy view-model coverage", () => {
    const source = readWindowSources();
    const fixture = fs.readFileSync(harden09a2FixturePath, "utf8");

    expect(fixture).toContain("public sealed class Harden09A2ViewModelTests");
    expect(fixture).toContain("RuntimePolicySummaryCoversUnavailableMalformedOlderActiveAndDegradedStates");
    expect(fixture).toContain("TimingSummaryDefaultsBoundsNormalizesAndIsDeterministic");
    expect(fixture).toContain("DiagnosticsBuildPreservesExactHeadingAndFieldOrderWithNullSafety");
    expect(fixture).toContain("DiagnosticsRedactionRemovesPrefixedBearerCookieJsonAndTokenValues");
    expect(fixture).toContain("DiagnosticsBuildRedactsLongValidJsonSecretBeforeHeadBounding");
    expect(fixture).toContain("DiagnosticsRedactionFailsClosedForUnterminatedJsonSecretsAndPreservesBenignMalformedText");
    expect(fixture).toContain("DiagnosticsRedactionUsesSeparatorDelimitedCredentialComponents");
    expect(fixture).toContain("DiagnosticsRedactionPreservesCredentialSubstringsWithoutSeparators");
    expect(fixture).toContain("DiagnosticsRedactionFailsClosedForSingleQuotedJsonLikeSecrets");
    expect(fixture).toContain("DiagnosticsRedactionAcceptsMismatchedKeyQuoteDelimiters");
    expect(fixture).toContain("ProductionLastErrorLinesRedactsMalformedNewestErrorAndTailBoundKeepsNewest");
    expect(fixture).toContain("ProductionLastErrorLinesAndBuildRedactMismatchedKeyQuotes");
    expect(fixture).not.toMatch(/File\.|Directory\.|Process\.|EditorWindow|CreateInstance|Stopwatch|Profiler|GUIUtility/);
    expect(source).not.toContain("RuntimePolicySummary(GatewayProcessManager.GatewayRuntimeStatus");
    expect(source).not.toContain("RedactDiagnosticsText(string text)");
    expect(fs.existsSync(`${harden09a2FixturePath}.meta`)).toBe(true);
  });

  it("ships bounded HARDEN-08C serialization, lifecycle, freshness, and action-state coverage", () => {
    const source = readWindowSources();
    const fixture = fs.readFileSync(harden08cFixturePath, "utf8");

    expect(source).toContain("[SerializeField]\n        McpWindowState m_windowState = new McpWindowState();");
    expect(source).not.toContain("m_initialTab");
    expect(source).toContain("[SerializeField, HideInInspector]\n        string m_currentTab = string.Empty;");
    expect(source).toContain("m_windowState.SelectTab(m_currentTab)");
    expect(source).toContain("McpRuntimeHealthViewState.Evaluate(processStatus, null, LocalBridgeHeartbeatStale())");
    expect(fixture).toContain("public sealed class Harden08CStateRestorationTests");
    expect(fixture).toContain("EditorSerializationRestoresSelectedTabIntoRecreatedGui");
    expect(fixture).toContain("EditorJsonUtility.ToJson(source)");
    expect(fixture).toContain("BridgeLifecycleTransitionsWithoutHeartbeatOrListener");
    expect(fixture).toContain("PackageUpdateJsonBoundsEncodedAdversarialFieldsAndRemainsValid");
    expect(fixture).toContain("JsonUtility.FromJson<PackageUpdatePayload>(json)");
    expect(fixture).toContain("RuntimeFreshnessRejectsEachStaleContractField");
    expect(fixture).toContain("SetActionNeededOnlyChangesClassAndTooltipAndIsNullSafe");
    expect(fixture).not.toContain("File.");
    expect(fixture).not.toContain("HeartbeatWriter");
    expect(fixture).not.toContain("button.clickable");
    expect(fs.existsSync(`${harden08cFixturePath}.meta`)).toBe(true);
  });
});
