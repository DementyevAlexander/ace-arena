import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const projectCommandsPath = path.join(packageRoot, "Editor", "Commands", "ProjectReadonlyCommands.cs");
const projectSettingsInspectionCommandsPath = path.join(packageRoot, "Editor", "Commands", "ProjectSettingsInspectionCommands.cs");

describe("Unity project read-only command source-contract", () => {
  it("declares a project settings summary command through the catalog", () => {
    const source = fs.readFileSync(projectCommandsPath, "utf8");

    expect(source).toContain('"project.settings.summary"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('Capability = "project_readonly"');
    expect(source).toContain('Tags = new string[] { "project", "settings", "unity-ai-assistant" }');
  });

  it("maps Unity AI Assistant static project settings without host paths", () => {
    const source = fs.readFileSync(projectCommandsPath, "utf8");

    expect(source).toContain("Unity.GetStaticProjectSettingsTool");
    expect(source).toContain("official_path_fields_omitted");
    expect(source).toContain("GraphicsSettings.currentRenderPipeline");
    expect(source).toContain("EditorUserBuildSettings.activeBuildTarget");
    expect(source).toContain("PlayerSettings.GetApiCompatibilityLevel");
    expect(source).toContain("Application.unityVersion");
    expect(source).toContain("ENABLE_INPUT_SYSTEM");
    expect(source).toContain("ENABLE_LEGACY_INPUT_MANAGER");
    expect(source).not.toContain("Application.dataPath");
    expect(source).not.toContain("ProjectPaths.ProjectRoot");
    expect(source).not.toContain("GetProjectRootPath");
    expect(source).not.toContain("GetAssetsPath");
    expect(source).not.toContain("GetPackagesPath");
    expect(source).not.toContain("GetProjectSettingsPath");
    expect(source).not.toContain("GetLibraryPath");
  });

  it("does not use file, network, process, or mutating project APIs", () => {
    const source = fs.readFileSync(projectCommandsPath, "utf8");

    expect(source).not.toContain("File.");
    expect(source).not.toContain("Directory.");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
    expect(source).not.toContain("Process.Start");
    expect(source).not.toContain("AssetDatabase.");
    expect(source).not.toContain("EditorBuildSettings.scenes =");
    expect(source).not.toContain("PlayerSettings.Set");
  });

  it("declares a bounded project settings inspect command for Unity.GetProjectSettings parity", () => {
    const source = fs.readFileSync(projectSettingsInspectionCommandsPath, "utf8");

    expect(source).toContain('"project.settings.inspect"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('Capability = "project_readonly"');
    expect(source).toContain('ResultShape = "object_details"');
    expect(source).toContain('Tags = new string[] { "project", "settings", "unity-ai-assistant" }');
    expect(source).toContain("Unity.GetProjectSettings");
    expect(source).toContain("persisted serialized Unity ProjectSettings data");
    expect(source).toContain("in Edit or Play Mode");
  });

  it("bounds and allowlists project settings inspection arguments", () => {
    const source = fs.readFileSync(projectSettingsInspectionCommandsPath, "utf8");

    expect(source).toContain('const string SchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('\\"AudioManager\\"');
    expect(source).toContain('\\"PlayerSettings\\"');
    expect(source).toContain('\\"QualitySettings\\"');
    expect(source).toContain('\\"property_path\\"');
    expect(source).toContain('\\"maxLength\\":512');
    expect(source).toContain('\\"max_depth\\"');
    expect(source).toContain('\\"maximum\\":6');
    expect(source).toContain('\\"max_array_elements\\"');
    expect(source).toContain('\\"maximum\\":20');
    expect(source).toContain('\\"max_chars\\"');
    expect(source).toContain('\\"maximum\\":16384');
    expect(source).toContain("CanonicalSettingsName(name.Trim())");
    expect(source).toContain("PropertyPathLooksUnsafe(propertyPath)");
  });

  it("loads only known ProjectSettings assets and redacts sensitive or host-path values", () => {
    const source = fs.readFileSync(projectSettingsInspectionCommandsPath, "utf8");

    expect(source).toContain('"ProjectSettings/DynamicsManager.asset"');
    expect(source).toContain('"ProjectSettings/NavMeshAreas.asset"');
    expect(source).toContain('"ProjectSettings/ProjectSettings.asset"');
    expect(source).toContain('"ProjectSettings/" + name + ".asset"');
    expect(source).toContain("InternalEditorUtility.LoadSerializedFileAndForget(candidate)");
    expect(source).toContain("DestroyLoadedSettings(loadedSettings)");
    expect(source).toContain("UnityEngine.Object.DestroyImmediate(loaded[index])");
    expect(source).toContain("new SerializedObject(settingsAsset)");
    expect(source).toContain("serializedObject.FindProperty(propertyPath)");
    expect(source).toContain("propertyPath.Replace('/', '.')");
    expect(source).toContain("ShouldRedactProperty(property)");
    expect(source).toContain("LooksLikeHostPath(property.stringValue)");
    expect(source).toContain('"redaction_applied\\":true"');
    expect(source).toContain('"value_source\\":\\"persisted_serialized_file\\""');
    expect(source).not.toContain("AssetDatabase.LoadAssetAtPath");
    expect(source).not.toContain("Directory.GetFiles");
    expect(source).not.toContain("File.");
    expect(source).not.toContain("Application.dataPath");
    expect(source).not.toContain("ProjectPaths.ProjectRoot");
    expect(source).not.toContain("SaveAssets");
    expect(source).not.toContain("RequiresEditorStatus");
  });
});
