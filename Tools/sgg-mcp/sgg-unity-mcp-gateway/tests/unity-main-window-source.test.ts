import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { execFileSync } from "node:child_process";

import { describe, expect, it } from "vitest";

import { runSetupCli } from "../src/cli.js";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const editorRoot = path.join(packageRoot, "Editor");
const windowPath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.cs");
const setupStatusPath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.SetupStatus.cs");
const ftuePath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.Ftue.cs");
const permissionsCommandsPath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.PermissionsCommands.cs");
const permissionsCommandsRefreshPath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.PermissionsCommands.Refresh.cs");
const plansSkillsPath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.PlansSkills.cs");
const unityAiDiagnosticsPath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.UnityAiDiagnostics.cs");
const unityCliPipelinePath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.UnityCliPipeline.cs");
const windowStylePath = path.join(editorRoot, "UI", "UnityMcpSetupWindow.uss");
const localizationRoot = path.join(editorRoot, "UI", "Localization");
const localizerPath = path.join(localizationRoot, "McpWindowLocalization.cs");
const windowStatePath = path.join(editorRoot, "UI", "State", "McpWindowState.cs");
const runtimeHealthStatePath = path.join(editorRoot, "UI", "State", "McpRuntimeHealthViewState.cs");
const runtimePolicyViewModelPath = path.join(editorRoot, "UI", "State", "McpRuntimePolicyViewModel.cs");
const timingViewModelPath = path.join(editorRoot, "UI", "State", "McpWindowTimingViewModel.cs");
const diagnosticsCopyViewModelPath = path.join(editorRoot, "UI", "State", "McpDiagnosticsCopyViewModel.cs");
const enXlfPath = path.join(localizationRoot, "mcp-window.en.xlf");
const ruXlfPath = path.join(localizationRoot, "mcp-window.ru.xlf");
const bridgeRuntimePath = path.join(editorRoot, "Bridge", "UnityBridgeRuntime.cs");
const heartbeatWriterPath = path.join(editorRoot, "Bridge", "HeartbeatWriter.cs");
const gatewayProcessManagerPath = path.join(editorRoot, "Setup", "GatewayProcessManager.cs");
const processUtilPath = path.join(editorRoot, "Setup", "ProcessUtil.cs");
const gatewayPolicyManagerPath = path.join(editorRoot, "Setup", "GatewayPolicyManager.cs");
const gatewayInstallerPath = path.join(editorRoot, "Setup", "GatewayInstaller.cs");
const gatewayNetworkModeManagerPath = path.join(editorRoot, "Setup", "GatewayNetworkModeManager.cs");
const openCodeConfigPathResolverPath = path.join(editorRoot, "Setup", "OpenCodeConfigPathResolver.cs");
const unityCliPipelineSetupPath = path.join(editorRoot, "Setup", "UnityCliPipelineSetup.cs");
const unityCliUpdateCheckPath = path.join(editorRoot, "Setup", "UnityCliUpdateCheck.cs");
const windowsCliUpdateOperationPath = path.join(editorRoot, "Setup", "WindowsCliUpdateOperation.cs");
const unityPipelineUpdateOperationPath = path.join(editorRoot, "Setup", "UnityPipelineUpdateOperation.cs");
const boundedProcessOperationPath = path.join(editorRoot, "Setup", "BoundedProcessOperation.cs");

function readEditorSources(): Array<{ file: string; source: string }> {
  const files: Array<{ file: string; source: string }> = [];
  const pending = [editorRoot];

  while (pending.length > 0) {
    const current = pending.pop()!;
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const fullPath = path.join(current, entry.name);
      if (entry.isDirectory()) {
        pending.push(fullPath);
      } else if (entry.isFile() && entry.name.endsWith(".cs")) {
        files.push({ file: path.relative(process.cwd(), fullPath), source: fs.readFileSync(fullPath, "utf8") });
      }
    }
  }

  return files;
}

function methodBody(source: string, methodName: string): string {
  const signatureMatch = new RegExp(`^\\s*(?:internal static |static |internal |public |private )?(?!return\\b)(?:[A-Za-z0-9_<>.]+\\s+)+${methodName}\\(`, "m").exec(source);
  const signatureIndex = signatureMatch?.index ?? -1;
  expect(signatureIndex).toBeGreaterThanOrEqual(0);
  const bodyStart = source.indexOf("{", signatureIndex);
  expect(bodyStart).toBeGreaterThanOrEqual(0);

  let depth = 0;
  let quote = "";
  let escaped = false;
  for (let index = bodyStart; index < source.length; index++) {
    if (quote) {
      if (escaped) {
        escaped = false;
      } else if (source[index] === "\\") {
        escaped = true;
      } else if (source[index] === quote) {
        quote = "";
      }
      continue;
    }
    if (source[index] === '"' || source[index] === "'") {
      quote = source[index]!;
      continue;
    }
    if (source[index] === "{") {
      depth++;
    } else if (source[index] === "}") {
      depth--;
      if (depth === 0) {
        return source.slice(bodyStart, index + 1);
      }
    }
  }

  throw new Error(`Could not find body for ${methodName}`);
}

function readWindowSources(): string {
  return [windowPath, setupStatusPath, ftuePath, permissionsCommandsPath, permissionsCommandsRefreshPath, plansSkillsPath, unityAiDiagnosticsPath, unityCliPipelinePath].map((file) => fs.readFileSync(file, "utf8")).join("\n");
}

function expectInOrder(source: string, values: string[]): void {
  let previous = -1;
  for (const value of values) {
    const current = source.indexOf(value, previous + 1);
    expect(current, `missing or out of order: ${value}`).toBeGreaterThan(previous);
    previous = current;
  }
}

function xliffTransUnitIds(source: string): string[] {
  return Array.from(source.matchAll(/<trans-unit\s+[^>]*id="([^"]+)"/g), (match) => match[1]!).sort();
}

