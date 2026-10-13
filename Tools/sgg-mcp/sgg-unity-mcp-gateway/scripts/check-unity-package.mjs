#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";

const root = process.cwd();
const unityPackageDir = path.join(root, "unity/Packages/com.sungeargames.unity-mcp");
const unityManifestPath = path.join(unityPackageDir, "package.json");
const editorAsmdefPath = path.join(unityPackageDir, "Editor/SGG.UnityMCP.Editor.asmdef");
const runtimeAsmdefPath = path.join(unityPackageDir, "Runtime/SGG.UnityMCP.Runtime.asmdef");
const canonicalPathAsmdefPath = path.join(unityPackageDir, "Editor/CanonicalPath/SGG.UnityMCP.Editor.CanonicalPath.asmdef");
const canonicalPathProviderDir = path.join(unityPackageDir, "Editor/CanonicalPath");
const unityTestFrameworkDependency = "1.1.33";
const requiredUnityModuleDependencies = [
  "com.unity.modules.imageconversion",
  "com.unity.modules.screencapture",
];
const requiredEditorFiles = [
  "Editor/Bridge/MainThreadDispatcher.cs",
  "Editor/Bridge/BridgeRuntimeState.cs",
  "Editor/Bridge/BridgeServer.cs",
  "Editor/Bridge/HeartbeatRecord.cs",
  "Editor/Bridge/HeartbeatWriter.cs",
  "Editor/Bridge/PackageUpdateMonitor.cs",
  "Editor/Bridge/ProjectPaths.cs",
  "Editor/Bridge/UnityBridgeRuntime.cs",
  "Editor/Setup/EditorRoslynInstaller.cs",
  "Editor/Setup/BoundedProcessOperation.cs",
  "Editor/Setup/UnityCliPipelineSetup.cs",
  "Editor/Setup/UnityCliUpdateCheck.cs",
  "Editor/Setup/WindowsCliUpdateOperation.cs",
  "Editor/Setup/UnityPipelineUpdateOperation.cs",
  "Editor/Setup/UnityCliProjectSkillInstaller.cs",
  "Editor/Setup/GatewayInstaller.cs",
  "Editor/Setup/GatewayTestHistoryModel.cs",
  "Editor/Setup/GatewayTestHistoryModel.cs.meta",
  "Editor/Setup/GatewayTestHistoryManager.cs",
  "Editor/Setup/GatewayTestHistoryManager.cs.meta",
  "Editor/Setup/GatewayProcessManager.cs",
  "Editor/Setup/NodeRuntimeInstaller.cs",
  "Editor/Setup/ProcessUtil.cs",
  "Editor/Setup/UnityMcpPackagePaths.cs",
  "Editor/GatewayRuntime/gateway-cli.mjs",
  "Editor/GatewayRuntime/gateway-cli.LEGAL.txt",
  "Editor/GatewayRuntime/gateway-cli.LEGAL.txt.meta",
  "Editor/GatewayRuntime/gateway-runtime.json",
  "Editor/GatewayRuntime/opencode-presentation/package.json",
  "Editor/GatewayRuntime/opencode-presentation/server.js",
  "Editor/GatewayRuntime/opencode-presentation/sgg-plugin-manifest.json",
  "Editor/GatewayRuntime/THIRD_PARTY_NOTICES.md",
  "Editor/GatewayRuntime/THIRD_PARTY_NOTICES.md.meta",
  "Editor/GatewayRuntime/sbom.cdx.json",
  "Editor/GatewayRuntime/sbom.cdx.json.meta",
  "Editor/GatewayRuntime/config/gateway.example.json",
  "Editor/GatewayRuntime/config/policy.example.json",
  "Editor/UI/UnityMcpSetupWindow.cs",
  "Editor/UI/UnityMcpSetupWindow.SetupStatus.cs",
  "Editor/UI/UnityMcpSetupWindow.SetupStatus.cs.meta",
  "Editor/UI/UnityMcpSetupWindow.PermissionsCommands.cs",
  "Editor/UI/UnityMcpSetupWindow.PermissionsCommands.cs.meta",
  "Editor/UI/UnityMcpSetupWindow.PermissionsCommands.Refresh.cs",
  "Editor/UI/UnityMcpSetupWindow.PermissionsCommands.Refresh.cs.meta",
  "Editor/UI/UnityMcpSetupWindow.PlansSkills.cs",
  "Editor/UI/UnityMcpSetupWindow.PlansSkills.cs.meta",
  "Editor/UI/UnityMcpSetupWindow.UnityAiDiagnostics.cs",
  "Editor/UI/UnityMcpSetupWindow.UnityAiDiagnostics.cs.meta",
  "Editor/UI/UnityMcpSetupWindow.UnityCliPipeline.cs",
  "Editor/UI/UnityMcpSetupWindow.UnityCliPipeline.cs.meta",
  "Editor/UI/UnityMcpSetupWindow.TestHistory.cs",
  "Editor/UI/UnityMcpSetupWindow.TestHistory.cs.meta",
  "Editor/UI/UnityMcpSetupWindow.uss",
  "Editor/UI/State.meta",
  "Editor/UI/State/McpWindowState.cs",
  "Editor/UI/State/McpWindowState.cs.meta",
  "Editor/UI/State/McpRuntimeHealthViewState.cs",
  "Editor/UI/State/McpRuntimeHealthViewState.cs.meta",
  "Editor/UI/State/McpRuntimePolicyViewModel.cs",
  "Editor/UI/State/McpRuntimePolicyViewModel.cs.meta",
  "Editor/UI/State/McpWindowTimingViewModel.cs",
  "Editor/UI/State/McpWindowTimingViewModel.cs.meta",
  "Editor/UI/State/McpDiagnosticsCopyViewModel.cs",
  "Editor/UI/State/McpDiagnosticsCopyViewModel.cs.meta",
  "Editor/UI/Localization/McpWindowLocalization.cs",
  "Editor/UI/Localization/mcp-window.en.xlf",
  "Editor/UI/Localization/mcp-window.ru.xlf",
  "Editor/UI/Fonts/NotoSans-Regular.ttf",
  "Editor/UI/Fonts/NotoSans-OFL.txt",
  "Editor/CanonicalPath/SGG.UnityMCP.Editor.CanonicalPath.asmdef",
  "Editor/CanonicalPath/CanonicalPathBridgeRegistration.cs",
  "Editor/Paths/CanonicalPathBridge.cs",
  "Editor/Paths/PathGuard.cs",
  "Editor/Paths/ICanonicalPathService.cs",
  "Editor/Paths/CanonicalPathService.cs",
  "Editor/Commands/AssetIndexCacheInvalidator.cs",
  "Editor/Commands/BuiltInCommands.cs",
  "Editor/Commands/AssetIndexCommands.cs",
  "Editor/Commands/CommandExecutionResult.cs",
  "Editor/Commands/CommandManifestScanner.cs",
  "Editor/Commands/CommandRegistry.cs",
  "Editor/Commands/ConsoleLogBuffer.cs",
  "Editor/Commands/EditorCSharpExecutionCommands.cs",
  "Editor/Commands/EditorSafeWriteCommands.cs",
  "Editor/Commands/EditorSafeWriteCommands.cs.meta",
  "Editor/Commands/EditorScreenshotCommands.cs",
  "Editor/Commands/EditorStatusCommands.cs",
  "Editor/Commands/EditorUtilityCommands.cs",
  "Editor/Commands/EditorUtilityCommands.cs.meta",
  "Editor/Commands/JobArtifactCommands.cs",
  "Editor/Commands/JsonUtil.cs",
  "Editor/Commands/KnowledgeCommands.cs",
  "Editor/Commands/McpCommandContext.cs",
  "Editor/Commands/PackageManagementCommands.cs",
  "Editor/Commands/RuntimeDebuggingCommands.cs",
  "Editor/Commands/SemanticDiffCommands.cs",
  "Editor/Commands/SemanticInspectionCommands.cs",
  "Editor/Commands/SerializeReferenceCommands.cs",
  "Editor/Commands/SerializeReferenceCommands.cs.meta",
];

