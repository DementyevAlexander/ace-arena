import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const bridgeDir = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp", "Editor", "Bridge");

describe("Unity package refresh source-contract", () => {
  it("detects package/runtime file changes without automatic refresh", () => {
    const monitorSource = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const builtInSource = readPackageFile("Editor/Commands/BuiltInCommands.cs");
    const testRunSource = readPackageFile("Editor/Commands/BuildTestAutomationCommands.cs");
    const stateSource = readPackageFile("Editor/Bridge/BridgeRuntimeState.cs");
    const serverSource = readPackageFile("Editor/Bridge/BridgeServer.cs");

    expect(monitorSource).toContain("PackageUpdateMonitor");
    expect(monitorSource).toContain("FileSystemWatcher");
    expect(monitorSource).toContain("Task.Run(() => ComputeFingerprintSafely(request))");
    expect(monitorSource).toContain("FallbackScanIntervalSeconds = 120.0");
    expect(monitorSource).toContain("Packages/manifest.json");
    expect(monitorSource).toContain("Packages/packages-lock.json");
    expect(monitorSource).toContain('var status = projectPackagesChanged ? "stale_package_resolution" : "package_refresh_required"');
    expect(monitorSource).toContain("package resolution must be refreshed before compile readiness can be trusted");
    expect(monitorSource).toContain("compile.refresh_and_wait");
    expect(monitorSource).toContain("validation.after_edit");
    expect(monitorSource).toContain("public void MarkRefreshObserved");
    expect(monitorSource).toContain("TryAppendProjectPackageFile(request.ProjectRoot, projectPackagesInput");
    expect(monitorSource).toContain("LocalPackageMetadataInspector.InspectCapturedPackages");
    expect(monitorSource).toContain("ApplyLocalMetadataStatus");
    expect(monitorSource).toContain("an AssetDatabase refresh or compile barrier does not prove UPM metadata agreement");
    expect(monitorSource.indexOf("ApplyCurrentSnapshot(snapshot)")).toBeLessThan(monitorSource.indexOf('SetStatus(state, "current", "Package/runtime files and project package manifest/lock were validated after an explicit refresh.'));
    expect(monitorSource).not.toContain("Client.Resolve");
    expect(monitorSource).not.toContain("AssetDatabase.Refresh");
    expect(monitorSource).not.toContain("TryGetRefreshBlocker");
    expect(monitorSource).not.toContain("RequestRefresh");
    expect(builtInSource).toContain("UnityBridgeRuntime.CompleteAssetRefresh(refreshObservation)");
    expect(builtInSource).toContain("RequireNoUnsavedSceneChanges(context.State, \"assets.refresh\", \"assets.refresh\")");
    expect(builtInSource).toContain('packageMetadataUnavailable ? "package_metadata_unavailable" : stalePackageResolution ? "stale_package_resolution" : "package_refresh_required"');
    expect(builtInSource).toContain('\\"package_refresh_required\\"');
    expect(builtInSource).toContain('\\"stale_package_resolution\\"');
    expect(stateSource).toContain('+ ",\\"auto_refresh_enabled\\":\\"unknown\\""');
    expect(stateSource).toContain("const int MaxPackageUpdateJsonChars = 1024");
    expect(stateSource).toContain("json = BuildPackageUpdateJson(true)");
    expect(stateSource).toContain("json = BuildPackageUpdateJson(true, true)");
    expect(stateSource).toContain("json.Length < MaxPackageUpdateJsonChars ? json : BuildMinimalPackageUpdateJson()");
    expect(stateSource).toContain("Package update diagnostics exceeded bounds.");
    expect(stateSource).toContain('+ ",\\"local_package_code_inventory\\":" + LocalPackageCodeInventoryJson(true, false)');
    expect(stateSource).toContain("Math.Min(MaxLocalPackageCount, Math.Max(0, LocalPackageCodeInventoryPackageCount))");
    expect(stateSource).toContain("Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventorySourceFileCount))");
    expect(stateSource).toContain("BoundedJsonString(PackageUpdateMessage, compact ? 8 : 128)");
    expect(stateSource).toContain("PackageFingerprintFailureJson()");
    expect(stateSource).toContain("encoded.Length + encodedLength + 1 > maxEncodedChars");
    expect(stateSource).toContain("public bool PackageRefreshRequired => PackageRefreshPending");
    expect(stateSource).toContain("public bool StalePackageResolution => PackageRefreshPending");
    expect(stateSource).toContain("public bool PackageMetadataUnavailable { get; private set; }");
    expect(stateSource).toContain("public string PackageLastObservedUtc { get; private set; }");
    expect(stateSource).toContain("public string PackageLastTransitionUtc { get; private set; }");
    expect(stateSource).toContain('+ ",\\\"last_observed_utc\\\":"');
    expect(stateSource).toContain('+ ",\\\"last_transition_utc\\\":"');
    expect(stateSource).toContain("&& localMetadataUnavailable");
    expect(serverSource).toContain("PackageMetadataUnavailableResult");
    expect(serverSource).toContain('CommandExecutionResult.Error("package_metadata_unavailable"');
    expect(stateSource).toContain('\\"stale_package_resolution\\"');
    expect(testRunSource).toContain('+ ",\\\"package_update\\\":"');
    expect(testRunSource).toContain("lifecycle.PackageUpdateJson");
    expect(testRunSource).not.toContain("state.PackageUpdateJson()");
    expect(testRunSource).toContain('\\"command_dispatched\\":false');
    expect(testRunSource).toContain('\\"test_runner_execute_invoked\\":false');
    const testRunBody = testRunSource.slice(testRunSource.indexOf("public static CommandExecutionResult TestRun"), testRunSource.indexOf("static CommandExecutionResult TestRunPreExecutePublicationFailure"));
    expect(testRunBody.indexOf("RequireSettledCompilationForTestRun(context.State)")).toBeLessThan(testRunBody.indexOf('FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi")'));
    expect(testRunBody.lastIndexOf("RequireSettledCompilationForTestRun(context.State)")).toBeLessThan(testRunBody.indexOf('InvokeReflected(api, "Execute", settings)'));
  });

  it("runs from the bridge update loop and reports status without changing heartbeat schema", () => {
    const runtimeSource = readPackageFile("Editor/Bridge/UnityBridgeRuntime.cs");
    const stateSource = readPackageFile("Editor/Bridge/BridgeRuntimeState.cs");
    const serverSource = readPackageFile("Editor/Bridge/BridgeServer.cs");
    const heartbeatSource = readPackageFile("Editor/Bridge/HeartbeatWriter.cs");

    expect(runtimeSource).toContain("static PackageUpdateMonitor PackageUpdateMonitor");
    expect(runtimeSource).toContain("PackageUpdateMonitor?.Update(State, EditorApplication.timeSinceStartup)");
    expect(stateSource).toContain("PackageUpdateJson()");
    expect(serverSource).toContain("\\\"package_update\\\":");
    expect(heartbeatSource).not.toContain("package_update");
    expect(heartbeatSource).not.toContain("package_update_status");

    const bridgeFiles = fs.readdirSync(bridgeDir);
    expect(bridgeFiles).toContain("PackageUpdateMonitor.cs");
    expect(bridgeFiles).toContain("PackageUpdateMonitor.cs.meta");
    expect(bridgeFiles).toContain("PackageUpdateAssetInvalidator.cs");
    expect(bridgeFiles).toContain("PackageUpdateAssetInvalidator.cs.meta");
  });

  it("retains lifecycle transitions and fingerprints without per-tick current-state churn", () => {
    const monitorSource = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const traceSource = readPackageFile("Editor/Bridge/BridgeReloadTrace.cs");

    expect(monitorSource).toContain("UnchangedTraceSampleIntervalSeconds = 60.0");
    expect(monitorSource).toContain("BeginNormalUpdateTrace");
    expect(monitorSource).toContain("ShouldRetainUpdateTrace");
    expect(monitorSource).toContain('BeginNormalUpdateTrace(traceNow, "unavailable", m_pendingFingerprint');
    expect(monitorSource).toContain("pendingFingerprintChanged");
    expect(monitorSource).toContain("m_pendingFingerprint, nextPendingFingerprint");
    expect(monitorSource).toContain("pendingFingerprintChanged = false");
    expect(monitorSource).toContain("BridgeReloadTrace.LogFingerprints(\"package_update_monitor.fingerprint.changed\"");
    expect(monitorSource).toContain("BridgeReloadTrace.LogError(\"package_update_monitor.update.error\"");
    expect(monitorSource).toContain('BridgeReloadTrace.Log("package_update_monitor.update.start")');
    expect(monitorSource).toContain('BridgeReloadTrace.Log("package_update_monitor.update.end"');
    const updateBody = monitorSource.slice(monitorSource.indexOf("public void Update"), monitorSource.indexOf("public void MarkRefreshObserved"));
    expect(updateBody).not.toContain('BridgeReloadTrace.Log("package_update_monitor.update.start")');
    expect(updateBody).not.toContain('BridgeReloadTrace.Log("package_update_monitor.update.end"');
    expect(traceSource).toContain("MaxTraceBytes = 262144");
    expect(traceSource).toContain("TrimToBytes = 131072");
  });

  it("does not treat stale same-hash runtime metadata as an installed current runtime", () => {
    const installerSource = readPackageFile("Editor/Setup/GatewayInstaller.cs");

    expect(installerSource).toContain("BuildRuntimeStatus(GatewayInstallStatus packageStatus, string runtimeRoot)");
    expect(installerSource).toContain("PopulateInstalledRuntimeStatus(targetStatus, targetRoot)");
    expect(installerSource).toContain("PackageGatewayVirtualCommandCount");
    expect(installerSource).toContain("gateway_virtual_commands_count");
    expect(installerSource).toContain("if (targetStatus.RuntimeFresh && ValidateRuntime(targetStatus, nodeStatus, out _))");
    expect(installerSource).toContain("TryRefreshSameCliRuntime(gatewayStatus, targetStatus, nodeStatus, confirmation, out var refreshResult)");
    expect(installerSource).toContain("ValidateConfirmedDowngradeStillCurrent(confirmation");
    expect(installerSource).toContain("CopyBundledRuntimeSidecarFiles(packageStatus, targetStatus.RuntimeRoot)");
    expect(installerSource).toContain("CopyDirectory(packageStatus.PackageRuntimeRoot, destinationRoot, \"gateway-cli.mjs\")");
    expect(installerSource).toContain("CanonicalizeCopiedRuntimeArtifacts(packageStatus, destinationRoot, true)");
    expect(installerSource).toContain("CanonicalizeCopiedRuntimeArtifacts(packageStatus, destinationRoot, false)");
    expect(installerSource).toContain("ValidateRuntimeMetadata(runtimeRoot, installedMetadata, installedMetadataShapeValid, false, out _)");
    expect(installerSource).toContain("TryReadCanonicalCheckoutArtifact(artifactPath, artifact.sha256, out _)");
    expect(installerSource).toContain("metadata.schema_version != 3");
    expect(installerSource).toContain("RuntimeManifestJsonShape.IsExactSchemaV3(json)");
    expect(installerSource).toContain("value.Keys.Count != expected.Length");
    expect(installerSource).toContain("actual.Count == expected.Length && Array.TrueForAll(expected, actual.Contains)");
    expect(installerSource).toContain("case 'u': value.Append(ReadUnicodeEscape()); break;");
    expect(installerSource).toContain("gateway-cli.LEGAL.txt");
    expect(installerSource).toContain("THIRD_PARTY_NOTICES.md");
    expect(installerSource).toContain("sbom.cdx.json");
    expect(installerSource).toContain("RuntimeStateMatchesPackage(status, FilesEqual(status.PackageRuntimeMetadataPath, status.RuntimeMetadataPath))");
    expect(installerSource).toContain("internal static bool RuntimeStateMatchesPackage(GatewayInstallStatus status, bool metadataFilesEqual)");
    expect(installerSource).toContain("metadata.gateway_virtual_commands_count <= 0");
    expect(installerSource).toContain("status.PackageGatewayVirtualCommandCount == status.InstalledGatewayVirtualCommandCount");
    expect(installerSource).toContain("&& metadataFilesEqual");
    expect(installerSource).toContain("(character >= 'a' && character <= 'f')");
    expect(installerSource).toContain("Gateway runtime compliance artifacts are missing, stale, or tampered.");
    expect(installerSource).not.toContain("if (ValidateRuntime(targetStatus, nodeStatus, out _))");
  });

  it("fails installation on invalid package metadata before Node checks or runtime path derivation", () => {
    const installerSource = readPackageFile("Editor/Setup/GatewayInstaller.cs");
    const installStart = installerSource.indexOf("public static NodeRuntimeInstaller.InstallResult InstallSourceDependencies()");
    const installEnd = installerSource.indexOf("public static NodeRuntimeInstaller.InstallResult EnsureUserConfigFiles", installStart);
    const installBody = installerSource.slice(installStart, installEnd);

    const statusIndex = installBody.indexOf("var gatewayStatus = GetStatus()");
    const metadataGateIndex = installBody.indexOf("if (!gatewayStatus.PackageMetadataValid)");
    expect(statusIndex).toBeGreaterThanOrEqual(0);
    expect(metadataGateIndex).toBeGreaterThan(statusIndex);
    expect(metadataGateIndex).toBeLessThan(installBody.indexOf("NodeRuntimeInstaller.GetStatus()"));
    expect(metadataGateIndex).toBeLessThan(installBody.indexOf("RuntimeGatewayRootForHash"));
    expect(metadataGateIndex).toBeLessThan(installBody.indexOf("CopyBundledRuntime"));
  });

  it("stores the Gateway virtual command count in bundled runtime metadata", () => {
    const buildSource = fs.readFileSync(path.join(root, "scripts", "build-gateway-runtime.mjs"), "utf8");
    const checkSource = fs.readFileSync(path.join(root, "scripts", "check-gateway-runtime.mjs"), "utf8");
    const runtimeMetadata = JSON.parse(readPackageFile("Editor/GatewayRuntime/gateway-runtime.json"));

    expect(runtimeMetadata.gateway_virtual_commands_count).toBe(62);
    expect(runtimeMetadata.schema_version).toBe(3);
    expect(runtimeMetadata.version).toBe(JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8")).version);
    expect(buildSource).toContain("renderGatewayRuntimeManifest");
    expect(checkSource).toContain("renderGatewayRuntimeManifest");
  });

  it("publishes command catalog refreshes transactionally so UI, heartbeat, and bridge commands stay consistent", () => {
    const registrySource = readPackageFile("Editor/Commands/CommandRegistry.cs");
    const runtimeSource = readPackageFile("Editor/Bridge/UnityBridgeRuntime.cs");
    const windowSource = readPackageFile("Editor/UI/UnityMcpSetupWindow.cs")
      + readPackageFile("Editor/UI/UnityMcpSetupWindow.PermissionsCommands.Refresh.cs");

    expect(registrySource).toContain("public void Rebuild()");
    expect(registrySource).toContain("var candidate = new CommandRegistry(m_state)");
    expect(registrySource).toContain("lock (m_rebuildGate)");
    expect(registrySource).toContain("Volatile.Write(ref m_snapshot, snapshot)");
    expect(registrySource).toContain("public CatalogSnapshot CaptureSnapshot()");
    expect(registrySource).toContain("public IReadOnlyList<CommandDescriptor> Descriptors => m_descriptors.Select(CloneDescriptor).ToArray()");
    expect(registrySource).toContain("CommandManifestScanner.ScanInto(this)");
    expect(runtimeSource).toContain("static CommandRegistry Registry");
    expect(runtimeSource).toContain("Registry.Rebuild()");
    expect(runtimeSource).toContain("State.PublishCatalog(catalog.CommandsHash, catalog.CatalogGeneration)");
    expect(windowSource).toContain("UnityBridgeRuntime.RefreshCommandCatalog(\"MCP Window command refresh\")");
    const catalogStep = windowSource.indexOf('TrackMcpWindowStep(details, "catalog", s_mcpWindowCatalogRefreshMarker, RefreshCommands)');
    const setupChecklistStep = windowSource.indexOf('TrackMcpWindowStep(details, "setup_checklist", () => RefreshSetupChecklist(processStatus))');
    expect(catalogStep).toBeGreaterThanOrEqual(0);
    expect(setupChecklistStep).toBeGreaterThan(catalogStep);
  });

  it("recovers a failed listener without replaying commands or restarting Unity", () => {
    const runtimeSource = readPackageFile("Editor/Bridge/UnityBridgeRuntime.cs");
    const serverSource = readPackageFile("Editor/Bridge/BridgeServer.cs");

    expect(serverSource).toContain("volatile bool m_listenerLoopRunning");
    expect(serverSource).toContain("m_listenerLoopRunning = false");
    expect(serverSource).toContain('BridgeReloadTrace.LogError("bridge.listener.loop.failed", exception)');
    expect(runtimeSource).toContain("TryRecoverBridgeListener()");
    expect(runtimeSource).toContain("new BridgeServer(Token, State, Registry)");
    expect(runtimeSource).toContain("automatic_command_replay_attempted=false;automatic_process_restart_attempted=false");
    expect(runtimeSource).toContain("MaxListenerRecoveryAttempts = 3");
    expect(runtimeSource).toContain("ListenerRecoveryStableSeconds = 10.0");
    expect(runtimeSource).toContain("EditorApplication.timeSinceStartup - s_listenerRecoveryStableSince >= ListenerRecoveryStableSeconds");
    expect(runtimeSource).toContain("public static int BridgePort => Server == null || !Server.IsRunning ? 0 : Server.Port");
  });

  it("keeps the static constructor minimal and waits for stable Unity idle", () => {
    const runtimeSource = readPackageFile("Editor/Bridge/UnityBridgeRuntime.cs");
    const staticCtorStart = runtimeSource.indexOf("static UnityBridgeRuntime()");
    const bootstrapUpdateStart = runtimeSource.indexOf("static void BootstrapUpdate()", staticCtorStart);
    const bootstrapRuntimeStart = runtimeSource.indexOf("static void BootstrapRuntime(string reason)", bootstrapUpdateStart);
    const updateStart = runtimeSource.indexOf("static void Update()", bootstrapRuntimeStart);
    const staticCtorBody = runtimeSource.slice(staticCtorStart, bootstrapUpdateStart);
    const bootstrapUpdateBody = runtimeSource.slice(bootstrapUpdateStart, bootstrapRuntimeStart);
    const bootstrapRuntimeBody = runtimeSource.slice(bootstrapRuntimeStart, updateStart);

    expect(staticCtorBody).toContain("EditorApplication.update += BootstrapUpdate");
    expect(staticCtorBody).toContain("BridgeBootstrapLifecycleObserver.MarkRuntimeInitializerObserved()");
    expect(staticCtorBody).not.toContain("BridgeReloadTrace.Log");
    expect(staticCtorBody).not.toContain("MainThreadDispatcher.Initialize");
    expect(staticCtorBody).not.toContain("ConsoleLogBuffer.Initialize");
    expect(staticCtorBody).not.toContain("State.UpdateFromEditor");
    expect(staticCtorBody).not.toContain("ScheduleDeferredStartup");
    expect(staticCtorBody).not.toContain("CommandRegistry.Build(State)");
    expect(staticCtorBody).not.toContain("PackageUpdateMonitor.Update(State");
    expect(staticCtorBody).not.toContain("Server.Start()");
    expect(runtimeSource).toContain("const double StartupIdleQuietSeconds = 2.0");
    expect(runtimeSource).toContain("const int StartupIdleQuietFrames = 2");
    expect(bootstrapUpdateBody).toContain("EditorApplication.isCompiling || EditorApplication.isUpdating");
    expect(bootstrapUpdateBody).toContain("BridgeBootstrapLifecycleObserver.MarkEditorUpdateObserved()");
    expect(bootstrapUpdateBody).toContain("BridgeBootstrapLifecycleObserver.ObserveIdleGate(EditorApplication.isCompiling, EditorApplication.isUpdating)");
    expect(bootstrapUpdateBody).toContain("s_idleStartedAt = -1.0");
    expect(bootstrapUpdateBody).toContain("s_idleFrameCount = 0");
    expect(bootstrapUpdateBody).toContain("now - s_idleStartedAt < StartupIdleQuietSeconds");
    expect(bootstrapUpdateBody).toContain("BootstrapRuntime(\"stable_idle\")");
    expect(bootstrapRuntimeBody).toContain('BridgeReloadTrace.Log("bridge.bootstrap.start"');
    expect(bootstrapRuntimeBody).toContain("MainThreadDispatcher.Initialize()");
    expect(bootstrapRuntimeBody).toContain("ConsoleLogBuffer.Initialize()");
    expect(bootstrapRuntimeBody).toContain("State.UpdateFromEditor()");
    expect(bootstrapRuntimeBody).toContain("EnsureBootstrapState()");
    expect(runtimeSource).toContain("CommandsHash = BootstrapPendingCommandsHash");
    expect(bootstrapRuntimeBody).toContain("ScheduleDeferredStartup(\"bootstrap\")");
    expect(runtimeSource).toContain("static void TryStartBridge(string reason)");
    expect(runtimeSource).toContain("EditorApplication.isCompiling || EditorApplication.isUpdating");
    expect(runtimeSource).toContain("State.MarkWaitingForIdle()");
    expect(runtimeSource).toContain("WriteStartupWaitingHeartbeat(reason)");
    expect(runtimeSource).toContain("HeartbeatWriter.WriteIfDue(EditorApplication.timeSinceStartup, HeartbeatIntervalSeconds)");
    expect(runtimeSource).toContain("HeartbeatWriter = new HeartbeatWriter(DefaultInstancesDirectory(), Token, State, () => Server == null || !Server.IsRunning ? 0 : Server.Port)");
    expect(runtimeSource).toContain('BridgeReloadTrace.Log("bridge.startup.waiting_for_idle"');
    expect(runtimeSource).toContain('BridgeReloadTrace.Log("bridge.startup.resume"');
    expect(runtimeSource).toContain("StartBridge(reason)");
    expect(runtimeSource).toContain("CommandRegistry.Build(State)");
  });

  it("writes lifecycle heartbeats before reload or quit listener shutdown", () => {
    const runtimeSource = readPackageFile("Editor/Bridge/UnityBridgeRuntime.cs");
    const stateSource = readPackageFile("Editor/Bridge/BridgeRuntimeState.cs");

    expect(runtimeSource).toContain("AssemblyReloadEvents.beforeAssemblyReload += WriteReloadingHeartbeat");
    expect(runtimeSource).toContain('BridgeReloadTrace.Log("bridge.reload.before")');
    expect(runtimeSource).toContain('WriteLifecycleHeartbeat("reloading", "assembly_reload")');
    expect(runtimeSource).toContain('WriteLifecycleHeartbeat("stopping", "editor_quitting")');
    expect(runtimeSource).toContain("State.MarkReloading()");
    expect(runtimeSource).toContain("State.MarkStopping()");
    expect(runtimeSource).toContain("Server.Dispose()");
    expect(stateSource).toContain("public void MarkReloading()");
    expect(stateSource).toContain('SetLifecycleStatus("reloading")');
    expect(stateSource).toContain('public bool IsReady => string.Equals(Status, "ready", StringComparison.Ordinal);');
  });
});

function readPackageFile(relativePath: string): string {
  return fs.readFileSync(path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp", relativePath), "utf8");
}