describe("Unity main setup window source-contract", () => {
  it("closes HARDEN-09E with final main ownership and body-identical tab leftovers", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    const permissionsCommands = fs.readFileSync(permissionsCommandsPath, "utf8");
    const plansSkills = fs.readFileSync(plansSkillsPath, "utf8");
    const unityAiDiagnostics = fs.readFileSync(unityAiDiagnosticsPath, "utf8");
    const baseline = execFileSync("git", ["show", "d2339f7:unity/Packages/com.sungeargames.unity-mcp/Editor/UI/UnityMcpSetupWindow.cs"], { encoding: "utf8" });
    const setupOwned = [
      "OpenRoslynCommandsFilter", "CanonicalPathManifestSnippet", "TryEnsureCanonicalPathScopedRegistry",
      "TryInsertScopedRegistriesProperty", "CanonicalPathRegistryObjectBlock", "InsertRegistryObjectIntoArray",
      "AddScopeToRegistryObject", "TryFindRegistryObjectWithUrl", "TryFindCanonicalPathRegistryObject",
      "RegistryUrlsMatch", "NormalizeRegistryUrl", "RegistryObjectContainsScope", "TryFindJsonArrayProperty",
      "FindJsonProperty", "IndexOfNextNonWhitespace", "FindMatchingBracket",
    ];

    for (const method of setupOwned) {
      expect(main).not.toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${method}\\s*\\(`, "m"));
      expect(methodBody(setupStatus, method), method).toBe(methodBody(baseline, method));
    }
    expect(main).not.toMatch(/^ {8}\S[^\n]*\bUnityAiAssistantPackageStatus\s*\(/m);
    expect(methodBody(unityAiDiagnostics, "UnityAiAssistantPackageStatus")).toBe(methodBody(baseline, "UnityAiAssistantPackageStatus"));

    for (const prefix of ["setup", "status", "checklist", "node", "gateway", "process", "mcpConnection", "bridge", "permissions", "policyEditor", "commands", "plans", "skills", "unityAiAssistant", "diagnostics"]) {
      expect(main, prefix).not.toMatch(new RegExp(`^ {8}(?:Label|TextField|PopupField<[^>]+>|ListView|Toggle|Button)\\s+m_${prefix}`, "m"));
    }
    expect(setupStatus).toContain("AddRequest m_canonicalPathInstallRequest;");
    expect(setupStatus).toContain("Label m_setupMcpTokenEnv;");
    expect(permissionsCommands).toContain("readonly List<CommandDescriptor> m_commandsShown");
    expect(plansSkills).toContain("Label m_plansLocalRoot;");
    expect(unityAiDiagnostics).toContain("AddRequest m_unityAiAssistantInstallRequest;");
    expect(unityAiDiagnostics).toContain("Label m_diagnosticsWindowTiming;");

    for (const retained of [
      "CreateGUI", "OnEnable", "OnDisable", "BuildTabs", "RegisterTab", "SelectTab", "RefreshAll",
      "RefreshHeartbeatDependentViews", "TrackMcpWindowStep", "ApplyLocalization", "RunScrollBenchmarkForCommand",
    ]) {
      expect(main).toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${retained}\\s*\\(`, "m"));
    }
    expect(main).toContain("string m_currentTab = string.Empty;");
    expect(main).toContain("m_windowState.SelectTab(m_currentTab);");
    expect(main).toContain("m_currentTab = string.Empty;");
  });

  it("keeps HARDEN-09E lifecycle, refresh, localization, and benchmark work identical to d2339f7", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const baseline = execFileSync("git", ["show", "d2339f7:unity/Packages/com.sungeargames.unity-mcp/Editor/UI/UnityMcpSetupWindow.cs"], { encoding: "utf8" });

    for (const method of ["ApplyLocalization", "RunScrollBenchmarkForCommand"]) {
      expect(methodBody(main, method), method).toBe(methodBody(baseline, method));
    }
    const refreshAll = methodBody(main, "RefreshAll");
    expect(refreshAll.match(/TrackMcpWindowStep\(/g)).toHaveLength(21);
    expect(refreshAll.match(/ApplyLocalization/g)).toHaveLength(1);
    expect(methodBody(main, "BuildGui").match(/Build(?:Ftue|Setup|Status|Permissions|Commands|Plans|Skills|UnityAiAssistant|Diagnostics)Panel\(\);/g)).toHaveLength(9);
    expect(methodBody(main, "CreateGUI")).toContain("BuildGui();");
    expect(main.split("\n").length).toBeLessThan(baseline.split("\n").length);
  });

  it("keeps HARDEN-09D2 as one sealed partial EditorWindow with exact Unity AI/Diagnostics ownership", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const extracted = fs.readFileSync(unityAiDiagnosticsPath, "utf8");
    const editorSources = readEditorSources().map(({ source }) => source).join("\n");
    const movedNames = Array.from(extracted.matchAll(/^ {8}(?:(?:internal|public|private|static) )*(?:[A-Za-z_]\w*(?:<[^\n]+>)?\s+)+([A-Za-z_]\w*)\s*\(/gm), (match) => match[1]!);

    expect(movedNames).toEqual([
      "UnityAiAssistantPackageStatus",
      "BuildUnityAiAssistantPanel", "BuildDiagnosticsPanel", "InstallUnityAiAssistantPackage", "PollUnityAiAssistantInstall",
      "OpenPackageManager", "CopyUnityAiAssistantToolMap", "CopyUnityAiAssistantSkillSourcesSummary", "CopyDiagnostics",
      "OpenProblemReportsFolder", "RefreshUnityAiAssistant", "RefreshUnityAiAssistantAndLocalize", "RefreshDiagnosticsAndLocalize",
      "RefreshDiagnostics", "PresentMissing", "LastErrorLines", "ProblemReportsRoot",
      "UnityAiAssistantWorksWithoutOfficialPackageSummary", "UnityAiAssistantPackageAddsSummary",
      "UnityAiAssistantSkillDiscoverySources", "UnityAiAssistantSkillImportSemantics", "UnityAiAssistantRelayStatus",
      "UnityAiAssistantSupportedToolMap", "UnityAiAssistantPartialToolMap", "UnityAiAssistantDeferredToolMap",
    ]);
    for (const method of movedNames) {
      expect(main).not.toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${method}\\s*\\(`, "m"));
      expect(editorSources.match(new RegExp(`^ {8}\\S[^\\n]*\\b${method}\\s*\\(`, "gm")), method).toHaveLength(1);
    }
    expect(extracted.match(/public sealed partial class UnityMcpSetupWindow\s*\{/g)).toHaveLength(1);
    expect(extracted).not.toMatch(/void (?:CreateGUI|RefreshAll|BuildTabs|SelectTab|ApplyLocalization|RefreshDiagnosticsTiming)\s*\(/);
    expect(editorSources.match(/class\s+UnityMcpSetupWindow\s*:\s*EditorWindow/g)).toHaveLength(1);
    for (const shared of ["CreateGUI", "RefreshAll", "BuildTabs", "SelectTab", "ApplyLocalization", "RefreshDiagnosticsTiming", "ReadFeatureFlagsSummary", "SafeReadAllText"]) {
      expect(main).toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${shared}\\s*\\(`, "m"));
    }
  });

  it("keeps every HARDEN-09D2 moved body byte-identical to base HEAD 5c96960", () => {
    const extracted = fs.readFileSync(unityAiDiagnosticsPath, "utf8");
    const baseline = execFileSync("git", ["show", "5c96960:unity/Packages/com.sungeargames.unity-mcp/Editor/UI/UnityMcpSetupWindow.cs"], { encoding: "utf8" });
    const methods = Array.from(extracted.matchAll(/^ {8}(?:(?:internal|public|private|static) )*(?:[A-Za-z_]\w*(?:<[^\n]+>)?\s+)+([A-Za-z_]\w*)\s*\(/gm), (match) => match[1]!);

    for (const method of methods) {
      expect(methodBody(extracted, method), method).toBe(methodBody(baseline, method));
    }
  });

  it("preserves HARDEN-09D2 ordering, install confirmation, and diagnostics security invariants", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const extracted = fs.readFileSync(unityAiDiagnosticsPath, "utf8");
    const diagnosticsViewModel = fs.readFileSync(diagnosticsCopyViewModelPath, "utf8");
    const createGui = methodBody(main, "BuildGui");
    const refreshAll = methodBody(main, "RefreshAll");
    const install = methodBody(extracted, "InstallUnityAiAssistantPackage");
    const copy = methodBody(extracted, "CopyDiagnostics");
    const report = methodBody(extracted, "OpenProblemReportsFolder") + methodBody(extracted, "ProblemReportsRoot");
    const diagnostics = methodBody(extracted, "BuildDiagnosticsPanel") + methodBody(extracted, "RefreshDiagnostics") + copy + report;

    expectInOrder(createGui, ["BuildPlansPanel();", "BuildSkillsPanel();", "BuildUnityAiAssistantPanel();", "BuildDiagnosticsPanel();"]);
    expectInOrder(refreshAll, ['"skills", RefreshSkills', '"unity_ai_assistant", RefreshUnityAiAssistant', '"diagnostics", RefreshDiagnostics', '"localization", ApplyLocalization']);
    expect(install.indexOf("EditorUtility.DisplayDialog")).toBeLessThan(install.indexOf("UnityEditor.PackageManager.Client.Add"));
    expect(install).toContain('DisplayDialog("Install Unity AI Assistant", message, "Install", "Cancel")');
    expect(install).toContain("UnityEditor.PackageManager.Client.Add(UnityAiAssistantPackageId)");
    expect(install).not.toMatch(/Process\.|ExecuteMenuItem|RunAction|Approval|permission|policy|GUIUtility|systemCopyBuffer/i);
    expect(copy).toContain("McpDiagnosticsCopyViewModel.Build(diagnostics)");
    expect(copy).toContain("GUIUtility.systemCopyBuffer = McpDiagnosticsCopyViewModel.Build(diagnostics);");
    expect(methodBody(diagnosticsViewModel, "HeadField")).toContain("var safe = Redact(value);");
    expect(methodBody(diagnosticsViewModel, "TailField")).toContain("var safe = Redact(value);");
    expect(report).toContain('Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "audit", "problem-reports")');
    expect(report).toContain("EditorUtility.RevealInFinder(root)");
    expect(diagnostics).not.toMatch(/WebRequest|HttpClient|Upload|File\.(?:Write|Delete|Move)|Directory\.Delete|Process\.|CreateApproval|ConsumeApproval/);
    expect(diagnostics).not.toContain("processStatus.HealthBody");
    expect(diagnostics).not.toContain("processStatus.Problem");
    expect(methodBody(extracted, "RefreshDiagnostics")).toContain('UnityBridgeRuntime.HasBridgeToken ? "Present" : "Missing"');
    expect(methodBody(extracted, "RefreshDiagnostics")).toContain("m_diagnosticsMcpToken.text = McpTokenStatus();");
  });

  it("ships HARDEN-09D2 source/meta package entries", () => {
    const meta = fs.readFileSync(`${unityAiDiagnosticsPath}.meta`, "utf8");
    const packageChecker = fs.readFileSync(path.join(process.cwd(), "scripts", "check-unity-package.mjs"), "utf8");

    expect(meta).toBe("fileFormatVersion: 2\nguid: e0f31bc8bba54316a6c6f9873bcb520d\n");
    expect(packageChecker).toContain('"Editor/UI/UnityMcpSetupWindow.UnityAiDiagnostics.cs"');
    expect(packageChecker).toContain('"Editor/UI/UnityMcpSetupWindow.UnityAiDiagnostics.cs.meta"');
  });

  it("keeps HARDEN-09D1 as one sealed partial EditorWindow with exact Plans/Skills ownership", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const plansSkills = fs.readFileSync(plansSkillsPath, "utf8");
    const editorSources = readEditorSources().map(({ source }) => source).join("\n");
    const movedNames = Array.from(plansSkills.matchAll(/^ {8}(?:(?:internal|public|private|static) )*(?:[A-Za-z_]\w*(?:<[^\n]+>)?\s+)+([A-Za-z_]\w*)\s*\(/gm), (match) => match[1]!);

    expect(movedNames).toEqual([
      "BuildPlansPanel", "BuildSkillsPanel", "OpenPlansFolder", "CopyPlanTemplate", "CopyLatestPlan", "RefreshPlans",
      "OpenSkillsFolder", "RefreshSkills", "RefreshPlansAndLocalize", "RefreshSkillsAndLocalize", "LocalPlansRoot",
      "LocalSkillsRoot", "BuiltInSkillsRoot", "SkillReviewMetadataPath", "SkillsValidationSummary", "SkillsReviewSummary",
      "VersionedPlansRoot", "LatestPlanFilesSummary", "LatestPlanFile", "PlanTemplate",
    ]);
    for (const method of movedNames) {
      expect(main).not.toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${method}\\s*\\(`, "m"));
      expect(editorSources.match(new RegExp(`^ {8}\\S[^\\n]*\\b${method}\\s*\\(`, "gm"))).toHaveLength(1);
    }
    expect(plansSkills.match(/public sealed partial class UnityMcpSetupWindow\s*\{/g)).toHaveLength(1);
    expect(plansSkills).not.toMatch(/void (?:RefreshAll|ApplyLocalization|BuildTabs|SelectTab)\s*\(/);
    expect(editorSources.match(/class\s+UnityMcpSetupWindow\s*:\s*EditorWindow/g)).toHaveLength(1);
  });

  it("keeps HARDEN-09D1 moved bodies byte-identical to 118d0a9", () => {
    const plansSkills = fs.readFileSync(plansSkillsPath, "utf8");
    let baseline: string;
    try {
      baseline = execFileSync("git", ["show", "118d0a9:unity/Packages/com.sungeargames.unity-mcp/Editor/UI/UnityMcpSetupWindow.cs"], { encoding: "utf8" });
    } catch {
      return;
    }

    for (const method of [
      "BuildPlansPanel", "OpenPlansFolder", "CopyPlanTemplate", "CopyLatestPlan", "RefreshPlans",
      "OpenSkillsFolder", "RefreshPlansAndLocalize", "RefreshSkillsAndLocalize", "LocalPlansRoot",
      "LocalSkillsRoot", "BuiltInSkillsRoot", "SkillReviewMetadataPath", "SkillsValidationSummary", "SkillsReviewSummary",
      "VersionedPlansRoot", "LatestPlanFilesSummary", "LatestPlanFile", "PlanTemplate",
    ]) {
      expect(methodBody(plansSkills, method)).toBe(methodBody(baseline, method));
    }
  });

  it("preserves HARDEN-09D1 CreateGUI/RefreshAll order and Plans/Skills security boundaries", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const plansSkills = fs.readFileSync(plansSkillsPath, "utf8");
    const createGui = methodBody(main, "BuildGui");
    const refreshAll = methodBody(main, "RefreshAll");
    const plans = methodBody(plansSkills, "BuildPlansPanel") + methodBody(plansSkills, "OpenPlansFolder")
      + methodBody(plansSkills, "CopyPlanTemplate") + methodBody(plansSkills, "CopyLatestPlan") + methodBody(plansSkills, "RefreshPlans");
    const skills = methodBody(plansSkills, "BuildSkillsPanel") + methodBody(plansSkills, "OpenSkillsFolder")
      + methodBody(plansSkills, "RefreshSkills") + methodBody(plansSkills, "SkillsValidationSummary") + methodBody(plansSkills, "SkillsReviewSummary");

    expectInOrder(createGui, ["BuildCommandsPanel();", "BuildPlansPanel();", "BuildSkillsPanel();", "BuildUnityAiAssistantPanel();"]);
    expectInOrder(refreshAll, ['"permissions", RefreshPermissions', '"plans", RefreshPlans', '"skills", RefreshSkills', '"unity_ai_assistant", RefreshUnityAiAssistant']);
    expect(plans).toContain("this UI never applies plans automatically");
    expect(plans).toContain("File.ReadAllText(latest)");
    expect(plans).not.toMatch(/RunAction|CommandRegistry|\.Invoke\s*\(|Process\.|ApprovalStore|CreateApproval|ConsumeApproval|File\.(?:Write|Delete|Move)/);
    expect(skills).toContain("skills cannot change policy or bypass PathGuard/CanonicalPath");
    expect(skills).toContain("text.Length <= 131072");
    expect(skills).toContain("validation is read-only and never executes SKILL.md");
    expect(skills).not.toMatch(/RunAction|CommandRegistry|\.Invoke\s*\(|Process\.|ApprovalStore|CreateApproval|ConsumeApproval|File\.(?:Write|Delete|Copy|Move)|Directory\.CreateDirectory|PackageManager|Install|Remove|Overwrite|secret|token/i);
    expect(methodBody(plansSkills, "LocalPlansRoot")).toContain('"Library", "SGGUnityMcp", "plans"');
    expect(methodBody(plansSkills, "VersionedPlansRoot")).toContain('".mcp", "plans"');
    expect(methodBody(plansSkills, "LocalSkillsRoot")).toContain('".mcp", "skills"');
  });

  it("ships the stable HARDEN-09D1 source meta and package checker entries", () => {
    const meta = fs.readFileSync(`${plansSkillsPath}.meta`, "utf8");
    const packageChecker = fs.readFileSync(path.join(process.cwd(), "scripts", "check-unity-package.mjs"), "utf8");

    expect(meta).toBe("fileFormatVersion: 2\nguid: f34dad6385b84749ba28a11d89c08b32\n");
    expect(packageChecker).toContain('"Editor/UI/UnityMcpSetupWindow.PlansSkills.cs"');
    expect(packageChecker).toContain('"Editor/UI/UnityMcpSetupWindow.PlansSkills.cs.meta"');
  });

  it("keeps HARDEN-09C2 as one sealed partial EditorWindow with Permissions/Commands ownership removed from main", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    const permissionsCommands = fs.readFileSync(permissionsCommandsPath, "utf8");
    const permissionsCommandsRefresh = fs.readFileSync(permissionsCommandsRefreshPath, "utf8");
    const createGui = methodBody(main, "BuildGui");
    const editorSources = readEditorSources().map(({ source }) => source).join("\n");

    expect(main.match(/public sealed partial class UnityMcpSetupWindow\s*:\s*EditorWindow/g)).toHaveLength(1);
    expect(setupStatus.match(/public sealed partial class UnityMcpSetupWindow\s*\{/g)).toHaveLength(1);
    expect(permissionsCommands.match(/public sealed partial class UnityMcpSetupWindow\s*\{/g)).toHaveLength(1);
    expect(permissionsCommandsRefresh.match(/public sealed partial class UnityMcpSetupWindow\s*\{/g)).toHaveLength(1);
    expect(editorSources.match(/class\s+UnityMcpSetupWindow\s*:\s*EditorWindow/g)).toHaveLength(1);
    expect(main).not.toMatch(/void Build(?:Setup|Status)Panel\s*\(/);
    expect(main).not.toMatch(/void Build(?:Permissions|Commands)Panel\s*\(/);
    expect(setupStatus.match(/void BuildSetupPanel\s*\(/g)).toHaveLength(1);
    expect(setupStatus.match(/void BuildStatusPanel\s*\(/g)).toHaveLength(1);
    expect(setupStatus).not.toContain("BuildCommandsPanel");
    expect(permissionsCommands.match(/void BuildPermissionsPanel\s*\(/g)).toHaveLength(1);
    expect(permissionsCommands.match(/void BuildCommandsPanel\s*\(/g)).toHaveLength(1);
    expect(Array.from(permissionsCommands.matchAll(/^ {8}void\s+([A-Za-z0-9_]+)\s*\(/gm), (match) => match[1])).toEqual([
      "BuildPermissionsPanel",
      "BuildCommandsPanel",
    ]);
    for (const movedMethod of [
      "RefreshPermissions",
      "RefreshProjectPolicyEditor",
      "TryReadProjectPolicyDraft",
      "SaveProjectPolicyDraft",
      "RefreshCommands",
      "MatchesCommandFilters",
      "SelectedCommand",
      "CommandPreview",
      "CopySelectedCommandId",
      "CopySelectedCommandCallSnippet",
    ]) {
      expect(main).not.toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${movedMethod}\\s*\\(`, "m"));
      expect(permissionsCommandsRefresh).toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${movedMethod}\\s*\\(`, "m"));
    }
    expectInOrder(createGui, [
      "BuildSetupPanel();",
      "BuildStatusPanel();",
      "BuildPermissionsPanel();",
      "BuildCommandsPanel();",
      "BuildPlansPanel();",
      "BuildSkillsPanel();",
      "BuildUnityAiAssistantPanel();",
      "BuildDiagnosticsPanel();",
    ]);
  });

  it("keeps HARDEN-09C2 representative moved bodies byte-identical to b2a5002", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const refresh = fs.readFileSync(permissionsCommandsRefreshPath, "utf8");
    let baseline: string;
    try {
      baseline = execFileSync("git", ["show", "b2a5002:unity/Packages/com.sungeargames.unity-mcp/Editor/UI/UnityMcpSetupWindow.cs"], { encoding: "utf8" });
    } catch {
      return;
    }

    for (const method of [
      "CopySelectedCommandEffectivePolicySummary",
      "ConfirmApplyProfile",
      "RefreshPermissions",
      "RefreshProjectPolicyEditor",
      "SaveProjectPolicyDraft",
      "RefreshCommands",
      "MatchesCommandFilters",
      "CommandPreview",
      "ReadProjectPolicyRulesSnapshot",
      "SaveProjectPolicyRules",
      "ReadPendingApprovalsSummary",
    ]) {
      expect(main).not.toMatch(new RegExp(`^ {8}\\S[^\\n]*\\b${method}\\s*\\(`, "m"));
      if (method !== "ConfirmApplyProfile" && method !== "SaveProjectPolicyRules" && method !== "RefreshCommands") {
        const body = methodBody(refresh, method);
        const unchangedBody = method === "RefreshPermissions"
          ? body.replace(/            if \(m_permissionsAutoAllowKnownUnityPopups != null\)\n            \{\n                m_permissionsAutoAllowKnownUnityPopups\.SetValueWithoutNotify\(GatewayPolicyManager\.GetAutoAllowKnownUnityPopups\(\)\);\n            \}\n\n/, "")
          : body;
        expect(unchangedBody).toBe(methodBody(baseline, method));
      }
    }
    expect(methodBody(refresh, "RefreshCommands")).toContain("RefreshPolicyEditorCommandChoices(descriptors)");
    expect(methodBody(refresh, "RefreshCommands")).not.toContain("RefreshPolicyEditorCatalogRows");
  });

  it("keeps the HARDEN-09C2 moved inventory unique and bounded to Permissions/Commands", () => {
    const sources = [windowPath, setupStatusPath, permissionsCommandsPath, permissionsCommandsRefreshPath]
      .map((file) => fs.readFileSync(file, "utf8"));
    const refresh = sources[3]!;
    const movedNames = Array.from(refresh.matchAll(/^ {8}(?:(?:internal|public|private|static) )*(?:[A-Za-z_][\w.<>,\[\]]*\s+)+([A-Za-z_]\w*)\s*\(/gm), (match) => match[1]!);
    const required = [
      "PolicyPatternsField", "CopySelectedCommandId", "CopySelectedCommandArgsExample", "CopySelectedCommandSchema",
      "CopySelectedCommandPolicyExplainArgsJson", "CopySelectedCommandEffectivePolicySummary", "CopySelectedCommandCallSnippet",
      "ConfirmApplyProfile", "ConfirmApplyProfile", "ConfirmEnableAllFeatureFlags", "ConfirmAllowAll", "ApplyProfileWithFtueEvidence", "ApplyProfileAndEnableFlags",
      "EnableAllAndApplyAllowAll", "AddBaselinePermissions", "RefreshPermissions", "PermissionsBaselineActionNeeded",
      "PermissionsAllowAllCompletionNeeded", "AllKnownFeatureFlagsEnabled", "ProjectPolicyAllowsAll", "RefreshCommandsAndLocalize",
      "RefreshPermissionsAndLocalize", "RefreshProjectPolicyRules", "CopyPolicyRulesSummary", "RefreshCatalogPolicySummary",
      "CopyCatalogPolicySummary", "RefreshProjectPolicyEditor", "ReloadProjectPolicyDraft", "ResetProjectPolicyDraft",
      "ReviewProjectPolicyDraft", "RefreshPolicyEditorCommandChoices", "UpdatePolicyEditorActionState", "ResetPolicyActionClasses", "PolicyActionClass",
      "PolicyActionBadge", "AddPolicyEditorDropdownRule", "AddSelectedCommandToPolicyDraft", "MoveExactPolicyPatternToSection",
      "RemoveExactPolicyPattern", "SaveProjectPolicyDraft", "UpdatePolicyEditorDraftStatus", "OnPolicyEditorDraftChanged",
      "TryReadProjectPolicyDraft", "ProjectPolicyEditorDirty", "SetAutoAllowAsk", "SetAutoAllowKnownUnityPopups", "RefreshCommands", "OnCommandSelectionChanged",
      "CommandListLabel", "BindCommandListItem", "ResetCommandRowClasses", "CommandRowStateClass", "PolicyActionBucket",
      "FeatureStatusBucket", "MatchesCommandFilters", "MatchesCommandSearchAndRiskFilters", "MatchesCommandGroupFilter",
      "MatchesPolicyActionFilter", "IndexOfCommandId", "ContainsCommandId", "SelectedCommand", "UpdateCommandDetails",
      "CommandStatusBadges", "UpdatePermissionsPreview", "CommandPreview", "CommandAvailability", "HumanCommandResult",
      "FeatureFlagStatus", "FeatureFlagStatus", "PolicyDecisionSummary", "PolicyDecisionSummary", "ReadPolicyPreviewContext",
      "ProjectPolicySource", "ReadProjectPolicyRulesSnapshot", "ReadProjectPolicyRules", "PolicySectionValues", "PolicyEditorText",
      "PolicyEditorPatterns", "DuplicatePolicyPatternAcrossSections", "ProjectPolicyChangeReview", "ProjectPolicyExactDraftChangesSummary",
      "CommandPolicyDraftExactChanged", "CommandPolicyDraftExactChangeSummary", "CommandPolicyDraftExactAction", "AddExactPolicyIds",
      "IsExactPolicyPattern", "ExactPolicyAction", "ExactPolicyListContains", "SectionChangeReview", "BroadPolicyPatternWarnings",
      "AddBroadPolicyPatternWarnings", "IsBroadPolicyPattern", "SaveProjectPolicyRules", "UpsertProjectPolicyRules",
      "ProjectPolicyObjectBlock", "AppendPolicyArray", "TryFindJsonObjectProperty", "FindJsonObjectEnd", "AtomicWriteText",
      "PolicySectionContains", "CommandMatchesPolicyPattern", "DefaultPolicyAction", "RiskDefaultsSummary", "ValuesSummary",
      "ReadApprovalsDir", "ReadApprovalsSummary", "ReadPendingApprovalsSummary", "ReadJsonStringFieldFromSource",
      "GatewayVisibility", "WhyUnavailable", "ExampleArgsJson", "PolicyExplainArgsJson", "EscapeSnippetString", "AddExampleField",
      "SchemaHasProperty", "EmptyAsNone", "StripRichText", "CombineFlags",
    ];
    expect(movedNames).toEqual(required);
    const declaredNames = sources.flatMap((source) => Array.from(source.matchAll(/^ {8}(?:(?:internal|public|private|static) )*(?:[A-Za-z_][\w.<>,\[\]]*\s+)+([A-Za-z_]\w*)\s*\(/gm), (match) => match[1]!));
    for (const method of new Set(movedNames)) {
      expect(declaredNames.filter((name) => name === method), method).toHaveLength(movedNames.filter((name) => name === method).length);
    }
    expect(refresh).not.toMatch(/void (?:RefreshAll|OpenRoslynCommandsFilter|RefreshDiagnostics|RefreshPlans|RefreshSkills)\s*\(/);
  });

  it("preserves HARDEN-09C2 refresh order and security boundaries", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const refresh = fs.readFileSync(permissionsCommandsRefreshPath, "utf8");
    const builders = fs.readFileSync(permissionsCommandsPath, "utf8");
    const refreshAll = methodBody(main, "RefreshAll");
    const save = methodBody(refresh, "SaveProjectPolicyRules");
    const approvals = methodBody(refresh, "ReadApprovalsSummary") + methodBody(refresh, "ReadPendingApprovalsSummary");
    const commands = methodBody(builders, "BuildCommandsPanel") + methodBody(refresh, "RefreshCommands") + methodBody(refresh, "OnCommandSelectionChanged");

    expectInOrder(refreshAll, ['"catalog", s_mcpWindowCatalogRefreshMarker, RefreshCommands', '"permissions", RefreshPermissions']);
    expect(save).toContain("AtomicWriteText(status.PolicyPath, updated);");
    expect(methodBody(refresh, "AtomicWriteText")).toContain("File.Replace(tempPath, path, backupPath, true);");
    expect(methodBody(refresh, "ProjectPolicyChangeReview")).toContain("Feature flags and risk defaults are unchanged");
    expect(approvals).not.toMatch(/File\.(?:Write|Delete)|CreateApproval|ConsumeApproval|ApprovalStore|approvePending/);
    expect(commands).not.toMatch(/RunAction|CommandRegistry|\.Invoke\s*\(|Process\.|File\.Write|ApprovalStore/);
    expect(refresh.match(/public sealed partial class UnityMcpSetupWindow\s*:\s*EditorWindow/g)).toBeNull();
  });

  it("keeps the Commands builder byte-identical while Permissions intentionally removes nested scrolling", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const permissionsCommands = fs.readFileSync(permissionsCommandsPath, "utf8");
    let baseline: string;
    try {
      baseline = execFileSync("git", ["show", "e78d3f5:unity/Packages/com.sungeargames.unity-mcp/Editor/UI/UnityMcpSetupWindow.cs"], { encoding: "utf8" });
    } catch {
      return;
    }

    expect(main).not.toMatch(/void BuildPermissionsPanel\s*\(/);
    expect(main).not.toMatch(/void BuildCommandsPanel\s*\(/);
    expect(methodBody(permissionsCommands, "BuildCommandsPanel")).toBe(methodBody(baseline, "BuildCommandsPanel"));
    const permissions = methodBody(permissionsCommands, "BuildPermissionsPanel");
    expect(permissions).toContain('new PopupField<string>("Catalog command"');
    expect(permissions).not.toContain("new ListView");
    expect(permissions).not.toContain("mcp-policy-editor-catalog-list");
  });

  it("keeps HARDEN-09C1 policy/feature UI separate and Commands read-only", () => {
    const permissionsCommands = fs.readFileSync(permissionsCommandsPath, "utf8");
    const permissionsBody = methodBody(permissionsCommands, "BuildPermissionsPanel");
    const commandsBody = methodBody(permissionsCommands, "BuildCommandsPanel");

    expectInOrder(permissionsBody, [
      'AddSection(m_permissionsPanel, "Project Policy Editor")',
      'AddSection(m_permissionsPanel, "Feature Flags")',
    ]);
    expect(permissionsBody).toContain("It does not change feature flags, risk defaults, approvals or execute commands");
    expect(permissionsBody).toContain("Feature flags are stored in Gateway config");
    expect(permissionsBody).toContain("agents cannot approve themselves");
    expect(permissionsBody).not.toMatch(/ApprovalStore|CreateApproval|ConsumeApproval/);
    expect(commandsBody).toContain("Commands is a read-only catalog browser");
    expect(commandsBody).toContain("Execution\", \"Disabled in this UI");
    expect(commandsBody).not.toMatch(/RunAction|sgg_unity_call\s*\(|CommandRegistry|\.Invoke\s*\(|Process\.|File\.|ApprovalStore|CreateApproval|ConsumeApproval/);
    expect(commandsBody.match(/AddButton\([^\n]+/g)?.every((line) => line.includes("Copy"))).toBe(true);
  });

  it("preserves HARDEN-09B1 Setup and Status controls and callback bindings in order", () => {
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    const setup = methodBody(setupStatus, "BuildSetupPanel");
    const status = methodBody(setupStatus, "BuildStatusPanel");

    expectInOrder(setup, [
      'AddSection(m_setupPanel, "Required CanonicalPath")',
      'AddSection(m_setupPanel, "Portable Node")',
      'AddButton(nodeActions, "Install Portable Node", () => RunAction("Install Portable Node", () => NodeRuntimeInstaller.InstallPortableNode(true)))',
      'AddSection(m_setupPanel, "Gateway")',
      'AddButton(gatewayActions, "Install Runtime", () => RunAction("Install Gateway Runtime", InstallGatewayRuntime, true))',
      'AddSection(m_setupPanel, "Gateway Process")',
      'AddButton(processActions, "Start Gateway", StartOrRestartGateway)',
      'AddButton(processActions, "Stop Gateway", StopGateway)',
      'AddButton(processActions, "Refresh Health", RefreshHealthAndChecklist)',
      'AddButton(processActions, "Copy Log", CopyGatewayLog)',
      'AddSection(m_setupPanel, "MCP Connection")',
      "RegisterValueChangedCallback(evt => OnMcpConnectionModeChanged(evt.newValue))",
      'AddSection(m_setupPanel, "MCP Client Config")',
      'AddButton(clientActions, "Copy MCP URL", () => CopyText("MCP URL", McpUrl))',
      'AddButton(clientActions, "Copy OpenCode Config", CopyOpenCodeConfig)',
      'AddButton(clientActions, "Copy Cursor Config", CopyCursorConfig)',
      'AddButton(clientActions, "Copy Assistant Config", CopyAssistantConfig)',
      'AddButton(clientActions, "Copy First Calls", CopyFirstCalls)',
      'AddSection(m_setupPanel, "Permission Setup")',
      'AddButton(firstPermissionsActions, "Create Config/Policy Files", () => RunAction("Create Config/Policy Files", GatewayPolicyManager.EnsureUserConfig))',
      'AddButton(firstPermissionsActions, "Apply Readonly (optional)", () => ConfirmApplyProfile(GatewayPolicyManager.ReadonlyProfile, "Readonly"))',
      'AddButton(firstPermissionsActions, "Restart Gateway", RestartGateway)',
      'AddButton(firstPermissionsActions, "Open Permissions", () => SelectTab("Permissions"))',
    ]);
    expectInOrder(status, [
      'AddSection(m_statusPanel, "Unity Bridge")',
      'AddSection(m_statusPanel, "Runtime Summary")',
      'AddSection(m_statusPanel, "Gateway Health")',
      'AddButton(gatewayActions, "Refresh Health", RefreshHealthAndChecklist)',
      'AddButton(gatewayActions, "Copy Log", CopyGatewayLog)',
      'AddSection(m_statusPanel, "MCP Client Endpoint")',
    ]);
  });

  it("resolves OpenCode config targets independently from the Unity project root", () => {
    const resolver = fs.readFileSync(openCodeConfigPathResolverPath, "utf8");

    expectInOrder(resolver, [
      "explicitWorkspaceRoot",
      "savedWorkspaceRoot",
      "resolveGitRoot(unityRoot)",
      "WorkspaceSource.UnityProjectFallback",
    ]);
    expect(resolver).toContain("new CanonicalPathService()");
    expect(resolver).toContain('parent.EndsWith("/", StringComparison.Ordinal) ? parent : parent + "/"');
    expect(resolver).toContain("canonicalPath.ProjectRootAliases(path)");
    expect(resolver).toContain("HostEnvironmentFor(unityProjectRoot, Path.DirectorySeparatorChar)");
    expect(resolver).toContain("SavedWorkspaceRejected = savedWorkspaceRejected");
    expect(resolver).toContain("jsoncExists && jsonExists");
    expect(resolver).toContain("ConfigState.Conflict");
    expect(resolver).toContain("state == ConfigState.Conflict || hasHiddenLayer ? null");
    expect(resolver).not.toContain("Directory.GetCurrentDirectory");
  });

  it("blocks a known local hidden config layer without selecting or migrating it", () => {
    const resolver = fs.readFileSync(openCodeConfigPathResolverPath, "utf8");
    const manager = fs.readFileSync(gatewayNetworkModeManagerPath, "utf8");
    const ftue = fs.readFileSync(ftuePath, "utf8");

    expect(resolver).toContain('Path.Combine(candidate.HostRoot, ".opencode", "opencode.jsonc")');
    expect(resolver).toContain('Path.Combine(candidate.HostRoot, ".opencode", "opencode.json")');
    expect(resolver).toContain("ConfigState.HiddenLayerConflict");
    expect(resolver).toContain("HasConflict => State == ConfigState.Conflict || HasHiddenLayer");
    expect(methodBody(manager, "ValidateOpenCodeSetupTarget")).toContain("HiddenLayerConflictMessage(target)");
    expect(methodBody(manager, "OpenOpenCodeConfig")).toContain("HiddenLayerConflictMessage(target)");
    expect(methodBody(manager, "GetStatus")).toContain("!openCodeTarget.HasConflict");
    expect(methodBody(manager, "GetStatus")).toContain("IsOpenCodeTargetReady(openCodeTarget");
    expect(methodBody(manager, "IsOpenCodeTargetReady")).toContain("!target.HasConflict");
    expect(ftue).toContain("networkStatus.OpenCodeTargetMessage");
    expect(ftue).toContain("Ancestor/global/inline config and the client connection are not verified");
  });

  it.each(["windows-stdio", "remote-http"])("uses the Unity behavioral fixture for native V2 CLI generation parity (%s)", (connectionMode) => {
    const unityFixture = fs.readFileSync(path.join(packageRoot, "Tests", "Editor", "McpFtueConfigTests.cs"), "utf8");
    const scriptLiteral = /const string ExpectedWindowsStdioScript = ("(?:[^"\\]|\\.)*");/.exec(unityFixture)?.[1];
    expect(scriptLiteral).toBeDefined();
    const expectedScript = JSON.parse(scriptLiteral!);
    const tempParent = path.join(os.tmpdir(), "opencode");
    fs.mkdirSync(tempParent, { recursive: true });
    const workspace = fs.mkdtempSync(path.join(tempParent, "unity-v2-parity-"));
    try {
      const remote = connectionMode === "remote-http";
      const url = remote ? "http://10.0.0.2:28182/mcp" : "http://127.0.0.1:28182/mcp";
      const configPath = path.join(workspace, "gateway.json");
      const outputPath = path.join(workspace, "opencode.jsonc");
      fs.writeFileSync(configPath, JSON.stringify({
        mcp: { host: remote ? "10.0.0.2" : "127.0.0.1", port: 28182, token_env: "CUSTOM_MCP_TOKEN", allow_non_loopback: remote, allowed_origins: remote ? ["http://localhost"] : [] },
        registry: { instances_dir: path.join(workspace, "instances"), stale_after_ms: 90000 },
        logging: { level: "info", audit_enabled: false, audit_dir: path.join(workspace, "audit") },
      }));
      const output: string[] = [];
      const errors: string[] = [];
      const result = runSetupCli(["opencode", "--config", configPath, "--output", outputPath, "--connection-mode", connectionMode, "--url", url, "--dry-run", "--json"], {
        cwd: workspace,
        env: { CUSTOM_MCP_TOKEN: "not-to-be-written" },
        stdout: (text) => output.push(text),
        stderr: (text) => errors.push(text),
      });
      expect(result, errors.join("")).toBe(0);
      const plan = JSON.parse(output.join(""));
      const server = plan.patch.mcp.servers["sgg-unity-mcp"];
      expect(plan.path).toBe(outputPath);
      expect(server.disabled).toBe(false);
      expect(server.timeout).toEqual({ startup: 30000, catalog: 30000, execution: 3900000 });
      expect(server.enabled).toBeUndefined();
      expect(plan.patch.mcp["sgg-unity-mcp"]).toBeUndefined();
      if (remote) {
        expect(server).toMatchObject({ type: "remote", url, oauth: false, headers: { "X-SGG-Unity-MCP-Token": "{env:CUSTOM_MCP_TOKEN}" } });
      } else {
        expect(server.command).toEqual(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", expectedScript]);
      }
      expect(output.join("")).not.toContain("not-to-be-written");
      expect(fs.existsSync(outputPath)).toBe(false);
    } finally {
      fs.rmSync(workspace, { recursive: true, force: true });
    }
  });

  it("uses one freshly resolved OpenCode target for status and every config action", () => {
    const manager = fs.readFileSync(gatewayNetworkModeManagerPath, "utf8");
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    const ftue = fs.readFileSync(ftuePath, "utf8");
    const getStatus = methodBody(manager, "GetStatus");
    const runSetup = manager.slice(manager.indexOf("static NodeRuntimeInstaller.InstallResult RunOpenCodeSetup"));
    const validateWriteTarget = manager.slice(manager.indexOf("internal static string ValidateOpenCodeWriteTarget"));
    const validateSetupTarget = methodBody(manager, "ValidateOpenCodeSetupTarget");
    const openConfig = methodBody(manager, "OpenOpenCodeConfig");
    const createConfig = methodBody(setupStatus, "CreateOrUpdateOpenCodeConfig");
    const refreshConnection = methodBody(setupStatus, "RefreshMcpConnection");

    expect(getStatus).toContain("TryResolveOpenCodeTarget");
    expect(getStatus).toContain("openCodeTarget.ConfigPath");
    expect(getStatus).toContain("!openCodeTarget.HasConflict");
    expectInOrder(runSetup, [
      "TryResolveOpenCodeTarget",
      "ValidateOpenCodeSetupTarget(target",
      "TryResolveOpenCodeTarget(out currentTarget",
      "ValidateOpenCodeSetupTarget(currentTarget",
      "ProcessUtil.Quote(target.ConfigPath)",
    ]);
    expect(runSetup.match(/TryResolveOpenCodeTarget/g)?.length).toBeGreaterThanOrEqual(2);
    expect(runSetup).toContain("File.Exists(gatewayStatus.ConfigPath) ? gatewayStatus.ConfigPath : gatewayStatus.ExampleConfigPath");
    expect(runSetup).not.toContain("EnsureUserConfigFiles");
    expect(validateWriteTarget).toContain("target.ConfigPath, expectedConfigPath");
    expect(validateWriteTarget).toContain("target.ConfigPath, confirmedFallbackConfigPath");
    expect(validateSetupTarget).toContain("target.HasConflict");
    expect(validateSetupTarget).toContain("ValidateOpenCodeWriteTarget(target");
    expect(openConfig).toContain("target.HasConflict");
    expect(openConfig).toContain("File.Exists(target.ConfigPath)");
    expect(createConfig).toContain('EditorUtility.DisplayDialog("Confirm OpenCode Config Target"');
    expect(createConfig).toContain("WriteOpenCodeConfig(mode, status.OpenCodePath, confirmedFallbackConfigPath)");
    expect(refreshConnection).toContain("status.OpenCodeWorkspaceRoot");
    expect(refreshConnection).toContain("status.OpenCodeTargetMessage");
    expect(ftue).toContain("networkStatus.OpenCodeConfigConflict");
    expect(manager).not.toContain("OpenCodeJsoncPath =>");
  });

  it("persists a validated project-scoped OpenCode workspace with Auto and Browse controls", () => {
    const manager = fs.readFileSync(gatewayNetworkModeManagerPath, "utf8");
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    const resolveTarget = manager.slice(manager.indexOf("static bool TryResolveOpenCodeTarget"));
    const setWorkspace = manager.slice(manager.indexOf("public static NodeRuntimeInstaller.InstallResult SetOpenCodeWorkspaceRoot"));
    const resetWorkspace = manager.slice(manager.indexOf("public static NodeRuntimeInstaller.InstallResult ResetOpenCodeWorkspaceRoot"));
    const browseWorkspace = methodBody(setupStatus, "BrowseOpenCodeWorkspaceRoot");
    const refreshConnection = methodBody(setupStatus, "RefreshOpenCodeConnection");

    expect(resolveTarget).toContain("SavedOpenCodeWorkspaceRoot(projectRoot)");
    expect(resolveTarget).toContain("savedWorkspaceRoot, s_openCodeGitRoot");
    expect(setWorkspace).toContain("ResolveWithKnownGitRoot(projectRoot, workspaceRoot, null, null)");
    expect(setWorkspace).toContain("ValidateOpenCodeWorkspaceSelection(projectRoot, workspaceRoot)");
    expect(manager).toContain("string.IsNullOrWhiteSpace(workspaceRoot)");
    expect(manager).toContain("Use Auto to clear the saved override");
    expect(setWorkspace).toContain("Directory.Exists(candidate.WorkspaceRoot)");
    expect(setWorkspace).toContain("EditorPrefs.SetString(OpenCodeWorkspacePrefsKey(projectRoot), candidate.WorkspaceRoot)");
    expect(resetWorkspace).toContain("EditorPrefs.DeleteKey(OpenCodeWorkspacePrefsKey(ProjectPaths.ProjectRoot))");
    expect(resetWorkspace).toContain("InvalidateOpenCodeGitRootCache()");
    expect(manager).toContain("new CanonicalPathService().NormalizeProjectRoot(projectRoot).Value");
    expect(manager).toContain("SHA256.Create()");
    expect(setupStatus).toContain('name = "setup-opencode-workspace-root"');
    expect(setupStatus).toContain("isDelayed = true");
    expect(setupStatus).toContain('workspaceAutoButton.name = "setup-opencode-workspace-auto"');
    expect(setupStatus).toContain('workspaceBrowseButton.name = "setup-opencode-workspace-browse"');
    expect(browseWorkspace).toContain("EditorUtility.OpenFolderPanel");
    expect(browseWorkspace).toContain("string.IsNullOrEmpty(selectedFolder)");
    expect(browseWorkspace).not.toContain("Directory.CreateDirectory");
    expect(browseWorkspace).not.toContain("SaveFolderPanel");
    expect(refreshConnection).toContain("InvalidateOpenCodeGitRootCache()");
    expect(setupStatus).toContain("status.OpenCodeSavedWorkspaceRejected");
  });

  it("keeps HARDEN-09B2 unchanged moved refresh and action bodies mechanically identical to ebff109", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    let baseline: string;
    try {
      baseline = execFileSync("git", ["show", "ebff109:unity/Packages/com.sungeargames.unity-mcp/Editor/UI/UnityMcpSetupWindow.cs"], { encoding: "utf8" });
    } catch {
      return;
    }

    const movedMethods = [
      "GenerateSessionToken", "UseSessionToken", "InstallCanonicalPathPackage", "InstallGatewayRuntime",
      "StartOrRestartGateway", "CopyGatewayLog", "RefreshCanonicalPathRegistry",
      "RefreshNode", "RefreshGateway", "RefreshBridge",
      "UpdateGatewayActionButtons", "RefreshMcpTokenSetup",
      "RefreshMcpConnection", "RefreshMcpClientConfig", "RefreshSetupPreferences", "RefreshOptionalToolsSetup",
    ];
    const normalize = (value: string): string => value.replace(/\r\n/g, "\n").trim();
    for (const method of movedMethods) {
      const signature = new RegExp(`^\\s{8}(?:internal static |static |internal |public |private )?(?:[A-Za-z0-9_<>.]+\\s+)+${method}\\(`, "m");
      expect(main).not.toMatch(signature);
      expect(setupStatus).toMatch(signature);
      // CanonicalPath now has an owner-confirmed upgrade path, not the old install-only body.
      if (method !== "InstallCanonicalPathPackage" && method !== "InstallGatewayRuntime" && method !== "StartOrRestartGateway" && method !== "UpdateGatewayActionButtons" && method !== "RefreshOptionalToolsSetup" && method !== "RefreshNode" && method !== "RefreshMcpConnection") {
        expect(normalize(methodBody(setupStatus, method))).toBe(normalize(methodBody(baseline, method)));
      }
    }
  });

  it("keeps HARDEN-09B2 shared refresh order and clean-install callback chains unchanged", () => {
    const main = fs.readFileSync(windowPath, "utf8");
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    const refreshAll = methodBody(main, "RefreshAll");
    const setup = methodBody(setupStatus, "BuildSetupPanel");

    expectInOrder(refreshAll, [
      '"node", RefreshNode', '"heartbeat"', '"health"', '"gateway"', '"bridge"', '"catalog"',
      '"setup_checklist"', '"canonical_path_registry"', '"mcp_token_setup"', '"mcp_connection"',
      '"mcp_client_config"', '"setup_preferences"', '"optional_tools_setup"', '"permissions"',
      '"plans"', '"skills"', '"unity_ai_assistant"', '"diagnostics"', '"header_shortcuts"', '"localization"',
    ]);
    expectInOrder(setup, [
      '"Install Portable Node"', '"Install Runtime"', '"Start Gateway"', '"Copy OpenCode Config"',
      '"Copy Cursor Config"', '"Copy Assistant Config"', '"Create Config/Policy Files"',
    ]);
    expect(methodBody(setupStatus, "InstallCanonicalPathPackage")).toContain("ConfigureCanonicalPathRegistry()");
    expect(methodBody(setupStatus, "InstallGatewayRuntime")).toContain("GatewayInstaller.InstallSourceDependencies()");
    expect(methodBody(setupStatus, "StartOrRestartGateway")).toContain('RunGatewayProcessAction("Start or Restart Gateway", false, false)');
    expectInOrder(methodBody(setupStatus, "RefreshHealthAndChecklist"), ["ForceHeartbeatBeforeHealthProbe(true)", "RefreshProcess(true)", "RefreshHeartbeatDependentViews(processStatus"]);
  });

  it("does not add extra EditorWindow classes", () => {
    const editorWindowClasses = readEditorSources().flatMap(({ file, source }) =>
      Array.from(source.matchAll(/class\s+(\w+)\s*:\s*EditorWindow/g), (match) => ({ file, className: match[1] })),
    );

    expect(editorWindowClasses).toEqual([{ file: path.relative(process.cwd(), windowPath), className: "UnityMcpSetupWindow" }]);
  });

  it("declares the required main window tabs", () => {
    const source = readWindowSources();
    const style = fs.readFileSync(windowStylePath, "utf8");

    const tabs = [
      { label: "FTUE", variable: "ftue", registerName: "FTUE" },
      { label: "Setup", variable: "setup", registerName: "Setup" },
      { label: "Status", variable: "status", registerName: "Status" },
      { label: "Permissions", variable: "permissions", registerName: "Permissions" },
      { label: "Commands", variable: "commands", registerName: "Commands" },
      { label: "Plans", variable: "plans", registerName: "Plans" },
      { label: "Skills", variable: "skills", registerName: "Skills" },
      { label: "Unity AI", variable: "unityAiAssistant", registerName: "Unity AI Assistant" },
      { label: "Diagnostics", variable: "diagnostics", registerName: "Diagnostics" },
    ];

    for (const tab of tabs) {
      expect(source).toContain(`text = "${tab.label}"`);
      expect(source).toContain(`RegisterTab(m_${tab.variable}Tab, "${tab.registerName}")`);
      expect(source).toContain(`SelectTabState(m_${tab.variable}Tab, m_${tab.variable}Panel`);
    }

    expect(source).not.toContain('text = "MCP Status"');
    expect(source).toContain("const float MinimumWindowWidth = 600f");
    expect(source).toContain("const float DefaultWindowWidth = 1200f");
    expect(source).toContain("GetWindowWithRect<UnityMcpSetupWindow>");
    expect(source).toContain("new Vector2(MinimumWindowWidth, MinimumWindowHeight)");
    expect(style).toContain(".mcp-tabs");
    expect(style).toContain("flex-wrap: wrap");
    expect(style).toContain("overflow: visible");
    expect(style).toContain("flex-shrink: 0");
  });

  it("shows the installed package version in the MCP Window title without health probes", () => {
    const source = fs.readFileSync(windowPath, "utf8");
    const open = methodBody(source, "OpenForCommand");
    const enable = methodBody(source, "OnEnable");
    const create = methodBody(source, "BuildGui");
    const refreshTitle = methodBody(source, "RefreshWindowTitle");
    const resolver = methodBody(source, "InstalledPackageVersion");

    expect(source).toContain('const string UnityMcpPackageId = "com.sungeargames.unity-mcp"');
    expect(source).toContain('return "SGG Unity MCP - " +');
    expect(source).toContain('"package version unavailable"');
    expect(resolver).toContain("UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UnityMcpSetupWindow).Assembly)");
    expect(resolver).toContain("UnityMcpPackageId");
    expect(open).toContain("BuildWindowTitle(InstalledPackageVersion())");
    expect(open).toContain("window.RefreshWindowTitle()");
    expect(enable).toContain("RefreshWindowTitle()");
    expect(create).toContain("RefreshWindowTitle()");
    expect(refreshTitle).toContain("titleContent = new GUIContent");
    expect(refreshTitle + resolver).not.toMatch(/GatewayProcessManager|RefreshAll|RefreshProcess|UnityWebRequest|HealthUrl/);
  });

  it("builds a first-tab FTUE from existing setup and permission actions", () => {
    const source = readWindowSources();
    const buildBody = methodBody(source, "BuildFtuePanel");
    const refreshBody = methodBody(source, "RefreshFtue");
    const visibilityBody = methodBody(source, "ApplyFtueVisibility");
    const bridgeServer = fs.readFileSync(path.join(editorRoot, "Bridge", "BridgeServer.cs"), "utf8");
    const steps = [
      "1. Unity bridge", "2. Portable Node", "3. Gateway Runtime", "4. Gateway Process",
      "5. MCP Token", "6. MCP Client Config", "7. Permissions", "8. Readonly permissions",
      "9. Allow All permissions", "10. First MCP Smoke Test",
    ];

    expectInOrder(buildBody, steps);
    expect(buildBody).toContain('RunAction("Install Portable Node", () => NodeRuntimeInstaller.InstallPortableNode(true))');
    expect(buildBody).toContain('RunAction("Install Gateway Runtime", InstallGatewayRuntime, true)');
    expect(buildBody).toContain('AddButton(processActions, "Start or Restart Gateway", StartOrRestartGateway)');
    expect(buildBody).toContain("CreateOrUpdateOpenCodeConfig");
    expect(buildBody).toContain('RunAction("Create Config/Policy Files", GatewayPolicyManager.EnsureUserConfig)');
    expect(buildBody).toContain('ConfirmApplyProfile(GatewayPolicyManager.ReadonlyProfile, "Readonly")');
    expect(buildBody).toContain("ConfirmAllowAll(false)");
    expect(buildBody).not.toContain("ConfirmAllowAll(true)");
    expect(refreshBody).toContain("McpFtueProfileTracker.HasCurrentExplicitProfile(ProjectPaths.ProjectRoot");
    expect(refreshBody).toContain("TokenContractReady = networkStatus.BindSecurityReady");
    expect(refreshBody).toContain("PermissionProfileReady = profileReady");
    expect(refreshBody).toContain("SmokeTestReady = McpFtueSmokeTracker.HasSuccessfulCompileStatus(ProjectPaths.ProjectRoot)");
    expect(refreshBody).toContain("ApplyFtueVisibility(!state.IsComplete)");
    expect(visibilityBody).toContain('SelectTab("FTUE")');
    expect(visibilityBody).toContain('SelectTab("Setup")');
    expect(bridgeServer).toContain('if (builtInCommand && command == "compile.status")');
    expect(bridgeServer).toContain("McpFtueSmokeTracker.RecordSuccessfulCompileStatus()");
  });

  it("adds conditional header shortcuts for actionable setup tasks", () => {
    const source = readWindowSources();
    const style = fs.readFileSync(windowStylePath, "utf8");
    const shortcutsBody = methodBody(source, "BuildHeaderShortcuts");
    const refreshBody = methodBody(source, "RefreshHeaderShortcuts");

    for (const shortcut of ["N+", "R+", "R↑", "S+", "CP+", "G*", "H*", "P+", "P∞"]) {
      expect(shortcutsBody).toContain(`AddHeaderShortcutButton("${shortcut}"`);
    }

    expect(source).toContain("m_installPortableNodeShortcutButton");
    expect(shortcutsBody).toContain("NodeRuntimeInstaller.InstallPortableNode(true)");
    expect(shortcutsBody).toContain("m_headerShortcutBar.style.display = DisplayStyle.None");
    expect(shortcutsBody).toContain("ConfirmAllowAll(true)");
    expect(shortcutsBody).toContain("AddBaselinePermissions");
    expect(shortcutsBody).toContain("RestartGateway");
    expect(source).toContain("button.style.display = DisplayStyle.None");
    expect(source).toContain("button.style.display = needed ? DisplayStyle.Flex : DisplayStyle.None");
    expect(refreshBody).toContain("var nodeStatus = NodeRuntimeInstaller.GetStatus()");
    expect(refreshBody).toContain("SetHeaderShortcut(m_installPortableNodeShortcutButton, !nodeStatus.IsPortable");
    expect(refreshBody).toContain("nodeStatus.IsPortable && GatewayRuntimeInstallShortcutNeeded(gatewayStatus)");
    expect(refreshBody).toContain("nodeStatus.IsPortable && GatewayRuntimeUpdateShortcutNeeded(gatewayStatus)");
    expect(refreshBody).toContain('m_updateRuntimeShortcutButton.text = runtimeDowngrade ? "R↓" : "R↑"');
    expect(refreshBody).toContain('m_updateRuntimeShortcutButton.AddToClassList("mcp-danger-button")');
    expect(refreshBody).toContain('m_updateRuntimeShortcutButton.RemoveFromClassList("mcp-danger-button")');
    expect(refreshBody).toContain("CanonicalPathRegistryActionNeeded()");
    expect(refreshBody).toContain("ReadCanonicalPathDependencyState()");
    expect(refreshBody).toContain('canonicalPathState.IsUpdate ? "CP↑" : "CP+"');
    expect(refreshBody).toContain("McpRuntimeHealthViewState.Evaluate(processStatus, gatewayStatus, LocalBridgeHeartbeatStale())");
    expect(refreshBody).toContain("runtimeHealth.StartOrRestartActionNeeded");
    expect(refreshBody).toContain("runtimeHealth.HealthRefreshActionNeeded");
    expect(refreshBody).toContain("PermissionsBaselineActionNeeded(policyStatus)");
    expect(refreshBody).toContain("PermissionsAllowAllCompletionNeeded(policyStatus)");
    expect(refreshBody).toContain("m_headerShortcutBar.style.display = anyNeeded ? DisplayStyle.Flex : DisplayStyle.None");
    expect(source).toContain("RefreshHeaderShortcuts(null, processStatus, null)");
    expect(source).toContain("GatewayRuntimePackageAvailable(gatewayStatus)");
    expect(source).toContain("AllKnownFeatureFlagsEnabled(policyStatus.ConfigPath)");
    expect(source).toContain("ProjectPolicyAllowsAll(policyStatus.PolicyPath, policyStatus.ProjectId)");
    expect(source).toContain('return allow.Contains("*") && ask.Count == 0 && deny.Count == 0');
    expect(source).toContain('AddHeaderShortcutButton("G*", "Start or Restart Gateway", RestartGateway)');
    expect(source).toContain("SetActionNeeded(m_addCanonicalPathRegistryButton, registryNeeded");
    expect(source).toContain("SetActionNeeded(m_installCanonicalPathButton, state.CanApply");
    expect(style).toContain(".mcp-header-shortcuts");
    expect(style).toContain(".mcp-header-shortcut-button");
    expect(style).toContain("min-width: 34px");
  });

  it("keeps Gateway process actions off the Unity main thread and bounds health without UnityWebRequest polling", () => {
    const source = fs.readFileSync(windowPath, "utf8");
    const setupStatus = fs.readFileSync(setupStatusPath, "utf8");
    const ftue = fs.readFileSync(ftuePath, "utf8");
    const permissions = fs.readFileSync(permissionsCommandsPath, "utf8");
    const manager = fs.readFileSync(gatewayProcessManagerPath, "utf8");
    const processUtil = fs.readFileSync(processUtilPath, "utf8");
    const processAction = methodBody(source, "RunGatewayProcessAction");
    const completion = methodBody(source, "CompleteGatewayProcessAction");
    const probe = methodBody(manager, "ProbeHealth");

    expect(processAction).toContain("s_gatewayProcessActionInProgress");
    expect(processAction).toContain("Task.Run");
    expect(processAction).toContain("TaskScheduler.FromCurrentSynchronizationContext()");
    expect(processAction).toContain("GatewayProcessManager.StartOrRestartGateway(gatewayStatus, windowsEditor, forceRestart)");
    expect(processAction).toContain("GatewayProcessManager.StopGateway(windowsEditor)");
    expect(completion).toContain("SetGatewayProcessActionButtonsEnabled(true)");
    expect(completion).toContain("RefreshAll(true)");
    expect(source).toContain("m_gatewayProcessActionGeneration++");
    expect(setupStatus).toContain('m_startGatewayButton = AddButton(processActions, "Start Gateway", StartOrRestartGateway)');
    expect(setupStatus).toContain('m_stopGatewayButton = AddButton(processActions, "Stop Gateway", StopGateway)');
    expect(setupStatus).toContain('AddButton(connectionActions, "Restart Gateway", RestartGateway)');
    expect(ftue).toContain('AddButton(processActions, "Start or Restart Gateway", StartOrRestartGateway)');
    expect(permissions).toContain('AddButton(policyActions, "Restart Gateway", RestartGateway)');
    expect(probe).toContain("HttpWebRequest");
    expect(probe).toContain("request.Timeout = HealthTimeoutMs");
    expect(probe).toContain("request.ReadWriteTimeout = HealthTimeoutMs");
    expect(probe).toContain('phase = "response body"');
    expect(probe).toContain('"Gateway health " + phase + ": "');
    expect(probe).toContain("exception.Status != WebExceptionStatus.ProtocolError");
    expect(probe).toContain("MaxHealthBodyCharacters");
    expect(probe).not.toMatch(/UnityWebRequest|operation\.isDone|Thread\.Sleep/);
    expect(processUtil).toContain("process.Exited += (_, __) => AppendProcessExitLogLine(logLock, logPath, process)");
    expect(processUtil).toContain("process.EnableRaisingEvents = true");
    expect(processUtil).toContain('AppendProcessLogLine(logLock, logPath, "exit", "pid=" + process.Id + " exit_code=" + process.ExitCode)');
  });

  it("falls back from a timed-out managed health request to bounded authenticated loopback TCP", () => {
    const manager = fs.readFileSync(gatewayProcessManagerPath, "utf8");
    const probe = methodBody(manager, "ProbeHealth");
    const fallback = methodBody(manager, "TryProbeHealthOverTcp");
    const request = methodBody(manager, "TryBuildHealthTcpRequest");
    const response = methodBody(manager.slice(manager.indexOf("internal static bool TryParseHealthTcpResponse(")), "TryParseHealthTcpResponse");

    expect(probe).toContain('phase == "request" && exception.Status == WebExceptionStatus.Timeout');
    expect(probe).toContain("s_preferTcpHealth = true");
    expect(fallback).toContain("IPAddress.Loopback, HealthPort");
    expect(fallback).toContain("TryBuildHealthTcpRequest(token, out var requestBytes, out error)");
    expect(fallback).toContain("MaxHealthHeaderBytes + 4 * MaxHealthBodyCharacters");
    expect(fallback).toContain("TryParseHealthTcpResponse(payload.ToArray(), out body, out error)");
    expect(request).toContain('"GET /health HTTP/1.0\\r\\nHost: 127.0.0.1:"');
    expect(request).toContain("McpTokenHeaderName");
    expect(request).toContain("character < '!' || character > '~'");
    expect(response).toContain("if (statusCode < 200 || statusCode >= 300)");
    expect(response).toContain("new UTF8Encoding(false, true)");
  });

  it("classifies and explicitly confirms Gateway runtime downgrades", () => {
    const source = fs.readFileSync(setupStatusPath, "utf8");
    const ftue = fs.readFileSync(ftuePath, "utf8");
    const installer = fs.readFileSync(gatewayInstallerPath, "utf8");
    const classifyBody = methodBody(source, "GatewayRuntimeDowngradeAvailable");
    const installBody = methodBody(source, "InstallGatewayRuntime");
    const admissionBody = methodBody(installer, "ValidateRuntimeInstallAdmission");

    expect(classifyBody).toContain("GatewayRuntimeUpdateShortcutNeeded(gatewayStatus)");
    expect(classifyBody).toContain("GatewayInstaller.TryCompareInstalledAndPackageVersions(gatewayStatus, out var comparison)");
    expect(classifyBody).toContain("comparison > 0");
    expectInOrder(installBody, [
      "GatewayInstaller.GetStatus()",
      "GatewayRuntimeDowngradeAvailable(gatewayStatus)",
      'EditorUtility.DisplayDialog("Downgrade Gateway Runtime"',
      "GatewayInstaller.InstallSourceDependencies(GatewayInstaller.CreateRuntimeDowngradeConfirmation(gatewayStatus))",
    ]);
    expect(installBody).toContain('"Downgrade", "Cancel"');
    expect(installBody).toContain("GatewayInstaller.InstallSourceDependencies()");
    expect(installer).toContain("if (!status.RuntimeFresh && !status.HasBundledCli)");
    expect(installer).toContain("ValidateRuntimeInstallAdmission(gatewayStatus");
    expect(admissionBody).toContain("TryCompareInstalledAndPackageVersions(status, out var comparison)");
    expect(admissionBody).toContain("confirmation.Matches(status)");
    expect(admissionBody).toContain("explicitly confirm the downgrade");
    expect(admissionBody).toContain("version direction could not be determined");
    expect(ftue).toContain('m_ftueInstallRuntimeButton.text = runtimeDowngrade ? "Downgrade Gateway Runtime" : "Install Gateway Runtime"');
    expect(ftue).toContain('m_ftueInstallRuntimeButton.AddToClassList("mcp-danger-button")');
  });

  it("probes gateway health with the session MCP token when present", () => {
    const source = fs.readFileSync(gatewayProcessManagerPath, "utf8");

    expect(source).toContain('const string McpTokenEnvName = "SGG_UNITY_MCP_TOKEN"');
    expect(source).toContain('const string McpTokenHeaderName = "X-SGG-Unity-MCP-Token"');
    expect(source).toContain("Environment.GetEnvironmentVariable(McpTokenEnvName)");
    expect(source).toContain("request.Headers[McpTokenHeaderName] = token");
  });

  it("restarts stale authenticated gateway listeners before starting", () => {
    const source = fs.readFileSync(gatewayProcessManagerPath, "utf8");

    expect(source).toContain("IsUnauthorizedHealthFailure(currentStatus.Message)");
    expect(source).toContain("health rejected the current MCP token");
    expect(source).toContain('message.IndexOf("401", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('message.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('message.IndexOf("invalid_token", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain("return StopGatewayByListeningPort(windowsEditor, out message, allowAbsentGateway)");
  });

  it("fails closed instead of launching a second gateway after a transient health failure", () => {
    const source = fs.readFileSync(gatewayProcessManagerPath, "utf8");
    const processUtil = fs.readFileSync(processUtilPath, "utf8");
    const startBody = methodBody(source, "StartOrRestartGateway");
    const inspectBody = methodBody(source, "TryInspectListeningPid");

    expectInOrder(startBody, [
      "GetStatus(true)",
      "TryInspectListeningPid(windowsEditor, out var listenerPid, out var listenerMessage)",
      "return StartGateway(nodeStatus, gatewayStatus)",
    ]);
    expect(startBody).toMatch(/if \(!TryInspectListeningPid\(windowsEditor, out var listenerPid, out var listenerMessage\)\)\s*\{\s*return NodeRuntimeInstaller\.InstallResult\.Fail\("Gateway health probe failed and the local listener could not be inspected\. No process was started\. "/s);
    expect(startBody).toContain("WaitForHealth(ExistingListenerHealthRecoveryTimeoutMs, out var recoveredHealthBody, out var recoveredHealthError, false)");
    expect(startBody).toContain("PopulateHealthMetadata(currentStatus)");
    expect(startBody).toContain("string.IsNullOrEmpty(currentStatus.HealthVersion) || string.IsNullOrEmpty(gatewayStatus.InstalledRuntimeVersion)");
    expect(startBody).toContain("Gateway health recovered during the bounded retry");
    expect(startBody).toContain("StopRunningGateway(windowsEditor, out var recoveredStopMessage)");
    expect(startBody).toContain("remained unavailable after a bounded retry. No second process was started. Use the explicit Restart Gateway action");
    expect(inspectBody).toContain('ProcessUtil.TryRunFiltered("netstat.exe", "-ano -p tcp"');
    expect(inspectBody).toContain("line => IsListeningPortLine(line, HealthPort)");
    expect(inspectBody).toContain("if (outputTruncated)");
    expect(inspectBody).toContain("TryFindListeningPid(stdout, HealthPort, out pid, out var parseMessage)");
    expect(processUtil).toContain("return process.ExitCode == 0 && !stdoutFilterFailed");
    expect(processUtil).toContain('AppendBounded(error, errorLock, "stdout filter failed: " + exception.Message)');
  });

  it("makes G* an explicit restart while preserving inspected cold start and ordinary Start refusal", () => {
    const window = fs.readFileSync(windowPath, "utf8");
    const setup = fs.readFileSync(setupStatusPath, "utf8");
    const manager = fs.readFileSync(gatewayProcessManagerPath, "utf8");
    expect(window).toContain('AddHeaderShortcutButton("G*", "Start or Restart Gateway", RestartGateway)');
    expect(methodBody(window, "RestartGateway")).toContain('RunGatewayProcessAction("Restart Gateway", true, false)');
    expect(methodBody(setup, "StartOrRestartGateway")).toContain('RunGatewayProcessAction("Start or Restart Gateway", false, false)');
    expect(methodBody(setup, "RefreshHeaderShortcuts")).toContain("Restart interrupts current connections; no separate Stop is required.");
    expect(methodBody(setup, "UpdateGatewayActionButtons")).toContain("GatewayStartOrRestartTooltip(gatewayStatus, runtimeHealth.ProcessNeedsRestart)");
    expect(methodBody(manager, "StartGateway")).toContain("Application.platform == RuntimePlatform.WindowsEditor, false)");
    const start = methodBody(manager, "StartOrRestartGateway");
    expectInOrder(start, ["if (forceRestart)", "StopRunningGateway(windowsEditor, out var forcedStopMessage, allowAbsentGateway: true)", "var forcedStart = StartGateway(nodeStatus, gatewayStatus)"]);
    const stop = methodBody(manager, "StopRunningGateway");
    expect(stop.match(/StopGatewayByListeningPort\(windowsEditor, out message, allowAbsentGateway\)/g)).toHaveLength(2);
    const inspected = methodBody(manager, "StopInspectedGatewayListener");
    expectInOrder(inspected, ["if (!inspect(out var pid, out message))", "if (pid < 0)", "if (pid == 0)", "return allowAbsentGateway", "return stop(pid, out message)"]);
    expect(methodBody(manager, "StopGatewayByListeningPort")).toContain("if (!windowsEditor)");
    expect(start).toContain("No second process was started. Use the explicit Restart Gateway action");
  });

  it("offers a confirmed CanonicalPath registry upgrade without converting sources or repeating changed requests", () => {
    const setup = fs.readFileSync(setupStatusPath, "utf8");
    const action = methodBody(setup, "InstallCanonicalPathPackage");
    expectInOrder(action, ["var state = ReadCanonicalPathDependencyState()", "if (!state.CanApply)",
      'state.IsUpdate ? "Update CanonicalPath" : "Install CanonicalPath"', "EditorUtility.DisplayDialog(McpWindowLocalization.Text(action)",
      "state.SameEvidence(ReadCanonicalPathDependencyState())", "UnityEditor.PackageManager.Client.Add(packageSpec)"]);
    expect(action).toContain("CanonicalPath change cancelled; no package request was sent.");
    expect(action).toContain("CanonicalPath state changed during confirmation");
    expect(action).toContain("!IsCanonicalPathRegistryConfigured()");
    expect(action).not.toContain("RegisteredPackageSummary(CanonicalPathPackageId)");
    const reader = methodBody(setup, "ReadCanonicalPathDependencyState");
    expect(reader).toContain("PackageInfo.GetAllRegisteredPackages()");
    expect(reader).toContain("installed.version");
    expect(reader).toContain("installed.source.ToString()");
    expect(reader).toContain("TryReadCanonicalPathManifestDependency(stream, out declared)");
    expect(reader).not.toContain("File.ReadAllText");
    const manifestReader = methodBody(setup, "TryReadCanonicalPathManifestDependency");
    expect(manifestReader).toContain("new byte[CanonicalPathManifestMaxBytes + 1]");
    expect(manifestReader).toContain("if (total > CanonicalPathManifestMaxBytes) return false");
    expect(manifestReader).toContain("PipelineJsonEvidenceParser.TryReadOptionalStringPath");
    expect(reader).toContain("CanonicalPathBridge.IsAvailable");
    const checklist = methodBody(setup, "RefreshSetupChecklist");
    expect(checklist).toContain('canonicalPathState.IsReady ? "ok"');
    expect(checklist).not.toContain('canonicalPathInstalled ? "ok"');
    const buttons = methodBody(setup, "UpdateCanonicalPathActionButtons");
    expect(buttons).toContain('state.IsUpdate ? "Update CanonicalPath" : "Install CanonicalPath"');
    expect(buttons).toContain('m_installCanonicalPathButton.text = McpWindowLocalization.Text(');
    expect(buttons).toContain("m_installCanonicalPathButton.SetEnabled(state.CanApply)");
    for (const language of ["en", "ru"]) {
      const xml = fs.readFileSync(path.join(packageRoot, "Editor/UI/Localization", `mcp-window.${language}.xlf`), "utf8");
      expect(xml).toContain('<trans-unit id="canonical.update">');
      expect(xml).toContain('<trans-unit id="canonical.versions">');
      expect(xml).toContain('<trans-unit id="canonical.changed">');
      const ids = xliffTransUnitIds(xml);
      expect(new Set(ids).size).toEqual(ids.length);
    }
  });

  it("parses and renders bounded typed nested runtime policy health", () => {
    const source = readWindowSources();
    const processManager = fs.readFileSync(gatewayProcessManagerPath, "utf8");
    const policyManager = fs.readFileSync(gatewayPolicyManagerPath, "utf8");
    const installer = fs.readFileSync(gatewayInstallerPath, "utf8");
    const policyViewModel = fs.readFileSync(runtimePolicyViewModelPath, "utf8");

    expect(processManager).toContain("JsonUtility.FromJson<HealthPayload>(healthBody)");
    expect(processManager).toContain("public PolicyHealthPayload policy");
    expect(processManager).toContain("internal static GatewayRuntimeStatus ParseHealthMetadata(string healthBody)");
    expect(processManager).toContain('Allowed(policy.state, "unconfigured", "active", "degraded", "error")');
    expect(processManager).toContain('NullableAllowed(policy.last_error_kind, "missing", "unreadable", "too_large", "unstable", "invalid_json", "invalid_schema")');
    expect(processManager).toContain('NullableAllowed(policy.last_error_code, "policy_reload_failed", "policy_invalid")');
    expect(processManager).toContain("ValidPolicyRevision(policy.effective_policy_revision)");
    expect(processManager).toContain("DateTimeOffset.TryParseExact");
    expect(processManager).toContain("BoundedHealthText(payload.version, 64)");
    expect(processManager).toContain("PolicyMetadataMalformed = true");
    expect(processManager).toContain('!Commands.JsonUtil.HasProperty(healthBody, "policy") || payload.policy == null');
    expect(processManager).not.toContain("Regex.Match");
    expect(processManager).not.toContain("JsonString(");
    expect(processManager).not.toContain("JsonInt(");

    expect(source).toContain('AddRow(processSection, "Health Summary")');
    expect(source).toContain('AddRow(gatewaySection, "Health Summary")');
    expect(source).toContain('AddRow(runtimeSection, "Runtime policy")');
    expect(source).toContain('AddRow(policySection, "Runtime Policy")');
    expect(source).toContain("bodyLabel.text = status.HealthSummary");
    expect(source).not.toContain('AddRow(processSection, "Health Body")');
    expect(source).not.toContain('AddRow(gatewaySection, "Health Body")');
    expect(policyViewModel).toContain("Risky commands fail closed; permitted reads use last-known-good policy");
    expect(policyViewModel).toContain("Restore a valid policy, then Refresh Health");
    expect(policyViewModel).toContain("No blocked command is replayed automatically");
    const runtimeHealthState = fs.readFileSync(runtimeHealthStatePath, "utf8");
    expect(runtimeHealthState).toContain("!processStatus.PolicyDegraded");
    expect(runtimeHealthState).toContain("!processStatus.PolicyMetadataMalformed");
    expect(source).toContain("runtimeHealth.ProcessNeedsRestart");
    expect(source).not.toContain("Restart Gateway after policy changes");
    expect(policyManager).not.toMatch(/Restart Gateway[^\n]*(?:policy|permission)|(?:policy|permission)[^\n]*Restart Gateway/i);
    expect(installer).toContain("Policy-only changes reload live");
  });

  it("clears Gateway token auth when applying loopback mode", () => {
    const source = fs.readFileSync(gatewayNetworkModeManagerPath, "utf8");

    expect(source).toContain('RemoveMcpValue(source, "token_env")');
    expect(source).toContain("disabled Gateway token auth for Windows loopback");
  });

  it("declares onboarding row primitives and per-tab help text", () => {
    const source = readWindowSources();

    for (const helper of ["HelpRow", "InfoRow", "StateRow", "InputRow", "SecretInputRow", "OptionalToolRow"]) {
      expect(source).toContain(helper);
    }

    expect(source).toContain("return InfoRow(parent, key, value)");
    for (const text of [
      "Setup installs and starts the local Gateway pieces",
      "Status is read-only health and connection visibility",
      "Configure what the local Gateway exposes to MCP clients",
      "Commands is a read-only catalog browser",
      "Plans is a non-applying scaffold",
      "Skills shows local skill locations and review status",
      "Unity AI Assistant is optional compatibility guidance",
      "Diagnostics is read-only troubleshooting output with secret redaction",
      "Language affects only this MCP Window",
    ]) {
      expect(source).toContain(text);
    }

    expect(source).toContain("mcp-help-row");
    expect(source).toContain("mcp-input-row");
    expect(source).toContain("mcp-input-field");
    expect(source).toContain("mcp-secret-row");
    expect(source).toContain("mcp-status-optional");
  });

  it("keeps a single MCP Window menu item and lets tabs handle navigation", () => {
    const source = fs.readFileSync(windowPath, "utf8");

    expect(source).toContain('[MenuItem("SGG/Unity MCP/MCP Window")]');
    for (const menu of ["Setup", "Status", "Commands", "Plans", "Skills", "Unity AI Assistant", "Diagnostics", "Settings"]) {
      expect(source).not.toContain(`[MenuItem("SGG/Unity MCP/${menu}")]`);
    }

    expect(source).toContain("GetWindowWithRect<UnityMcpSetupWindow>");
    expect(source).toContain("window.WindowState.SelectTab(tabName)");
    expect(source).toContain("window.SelectTab(window.WindowState.SelectedTab)");
    expect(source).toContain("var selectedTab = WindowState.SelectedTab");
    expect(source).toContain("SelectTab(selectedTab)");
  });

  it("extracts bounded serializable tab and pure runtime health view state", () => {
    const source = readWindowSources();
    const windowState = fs.readFileSync(windowStatePath, "utf8");
    const runtimeHealthState = fs.readFileSync(runtimeHealthStatePath, "utf8");

    expect(source).toContain("[SerializeField]\n        McpWindowState m_windowState = new McpWindowState();");
    expect(source).toContain("return WindowState.SelectedTab");
    expect(source).toContain("[SerializeField, HideInInspector]\n        string m_currentTab = string.Empty;");
    expect(source).toContain("if (!string.IsNullOrEmpty(m_currentTab))");
    expect(source).toContain("if (!m_windowState.HasExplicitSelection)");
    expect(source).toContain("m_windowState.SelectTab(m_currentTab)");
    expect(source).toContain("m_currentTab = string.Empty;");
    expect(source.match(/m_currentTab/g)).toHaveLength(4);
    expect(source).not.toContain("static string NormalizeTab");
    expect(windowState).toContain("[Serializable]");
    expect(windowState).toContain("public static bool IsSupportedTab");
    expect(windowState).toContain("internal bool HasExplicitSelection");
    expect(windowState).toContain("string m_selectedTab = string.Empty");
    expect(windowState).not.toContain("m_hasExplicitSelection");
    expect(windowState).not.toContain("UnityEditor");
    expect(windowState).not.toContain("UIElements");

    for (const decision of [
      "VersionMismatch",
      "ActionableStaleProjects",
      "ProcessNeedsRestart",
      "StartOrRestartActionNeeded",
      "HealthRefreshActionNeeded",
      "CurrentHealthyRuntime",
    ]) {
      expect(runtimeHealthState).toContain(`public bool ${decision}`);
      expect(source).toContain(`.${decision}`);
    }
    expect(runtimeHealthState).not.toMatch(/System\.IO|File\.|Process\.|UnityEditor|UIElements/);
  });

  it("keeps the Commands tab as a read-only real catalog display", () => {
    const source = readWindowSources();
    const commandsBody = methodBody(fs.readFileSync(permissionsCommandsPath, "utf8"), "BuildCommandsPanel");

    expect(commandsBody).toContain("Read-only command catalog display");
    expect(commandsBody).toContain("This tab does not execute Unity commands directly");
    expect(commandsBody).toContain("UnityBridgeRuntime.CommandDescriptors");
    expect(commandsBody).toContain("new ListView");
    expect(commandsBody).toContain("selectionChanged += OnCommandSelectionChanged");
    expect(commandsBody).toContain("BindCommandListItem");
    expect(commandsBody).toContain("mcp-command-row");
    expect(commandsBody).toContain("Group");
    expect(commandsBody).toContain("Risk");
    expect(commandsBody).toContain("Feature flag");
    expect(commandsBody).toContain("Policy action");
    expect(commandsBody).toContain("feature_hidden");
    expect(commandsBody).toContain("draft_changed");
    expect(commandsBody).toContain("Search command id/title/description");
    expect(commandsBody).toContain("m_commandsSearchField.RegisterValueChangedCallback(_ => RefreshCommandsAndLocalize())");
    expect(commandsBody).toContain("m_commandsPolicyFilter.RegisterValueChangedCallback(_ => RefreshCommandsAndLocalize())");
    expect(commandsBody).toContain("Status badges");
    expect(commandsBody).toContain("Availability / why unavailable");
    expect(commandsBody).toContain("args_json template");
    expect(commandsBody).toContain("Copy args_json Template");
    expect(commandsBody).toContain("Copy Schema");
    expect(commandsBody).toContain("Copy policy.explain args_json");
    expect(commandsBody).toContain("Copy Effective Policy Summary");
    expect(commandsBody).toContain("Copy sgg_unity_call Snippet");
    expect(commandsBody).toContain("artifact");
    expect(commandsBody).toContain("profiler");
    expect(source).toContain("MatchesCommandFilters");
    expect(source).toContain("MatchesPolicyActionFilter");
    expect(source).toContain("CommandPolicyDraftExactChanged");
    expect(source).toContain("CommandPolicyDraftExactChangeSummary");
    expect(source).toContain("policy_draft_exact_change");
    expect(source).toContain("descriptor.Title ?? string.Empty");
    expect(source).toContain("descriptor.Description ?? string.Empty");
    expect(source).toContain("CommandStatusBadges");
    expect(source).toContain("CommandRowStateClass");
    expect(source).toContain("PolicyActionBucket");
    expect(source).toContain("FeatureStatusBucket");
    expect(source).toContain("mcp-command-row--feature-hidden");
    expect(source).toContain("CommandPreview");
    expect(source).toContain("FeatureFlagStatus");
    expect(source).toContain("PolicyDecisionSummary");
    expect(source).toContain("GatewayVisibility");
    expect(source).toContain("why_unavailable");
    expect(source).toContain("feature_disabled");
    expect(source).toContain("permission_denied");
    expect(source).toContain("handler_unavailable");
    expect(source).toContain("catalog/manifest entry exists");
    expect(source).toContain("package/source/trust");
    expect(source).toContain("supports_job/supports_cancel");
    expect(commandsBody).not.toContain("RunAction");
    expect(commandsBody).not.toContain("GatewayProcessManager");
    expect(commandsBody).not.toContain("EditorApplication.ExecuteMenuItem");
    expect(commandsBody).not.toContain("sgg_unity_call(");
  });

  it("adds selected command copy helpers without executing or mutating policy", () => {
    const source = fs.readFileSync(permissionsCommandsRefreshPath, "utf8");
    const schemaBody = methodBody(source, "CopySelectedCommandSchema");
    const argsBody = methodBody(source, "CopySelectedCommandArgsExample");
    const explainBody = methodBody(source, "CopySelectedCommandPolicyExplainArgsJson");
    const effectivePolicyBody = methodBody(source, "CopySelectedCommandEffectivePolicySummary");
    const snippetBody = methodBody(source, "CopySelectedCommandCallSnippet");

    expect(source).toContain("PolicyExplainArgsJson(CommandDescriptor command)");
    expect(source).toContain("CopySelectedCommandEffectivePolicySummary");
    expect(source).toContain("EscapeSnippetString(string value)");
    expect(schemaBody).toContain("SelectedCommand()");
    expect(schemaBody).toContain("command.SchemaJson");
    expect(argsBody).toContain("ExampleArgsJson(command.SchemaJson)");
    expect(explainBody).toContain("PolicyExplainArgsJson(command)");
    expect(effectivePolicyBody).toContain("FeatureFlagStatus(command.FeatureFlag)");
    expect(effectivePolicyBody).toContain("PolicyDecisionSummary(command)");
    expect(effectivePolicyBody).toContain("GatewayVisibility(command, featureStatus, policyDecision)");
    expect(effectivePolicyBody).toContain("WhyUnavailable(command, featureStatus, policyDecision)");
    expect(effectivePolicyBody).toContain("This copy helper does not execute the selected command");
    expect(source).toContain("JsonUtil.String(command.Id)");
    expect(snippetBody).toContain("sgg_unity_call(project=");
    expect(snippetBody).toContain("ProjectPaths.ProjectId");
    expect(snippetBody).toContain("EscapeSnippetString(argsJson)");

    for (const body of [schemaBody, argsBody, explainBody, effectivePolicyBody, snippetBody]) {
      expect(body).toContain("CopyText");
      expect(body).not.toContain("RunAction");
      expect(body).not.toContain("GatewayPolicyManager");
      expect(body).not.toContain("EditorApplication.ExecuteMenuItem");
      expect(body).not.toContain("UnityBridgeRuntime.Handle");
      expect(body).not.toContain("File.Write");
    }

    for (const body of [schemaBody, argsBody, explainBody, effectivePolicyBody]) {
      expect(body).not.toContain("sgg_unity_call(");
    }
  });

  it("uses bounded generic args_json templates for common required fields", () => {
    const source = fs.readFileSync(permissionsCommandsRefreshPath, "utf8");
    const signatureIndex = source.indexOf("string ExampleArgsJson(");
    expect(signatureIndex).toBeGreaterThanOrEqual(0);
    const addExampleIndex = source.indexOf("void AddExampleField(", signatureIndex);
    expect(addExampleIndex).toBeGreaterThan(signatureIndex);
    const exampleBody = source.slice(signatureIndex, addExampleIndex);

    expect(source).toContain("SchemaHasProperty(schemaJson, fieldName)");
    expect(source).toContain("schemaJson.Contains(\"\\\"\" + fieldName + \"\\\":{\")");
    for (const snippet of [
      '\\"command\\":\\"compile.status\\"',
      '\\"query\\":\\"example query\\"',
      '\\"asset_path\\":\\"Assets/Example.asset\\"',
      '\\"scene_path\\":\\"Assets/Scenes/Example.unity\\"',
      '\\"object_path\\":\\"ExampleRoot/ExampleChild\\"',
      '\\"package_name\\":\\"com.example.package\\"',
      '\\"name\\":\\"ExampleName\\"',
      '\\"content\\":\\"example bounded content\\"',
      '\\"report_id\\":\\"example-report\\"',
      '\\"menu_path\\":\\"GameObject/Create Empty\\"',
      '\\"type\\":\\"screenshot\\"',
      '\\"tool\\":\\"Move\\"',
      '\\"older_than_days\\":7',
      '\\"max_delete\\":10',
    ]) {
      expect(exampleBody).toContain(snippet);
    }

    expect(exampleBody).not.toContain("http://");
    expect(exampleBody).not.toContain("https://");
    expect(exampleBody).not.toContain("<YOUR_MCP_TOKEN>");
    expect(exampleBody).not.toContain("C:\\\\");
  });

  it("makes the Setup first page complete with checklist, token, client config, permissions and optional tools", () => {
    const source = readWindowSources();
    const setupBody = methodBody(source, "BuildSetupPanel");

    for (const label of [
      "Setup Checklist",
      "Unity bridge",
      "Portable Node",
      "Gateway Runtime",
      "Gateway Process",
      "MCP Token",
      "MCP Client Config",
      "Permissions policy",
      "Catalog policy summary",
      "Roslyn Compiler",
      "Unity AI Assistant",
      "required",
      "recommended",
      "optional",
      "Next action",
    ]) {
      expect(setupBody).toContain(label);
    }

    for (const label of [
      "Token env",
      "Token status",
      "Client header",
      "Session token input",
      "Generate Token",
      "Use Token For This Session",
      "Copy Client Header",
      "Copy Env Command",
      "Clear Session Token",
      "MCP Connection",
      "Connection mode",
      "Selected mode",
      "Gateway bind",
      "WSL URL",
      "Security status",
      "Apply Stdio Shim Mode",
      "Apply Remote HTTP Mode",
      "Copy Firewall Rule",
      "OpenCode workspace root",
      "OpenCode config",
      "Config target status",
      "Preview OpenCode Config Change",
      "Create/Update OpenCode Config",
      "Open OpenCode Config",
      "Refresh Connection",
      "MCP URL",
      "Health URL",
      "Project Id",
      "Copy MCP URL",
      "Copy OpenCode Config",
      "Copy Cursor Config",
      "Copy Assistant Config",
      "Copy First Calls",
      "First MCP Smoke Test",
      "Goal",
      "Confirm the client can see this Unity project and run one read-only status command.",
      "Calls",
      'sgg_unity_projects -> sgg_unity_describe(selector=\\"commands\\") -> compile.status',
      "Expected result",
      "compile.status returns editor compile state without mutating project assets.",
      "After success",
      "Open Permissions to choose a broader profile only for the workflow you need.",
      "Copy Smoke Test Calls",
      "MCP Window Preferences",
      "Language",
      "Current language",
      "Permission Setup",
      "Apply Readonly (optional)",
      "Restart Gateway",
      "Open Permissions",
      "Optional Tools",
      "Roslyn status",
      "Roslyn path",
      "Roslyn feature",
      "Roslyn policy risk",
      "Install Roslyn Compiler",
      "Uninstall Roslyn Compiler",
      "Copy Roslyn Status",
      "Open Commands Filter",
      "Install Unity AI Assistant (optional)",
      "Open Unity AI Details",
      "Copy Package Id",
    ]) {
      expect(setupBody).toContain(label);
    }

    expect(source).toContain("RefreshSetupChecklist");
    expect(source).toContain("CopySmokeTestCalls");
    expect(source).toContain("SmokeTestCallsText");
    expect(source).toContain("Expected: compile.status returns read-only editor compile state.");
    expect(source).toContain("AddChecklistRow(checklistSection, \"MCP Client Config\", string.Empty");
    expect(source).toContain("AddChecklistRow(checklistSection, \"Catalog policy summary\", string.Empty");
    expect(source).toContain("string ChecklistKeyText(string key, string importance)");
    expect(source).toContain("string.IsNullOrWhiteSpace(importance)");
    expect(source).toContain("GatewayVirtualCommandCount()");
    expect(source).toContain("CommandCountExpression(unityBridgeCommandCount, gatewayVirtualCommandCount)");
    expect(source).toContain("unityBridgeCommandCount + \"+\" + gatewayVirtualCommandCount");
    expect(source).toContain("return \"0\";");
    expect(source).toContain("Unity Bridge policy:");
    expect(source).toContain("assigned permissions:");
    expect(source).toContain('checklistState = assignedPermissions == 0 ? "error" : assignedPermissions == unityBridgeCommandCount ? "ok" : "info"');
    expect(source).toContain("Allow/default allow");
    expect(source).toContain("Ask/default ask");
    expect(source).toContain("Deny/default deny");
    expect(source).toContain("feature-hidden");
    expect(source).toContain("Gateway virtual");
    expect(source).toContain("RefreshMcpTokenSetup");
    expect(source).toContain("RefreshMcpConnection");
    expect(source).toContain("RefreshSetupPreferences");
    expect(source).toContain("RefreshMcpClientConfig");
    expect(source).toContain("RefreshOptionalToolsSetup");
    expect(source).toContain("EditorRoslynInstaller.Install(true)");
    expect(source).toContain("EditorRoslynInstaller.Uninstall()");
    expect(source).toContain("WrapRoslynResult(EditorRoslynInstaller.InstallResult result)");
    expect(source).toContain("SelectTab(\"Unity AI Assistant\")");
    expect(source).toContain("mcp-checklist-row");
    expect(source).toContain("mcp-checklist-field");
    expect(source).toContain("mcp-checklist-icon");
    expect(source).toContain("mcp-checklist-value");
    expect(source).toContain("InstallGatewayRuntime");
    expect(source).toContain("m_lastGatewayRuntimeError");
    expect(source).toContain("SetChecklistState");
    expect(source).toContain("ChecklistIcon");
    expect(source).toContain("◇");
    expect(source).toContain("mcp-checklist--ok");
    expect(source).toContain("mcp-checklist--warn");
    expect(source).toContain("mcp-checklist--error");
    expect(source).toContain("mcp-checklist--optional");
    expect(source).toContain("mcp-checklist--info");
    expect(source).toContain("mcp-checklist--loading");
    expect(source).toContain("◷");
  });

  it("keeps Setup token handling secret and copies only placeholders or env references", () => {
    const source = readWindowSources();
    const setupBody = methodBody(source, "BuildSetupPanel");
    const generateBody = methodBody(source, "GenerateSessionToken");
    const useBody = methodBody(source, "UseSessionToken");
    const copyHeaderBody = methodBody(source, "CopyClientHeaderPlaceholder");
    const copyEnvBody = methodBody(source, "CopyTokenEnvCommandPlaceholder");
    const opencodeBody = methodBody(source, "CopyOpenCodeConfig");
    const cursorBody = methodBody(source, "CopyCursorConfig");
    const assistantBody = methodBody(source, "CopyAssistantConfig");

    expect(setupBody).toContain("SecretInputRow(tokenSection, \"Session token input\")");
    expect(source).toContain("const string McpTokenEnvName = \"SGG_UNITY_MCP_TOKEN\"");
    expect(generateBody).toContain("RandomNumberGenerator.Create()");
    expect(generateBody).toContain("m_setupMcpSessionToken.value");
    expect(useBody).toContain("Environment.SetEnvironmentVariable(McpTokenEnvName, token.Trim())");
    expect(useBody).toContain("process only");
    expect(copyHeaderBody).toContain("X-SGG-Unity-MCP-Token: ${");
    expect(copyEnvBody).toContain("<YOUR_MCP_TOKEN>");
    expect(opencodeBody).toContain("GatewayNetworkModeManager.OpenCodeConfigSnippet");
    expect(source).toContain("ModeWindowsStdioLabel");
    expect(source).toContain("ModeRemoteHttpLabel");
    expect(source).toContain("Remote HTTP exposes the Gateway outside Windows loopback");
    expect(source).toContain("GatewayNetworkModeManager.PreviewOpenCodeConfig");
    expect(source).toContain("GatewayNetworkModeManager.WriteOpenCodeConfig");
    expect(cursorBody).toContain("stdio-shim");
    expect(cursorBody).toContain("<YOUR_MCP_TOKEN>");
    expect(assistantBody).toContain("\\\"enabled\\\": true");
    expect(assistantBody).toContain("\\\"type\\\": \\\"stdio\\\"");
    expect(assistantBody).toContain("stdio-shim");
    expect(assistantBody).toContain("<YOUR_MCP_TOKEN>");
    for (const body of [copyHeaderBody, copyEnvBody, opencodeBody, cursorBody, assistantBody]) {
      expect(body).not.toContain("m_setupMcpSessionToken.value");
      expect(body).not.toContain("Environment.GetEnvironmentVariable(McpTokenEnvName)");
    }
    expect(source).toContain("Token values stay in memory/process environment only");
    expect(source).toContain("copy helpers use env/placeholder text");
  });

  it("keeps language in Setup and removes Settings tab", () => {
    const source = readWindowSources();
    const setupBody = methodBody(source, "BuildSetupPanel");
    const tabsBody = methodBody(source, "BuildTabs");

    expect(setupBody).toContain("MCP Window Preferences");
    expect(setupBody).toContain("Required CanonicalPath");
    expect(setupBody).toContain("Add Scoped Registry");
    expect(setupBody).toContain("Install CanonicalPath");
    expect(setupBody).toContain("m_addCanonicalPathRegistryButton = AddButton");
    expect(setupBody).toContain("m_installCanonicalPathButton = AddButton");
    expect(setupBody).toContain("CanonicalPath is required for bridge discovery and path-bound MCP commands");
    expect(setupBody).toContain("Language");
    expect(source).toContain("RefreshSetupPreferences");
    expect(tabsBody).not.toContain("Settings");
    expect(source).not.toContain("BuildSettingsPanel");
  });

  it("polishes Permissions tab previews, project policy editing and approval visibility", () => {
    const source = readWindowSources();
    const permissionsBody = methodBody(source, "BuildPermissionsPanel");

    for (const label of ["Project Policy", "Project Policy Rules", "Saved project rules", "Allow rules", "Ask rules", "Deny rules", "Risk defaults", "Project Policy Editor", "Draft status", "Pattern format", "Allow patterns", "Ask patterns", "Deny patterns", "Change review", "Catalog Policy Summary", "Current catalog", "Feature Flags", "Current flags", "Selected Command Preview", "Risk", "Feature flag", "Policy decision", "Visibility", "Human result", "Local preview caveat", "One-shot Approvals", "Temporary ask bypass", "Auto-allow ask (temporary)", "Approvals dir", "Pending requests", "Approvals list", "Refresh Approvals"]) {
      expect(permissionsBody).toContain(label);
    }

    for (const label of ["Create Config/Policy Files", "Open Gateway Config", "Open Policy File", "Copy Project Policy Snippet", "Refresh Health", "Restart Gateway", "Refresh Rules", "Copy Policy Rules Summary", "Reload Saved Policy", "Reset Draft", "Review Draft", "Save Project Policy", "Refresh Catalog Policy", "Copy Catalog Policy Summary", "Copy Effective Policy Summary", "Draft Allow Rule", "Draft Ask Rule", "Draft Deny Rule", "Test Validation", "API Reflection", "Unity Docs", "Profiler Readonly", "Profiler Recording", "Apply Test Validation", "Apply API Reflection", "Apply Unity Docs", "Apply Profiler Readonly", "Apply Profiler Recording", "Apply + Enable Flags", "Enable All Feature Flags", "Danger: Allow All", "Enable All + Allow All"]) {
      expect(permissionsBody).toContain(label);
    }

    for (const text of [
      "Creates user-editable config and policy files if they are missing",
      "Advanced manual edit for feature flags and Gateway settings",
      "Advanced manual edit for the current Project Id policy",
      "Copy-only helper; it does not write files or change policy",
      "Policy-only changes do not require restart",
      "Read-only snapshot of saved policy.json for the current Project Id",
      "This slice does not save drafts, create approvals, consume approvals or execute commands",
      "Use profile buttons for coarse changes or Open Policy File for manual edits",
      "Edits only the current Project Id allow/ask/deny lists in local policy.json",
      "It does not change feature flags, risk defaults, approvals or execute commands",
      "One command id or wildcard per line",
      "Use exact ids such as compile.status or suffix wildcards such as asset.*",
      "Read-only summary of the current Unity Bridge command catalog against feature flags and project policy",
      "It does not execute commands or call policy.explain",
      "Feature flags make command families visible",
      "Feature flags live in Gateway config",
      "Policy is project-scoped allow/ask/deny rules",
      "Feature flags are stored in Gateway config",
      "Policy still decides allow/ask/deny",
      "test.run ask-gated",
      "test.run remains ask-gated",
      "Allows status, logs, package list",
      "Needs related read-only feature flags",
      "allows every built-in/trusted Gateway-visible command",
      "Pending requests are created by confirmation_required",
      "approvals live outside MCP",
      "Auto-allow ask does not bypass deny",
      "Disable when OpenCode/OpenChamber native prompts exist",
      "Gateway policy.explain is authoritative and copyable from Commands",
      "This UI never executes policy.explain or the selected command",
    ]) {
      expect(permissionsBody).toContain(text);
    }

    for (const text of [
      "matching feature flag is disabled or missing",
      "Gateway config, restart/refresh",
      "policy alone cannot show it",
      "Blocked by current Project Id policy",
      "apply the smallest matching profile or edit policy",
      "Do not bypass policy",
      "execution needs human approval outside MCP",
      "this UI cannot approve or consume approvals",
      "feature or policy changes alone may not fix it",
      "Visible and allowed by local preview",
    ]) {
      expect(source).toContain(text);
    }

    expect(permissionsBody).not.toContain("Not implemented: policy preview");
    expect(permissionsBody).not.toContain("Create User Config");
    expect(permissionsBody).not.toContain("Copy Snippet");
    expect(source).toContain("ReadApprovalsDir");
    expect(source).toContain("ReadPendingApprovalsSummary");
    expect(source).toContain("ReadApprovalsSummary");
    expect(source).toContain("No pending approval requests for current project");
    expect(source).toContain("pending_id");
    expect(source).toContain("action_hint");
    expect(source).toContain("ProjectPolicyReady");
    expect(source).toContain("ProjectPolicySource");
    expect(source).toContain("ReadProjectPolicyRulesSnapshot");
    expect(source).toContain("PolicySectionValues");
    expect(source).toContain("RiskDefaultsSummary");
    expect(source).toContain("ValuesSummary");
    expect(source).toContain("PolicyRulesSnapshot");
    expect(source).toContain("ProjectPolicyRulesState");
    expect(source).toContain("RefreshProjectPolicyEditor");
    expect(source).toContain("TryReadProjectPolicyDraft");
    expect(source).toContain("ProjectPolicyChangeReview");
    expect(source).toContain("ProjectPolicyExactDraftChangesSummary");
    expect(source).toContain("Exact command changes");
    expect(source).toContain("wildcard effects stay in Review Draft warnings");
    expect(source).toContain("Catalog command");
    expect(source).toContain("Draft action");
    expect(source).toContain("Draft action badge");
    expect(source).toContain("Draft Dropdown Rule");
    expect(source).toContain("RefreshPolicyEditorCommandChoices");
    expect(source).not.toContain("RefreshPolicyEditorCatalogRows");
    expect(source).not.toContain("BindPolicyEditorCatalogItem");
    expect(source).not.toContain("OnPolicyEditorCatalogSelectionChanged");
    expect(source).toContain("AddPolicyEditorDropdownRule");
    expect(source).not.toContain("mcp-policy-editor-catalog-list");
    expect(source).toContain("mcp-policy-action-field");
    expect(source).toContain("PolicyActionBadge");
    expect(source).toContain("PolicyActionClass");
    expect(source).toContain("CommandPolicyDraftExactAction");
    expect(source).not.toContain("draft_action=");
    expect(source).toContain("Wildcards and bulk edits stay in the raw pattern lists");
    expect(source).toContain("BroadPolicyPatternWarnings");
    expect(source).toContain("AddBroadPolicyPatternWarnings");
    expect(source).toContain("IsBroadPolicyPattern");
    expect(source).toContain("specific entry may be shadowed by local preview order");
    expect(source).toContain("AddSelectedCommandToPolicyDraft");
    expect(source).toContain("MoveExactPolicyPatternToSection");
    expect(source).toContain("RemoveExactPolicyPattern");
    expect(source).toContain("Review and Save Project Policy to write policy.json");
    expect(source).toContain("SaveProjectPolicyDraft");
    expect(source).toContain("SaveProjectPolicyRules");
    expect(source).toContain("UpsertProjectPolicyRules");
    expect(source).toContain("ProjectPolicyObjectBlock");
    expect(source).toContain("AtomicWriteText");
    expect(source).toContain("File.Replace");
    expect(source).toContain("This writes only the current Project Id allow/ask/deny lists in local policy.json");
    expect(source).toContain("Feature flags and risk defaults are unchanged; approvals are not created, consumed or deleted");
    expect(source).toContain("RefreshCatalogPolicySummary");
    expect(source).toContain("CopyCatalogPolicySummary");
    expect(source).toContain("StripRichText(CatalogPolicySummaryText(out _))");
    expect(source).toContain("featureHidden++");
    expect(source).toContain("feature flags still control command visibility");
    expect(source).toContain("read-only snapshot; this UI slice does not create approvals, consume approvals or execute commands");
    expect(source).toContain("project policy missing");
    expect(source).toContain("HumanCommandResult");
    expect(source).toContain("remaining_ms");
    expect(source).toContain("approvals.auto_allow_ask");
    expect(source).toContain("SetAutoAllowAsk");
    expect(source).toContain("Gateway policy.explain is authoritative");
    expect(source).toContain("ApiReflectionProfile");
    expect(source).toContain("UnityDocsProfile");
    expect(source).toContain("ProfilerReadonlyProfile");
    expect(source).toContain("ProfilerRecordingProfile");
    expect(source).toContain("TestValidationProfile");
    expect(source).toContain("TestValidationFeatureFlags");
    expect(source).toContain("ProfilerRecordingFeatureFlags");
    expect(permissionsBody).not.toContain("approval create");
    expect(permissionsBody).not.toContain("approval delete");
    expect(permissionsBody).not.toContain("ApprovalStore");
    expect(permissionsBody).not.toContain("sgg_unity_call(");
    expect(permissionsBody).not.toContain("EditorApplication.ExecuteMenuItem");
  });

  it("offers a separate opt-in checkbox for known Unity popup replies with one enable warning", () => {
    const builders = fs.readFileSync(permissionsCommandsPath, "utf8");
    const refresh = fs.readFileSync(permissionsCommandsRefreshPath, "utf8");
    const permissions = methodBody(builders, "BuildPermissionsPanel");
    const toggle = methodBody(refresh, "SetAutoAllowKnownUnityPopups");

    expect(builders).toContain("Toggle m_permissionsAutoAllowKnownUnityPopups;");
    expect(permissions).toContain('"Do not ask again for known Unity popups"');
    expect(permissions).toContain('"Не спрашивать снова для известных окон Unity"');
    expect(permissions).toContain('McpWindowLocalization.CurrentLanguage == "ru"');
    expect(permissions).toContain("Default OFF. Explicit opt-in to agent-chosen supported replies");
    expect(permissions).toContain("discard of unsaved changes, reload and downgrade");
    expect(permissions).toContain("DENY, feature flags, exact owner/window/process checks and one-shot execution with no automatic replay remain");
    expect(permissions).toContain("auto_allow_ask is unchanged");
    expect(permissions).toContain("RegisterValueChangedCallback(change => SetAutoAllowKnownUnityPopups(change.newValue))");
    expect(permissions).not.toContain("m_permissionsAutoAllowKnownUnityPopups.value = true");
    expect(methodBody(refresh, "RefreshPermissions")).toContain("m_permissionsAutoAllowKnownUnityPopups.SetValueWithoutNotify(GatewayPolicyManager.GetAutoAllowKnownUnityPopups())");
    expect(toggle).toContain("if (enabled && !GatewayPolicyManager.GetAutoAllowKnownUnityPopups())");
    expectInOrder(toggle, ["EditorUtility.DisplayDialog(title, warning", "SetValueWithoutNotify(false)", "return;", "GatewayPolicyManager.SetAutoAllowKnownUnityPopups(enabled)"]);
    expect(toggle).toContain('russian ? "Включить" : "Enable"');
    expect(toggle).toContain('russian ? "Отмена" : "Cancel"');
    expect(toggle).not.toMatch(/GatewayPolicyManager\.SetAutoAllowAsk|RestartGateway\(|RunAction|CommandRegistry|Process\.|File\./);
  });

  it("describes only saved known-popup consent as live without weakening restart or human warning requirements", () => {
    const builders = fs.readFileSync(permissionsCommandsPath, "utf8");
    const refresh = fs.readFileSync(permissionsCommandsRefreshPath, "utf8");
    const manager = fs.readFileSync(gatewayPolicyManagerPath, "utf8");
    const permissions = methodBody(builders, "BuildPermissionsPanel");
    const popupSettings = permissions.slice(permissions.indexOf("var popupRussian"), permissions.indexOf("approvalsSection.Add(m_permissionsAutoAllowKnownUnityPopups)"));
    const toggle = methodBody(refresh, "SetAutoAllowKnownUnityPopups");
    const set = methodBody(manager, "SetAutoAllowKnownUnityPopups");

    for (const source of [popupSettings, toggle, set]) {
      expect(source).toContain("On a Gateway runtime supporting live popup settings");
      expect(source).toContain("saved enable/disable changes apply on the next request without restart");
      expect(source).toContain("rechecked before response issuance");
      expect(source).toContain("diagnostics.status popup_response_approval.enabled");
      expect(source).not.toMatch(/restart Gateway|restart (?:is )?required|requires? (?:a )?restart|перезапустите Gateway|требуется перезапуск/i);
    }
    for (const source of [popupSettings, toggle]) {
      expect(source).toContain("со следующего запроса без перезапуска");
      expect(source).toContain("перед отправкой ответа настройка проверяется снова");
      expect(source).toContain("auto_allow_ask is unchanged");
      expect(source).not.toMatch(/m_permissionsAutoAllowKnownUnityPopups\.(?:value\s*=\s*true|SetValueWithoutNotify\(true\))|GatewayPolicyManager\.SetAutoAllowKnownUnityPopups\(true\)/);
    }
    expect(popupSettings).toContain("this checkbox shows the saved setting");
    expect(popupSettings).toContain("галочка показывает сохранённое значение");
    expect(permissions).toContain("Other Gateway config, feature flag, approval, token, network or runtime changes require restart");
    expect(permissions).toContain("saved approvals.auto_allow_known_unity_popups is the only live Gateway config setting");
    expect(permissions).toContain("Feature flags are stored in Gateway config. Enable the command families you need, then restart Gateway.");
    expect(permissions).toContain('m_permissionsAutoAllowAsk.tooltip = "Edits approvals.auto_allow_ask in the user gateway config. Restart Gateway after changing."');
    expect(methodBody(manager, "SetAutoAllowAsk")).toContain("Restart Gateway to reload the config");
    expect(methodBody(manager, "EnableFeatureFlags")).toContain("Restart Gateway to reload the config");
    expect(popupSettings).toContain("Default OFF");
    expect(toggle).toContain("This can discard unsaved changes, reload a scene or accept a project downgrade");
    expectInOrder(toggle, ["if (enabled && !GatewayPolicyManager.GetAutoAllowKnownUnityPopups())", "EditorUtility.DisplayDialog(title, warning", "SetValueWithoutNotify(false)", "return;", "GatewayPolicyManager.SetAutoAllowKnownUnityPopups(enabled)"]);
  });

  it("updates only structured popup approval config using a fail-closed atomic replacement", () => {
    const manager = fs.readFileSync(gatewayPolicyManagerPath, "utf8");
    const get = methodBody(manager, "GetAutoAllowKnownUnityPopups");
    const set = methodBody(manager, "SetAutoAllowKnownUnityPopups");
    const read = methodBody(manager, "ReadAutoAllowKnownUnityPopups");
    const upsert = methodBody(manager, "UpsertAutoAllowKnownUnityPopups");
    const atomic = methodBody(manager, "AtomicWritePopupApprovalConfigLocked");
    const coordinatedAtomic = methodBody(manager, "AtomicWritePopupApprovalConfig");
    const unityTests = fs.readFileSync(path.join(packageRoot, "Tests", "Editor", "GatewayPopupApprovalSettingsTests.cs"), "utf8");

    expect(manager).toContain('AutoAllowKnownUnityPopupsKey = "auto_allow_known_unity_popups"');
    expect(get).toContain("ReadAutoAllowKnownUnityPopups(File.ReadAllText(configPath))");
    expect(get).toContain("return false;");
    expectInOrder(set, ["AcquireGatewayConfigWriteLock(configPath)", "File.ReadAllText(configPath)", "UpsertAutoAllowKnownUnityPopups(source, enabled)", "AtomicWritePopupApprovalConfigLocked(configPath, source, updated)"]);
    expect(set).not.toMatch(/EnsureUserConfig|SetAutoAllowAsk|File\.Write|Process\./);
    expect(read).toContain("catch (FormatException)");
    expect(read).toContain("return false;");
    expect(upsert).toContain("PopupApprovalConfigParser.Parse(source)");
    expect(upsert).toContain("source.Substring(0, setting.Start) + value + source.Substring(setting.End)");
    expect(upsert).not.toMatch(/Regex|auto_allow_ask/);
    expect(manager).toContain('root.Members.TryGetValue("approvals", out var approvals)');
    expect(manager).toContain("value.Members.ContainsKey(name)");
    expect(manager).toContain("depth > 64");
    expect(atomic).toContain("FileMode.CreateNew");
    expect(atomic).toContain("stream.Flush(true)");
    expect(atomic).toContain("FileAttributes.ReparsePoint");
    expectInOrder(coordinatedAtomic, ["using (AcquireGatewayConfigWriteLock(configPath))", "AtomicWritePopupApprovalConfigLocked(configPath, original, updated, beforeReplacement)"]);
    for (const method of ["SetAutoAllowAsk", "EnableFeatureFlags"]) {
      expectInOrder(methodBody(manager, method), ["AcquireGatewayConfigWriteLock(configPath)", "File.ReadAllText(configPath)", "File.WriteAllText(configPath"]);
    }
    const writerLock = methodBody(manager, "AcquireGatewayConfigWriteLock");
    expect(writerLock).toContain('configPath + ".settings-write.lock"');
    expect(writerLock).toContain("FileShare.None");
    expect(writerLock).not.toContain("File.Delete");
    expectInOrder(atomic, ["using (var destination = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))", "new StreamReader(destination, Encoding.UTF8, true, 4096, true)", "current = reader.ReadToEnd()", "string.Equals(current, original, StringComparison.Ordinal)", "beforeReplacement?.Invoke()", "File.Replace(tempPath, configPath, null)"]);
    expectInOrder(atomic, ["string.Equals(current, original, StringComparison.Ordinal)", "if (string.Equals(original, updated, StringComparison.Ordinal)) return;", "beforeReplacement?.Invoke()"]);
    expect(atomic).toContain("not universal CAS against hostile external writers");
    expect(atomic).toContain("File.Replace(tempPath, configPath, null)");
    expect(atomic).not.toMatch(/File\.(?:Copy|WriteAllText)|ignoreMetadataErrors/);
    expect(unityTests).toContain("GatewayPolicyManager.ReadAutoAllowKnownUnityPopups");
    expect(unityTests).toContain("GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups");
    expect(unityTests).not.toMatch(/GatewayPolicyManager\.(?:Get|Set)\w+\s*\(|UnityMcpPackagePaths|GatewayInstaller|Environment\.(?:GetFolderPath|GetEnvironmentVariable)|LocalApplicationData|EditorPrefs/);
    expect(methodBody(unityTests, "WithTemporaryConfig")).toContain('Path.Combine(Path.GetTempPath(), "sgg-popup-setting-test-"');
    for (const method of ["AtomicReplacementPreservesUnrelatedValuesAndRemovesItsStagingFile", "ChangedDestinationIsNotOverwritten", "InPlaceWriterIsBlockedBetweenValidationAndReplacementWithoutRestoringUnrelatedFields", "CooperatingConfigWriterLockRefusesConcurrentConsentReplacement", "AlreadyOpenInPlaceWriterRefusesConsentReplacement", "NoOpConsentStillRejectsAChangedDestinationWithoutRewritingIt", "FailedReplacementDoesNotFallBackToOverwritingTheLockedDestination"]) {
      expect(methodBody(unityTests, method)).toContain("WithTemporaryConfig(configPath =>");
    }
    expect(fs.readFileSync(path.join(packageRoot, "Tests", "Editor", "GatewayPopupApprovalSettingsTests.cs.meta"), "utf8")).toMatch(/^fileFormatVersion: 2\nguid: [a-f0-9]{32}\n$/);
  });

  it("implements diagnostics fields and redacted clipboard copy", () => {
    const source = fs.readFileSync(unityAiDiagnosticsPath, "utf8");
    const diagnosticsBody = methodBody(source, "BuildDiagnosticsPanel");
    const copyBody = methodBody(source, "CopyDiagnostics");
    const openReportsBody = methodBody(source, "OpenProblemReportsFolder");
    const diagnosticsModel = fs.readFileSync(diagnosticsCopyViewModelPath, "utf8");
    const lastErrorsBody = methodBody(source, "LastErrorLines");

    for (const label of ["Runtime", "Gateway config", "Policy file", "Gateway log", "Heartbeat", "Problem reports folder", "Bridge token", "MCP token", "CanonicalPath dependency", "PathGuard", "Feature flags", "Audit log", "Availability badges", "Redaction", "Last errors"]) {
      expect(diagnosticsBody).toContain(label);
    }

    expect(diagnosticsBody).toContain("Open Problem Reports Folder");
    expect(source).toContain('Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "audit", "problem-reports")');
    expect(openReportsBody).toContain("ProblemReportsRoot()");
    expect(openReportsBody).toContain("Directory.Exists(root)");
    expect(openReportsBody).toContain("No problem reports yet");
    expect(openReportsBody).toContain("EditorUtility.RevealInFinder(root)");
    expect(openReportsBody).toContain("manual attachment only");
    expect(openReportsBody).not.toContain("Directory.CreateDirectory");
    expect(openReportsBody).not.toContain("File.Write");
    expect(openReportsBody).not.toContain("File.Delete");
    expect(openReportsBody).not.toContain("UnityWebRequest");
    expect(openReportsBody).not.toContain("HttpClient");
    expect(openReportsBody).not.toContain("WebRequest");
    expect(openReportsBody).not.toContain("Upload");
    expect(openReportsBody).not.toContain("upload");
    expect(copyBody).toContain("McpDiagnosticsCopyViewModel.Build(diagnostics)");
    expect(diagnosticsModel).toContain("Problem reports folder: ");
    expect(diagnosticsBody).toContain("Bridge token displays Present/Missing; MCP token displays Present/Not configured");
    expect(source).toContain("Copy Diagnostics redacts authorization, token, API key, password and cookie-like values");
    expect(source).toContain("config=");
    expect(source).toContain("pathguard=Present");
    expect(readWindowSources()).toContain('return string.IsNullOrEmpty(token) ? "Not configured" : "Present";');
    expect(diagnosticsModel).toContain("authorization");
    expect(diagnosticsModel).toContain("api[_-]?key");
    expect(diagnosticsModel).toContain("password");
    expect(diagnosticsModel).toContain("cookie");
    expect(diagnosticsModel).toContain("HeadField(input.Runtime)");
    expect(diagnosticsModel).toContain("Last errors: \" + TailField(input.LastErrors)");
    expect(diagnosticsModel).toContain("var safe = Redact(value)");
    expect(diagnosticsModel).toContain("TruncationMarker");
    expect(diagnosticsModel).toContain("ComponentPrefix");
    expect(diagnosticsModel).toContain("AuthorizationKeyPattern");
    expect(diagnosticsModel).toContain("CookieKeyPattern");
    expect(diagnosticsModel).not.toContain("[a-z0-9_-]*(?:token");
    expect(diagnosticsModel).not.toContain("redacted.Substring(0, MaxOutputChars)");
    expect(source).toContain("internal static string LastErrorLines(string text)");
    expect(lastErrorsBody).toContain("McpDiagnosticsCopyViewModel.Redact(result)");
    expect(copyBody).not.toContain("UnityBridgeRuntime.Token");
    expect(diagnosticsBody).not.toContain("InputRow");
    expect(diagnosticsBody).not.toContain("SecretInputRow");
  });

  it("instruments MCP Window refresh timing without log spam", () => {
    const source = fs.readFileSync(windowPath, "utf8");
    const refreshAllBody = methodBody(source, "RefreshAll");
    const heartbeatBody = methodBody(source, "RefreshWindowAfterHeartbeat");
    const summaryBody = methodBody(source, "SetMcpWindowTimingSummary");

    expect(source).toContain("using Unity.Profiling");
    expect(source).toContain("using Stopwatch = System.Diagnostics.Stopwatch");
    for (const marker of [
      "SGG.MCPWindow.Refresh",
      "SGG.MCPWindow.HealthCheck",
      "SGG.MCPWindow.CatalogRefresh",
      "SGG.MCPWindow.HeartbeatReadOrWrite",
    ]) {
      expect(source).toContain(marker);
    }

    expect(source).toContain("m_diagnosticsWindowTiming");
    expect(source).toContain("MCP Window timing");
    expect(source).toContain('"No MCP Window timing captured yet."');
    expect(refreshAllBody).toContain("s_mcpWindowRefreshMarker.Auto()");
    expect(refreshAllBody).toContain("TrackMcpWindowStep(details, \"health\", s_mcpWindowHealthCheckMarker");
    expect(refreshAllBody).toContain("TrackMcpWindowStep(details, \"catalog\", s_mcpWindowCatalogRefreshMarker");
    expect(refreshAllBody).toContain("TrackMcpWindowStep(details, \"heartbeat\", s_mcpWindowHeartbeatMarker");
    expect(heartbeatBody).toContain("s_mcpWindowHeartbeatMarker.Auto()");
    expect(heartbeatBody).toContain("HeartbeatRefresh");
    expect(summaryBody).toContain("McpWindowTimingViewModel.BuildSummary");
    expect(summaryBody).toContain("RefreshDiagnosticsTiming()");
    expect(methodBody(source, "TrackMcpWindowStep")).toContain("catch (Exception exception)");
    expect(methodBody(source, "TrackMcpWindowStep")).toContain("Debug.LogWarning");
    expect(methodBody(source, "TrackMcpWindowStep")).not.toContain("Debug.Log(");
    expect(methodBody(source, "RefreshDiagnosticsTiming")).toContain("m_lastMcpWindowTimingSummary");
    expect(source).not.toContain("Profiler.BeginSample");
    expect(source).not.toContain("Profiler.EndSample");
  });

  it("delegates bounded HARDEN-09A2 pure presentation while retaining shared services and one window", () => {
    const source = readWindowSources();
    const policyModel = fs.readFileSync(runtimePolicyViewModelPath, "utf8");
    const timingModel = fs.readFileSync(timingViewModelPath, "utf8");
    const diagnosticsModel = fs.readFileSync(diagnosticsCopyViewModelPath, "utf8");

    expect(source).toContain("McpRuntimePolicyViewModel.BuildSummary(status)");
    expect(source).toContain("McpWindowTimingViewModel.LatestSummary(m_lastMcpWindowTimingSummary)");
    expect(source).toContain("McpWindowTimingViewModel.BuildSummary(source, totalMs");
    expect(source).toContain("McpDiagnosticsCopyViewModel.Build(diagnostics)");
    expect(source).toContain("McpDiagnosticsCopyViewModel.Redact(result)");
    expect(source).not.toContain("RuntimePolicySummary(GatewayProcessManager.GatewayRuntimeStatus");
    expect(source).not.toContain("RedactDiagnosticsText(string text)");
    expect(policyModel).toContain("GatewayProcessManager.GatewayRuntimeStatus status");
    expect(timingModel).not.toMatch(/Stopwatch|Profiler|UnityEngine|UnityEditor/);
    expect(diagnosticsModel).not.toMatch(/File\.|Directory\.|Environment\.|GUIUtility|UnityEngine|UnityEditor/);

    expect(fs.existsSync(gatewayPolicyManagerPath)).toBe(true);
    expect(source).toContain("GatewayPolicyManager.GetStatus()");
    expect(source).toContain("McpWindowLocalization.ApplyTo(rootVisualElement)");
    expect(source).toContain("BuildSetupPanel()");
    expect(source).toContain("BuildStatusPanel()");
    expect(source).toContain("BuildPermissionsPanel()");
    expect(source).toContain("BuildCommandsPanel()");
  });

  it("adds Setup localization with XLIFF language files", () => {
    const source = readWindowSources();
    const localizer = fs.readFileSync(localizerPath, "utf8");
    const setupBody = methodBody(source, "BuildSetupPanel");
    const enXlf = fs.readFileSync(enXlfPath, "utf8");
    const ruXlf = fs.readFileSync(ruXlfPath, "utf8");

    expect(source).not.toContain("m_settingsPanel");
    expect(source).not.toContain("m_settingsTab");
    expect(source).not.toContain("BuildSettingsPanel()");
    expect(source).toContain("McpWindowLocalization.ApplyTo(rootVisualElement)");
    expect(setupBody).toContain("MCP Window Preferences");
    expect(setupBody).toContain("Language");
    expect(setupBody).toContain("Language affects only this MCP Window");
    expect(setupBody).toContain("Only MCP Window UI text; gateway/runtime output and generated snippets stay unchanged");
    expect(setupBody).toContain("new PopupField<string>");
    expect(setupBody).toContain("OnLanguageChanged");
    expect(localizer).toContain("EditorPrefs.SetString");
    expect(localizer).toContain("const string LocalizationFolder = \"Editor/UI/Localization\"");
    expect(localizer).toContain("public const string LanguagePrefsKey = \"SGG.UnityMCP.McpWindow.Language\"");
    expect(localizer).toContain("MaxCachedTextEntriesPerLanguage");
    expect(localizer).toContain("s_textCacheByLanguage");
    expect(localizer).toContain("TextCacheForLanguage(language)");
    expect(localizer).toContain("cache.Clear()");
    expect(enXlf).toContain("<trans-unit");
    expect(enXlf).toContain('target-language="en"');
    expect(ruXlf).toContain('target-language="ru"');
    expect(ruXlf).toContain("<target>Настройки</target>");
    expect(ruXlf).toContain("<target>Локализация</target>");
    expect(ruXlf).toContain("<target>Обязательный CanonicalPath</target>");
    expect(ruXlf).toContain("<target>Добавить scoped registry</target>");
    expect(xliffTransUnitIds(ruXlf)).toEqual(xliffTransUnitIds(enXlf));
    expect(ruXlf).not.toContain("<target></target>");
  });

  it("keeps the Plans tab as a non-applying scaffold with copy/open helpers", () => {
    const source = fs.readFileSync(plansSkillsPath, "utf8");
    const plansBody = methodBody(source, "BuildPlansPanel");

    for (const label of ["Open Plans Folder", "Copy Plan Template", "Refresh Plans", "Copy Latest Plan", "Local plans root", "Versioned plans root", "Latest plan files"]) {
      expect(plansBody).toContain(label);
    }

    expect(plansBody).toContain("Not implemented: automatic plan apply");
    expect(plansBody).toContain("Empty/missing local plans folder is optional");
    expect(plansBody).not.toContain("sgg_unity_call(");
    expect(plansBody).not.toContain("EditorApplication.ExecuteMenuItem");
    expect(methodBody(source, "OpenPlansFolder")).not.toContain("Directory.CreateDirectory");
    expect(source).toContain('"Library", "SGGUnityMcp", "plans"');
    expect(source).toContain('".mcp", "plans"');
    expect(source).not.toContain("plan.mark_approved");
    expect(source).not.toContain("Apply Plan");
  });

  it("keeps the Skills tab as a non-executing scaffold with validation and review status", () => {
    const source = fs.readFileSync(plansSkillsPath, "utf8");
    const skillsBody = methodBody(source, "BuildSkillsPanel");

    for (const label of ["Local skills folder", "Built-in examples", "Validation status", "Review status", "Project knowledge", "Open Skills Folder", "Refresh Skills"]) {
      expect(skillsBody).toContain(label);
    }

    expect(skillsBody).toContain("Skills are not executed or auto-enabled");
    expect(skillsBody).toContain("Missing .mcp/skills folder is optional");
    expect(source).toContain("Missing .mcp/skills folder is optional; skill.list will return built-in examples when enabled.");
    expect(source).toContain('".mcp", "skills"');
    expect(source).toContain("CanonicalizePath(LocalSkillsRoot())");
    expect(source).toContain('"Assets", "UnityMcpKnowledge"');
    expect(source).toContain('"Library", "SGGUnityMcp", "skills", "reviewed-skills.json"');
    expect(source).toContain("unknown skills are not auto-enabled");
    expect(skillsBody).not.toContain("sgg_unity_call(");
    expect(skillsBody).not.toContain("EditorApplication.ExecuteMenuItem");
    expect(skillsBody).not.toContain("GatewayProcessManager");
    expect(source).not.toContain("Enable Skill");
    expect(source).not.toContain("Run Skill");
  });

  it("adds a Unity AI Assistant tab for optional local compatibility guidance", () => {
    const source = `${fs.readFileSync(windowPath, "utf8")}\n${fs.readFileSync(unityAiDiagnosticsPath, "utf8")}`;
    const panelBody = methodBody(source, "BuildUnityAiAssistantPanel");
    const installBody = methodBody(source, "InstallUnityAiAssistantPackage");
    const copySkillSourcesBody = methodBody(source, "CopyUnityAiAssistantSkillSourcesSummary");

    for (const label of ["Unity AI Assistant MCP 2.8", "Baseline", "Official package", "Official relay", "Works Without Official Package", "Adds When Official Package Is Installed", "Skill Sources And Import Flow", "Skills sources", "Discovery sources", "Import semantics", "Tool Compatibility", "Supported local parity", "Partial local parity", "Deferred / diagnostic-only"]) {
      expect(panelBody).toContain(label);
    }

    for (const label of ["Install Optional Package", "Open Package Manager", "Copy Package Id", "Copy Skill Sources Summary", "Copy Tool Map", "Refresh"]) {
      expect(panelBody).toContain(label);
    }

    expect(source).toContain('const string UnityAiAssistantPackageId = "com.unity.ai.assistant"');
    expect(source).toContain("Cloudless SGGMcp baseline does not require Unity Assistant chat");
    expect(source).toContain("SGGMcp works without com.unity.ai.assistant, Unity Assistant chat, Unity Cloud, credits, subscription, or official relay");
    expect(source).toContain("MCP connection, project discovery, command discovery, console/status, assets/scripts, project settings, scenes/editor metadata, package metadata, Unity AI tool parity via sgg_unity_call, and skills when enabled");
    expect(source).toContain("official Unity Assistant UI, package resources/assets, package skill sources under Packages/**/AIAssistantSkills/**/SKILL.md, compatibility validation, and official relay status diagnostic-only");
    expect(source).toContain("Unity_ListResources -> asset.list");
    expect(source).toContain("Unity_ReadResource -> asset.text.read");
    expect(source).toContain("Unity_FindInFile -> asset.text.find");
    expect(source).toContain("Unity.FindFiles / Unity.Grep -> asset.search / asset.text.search");
    expect(source).toContain("Unity_GetSha -> script.hash");
    expect(source).toContain("Unity_ValidateScript -> script.validate");
    expect(source).toContain("Unity_ManageScript_capabilities -> script.capabilities");
    expect(source).toContain("Unity_ApplyTextEdits -> script.text_edits.apply");
    expect(source).toContain("unity://spec/script-edits -> script.text_edits.spec");
    expect(source).toContain("Unity.GetStaticProjectSettingsTool -> project.settings.summary");
    expect(source).toContain("Unity.GetProjectSettings -> project.settings.inspect");
    expect(source).toContain("Unity.GetProjectData -> project.assets.taxonomy");
    expect(source).toContain("Unity.PackageManager.GetData -> package.info / package.list");
    expect(source).toContain("Unity.GetObjectData -> asset.serialized.inspect");
    expect(source).toContain("Unity.GameObject.GetBuiltinAssets -> gameobject.builtin_assets");
    expect(source).toContain("Unity.Camera.GetVisibleObjects -> scene.visible_objects");
    expect(source).toContain("Unity.GameObject.GetGameObjectBounds -> gameobject.bounds");
    expect(source).toContain(".mcp/skills project-local reviewed skills");
    expect(source).toContain("Assets/**/SKILL.md project assets");
    expect(source).toContain("Packages/**/AIAssistantSkills/**/SKILL.md");
    expect(source).toContain("%APPDATA%/Unity/AIAssistantSkills");
    expect(source).toContain("built-in SGGMcp examples");
    expect(source).toContain("Missing source folders are optional, not errors");
    expect(source).toContain("Use skill.import dry_run to preview a reviewed copy into .mcp/skills/<target_name>");
    expect(source).toContain("Unity skills -> skill.list / skill.read / skill.validate / skill.import");
    expect(source).toContain("unity.relay.status diagnostic-only");
    expect(source).toContain("ReadProjectManifestDependencyVersion(UnityAiAssistantPackageId)");
    expect(installBody).toContain("EditorUtility.DisplayDialog");
    expect(installBody).toContain("not required for local SGGMcp compatibility");
    expect(installBody).toContain("UnityEditor.PackageManager.Client.Add(UnityAiAssistantPackageId)");
    expect(panelBody).not.toContain("GatewayProcessManager");
    expect(panelBody).not.toContain("RunAction");
    expect(panelBody).not.toContain("Import Skill");
    expect(panelBody).not.toContain("Enable Skill");
    expect(panelBody).not.toContain("Auto-enable");
    expect(panelBody).not.toContain("UnityRelayClient");
    expect(panelBody).not.toContain("File.Write");
    expect(panelBody).not.toContain("Directory.CreateDirectory");
    expect(copySkillSourcesBody).not.toContain("File.Write");
    expect(copySkillSourcesBody).not.toContain("Directory.CreateDirectory");
    expect(copySkillSourcesBody).not.toContain("UnityEditor.PackageManager.Client.Add");
    expect(source).not.toContain("UnityRelayClient.Start");
    expect(source).not.toContain("skill.enable");
    expect(source).not.toContain("ImportUnityAiAssistantSkill");
  });

  it("keeps per-component checks installed-only and exposes confirmed optional installs locally", () => {
    const windowSource = readWindowSources();
    const feature = fs.readFileSync(unityCliPipelinePath, "utf8");
    const setup = fs.readFileSync(unityCliPipelineSetupPath, "utf8");
    const updateSection = methodBody(feature, "BuildUnityCliPipelineSection");
    const skillSection = methodBody(feature, "BuildUnityCliSkillSection");

    for (const label of [
      "Windows Unity CLI", "WSL unity resolution", "CLI environment match", "Pipeline package", "Pipeline readiness",
      "Unity CLI / Pipeline / Skill Updates", "Refresh CLI / WSL Detection", "Verify Pipeline Connection",
      "Unity CLI agent skill", "Open Skill Details",
      "Official Unity CLI Agent Skill", "Open Project Skill Folder",
      "Update Unity CLI Skill",
      "Check CLI Updates", "Check Pipeline Updates", "Check Skill Updates", "Update Windows CLI", "Update Pipeline", "Refresh Local State", "Copy Update Diagnostics",
      "Windows CLI update", "Pipeline update", "Unity CLI skill update",
    ]) {
      expect(windowSource).toContain(label);
    }
    for (const [block, actionParent, label] of [
      ["cli", "cliActions", "Install Windows CLI"], ["pipeline", "pipelineActions", "Install Pipeline"],
      ["skill", "skillActions", "Install Unity CLI Skill"],
    ]) {
      expect(updateSection).toContain(`${block}.name = "unity-tool-${block}"`);
      expect(updateSection).toContain(`AddButton(${actionParent}, "${label}"`);
    }
    expect(updateSection).toContain("checks skip missing components.");
    expect(updateSection).toContain("Windows Subsystem for Linux");
    expect(updateSection).toContain("UnityToolUpdateComponents.WindowsCli");
    expect(updateSection).toContain("UnityToolUpdateComponents.Pipeline");
    expect(updateSection).toContain("UnityToolUpdateComponents.Skill");
    expect(skillSection).toContain("Existing unmanaged or modified targets are never overwritten.");
    expect(methodBody(feature, "UnityToolFreshnessState")).toContain('if (knownAbsent) return "optional"');
    expect(methodBody(feature, "UnityToolFreshnessState")).toContain('CheckStateChecked');
    expect(methodBody(feature, "UnityToolFreshnessState")).toContain('UpdateStateAvailable) return "error"');
    expect(methodBody(feature, "UnityToolFreshnessState")).toContain('UpdateStateCurrent ? "ok" : "info"');
    expect(methodBody(feature, "RefreshUnityCliPipelineSetup")).toContain("TryGetOfficialWindowsCliPresence");
    expect(methodBody(feature, "RefreshUnityToolUpdateStatus")).toContain("RenderUnityToolUpdateStatus");
    expect(methodBody(feature, "RenderUnityToolUpdateStatus")).toContain("var noInstalledComponents = cliAbsent && pipelineAbsent && skillAbsent");
    expect(methodBody(feature, "RenderUnityToolUpdateStatus")).toContain("FormatObservedUpdateStatus");
    expect(methodBody(setup, "ReadManifestVersion")).toContain("PipelineJsonEvidenceParser.TryReadOptionalStringPath");
    expect(methodBody(setup, "BuildWindowsCliInstallScript")).not.toContain("$env:LOCALAPPDATA");
    for (const method of ["TryStartPipelineInstall", "TryStartPipelineVerification"]) {
      expect(methodBody(setup, method)).toContain("WindowsCliAuthenticode.BuildBoundCommandArguments");
      expect(methodBody(setup, method)).toContain("s_windowsStatus.ExecutableSha256");
      expect(methodBody(setup, method)).toContain("s_windowsStatus.ExecutableSignatureThumbprint");
    }
    expect(feature).toContain("UnityCliPipelineSetup.TryStartPipelineInstall");
    expect(feature).toContain("UnityCliPipelineSetup.TryStartWindowsCliInstall");
    expect(feature).toContain("UnityCliProjectSkillInstaller.TryStartInstall");
    expect(feature).toContain("EditorUtility.DisplayDialog");
    expect(feature).toContain("allowed-tools: Bash");
    expect(feature).toContain(".agents");
    expect(feature).toContain("Blocked - first click 1. Detect/Validate CLI.");
    expect(feature).toContain("Unity CLI was found at the official Windows install path but this Editor inherited an older PATH.");
    expect(feature).toContain("Detected in the official install location after a failed/older probe; refresh local CLI detection.");
    expect(feature).toContain("Windows Unity CLI installed and validated");
    expect(feature).toContain("It does not authenticate or install Pipeline");
    expect(feature).toContain("UnityCliPipelineSetup.ProbeOfficialWindowsCli()");
    expect(feature).toContain("s_windowsCliProbeRunning");
    expect(methodBody(feature, "ProbeUnityCliEnvironments")).toContain("finally");
    expect(methodBody(feature, "InstallWindowsUnityCli")).toContain("UnityToolOperationRunning()");
    expect(feature).toContain("AssemblyReloadEvents.beforeAssemblyReload += CancelWindowsUnityCliInstall");
    expect(feature).toContain("s_windowsCliInstallOperation.Dispose()");
    expect(setup).toContain("https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1");
    expect(setup).toContain("Invoke-WebRequest -UseBasicParsing");
    expect(setup).toContain("-OutFile $installer");
    expect(setup).toContain("Remove-Item -LiteralPath $installer");
    expect(setup).toContain("SecurityProtocolType]::Tls12");
    expect(methodBody(setup, "BuildWindowsCliInstallScript")).not.toMatch(/Invoke-Expression|\|\s*iex/);
    expect(methodBody(setup, "TryStartWindowsCliInstall")).toContain("TimeSpan.FromMinutes(5)");
    expect(methodBody(setup, "ProbeOfficialWindowsCli")).toContain("OfficialWindowsInstallPath");
    expect(methodBody(setup, "ProbeOfficialWindowsCli")).not.toMatch(/where\.exe|ProbeWindowsCli/);
    expect(feature).toContain("Installing - wait for the CLI command, Package Manager import, and compilation.");
    expect(feature).toContain("Ready - source-verified official unity-cli skill is installed at .agents/skills/unity-cli.");
    expect(feature).toContain("Not installed - freshness checks skip this optional project skill.");
    expect(feature).toContain("UnityCliPipelineSetup.TryStartWslWrapperInstall");
    expect(feature).toContain("An existing file or symlink is never overwritten");
    expect(feature).toContain("m_lastNodeRuntimeStatus ?? NodeRuntimeInstaller.GetStatus()");
    expect(feature).not.toContain("PackageManager.Client.Add");
    expect(feature).not.toContain("--global");
    expect(methodBody(feature, "RefreshUnityCliPipelineSetup")).not.toMatch(/TryRun|TryStart|ProbeWindowsCli|ProbeDefaultWslCli/);
    expect(methodBody(feature, "RefreshUnityCliProjectSkill")).not.toMatch(/TryRun|TryStart/);
    expect(feature).toContain("Button m_updateUnityCliSkillButton;");
    expect(feature).toContain("Only an unmodified, source-verified older Unity CLI skill can be replaced");
    expect(feature).toContain("automatic immutable-source/content verification");
    expect(methodBody(feature, "InstallUnityCliProjectSkill")).toContain("HasFreshOfficialSkillTarget");
    expect(methodBody(feature, "HasFreshOfficialSkillTarget")).toContain("status.SourceIdentitySha256 == UnityCliUpdateCheck.SkillSourceIdentity(status.AvailableVersion)");
    expect(methodBody(feature, "HasFreshOfficialSkillTarget")).toContain("status.ExpiresAtUtc > utcNow");
    expect(methodBody(feature, "BuildUnityCliSkillSection")).toContain("m_updateUnityCliSkillButton = AddButton(actions, \"Update Unity CLI Skill\", UpdateUnityCliProjectSkill);");
    expect(methodBody(feature, "BuildUnityCliSkillSection")).toContain("m_updateUnityCliSkillButton.SetEnabled(false);");
    expect(methodBody(feature, "BuildUnityCliSkillSection")).toContain("m_updateUnityCliSkillButton.tooltip = UnityCliSkillUpdateDisabledReason;");
    expect(methodBody(feature, "RefreshUnityCliProjectSkill")).toContain("UnityCliProjectSkillInstaller.TryCreateUpdateBinding");
    expect(methodBody(feature, "RefreshUnityCliProjectSkill")).toContain("m_updateUnityCliSkillButton.SetEnabled(canUpdate);");
    expect(methodBody(feature, "UpdateUnityCliProjectSkill")).toContain("UnityCliProjectSkillInstaller.TryCreateUpdateBinding");
    expect(methodBody(feature, "UpdateUnityCliProjectSkill")).toContain("binding.BindingIdentitySha256");
    expect(methodBody(feature, "UpdateUnityCliProjectSkill")).toContain("UnityCliProjectSkillInstaller.TryStartUpdate");
    expect(methodBody(feature, "UpdateUnityCliProjectSkill")).toContain("backs up the existing source-verified target");
    expect(methodBody(feature, "PollUnityCliProjectSkillInstall")).toContain("UnityCliProjectSkillInstaller.TryFinalizeUpdate");
    expect(methodBody(feature, "RefreshUnityCliProjectSkill")).not.toMatch(/TryStart|TryRun|Process|UnityWebRequest|File\.Write|Directory\.CreateDirectory/);
    expect(methodBody(feature, "CopyWslUnityCliWrapperSetup")).not.toMatch(/Process|TryRun|File\.Write/);
    expect(fs.readFileSync(windowPath, "utf8")).toContain("CancelWindowsUnityCliInstall();");
    const updateCheck = fs.readFileSync(unityCliUpdateCheckPath, "utf8");
    const windowsCliUpdate = fs.readFileSync(windowsCliUpdateOperationPath, "utf8");
    const pipelineUpdate = fs.readFileSync(unityPipelineUpdateOperationPath, "utf8");
    const boundedProcess = fs.readFileSync(boundedProcessOperationPath, "utf8");
    expect(feature).toContain("UnityCliUpdateCheck.Start(ProjectPaths.ProjectRoot, components)");
    expect(feature).toContain("UnityCliUpdateCheck.TryStartAutomatic(ProjectPaths.ProjectRoot, DateTime.UtcNow");
    expect(feature).toContain("UnityCliUpdateCheck.RecordFreshness(ProjectPaths.ProjectRoot, completed, DateTime.UtcNow)");
    expect(methodBody(feature, "TryStartAutomaticUnityToolFreshnessCheck")).toContain("m_unityToolFreshnessVisibilityHandled");
    expect(methodBody(feature, "TryStartAutomaticUnityToolFreshnessCheck")).toContain("m_skipAutomaticRefreshForTests");
    expect(methodBody(feature, "TryStartAutomaticUnityToolFreshnessCheck")).toContain("m_initializingGui");
    expect(methodBody(fs.readFileSync(windowPath, "utf8"), "SelectTab")).toContain("TryStartAutomaticUnityToolFreshnessCheck()");
    expect(feature).toContain("AssemblyReloadEvents.beforeAssemblyReload += CancelUnityToolUpdateCheck");
    expect(feature).toContain("Bounded update diagnostics copied without paths, credentials, or raw metadata.");
    expect(methodBody(feature, "RefreshUnityCliPipelineSetup")).not.toContain("UnityCliUpdateCheck.Start");
    expect(methodBody(feature, "RefreshUnityToolUpdateStatus")).not.toMatch(/TryStart|ProcessUtil|UnityWebRequest/);
    expect(methodBody(feature, "RefreshUnityToolLocalState")).not.toContain("UnityCliUpdateCheck.Start");
    expect(methodBody(feature, "RefreshUnityToolLocalState")).toContain("TryStartOfficialWindowsCliProbe");
    expect(methodBody(feature, "CopyUnityToolUpdateDiagnostics")).toContain("InvalidateIfLocalStateChanged");
    expect(methodBody(feature, "VerifyUnityPipeline")).toContain("UnityToolOperationRunning()");
    expect(methodBody(feature, "InstallUnityPipeline")).toContain("UnityToolOperationRunning()");
    expect(methodBody(feature, "InstallUnityCliProjectSkill")).toContain("UnityToolOperationRunning()");
    expect(methodBody(feature, "InstallWslUnityCliWrapper")).toContain("UnityToolOperationRunning()");
    expect(fs.readFileSync(windowPath, "utf8")).toContain("CancelUnityToolUpdateCheck();");
    expect(fs.readFileSync(windowPath, "utf8")).toContain("CancelWindowsUnityCliUpdate();");
    expect(updateCheck).toContain('new[] { "upgrade", "--check", "--channel", UnityCliUpdateCheck.CliChannel, "--format", "json", "--non-interactive", "--no-banner" }');
    expect(updateCheck).toContain('new[] { "--version", "--no-banner" }');
    expect(updateCheck).toContain('new[] { "upgrade", "--help", "--no-banner" }');
    expect(updateCheck).toContain('new[] { "pipeline", "install", "--help", "--no-banner" }');
    expect(updateCheck).toContain('new[] { "pipeline", "list-versions", "--format", "json", "--non-interactive", "--no-banner" }');
    expect(updateCheck).toContain('SkillLatestApiUrl = "https://api.github.com/repos/Unity-Technologies/skills/commits?path=skills%2Funity-cli&per_page=1"');
    expect(updateCheck).toContain("Invoke-RestMethod -Uri '");
    expect(updateCheck).toContain("+[char]9+'");
    expect(updateCheck).not.toMatch(/ls-remote|refs\/heads\/main/);
    expect(updateCheck).not.toMatch(/Invoke-Expression|\|\s*iex|auth\s+login|pipeline\s+install\s+--project-path|upgrade\s+--yes/);
    expect(updateCheck).toContain('CheckStateSkippedNotInstalled = "skipped_not_installed"');
    expect(updateCheck).toContain('UpdateStateNotInstalled = "not_installed"');
    expect(updateCheck).toContain("AutomaticCheckInterval = TimeSpan.FromHours(24)");
    expect(updateCheck).toContain("AutomaticFailureBackoff = TimeSpan.FromHours(1)");
    expect(updateCheck).toContain("AuthoritativeForMutation = false");
    expect(windowsCliUpdate).toContain("Get-AuthenticodeSignature -LiteralPath $path");
    expect(methodBody(windowsCliUpdate, "BuildProbeArguments")).toContain("Microsoft.PowerShell.Security\\\\Get-AuthenticodeSignature");
    expect(methodBody(windowsCliUpdate, "BuildProbeArguments")).not.toMatch(/\$env:WINDIR|\$env:LOCALAPPDATA/);
    expect(methodBody(windowsCliUpdate, "BuildProbeArguments")).toContain("[IO.FileShare]::Read");
    expect(methodBody(windowsCliUpdate, "BuildProbeArguments")).not.toContain("[IO.FileShare]::ReadWrite");
    expect(windowsCliUpdate).toContain("SecurityModulePath");
    expect(windowsCliUpdate).toContain("ExecutableSha256");
    expect(windowsCliUpdate).toContain('string.Equals(envelope.status, "Valid", StringComparison.Ordinal)');
    expect(windowsCliUpdate).toContain('HasDistinguishedNameField(envelope.subject, "CN", UnitySigner)');
    expect(windowsCliUpdate).toContain('HasDistinguishedNameField(envelope.subject, "O", UnitySigner)');
    expect(methodBody(windowsCliUpdate, "BuildMutationArguments")).toContain('new[] { "upgrade", "--target", targetVersion, "--yes", "--format", "json", "--non-interactive", "--no-banner" }');
    expect(methodBody(windowsCliUpdate, "BuildMutationArguments")).not.toMatch(/--channel|auth|pipeline|rollback|dry-run/);
    expect(methodBody(windowsCliUpdate, "BuildTrackedUpdateArguments")).toContain("Start-Process -FilePath $path");
    expect(methodBody(windowsCliUpdate, "BuildTrackedUpdateArguments")).toContain("-NoNewWindow -PassThru");
    expect(methodBody(windowsCliUpdate, "BuildTrackedUpdateArguments")).toContain("-PassThru}finally{$stream.Dispose()};");
    expect(methodBody(windowsCliUpdate, "BuildTrackedUpdateArguments")).toContain("$process.WaitForExit();");
    expect(methodBody(windowsCliUpdate, "BuildTrackedUpdateArguments")).toContain("$expectedSha256");
    expect(methodBody(windowsCliUpdate, "BuildTrackedUpdateArguments")).not.toMatch(/\$env:WINDIR|\$env:LOCALAPPDATA|--channel|auth\s+login|pipeline\s+install|rollback|dry-run/);
    expect(methodBody(windowsCliUpdate, "TryCreateBinding")).toContain("CliSourceIdentity(update.InstalledVersion, update.AvailableVersion)");
    expect(methodBody(feature, "UpdateWindowsUnityCli")).toContain("WindowsCliUpdateCoordinator.TryCreateBinding");
    expect(methodBody(feature, "UpdateWindowsUnityCli")).toContain("binding.BindingIdentitySha256");
    expect(methodBody(feature, "UpdateWindowsUnityCli")).toContain("new WindowsCliUpdateCoordinator(snapshot, rebound)");
    expect(methodBody(feature, "UpdateWindowsUnityCli")).toContain("does not authenticate, change channel, downgrade, update Pipeline, or retry after timeout");
    expect(feature).toContain("AssemblyReloadEvents.beforeAssemblyReload += CancelWindowsUnityCliUpdate");
    expect(methodBody(feature, "CancelWindowsUnityCliUpdate")).toContain("s_windowsCliUpdateOperation.Cancel()");
    expect(methodBody(feature, "ResumeUnityCliOperations")).not.toContain("s_windowsCliUpdateOperation");
    expect(feature).toContain("WindowsCliUpdateCoordinator.RequiresLocalRefresh");
    expect(feature).toContain("WindowsCliUpdateCoordinator.CanRefreshLocalState");
    expect(methodBody(feature, "RefreshUnityToolLocalState")).toContain("ClearLocalRefreshRequirement");
    expect(windowsCliUpdate).toContain("UnconfirmedProcessIdSessionKey");
    expect(windowsCliUpdate).toContain("TerminationUnconfirmedSessionKey");
    expect(windowsCliUpdate).toContain("Environment.Is64BitProcess");
    expect(windowsCliUpdate).toContain("PreflightCommandSurface");
    expect(windowsCliUpdate).toContain("TryValidateCliUpgradeHelp");
    expect(boundedProcess).toContain('Path.Combine(systemDirectory, "taskkill.exe")');
    expect(boundedProcess).not.toContain('FileName = "taskkill.exe"');
    expect(boundedProcess).toContain("TerminationConfirmed = TryKill()");
    expect(boundedProcess).toContain("m_process.WaitForExit(5000)");
    expect(windowsCliUpdate).not.toMatch(/Invoke-Expression|\|\s*iex|auth\s+login|pipeline\s+install/);
    expect(methodBody(feature, "UpdateUnityPipeline")).toContain("UnityPipelineUpdateOperation.TryCreateBinding");
    expect(methodBody(feature, "UpdateUnityPipeline")).toContain("binding.BindingIdentitySha256");
    expect(methodBody(feature, "UpdateUnityPipeline")).toContain("new UnityPipelineUpdateCoordinator(snapshot, rebound)");
    expect(methodBody(feature, "UpdateUnityPipeline")).toContain("Expected Packages/manifest.json delta");
    expect(methodBody(feature, "UpdateUnityPipeline")).toContain("Expected Packages/packages-lock.json delta");
    expect(feature).toContain("AssemblyReloadEvents.beforeAssemblyReload += CancelUnityPipelineUpdate");
    expect(methodBody(feature, "CancelUnityPipelineUpdate")).toContain("s_pipelineUpdateOperation.Cancel()");
    expect(fs.readFileSync(windowPath, "utf8")).toContain("CancelUnityPipelineUpdate();");
    expect(methodBody(pipelineUpdate, "BuildMutationArguments")).toContain('"--package-version", binding.TargetVersion');
    expect(methodBody(pipelineUpdate, "BuildMutationArguments")).toContain('"--project-path", binding.WindowsProjectRoot');
    expect(methodBody(pipelineUpdate, "BuildMutationArguments")).not.toMatch(/--force|auth|login|uninstall|rollback/);
    expect(methodBody(pipelineUpdate, "BuildTrackedMutationArguments")).toContain("Start-Process -FilePath $cli");
    expect(methodBody(pipelineUpdate, "BuildTrackedMutationArguments")).toContain("$expectedManifestSha");
    expect(methodBody(pipelineUpdate, "BuildTrackedMutationArguments")).toContain("FileAttributes]::ReparsePoint");
    expect(methodBody(pipelineUpdate, "BuildTrackedMutationArguments")).not.toMatch(/Invoke-Expression|\|\s*iex|--force|auth\s+login|uninstall|rollback/);
    expect(pipelineUpdate).toContain("SessionStateKey");
    expect(pipelineUpdate).toContain("dispatchCount = 1");
    expect(pipelineUpdate).toContain("PreflightCommandSurface");
    expect(pipelineUpdate).toContain("TryValidatePipelineInstallHelp");
    expect(pipelineUpdate).toContain("ExplicitCompilationBarrier.Snapshot");
    expect(pipelineUpdate).toContain("barrier.PostReloadObserved");
    expect(pipelineUpdate).toContain("ConsoleLogBuffer.HasCompileErrors()");
  });

  it("keeps periodic heartbeat refreshes free of blocking setup and process work", () => {
    const source = readWindowSources();
    const runtimeHealthState = fs.readFileSync(runtimeHealthStatePath, "utf8");
    const bridgeRuntime = fs.readFileSync(bridgeRuntimePath, "utf8");
    const heartbeatWriter = fs.readFileSync(heartbeatWriterPath, "utf8");
    const gatewayProcessManager = fs.readFileSync(gatewayProcessManagerPath, "utf8");
    const refreshAllBody = methodBody(source, "RefreshAll");
    const refreshGatewayBody = methodBody(source, "RefreshGateway");
    const refreshWindowAfterHeartbeatBody = methodBody(source, "RefreshWindowAfterHeartbeat");
    const heartbeatViewsBody = methodBody(source, "RefreshHeartbeatViews");
    const heartbeatChecklistBody = methodBody(source, "RefreshUnityBridgeChecklist");
    const heartbeatCatalogBody = methodBody(source, "RefreshCatalogPolicySummaryAfterHeartbeat");
    const catalogSummaryBuilderBody = methodBody(source, "BuildCatalogPolicySummaryText");
    const heartbeatHealthActionsBody = methodBody(source, "RefreshHeartbeatHealthActions");
    const refreshBridgeBody = methodBody(source, "RefreshBridge");
    const refreshHealthBody = methodBody(source, "RefreshHealthAndChecklist");
    const heartbeatDependentBody = methodBody(source, "RefreshHeartbeatDependentViews");
    const checklistProcessIndex = source.indexOf("GatewayProcessManager.GatewayRuntimeStatus ChecklistProcessStatus(");
    expect(checklistProcessIndex).toBeGreaterThanOrEqual(0);
    const checklistProcessBody = source.slice(checklistProcessIndex, source.indexOf("void SetChecklist", checklistProcessIndex));

    expect(source).toContain("const double HeartbeatWindowRefreshDelaySeconds = 1.0");
    expect(source).toContain("m_lastProbedProcessStatus");
    expect(source).toContain("m_installPortableNodeButton");
    expect(source).toContain("m_installRuntimeButton");
    expect(source).toContain("m_startGatewayButton");
    expect(source).toContain("m_setupRefreshHealthButton");
    expect(source).toContain("m_statusRefreshHealthButton");
    expect(source).toContain("m_restartGatewayButtons");
    expect(source).toContain("Button AddButton");
    expect(source).toContain("RefreshAll(true);");
    expect(source).toContain("RunAction(\"Install Gateway Runtime\", InstallGatewayRuntime, true)");
    expect(source).toContain("AddButton(gatewayActions, \"Refresh\", () => RefreshAll(true))");
    expect(source).toContain('m_stopGatewayButton = AddButton(processActions, "Stop Gateway", StopGateway)');
    expect(source).toContain("SetActionNeeded(m_installPortableNodeButton, !status.IsPortable");
    expect(source).toContain("SetActionNeeded(m_installRuntimeButton, GatewayRuntimeInstallActionNeeded(gatewayStatus)");
    expect(source).toContain("SetActionNeeded(m_startGatewayButton, runtimeHealth.StartOrRestartActionNeeded");
    expect(source).toContain("SetActionNeeded(m_setupRefreshHealthButton, refreshNeeded");
    expect(source).toContain("SetActionNeeded(m_statusRefreshHealthButton, refreshNeeded");
    expect(source).toContain("button.AddToClassList(\"mcp-action-needed\")");
    expect(source).toContain("LocalBridgeHeartbeatStale()");
    expect(source).toContain("GatewayRuntimeStatusText(status, processStatus)");
    expect(source).toContain("GatewayRuntimeIsCurrent(status, processStatus)");
    expect(source).toContain("GatewayRuntimeVersionSummary(status, processStatus)");
    expect(source).toContain("UpdateGatewayActionButtons(status, processStatus)");
    expect(source).toContain("GatewayProcessStatusText(status, gatewayStatus)");
    expect(source).toContain("GatewayProcessChecklistText(processStatus, gatewayStatus)");
    expect(source).toContain("runtimeHealth.VersionMismatch");
    expect(source).toContain("RuntimeVersionTransition(processStatus.HealthVersion, gatewayStatus.InstalledRuntimeVersion)");
    expect(source).toContain("restart required: ");
    expect(source).toContain("Installed runtime ");
    expect(source).toContain("running gateway is");
    expect(source).toContain("invalid heartbeat record(s)");
    expect(source).toContain("stale heartbeat record(s)");
    expect(source).toContain("Press \" + GatewayRestartButtonText(processStatus, gatewayStatus) + \" to restart it on the active runtime");
    expect(source).toContain("UnityBridgeRuntime.HeartbeatWritten += ScheduleWindowRefreshAfterHeartbeat");
    expect(source).toContain("UnityBridgeRuntime.HeartbeatWritten -= ScheduleWindowRefreshAfterHeartbeat");
    expect(source).toContain("EditorApplication.update += RefreshWindowAfterHeartbeat");
    expect(refreshAllBody).toContain("GatewayProcessManager.GatewayRuntimeStatus processStatus = null");
    expect(refreshAllBody).toContain("processStatus = RefreshProcess(probeHealth)");
    expect(refreshAllBody).toContain("RefreshGateway(processStatus)");
    expect(refreshAllBody).toContain("RefreshSetupChecklist(processStatus)");
    expect(refreshGatewayBody).toContain("ChecklistProcessStatus(processStatusOverride)");
    expect(refreshWindowAfterHeartbeatBody).toContain("RefreshHeartbeatViews(details)");
    expect(refreshWindowAfterHeartbeatBody).not.toContain("RefreshProcess");
    expect(refreshWindowAfterHeartbeatBody).not.toContain("RefreshHeartbeatDependentViews");
    expect(heartbeatViewsBody.replace(/\s+/g, " ").trim()).toBe('{ TrackMcpWindowStep(details, "heartbeat_views.bridge", RefreshBridge); RefreshUnityBridgeChecklist(); RefreshCatalogPolicySummaryAfterHeartbeat(); RefreshHeartbeatHealthActions(); RefreshFtueAfterHeartbeat(); }');
    expect(heartbeatChecklistBody).toContain("File.Exists(UnityBridgeRuntime.HeartbeatFilePath)");
    expect(heartbeatChecklistBody).toContain("UnityBridgeRuntime.BridgePort > 0 && heartbeatExists");
    expect(heartbeatChecklistBody).toContain('SetChecklist(m_checklistUnityBridge, bridgeReady ? "ok" : "error"');
    expect(heartbeatCatalogBody).toContain("commandCount == m_lastCatalogPolicySummaryCommandCount");
    expect(heartbeatCatalogBody).toContain("BuildCatalogPolicySummaryText(out var state, m_lastGatewayVirtualCommandCount, m_lastCatalogPolicyContext)");
    expect(heartbeatCatalogBody).toContain("m_lastCatalogPolicyContext == null");
    expect(heartbeatCatalogBody).toContain("SetChecklist(m_checklistCatalogPolicySummary, state, text)");
    expect(heartbeatHealthActionsBody).toContain("m_lastProbedProcessStatus ?? m_lastProcessStatus");
    expect(heartbeatHealthActionsBody).toContain("McpRuntimeHealthViewState.Evaluate(processStatus, null, LocalBridgeHeartbeatStale())");
    expect(heartbeatHealthActionsBody).toContain("shortcutNeeded = healthRefreshNeeded || CatalogRefreshActionNeeded()");
    expect(heartbeatHealthActionsBody).toContain("SetActionNeeded(m_setupRefreshHealthButton, healthRefreshNeeded");
    expect(heartbeatHealthActionsBody).toContain("SetHeaderShortcut(m_refreshHealthShortcutButton, shortcutNeeded");
    expect(heartbeatHealthActionsBody).toContain("AnyHeaderShortcutVisible() ? DisplayStyle.Flex : DisplayStyle.None");
    for (const blockingOrHeavyCall of [
      "RefreshProcess", "RefreshGateway", "RefreshSetupChecklist", "RefreshCanonicalPathRegistry",
      "RefreshMcpConnection", "RefreshDiagnostics", "RefreshHeaderShortcuts", "ApplyLocalization",
      "GatewayProcessManager", "GatewayInstaller", "NodeRuntimeInstaller", "GatewayNetworkModeManager",
      "ProcessUtil", "UnityWebRequest", "WaitForExit", "Thread.Sleep", "Task.Run", "File.ReadAllText",
    ]) {
      expect(refreshWindowAfterHeartbeatBody).not.toContain(blockingOrHeavyCall);
      expect(heartbeatViewsBody).not.toContain(blockingOrHeavyCall);
      expect(heartbeatChecklistBody).not.toContain(blockingOrHeavyCall);
      expect(heartbeatCatalogBody).not.toContain(blockingOrHeavyCall);
      expect(catalogSummaryBuilderBody).not.toContain(blockingOrHeavyCall);
      expect(heartbeatHealthActionsBody).not.toContain(blockingOrHeavyCall);
      expect(refreshBridgeBody).not.toContain(blockingOrHeavyCall);
    }
    expect(runtimeHealthState).toContain("processStatus.LiveProjectsCount > 0 || processStatus.LiveProjectRecordsCount > 0");
    expect(runtimeHealthState).toContain("processStatus.StaleProjectRecordsCount > 0");
    expect(runtimeHealthState).toContain("&& !hasLiveProjectCoverage;");
    expect(runtimeHealthState).toContain("&& VersionMismatch;");
    expect(runtimeHealthState).not.toContain("VersionMismatch || processStatus.InvalidProjectRecordsCount");
    expect(runtimeHealthState).not.toContain("processStatus.InvalidProjectRecordsCount == 0");
    expect(refreshAllBody).toContain("ForceHeartbeatBeforeHealthProbe(probeHealth)");
    expect(refreshHealthBody).toContain("ForceHeartbeatBeforeHealthProbe(true)");
    expect(source).toContain("UnityBridgeRuntime.ForceHeartbeat(\"refresh_health\")");
    expect(source).toContain("var displayStatus = ChecklistProcessStatus(status)");
    expect(source).toContain("UpdateRefreshHealthActionButtons(displayStatus)");
    expect(source).toContain("return displayStatus");
    expect(refreshHealthBody).toContain("RefreshProcess(true)");
    expect(refreshHealthBody).toContain("RefreshHeartbeatDependentViews(processStatus, details");
    expect(refreshHealthBody).toContain('TrackMcpWindowStep(details, "mcp_connection", () => networkStatus = RefreshMcpConnection())');
    expectInOrder(refreshHealthBody, ["RefreshHeartbeatDependentViews(processStatus", "networkStatus = RefreshMcpConnection()", "RefreshFtue(processStatus, networkStatus)"]);
    expect(heartbeatDependentBody).toContain("RefreshGateway(processStatus)");
    expect(heartbeatDependentBody).toContain("prefix + \"bridge\", RefreshBridge");
    expect(heartbeatDependentBody).toContain("RefreshSetupChecklist(processStatus)");
    expect(heartbeatDependentBody).not.toContain("RefreshMcpConnection");
    expect(heartbeatDependentBody).not.toContain('prefix + "mcp_connection"');
    expect(heartbeatDependentBody).toContain("prefix + \"diagnostics\", RefreshDiagnostics");
    expect(source).toContain('runtimeHealth.HealthRefreshActionNeeded || CatalogRefreshActionNeeded()');
    expect(source).toContain('if (unityBridgeCommandCount == 0)');
    expect(source).toContain('checklistState = "loading"');
    expect(source).toContain('Click H* Refresh after bridge startup completes.');
    expect(source).toContain("BuildCatalogPolicySummaryText(out checklistState, GatewayVirtualCommandCount(), ReadPolicyPreviewContext())");
    expect(source).toContain('TrackMcpWindowStep(details, "permissions", RefreshPermissions)');
    expect(source).toContain('TrackMcpWindowStep(details, "skills", RefreshSkills)');
    expect(source).toContain('TrackMcpWindowStep(details, "unity_ai_assistant", RefreshUnityAiAssistant)');
    expect(source).toContain('TrackMcpWindowStep(details, "localization", ApplyLocalization)');
    expect(checklistProcessBody).toContain("!processStatus.HealthProbed");
    expect(checklistProcessBody).toContain("m_lastProbedProcessStatus");
    expect(bridgeRuntime).toContain("public static event Action HeartbeatWritten");
    expect(bridgeRuntime).toContain("NotifyHeartbeatWritten()");
    expect(heartbeatWriter).toContain("public bool WriteIfDue");
    expect(heartbeatWriter).toContain("return true");
    expect(gatewayProcessManager).toContain("HealthProbed = probeHealth");
    expect(gatewayProcessManager).toContain("public bool HealthProbed");
    expect(gatewayProcessManager).toContain("status.HealthVersion = parsed.HealthVersion");
    expect(gatewayProcessManager).toContain("status.LiveProjectsCount = parsed.LiveProjectsCount");
    expect(gatewayProcessManager).toContain("status.StaleProjectsCount = parsed.StaleProjectsCount");
    expect(gatewayProcessManager).toContain("status.StaleProjectRecordsCount = parsed.StaleProjectRecordsCount");
    expect(gatewayProcessManager).toContain("status.InvalidProjectRecordsCount = parsed.InvalidProjectRecordsCount");
    expect(gatewayProcessManager).toContain("public int StaleProjectsCount");
    expect(gatewayProcessManager).toContain("public int StaleProjectRecordsCount");
    expect(gatewayProcessManager).toContain("GatewayRuntimeVersionMatches(currentStatus, gatewayStatus)");
    expect(gatewayProcessManager).toContain("StopRunningGateway(windowsEditor, out var stopMessage)");
  });

  it("adds copy buttons and colorblind-safe status icons to text fields", () => {
    const source = readWindowSources();
    const style = fs.readFileSync(windowStylePath, "utf8");

    expect(source).toContain("mcp-copy-button");
    expect(source).toContain("CopyText(key, valueLabel.text)");
    expect(source).toContain("CopyText(\"Status\", statusLabel.text)");
    expect(source).toContain("✔");
    expect(source).toContain("▲");
    expect(source).toContain("●");
    expect(source).toContain("Status Legend");
    expect(source).toContain("MCP Client Endpoint");
    expect(source).toContain("Bridge token displays Present/Missing; MCP token displays Present/Not configured");
    expect(style).toContain("--mcp-info");
    expect(style).toContain("--mcp-optional");
    expect(style).toContain("border-left-color: var(--mcp-info)");
    expect(style).toContain(".mcp-command-row--allow");
    expect(style).not.toContain(".mcp-policy-editor-catalog-list");
    expect(style).toContain(".mcp-command-row--ask");
    expect(style).toContain(".mcp-command-row--deny");
    expect(style).toContain(".mcp-command-row--feature-hidden");
    expect(style).toContain(".mcp-policy-action-field");
    expect(style).toContain(".mcp-policy-action--allow");
    expect(style).toContain(".mcp-policy-action--ask");
    expect(style).toContain(".mcp-policy-action--deny");
    expect(style).toContain(".mcp-checklist-field");
    expect(style).toContain(".mcp-checklist-icon--optional");
    expect(style).toContain(".mcp-checklist--info");
    expect(style).toContain(".mcp-checklist-icon--info");
    expect(style).toContain(".mcp-checklist--loading");
    expect(style).toContain(".mcp-checklist-icon--loading");
    expect(style).toContain(".mcp-action-needed");
    expect(style).toContain("background-color: rgba(255, 214, 82, 0.95)");
    expect(style).toContain("color: var(--mcp-optional)");
    expect(source).not.toContain("OpenCode Endpoint");
    expect(source).toContain("Active/Success");
    expect(source).toContain("Warning/Caution");
    expect(source).toContain("Error/Danger");
  });
});