if (!fs.existsSync(unityPackageDir)) {
  console.log("[check-unity-package] skipped: Unity package is not present yet");
  process.exit(0);
}

let failed = false;

function fail(message) {
  console.error(`[check-unity-package] ${message}`);
  failed = true;
}

function editorSourceFiles(dir, files = []) {
  if (!fs.existsSync(dir)) {
    return files;
  }

  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const entryPath = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      editorSourceFiles(entryPath, files);
    } else if (entry.isFile() && entry.name.endsWith(".cs")) {
      files.push(entryPath);
    }
  }

  return files;
}

function sourceLinesWithDirectCanonicalPathReferences(filePath) {
  const source = fs.readFileSync(filePath, "utf8");
  return source
    .split(/\r?\n/)
    .map((line, index) => ({ lineNumber: index + 1, line }))
    .filter(({ line }) => /\busing\s+CanonicalPath(?:\.|;)|\b(?:global::)?CanonicalPath\.[A-Za-z_]/.test(line));
}

if (!fs.existsSync(unityManifestPath)) {
  fail("missing unity/Packages/com.sungeargames.unity-mcp/package.json");
} else {
  const rootPackage = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  const unityPackage = JSON.parse(fs.readFileSync(unityManifestPath, "utf8"));

  if (unityPackage.name !== "com.sungeargames.unity-mcp") {
    fail(`Unity package name must be com.sungeargames.unity-mcp, got ${JSON.stringify(unityPackage.name)}`);
  }

  if (unityPackage.version !== rootPackage.version) {
    fail(`Unity package version ${unityPackage.version} must match root package.json version ${rootPackage.version}`);
  }

  if (unityPackage.dependencies?.["com.romanilyin.canonicalpath"]) {
    fail("Unity package must not declare CanonicalPath as a hard dependency; setup installs it as a required project dependency");
  }

  if (unityPackage.dependencies?.["com.unity.test-framework"] !== unityTestFrameworkDependency) {
    fail("Unity package must declare Unity Test Framework 1.1.33 for Unity test automation compatibility");
  }
  for (const dependency of requiredUnityModuleDependencies) {
    if (unityPackage.dependencies?.[dependency] !== "1.0.0") {
      fail(`Unity package must declare ${dependency} 1.0.0 for Game View capture compatibility`);
    }
  }
}

