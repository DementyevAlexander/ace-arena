import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const editorPath = path.join(packageRoot, "Editor", "Commands", "EditorScreenshotCommands.cs");
const runtimePath = path.join(packageRoot, "Runtime", "GameViewCaptureBridge.cs");
const trackerPath = path.join(packageRoot, "Editor", "Bridge", "PlayModeTransitionTracker.cs");
const policyPath = path.join(packageRoot, "Editor", "Commands", "GameViewCapturePolicy.cs");
const editorContextPath = path.join(packageRoot, "Editor", "Commands", "GameViewCaptureEditorContext.cs");
const sceneIdentityPath = path.join(packageRoot, "Editor", "Commands", "GameViewSceneIdentity.cs");

describe("MCP-PR-097 authoritative Game View capture", () => {
  it("uses an editor-constrained runtime coroutine and no Awaitable authoritative path", () => {
    const editor = read(editorPath);
    const runtime = read(runtimePath);
    const runtimeAsmdef = JSON.parse(read(path.join(packageRoot, "Runtime", "SGG.UnityMCP.Runtime.asmdef")));
    const editorAsmdef = JSON.parse(read(path.join(packageRoot, "Editor", "SGG.UnityMCP.Editor.asmdef")));

    expect(editor).not.toContain("Awaitable.EndOfFrameAsync");
    expect(runtime).toContain("sealed class GameViewCaptureBridge : MonoBehaviour");
    expect(runtime).toContain("new WaitForEndOfFrame()");
    expect(runtime).toContain("ScreenCapture.CaptureScreenshotIntoRenderTexture(target)");
    expect(runtime.lastIndexOf("RenderTexture.active = previousActive;", runtime.indexOf("ScreenCapture.CaptureScreenshotIntoRenderTexture(target)"))).toBeGreaterThan(runtime.indexOf("GL.Clear("));
    expect(runtime).toContain("Time.frameCount");
    expect(runtime).toContain("[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]");
    expect(runtime).toContain('tag = "EditorOnly"');
    expect(runtime).toContain("hideFlags = HideFlags.HideInHierarchy");
    expect(runtime).not.toContain("HideFlags.HideAndDontSave");
    expect(runtime).not.toContain("s_instance.hideFlags");
    expect(runtime).toContain("DontDestroyOnLoad(bridgeObject)");
    expect(runtime.indexOf("AddComponent<GameViewCaptureBridge>()")).toBeLessThan(runtime.indexOf("DontDestroyOnLoad(bridgeObject)"));
    expect(runtimeAsmdef.includePlatforms).toEqual([]);
    expect(runtimeAsmdef.defineConstraints).toEqual(["UNITY_EDITOR"]);
    expect(editorAsmdef.references).toContain("SGG.UnityMCP.Runtime");
  });

  it("defaults and aliases to screen_capture without fallback", () => {
    const source = read(editorPath);
    const command = source.slice(source.indexOf("public static CommandExecutionResult GameViewScreenshot"), source.indexOf('[McpCommand(\n            "game.view.screenshot.status"'));

    expect(command).toContain('requestedBackend = "screen_capture"');
    expect(command).toContain('requestedBackend == "end_of_frame" ? "screen_capture" : requestedBackend');
    expect(command).toContain('CommandExecutionResult.Error("invalid_field_value"');
    expect(command).toContain("fallback_attempted\\\":false");
    expect(command).not.toContain('backend = "screen_capture"');
    expect(source).not.toContain('fallback":{"backend":"camera_render"');
  });

  it("enforces lifecycle and exact dimension preflight before scheduling", () => {
    const source = read(editorPath);
    const tracker = read(trackerPath);

    expect(source).toContain('"capture_lifecycle_barrier_required"');
    expect(source).toContain("PlayModeTransitionTracker.TryGetCurrentPlayModeBarrier");
    expect(source).toContain("Application.isBatchMode");
    expect(source).toContain("EditorApplication.isCompiling");
    expect(source).toContain("EditorApplication.isUpdating");
    expect(source).toContain("width and height must be both omitted or both provided");
    expect(source).toContain("GameViewCapturePolicy.ResolutionMatches(editorWidth, editorHeight, screenWidth, screenHeight");
    expect(tracker).toContain("PlayModeStateChange.EnteredPlayMode");
    expect(tracker).toContain("BarrierGenerationSessionStateKey");
    expect(tracker).toContain("TryGetCurrentPlayModeBarrier");
  });

  it("owns terminal irreversibility, staging cleanup, and late completion discard", () => {
    const source = read(editorPath);
    const runtime = read(runtimePath);
    const policy = read(policyPath);

    expect(source).toContain('return status == "resizing" || status == "scheduled" || status == "settling" || status == "submitted"');
    expect(source).toContain('"timed_out"');
    expect(source).toContain('"cancelled"');
    expect(source).toContain('"interrupted"');
    expect(source).toContain("LateCompletionDiscarded = true");
    expect(source).toContain("CleanupGameViewCaptureStaging(capture)");
    expect(source).toContain("GameViewCapturePolicy.TryStage(");
    expect(source).toContain("GameViewCapturePolicy.TryPublishReserved(");
    expect(source).toContain("GameViewCapturePolicy.TerminalRequiresBridgeDisposal(status)");
    const progress = source.slice(source.indexOf("static void OnGameViewCaptureProgress"), source.indexOf("static void OnGameViewCaptureCompleted"));
    expect(progress).toContain('MarkGameViewCaptureTerminalLocked(capture, "timed_out"');
    expect(policy).toContain("File.Replace(stagingPath, fullPath, backupPath)");
    expect(runtime.indexOf("ReleaseRequest(requestId, false);")).toBeLessThan(runtime.indexOf("InvokeCompleted(completed, result)"));
    expect(runtime).toContain("if (!OwnsRequest(requestId)) yield break;");
    expect(source).toContain('GameViewCapturePolicy.CompletedArtifactIntegrity(ProjectPaths.ProjectRoot, capture.FullPath, capture.Bytes, capture.Sha256)');
  });

  it("reports authority, fidelity evidence, hashes, and camera-only semantics", () => {
    const source = read(editorPath);

    expect(source).toContain('Composition = "final_game_view"');
    expect(source).toContain('"composition\\\":\\\"camera_only\\\""');
    expect(source).toContain('"authoritative\\\":false"');
    expect(source).toContain("SHA256.Create()");
    expect(source).toContain("HasStrongFidelityFailure");
    expect(read(policyPath)).toContain("return allRgbaZero || allSentinel");
    expect(source).toContain('"live_pixel_agreement_claimed\\\":false');
    expect(source).toContain('"visual_review_required\\\":true');
    expect(source).not.toContain("NearBlackSampleCount == capture.SampleCount");
    expect(source).not.toContain("TransparentSampleCount == capture.SampleCount");
  });

  it("revalidates scene, lifecycle, dimensions, and barrier before publication", () => {
    const source = read(editorPath);
    const policy = read(policyPath);

    expect(source).toContain("ScheduledSceneHandle = GameViewSceneIdentity.HandleIdentity(activeScene)");
    expect(source).toContain("ValidateCurrentCaptureContext(capture, result.Width, result.Height)");
    expect(source).toContain("finalValidation = ValidateCurrentCaptureContext");
    expect(policy).toContain('"capture_scene_changed"');
    expect(policy).toContain('"capture_dimensions_changed"');
    expect(source).toContain('"play_mode_window_rendering_resolution_at_schedule"');
    expect(source).toContain('capture.DimensionsSource = "play_mode_window_rendering_resolution_at_submission"');
    expect(source).toContain("capture.FinalGameViewResized");
  });

  it("requires the selected Play Mode view to be Game View without changing it", () => {
    const source = read(editorPath);
    const context = read(editorContextPath);

    expect(context).toContain("PlayModeWindow.GetViewType()");
    expect(context).toContain("PlayModeWindow.PlayModeViewTypes.GameView");
    expect(context).toContain("PlayModeWindow.GetRenderingResolution(out var width, out var height)");
    expect(context).toContain("#if UNITY_6000_0_OR_NEWER");
    expect(context).toContain('GetType("UnityEditor.PlayModeView", false)');
    expect(context).toContain('GetMethod("GetMainPlayModeView"');
    expect(context).toContain('GetProperty("targetSize"');
    expect(context).toContain("mainView.GetType() == gameViewType");
    expect(context).not.toContain("SetViewType");
    expect(context).not.toContain("Focus()");
    expect(source).toContain('"capture_game_view_required"');
    expect(source).toContain('"automatic_view_switch_attempted\\\":false');
    expect(source).toContain("GameViewCaptureEditorContext.TryInspect");
  });

  it("counts settling by actual EOF and uses nonblocking async GPU readback", () => {
    const source = read(editorPath);
    const runtime = read(runtimePath);
    const asyncCompletion = runtime.slice(runtime.indexOf("static void CompleteAsyncReadback"), runtime.indexOf("bool OwnsRequest"));
    const lifecycle = runtime.slice(runtime.indexOf("void CleanupLifecycle()"), runtime.indexOf("sealed class PendingCaptureRequest"));

    expect(runtime).not.toContain("yield return null");
    expect(runtime).toContain("yield return waitForEndOfFrame");
    expect(runtime).toContain("Time.renderedFrameCount");
    expect(runtime.indexOf("yield return waitForEndOfFrame")).toBeLessThan(runtime.indexOf("completedEndOfFrames++"));
    expect(runtime).toContain("SystemInfo.supportsAsyncGPUReadback");
    expect(runtime).toContain("GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, false)");
    expect(runtime).toContain("GraphicsFormatUsage.ReadPixels");
    expect(runtime).toContain("FormatUsage.ReadPixels");
    expect(runtime).toContain("SystemInfo.graphicsUVStartsAtTop");
    expect(runtime).toContain("OrientGameViewPixels(texture");
    expect(runtime).toContain("AsyncGPUReadback.Request(target");
    expect(runtime).not.toContain("WaitForCompletion");
    expect(runtime).toContain('"async_gpu_readback"');
    expect(runtime).toContain('"synchronous_readpixels_unsupported_async"');
    expect(runtime).toContain("pending.Abandoned = true");
    expect(asyncCompletion).toContain("RenderTexture.ReleaseTemporary(pending.Target)");
    expect(asyncCompletion).toContain("if (!pending.SuppressCompletion) InvokeCompleted");
    expect(lifecycle).not.toContain("RenderTexture.ReleaseTemporary");
    const begin = source.slice(source.indexOf("static void BeginGameViewCapture"), source.indexOf("static void OnGameViewCaptureProgress"));
    expect(begin).toContain("EditorApplication.QueuePlayerLoopUpdate();");
    expect(begin.indexOf("EditorApplication.QueuePlayerLoopUpdate();")).toBeGreaterThan(begin.indexOf("GameViewCaptureBridge.Begin("));
  });

  it("releases synchronous targets and bridge ownership after every readback result", () => {
    const runtime = read(runtimePath);
    const synchronousBranch = runtime.slice(runtime.indexOf("CaptureResult result;"), runtime.indexOf("static bool SupportsAsyncReadback"));
    const synchronousReadback = runtime.slice(runtime.indexOf("static CaptureResult CompleteSynchronousReadback"), runtime.indexOf("static void CompleteAsyncReadback"));

    expect(synchronousBranch).toContain("finally");
    expect(synchronousBranch).toContain("RenderTexture.ReleaseTemporary(target);");
    expect(synchronousBranch).toContain('result = CaptureResult.Failure(requestId');
    expect(synchronousBranch).toContain("ReleaseRequest(requestId, false);");
    expect(synchronousBranch.indexOf("ReleaseRequest(requestId, false);")).toBeLessThan(synchronousBranch.indexOf("InvokeCompleted(completed, result)"));
    expect(synchronousReadback).toContain("Texture2D texture = null;");
    expect(synchronousReadback.indexOf("try")).toBeLessThan(synchronousReadback.indexOf("texture = new Texture2D"));
    expect(synchronousReadback).toContain("if (texture != null) Destroy(texture);");
  });

  it("enqueues onto the persistent runner and schedules from runtime LateUpdate", () => {
    const runtime = read(runtimePath);
    const begin = runtime.slice(runtime.indexOf("public static bool Begin"), runtime.indexOf("void LateUpdate()"));
    const lateUpdate = runtime.slice(runtime.indexOf("void LateUpdate()"), runtime.indexOf("public static bool Cancel"));
    const cancel = runtime.slice(runtime.indexOf("public static bool Cancel"), runtime.indexOf("void OnDisable()"));
    const release = runtime.slice(runtime.indexOf("void ReleaseRequest"), runtime.indexOf("void CleanupLifecycle"));
    const lifecycle = runtime.slice(runtime.indexOf("void CleanupLifecycle"), runtime.indexOf("sealed class PendingCaptureRequest"));

    expect(begin).toContain("var runner = s_instance;");
    expect(begin).toContain("runner == null || !runner.isActiveAndEnabled");
    expect(begin).toContain("!string.IsNullOrEmpty(runner.m_requestId)");
    expect(begin).toContain("runner.m_previousRunInBackground = Application.runInBackground;");
    expect(begin).toContain("runner.m_runInBackgroundLeaseActive = true;");
    expect(begin).toContain("Application.runInBackground = true;");
    expect(begin).toContain("runner.m_pendingCapture = new PendingCaptureRequest(requestId");
    expect(begin).not.toContain("new GameObject");
    expect(begin).not.toContain("AddComponent");
    expect(begin).not.toContain("StartCoroutine");
    expect(lateUpdate).toContain("m_pendingCapture = null;");
    expect(lateUpdate).toContain("OwnsRequest(pending.RequestId)");
    expect(lateUpdate).toContain('new CaptureProgress(pending.RequestId, "runtime_late_update"');
    expect(lateUpdate).toContain("StartCoroutine(Capture(pending.RequestId, pending.SettleFrames");
    expect(lateUpdate).toContain("pending.DeadlineUtcTicks, pending.Progress, pending.Completed");
    expect(lateUpdate.indexOf("m_pendingCapture = null;")).toBeLessThan(lateUpdate.indexOf("StartCoroutine"));
    expect(cancel).toContain("runner.ReleaseRequest(requestId, true)");
    expect(release).toContain("m_requestId = null;");
    expect(release).toContain("RestoreRunInBackground(requestId);");
    expect(release).toContain("!OwnsRequest(requestId)");
    expect(release).toContain("m_runInBackgroundLeaseActive = false;");
    expect(release).toContain("Application.runInBackground = previous;");
    expect(runtime.match(/Application\.runInBackground = previous;/g)).toHaveLength(1);
    expect(release.indexOf("RestoreRunInBackground(requestId);")).toBeLessThan(release.indexOf("m_requestId = null;"));
    expect(release).not.toContain("s_instance = null;");
    expect(release).not.toContain("Destroy(gameObject)");
    expect(runtime).toContain("void OnDisable()");
    expect(runtime).toContain("void OnDestroy()");
    expect(lifecycle).toContain("m_pendingCapture = null;");
    expect(lifecycle).toContain("m_pendingReadback.SuppressCompletion = true;");
    expect(lifecycle).toContain("RestoreRunInBackground(m_requestId);");
    expect(lifecycle).toContain("ReferenceEquals(s_instance, this)");
    expect(lifecycle.indexOf("s_instance = null;")).toBeLessThan(lifecycle.indexOf("StopCoroutine(capture)"));
    expect(runtime).not.toContain("Destroy(gameObject)");
  });

  it("does not let an old async callback release a newer request on the persistent runner", () => {
    const runtime = read(runtimePath);
    const completion = runtime.slice(runtime.indexOf("static void CompleteAsyncReadback"), runtime.indexOf("bool OwnsRequest"));
    const ownership = runtime.slice(runtime.indexOf("bool OwnsPendingReadback"), runtime.indexOf("void ReleaseRequest"));

    expect(completion).toContain("pending.Owner.OwnsPendingReadback(pending)");
    expect(completion.indexOf("pending.Owner.OwnsPendingReadback(pending)")).toBeLessThan(completion.indexOf("pending.Owner.m_pendingReadback = null"));
    expect(ownership).toContain("ReferenceEquals(m_pendingReadback, pending)");
    expect(ownership).toContain("string.Equals(m_requestId, pending.RequestId, StringComparison.Ordinal)");
    expect(completion).toContain("pending.Owner.ReleaseRequest(pending.RequestId, false)");
    const restore = runtime.slice(runtime.indexOf("void RestoreRunInBackground"), runtime.indexOf("void CleanupLifecycle"));
    expect(restore).toContain("!OwnsRequest(requestId)");
  });

  it("does CPU staging work outside the state lock and gates late readback", () => {
    const source = read(editorPath);
    const policy = read(policyPath);
    const completion = source.slice(source.indexOf("static void OnGameViewCaptureCompleted"), source.indexOf("static void EnsureGameViewScreenCapturePollHook"));

    expect(completion).toContain("GameViewCapturePolicy.ShouldAcceptReadbackCompletion");
    expect(completion).toContain("GameViewCapturePolicy.TryStage(");
    expect(completion.indexOf("GameViewCapturePolicy.TryStage(")).toBeLessThan(completion.lastIndexOf("lock (s_gameViewCaptureLock)"));
    expect(policy).toContain("!abandoned");
    expect(policy).toContain("completionTicks < deadlineTicks");
    expect(policy).toContain("new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read");
    expect(policy).not.toContain("File.ReadAllBytes(fullPath)");
  });

  it("supports legacy int and Unity 6000.4+ scene handles", () => {
    const helper = read(sceneIdentityPath);
    const editor = read(editorPath);

    expect(helper).toContain("#if UNITY_6000_4_OR_NEWER");
    expect(helper).toContain("return scene.handle.GetRawData();");
    expect(helper).toContain("return LegacyHandleIdentity(scene.handle);");
    expect(helper).toContain("unchecked((ulong)(uint)handle)");
    expect(editor).not.toContain("activeScene.handle.GetRawData()");
  });

  it("fingerprints the complete bounded loaded scene set", () => {
    const helper = read(sceneIdentityPath);
    const source = read(editorPath);
    const policy = read(policyPath);

    expect(helper).toContain("SceneManager.sceneCount");
    expect(helper).toContain("entries.Sort(SceneEntry.Compare)");
    expect(helper).toContain("SHA256.Create()");
    expect(source).toContain("ScheduledSceneSetFingerprint = loadedSceneSet.Fingerprint");
    expect(source).toContain("GameViewSceneIdentity.TryCaptureLoadedScenes");
    expect(policy).toContain('"capture_scene_set_changed"');
  });

  it("publishes outside the lock with reversible ownership finalization", () => {
    const source = read(editorPath);
    const policy = read(policyPath);
    const completion = source.slice(source.indexOf("static void OnGameViewCaptureCompleted"), source.indexOf("static void EnsureGameViewScreenCapturePollHook"));
    const publishCall = completion.indexOf("GameViewCapturePolicy.TryPublishReserved(");
    const reservationLock = completion.indexOf("capture.PublishReserved = true");
    const finalLock = completion.indexOf("lock (s_gameViewCaptureLock)", reservationLock + 1);

    expect(reservationLock).toBeGreaterThanOrEqual(0);
    expect(publishCall).toBeGreaterThan(reservationLock);
    expect(publishCall).toBeLessThan(finalLock);
    expect(policy).toContain("RollbackPublication");
    expect(policy).toContain("OutputPreExisted");
    expect(policy).toContain("File.Replace(transaction.BackupPath, transaction.FullPath, null)");
    expect(policy).toContain("File.Delete(transaction.FullPath)");
    const commitCall = completion.indexOf("GameViewCapturePolicy.CommitPublication(publication");
    const completedState = completion.indexOf('capture.Status = "completed"');
    expect(commitCall).toBeGreaterThan(publishCall);
    expect(commitCall).toBeLessThan(completedState);
    expect(completion).toContain("GameViewCapturePolicy.RollbackPublication(publication");
  });

  it("requires verified current artifact bytes and rejects linked output chains", () => {
    const source = read(editorPath);
    const policy = read(policyPath);

    expect(source).toContain('var artifactAvailable = artifactIntegrity == "verified"');
    expect(source).toContain('builder.Append(",\\\"artifact_integrity\\\":{\\\"status\\\":")');
    expect(policy).toContain('return "length_mismatch"');
    expect(policy).toContain('"sha256_mismatch"');
    expect(policy).toContain("FileAttributes.ReparsePoint");
    expect(source).toContain("GameViewCapturePolicy.IsConfinedOutputPath(projectRoot, candidate)");
    expect(policy).toContain("IsConfinedOutputPath(projectRoot, fullPath)");
  });

  it("reports dry runs as planned rather than scheduled", () => {
    const source = read(editorPath);

    expect(source).toContain('Status = dryRun ? "dry_run" : resolutionChangeRequired ? "resizing" : "scheduled"');
    expect(source).toContain('capture.DryRun ? "planned" : capture.Status');
  });

  it("strictly validates recovery ids and sanitizes filesystem failures", () => {
    const source = read(editorPath);
    const policy = read(policyPath);

    expect(source).toContain("GameViewCapturePolicy.IsValidCaptureId(capture.CaptureId)");
    expect(source).toContain("GameViewCapturePolicy.BuildStagingPath");
    expect(policy).toContain('PublishFailureMessage = "Screenshot staging or publication failed."');
    const capturePipeline = source.slice(source.indexOf("static CommandExecutionResult ScheduleGameViewOverlayCapture"), source.indexOf("static CommandExecutionResult IncompatibleGameViewCaptureOptions"));
    expect(capturePipeline).not.toContain("exception.Message");
  });

  it("persists bounded current state and interrupts recovery rather than replaying", () => {
    const source = read(editorPath);

    expect(source).toContain("GameViewCaptureSessionStateKey");
    expect(source).toContain("json.Length <= 16384");
    expect(source).toContain("RecoverPersistedGameViewCapture");
    expect(source).toContain('"capture_domain_reload_interrupted"');
    expect(source).toContain("AssemblyReloadEvents.beforeAssemblyReload");
    const recovery = source.slice(source.indexOf("static void RecoverPersistedGameViewCapture"), source.indexOf("static void InterruptGameViewCaptureForReload"));
    expect(recovery).not.toContain("GameViewCaptureBridge.Begin");
  });
});

function read(file: string): string {
  return fs.readFileSync(file, "utf8");
}
