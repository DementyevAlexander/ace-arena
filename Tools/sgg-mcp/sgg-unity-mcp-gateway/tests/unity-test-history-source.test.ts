import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

const root = path.join(process.cwd(), "unity/Packages/com.sungeargames.unity-mcp");
const read = (relative: string) => fs.readFileSync(path.join(root, relative), "utf8");

describe("Unity test history UI integration contracts", () => {
  it("ships independent requested defaults and finite settings", () => {
    const source = read("Editor/Setup/GatewayTestHistoryModel.cs");
    const config = JSON.parse(fs.readFileSync("config/gateway.example.json", "utf8"));
    expect(source).toContain("DefaultMiB = 200;");
    expect(source).toContain("DefaultBindings = 192000;");
    expect(source).toContain("MaxBindings = 1000000;");
    expect(config.test_runs.storage).toEqual({ max_bytes: 209715200, max_bindings: 192000, retention_days: 30 });
    const ui = read("Editor/UI/UnityMcpSetupWindow.TestHistory.cs");
    expect(ui).toContain('name = "setup-test-history-bytes"');
    expect(ui).toContain('name = "setup-test-history-bindings"');
    expect(ui).toContain("Percent(snapshot.bytes, snapshot.max_bytes)");
    expect(ui).toContain("Percent(snapshot.bindings, snapshot.max_bindings)");
    expect(ui).toContain("Running limits not verified.");
  });

  it("uses only a bounded integrity-checked local CLI, never a shell or direct metadata delete", () => {
    const manager = read("Editor/Setup/GatewayTestHistoryManager.cs");
    const ui = read("Editor/UI/UnityMcpSetupWindow.TestHistory.cs");
    expect(manager).toContain("TryReadCanonicalCheckoutArtifact(target.CliPath, cliHash");
    expect(manager).not.toContain("GatewayInstaller.GetStatus()");
    expect(manager).not.toContain("NodeRuntimeInstaller.GetStatus()");
    expect(manager).toContain("TryReadInt64Path");
    expect(manager).toContain("ConfigurationSaved");
    expect(manager).toContain('"--test-run-store"');
    expect(manager).toContain("20000");
    expect(manager).toContain('"admin_outcome_uncertain"');
    expect(manager).toContain('"--preview-fingerprint"');
    expect(manager).toContain('"--config-fingerprint"');
    expect(ui).toContain("Task.Run(() => GatewayTestHistoryManager.Run");
    expect(ui).toContain("ConfirmHistory(");
    expect(ui).toContain("generation != m_historyUiGeneration");
    expect(manager + ui).not.toMatch(/File\.(?:Delete|Write|Move)|Directory\.Delete|powershell|cmd\.exe|--auto-allow|--policy/);
  });

  it("prevents optional checks and global fixture suppression from blanking initial local rows", () => {
    const source = read("Editor/UI/UnityMcpSetupWindow.cs");
    expect(source).toContain("m_skipAutomaticRefreshForTests = SkipAutomaticRefreshForTests;");
    expect(source).toContain("if (!m_skipAutomaticRefreshForTests)");
    expect(source).toContain('new Label(string.IsNullOrEmpty(value) ? "Not checked." : value)');
    expect(source).toContain("m_refreshFailures.Add(failure)");
    expect(source).toContain("if (m_isIsolatedTestFixture) return;");
    expect(source).toContain("if (!m_isIsolatedTestFixture) ResumeUnityCliOperations();");
    const body = source.slice(source.indexOf("void BuildGui()"), source.indexOf("static void ApplyWindowFont"));
    expect(body.indexOf("RefreshAll(true)")).toBeLessThan(body.indexOf("SelectTab(WindowState.SelectedTab)"));
    const freshness = read("Editor/UI/UnityMcpSetupWindow.UnityCliPipeline.cs");
    expect(freshness).toContain("m_initializingGui || SkipToolFreshnessForTests");
    const regressions = read("Tests/Editor/UnityMcpSetupPopulationTests.cs");
    expect(regressions).toContain("InitialGuiPopulatesRealLocalRows");
    expect(regressions).toContain("window.CreateGUI();");
    expect(regressions).not.toContain("Assert.Ignore");
  });

  it("pairs all new history text in English and Russian", () => {
    const en = read("Editor/UI/Localization/mcp-window.en.xlf");
    const ru = read("Editor/UI/Localization/mcp-window.ru.xlf");
    const ids = (text: string) => [...text.matchAll(/<trans-unit id="((?:history|setup\.refresh|setup\.not_checked)[^"]*)"/g)].map(match => match[1]).sort();
    expect(ids(en)).toEqual(ids(ru));
    expect(ids(en).length).toBeGreaterThan(30);
    expect(ru).toContain("200 МиБ; 192000 записей.");
    expect(read("Editor/UI/Localization/McpWindowLocalization.cs")).toContain("element as IntegerField");
  });
});