if (!fs.existsSync(editorAsmdefPath)) {
  fail("missing Editor/SGG.UnityMCP.Editor.asmdef");
} else {
  const editorAsmdef = JSON.parse(fs.readFileSync(editorAsmdefPath, "utf8"));
  if (editorAsmdef.references?.includes("CanonicalPath.UnityBridge")) {
    fail("Main editor asmdef must not hard-reference CanonicalPath.UnityBridge; the CanonicalPath provider asmdef owns direct calls");
  }
  const canonicalVersionDefine = editorAsmdef.versionDefines?.find((entry) => entry?.name === "com.romanilyin.canonicalpath");
  if (canonicalVersionDefine) {
    fail("Main editor asmdef must not define SGG_UNITY_MCP_CANONICALPATH without a CanonicalPath assembly reference");
  }
}

if (!fs.existsSync(runtimeAsmdefPath)) {
  fail("missing Runtime/SGG.UnityMCP.Runtime.asmdef");
} else {
  const runtimeAsmdef = JSON.parse(fs.readFileSync(runtimeAsmdefPath, "utf8"));
  if (JSON.stringify(runtimeAsmdef.includePlatforms) !== JSON.stringify([])) {
    fail("Runtime capture bridge asmdef must remain PlayerLoop-loadable in the Editor");
  }
  if (!runtimeAsmdef.defineConstraints?.includes("UNITY_EDITOR")) {
    fail("Runtime capture bridge asmdef must retain the UNITY_EDITOR constraint");
  }
}

if (!fs.existsSync(canonicalPathAsmdefPath)) {
  fail("missing Editor/CanonicalPath/SGG.UnityMCP.Editor.CanonicalPath.asmdef");
} else {
  const canonicalPathAsmdef = JSON.parse(fs.readFileSync(canonicalPathAsmdefPath, "utf8"));
  if (!canonicalPathAsmdef.references?.includes("SGG.UnityMCP.Editor")) {
    fail("CanonicalPath provider asmdef must reference SGG.UnityMCP.Editor to register the provider");
  }
  if (!canonicalPathAsmdef.references?.includes("CanonicalPath.UnityBridge")) {
    fail("CanonicalPath provider asmdef must direct-reference CanonicalPath.UnityBridge");
  }
  if (!canonicalPathAsmdef.defineConstraints?.includes("SGG_UNITY_MCP_CANONICALPATH")) {
    fail("CanonicalPath provider asmdef must be gated by SGG_UNITY_MCP_CANONICALPATH");
  }
  const canonicalVersionDefine = canonicalPathAsmdef.versionDefines?.find((entry) => entry?.name === "com.romanilyin.canonicalpath");
  if (canonicalVersionDefine?.define !== "SGG_UNITY_MCP_CANONICALPATH") {
    fail("CanonicalPath provider asmdef must define SGG_UNITY_MCP_CANONICALPATH from com.romanilyin.canonicalpath");
  }
}

for (const filePath of editorSourceFiles(path.join(unityPackageDir, "Editor"))) {
  if (filePath.startsWith(canonicalPathProviderDir + path.sep)) {
    continue;
  }

  const directReferences = sourceLinesWithDirectCanonicalPathReferences(filePath);
  for (const reference of directReferences) {
    fail(`Direct CanonicalPath API references must stay inside Editor/CanonicalPath provider assembly: ${path.relative(unityPackageDir, filePath)}:${reference.lineNumber}`);
  }
}

for (const relativePath of requiredEditorFiles) {
  if (!fs.existsSync(path.join(unityPackageDir, relativePath))) {
    fail(`missing ${relativePath}`);
  }
}

if (!failed) {
  console.log("[check-unity-package] ok");
} else {
  process.exitCode = 1;
}
