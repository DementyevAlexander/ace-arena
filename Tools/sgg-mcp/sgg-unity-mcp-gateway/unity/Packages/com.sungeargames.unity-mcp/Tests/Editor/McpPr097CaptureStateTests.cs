using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr097CaptureStateTests
    {
        [TestCase("scheduled", true)]
        [TestCase("settling", true)]
        [TestCase("submitted", true)]
        [TestCase("completed", false)]
        [TestCase("timed_out", false)]
        [TestCase("cancelled", false)]
        [TestCase("interrupted", false)]
        public void TerminalCaptureStagesAreIrreversible(string stage, bool active)
        {
            var method = typeof(EditorScreenshotCommands).GetMethod("IsActiveGameViewCaptureStatus", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That((bool)method.Invoke(null, new object[] { stage }), Is.EqualTo(active));
        }

        [Test]
        public void PersistedPlayModeBarrierRejectsMalformedAndRoundTripsActiveEvidence()
        {
            var barrierType = typeof(PlayModeTransitionTracker).GetNestedType("PlayModeBarrierState", BindingFlags.NonPublic);
            Assert.That(barrierType, Is.Not.Null);
            var parse = barrierType.GetMethod("Parse", BindingFlags.Static | BindingFlags.Public);
            Assert.That(parse.Invoke(null, new object[] { "not-a-barrier" }), Is.Null);

            var id = new string('a', 32);
            var blob = "v=1;barrier_id=" + id + ";generation=7;entered_ticks=" + DateTime.UtcNow.Ticks + ";active=1";
            var barrier = parse.Invoke(null, new object[] { blob });
            Assert.That(barrier, Is.Not.Null);
            Assert.That(barrierType.GetProperty("Generation").GetValue(barrier), Is.EqualTo(7));
            Assert.That(barrierType.GetProperty("Active").GetValue(barrier), Is.EqualTo(true));

            var deactivated = barrierType.GetMethod("Deactivate", BindingFlags.Instance | BindingFlags.Public).Invoke(barrier, null);
            Assert.That(barrierType.GetProperty("Active").GetValue(deactivated), Is.EqualTo(false));
        }

        [TestCase("completed")]
        [TestCase("timed_out")]
        [TestCase("corrupt")]
        [TestCase("fidelity_suspect")]
        [TestCase("interrupted")]
        [TestCase("cancelled")]
        public void EveryTerminalStageRequiresRuntimeBridgeDisposal(string status)
        {
            Assert.That(GameViewCapturePolicy.TerminalRequiresBridgeDisposal(status), Is.True);
        }

        [Test]
        public void CompletionValidationFailsClosedForLifecycleAndBarrierChanges()
        {
            var lifecycle = Validate(canAdvance: false, barrierCurrent: true);
            var barrier = Validate(canAdvance: true, barrierCurrent: false);

            Assert.That(lifecycle.IsValid, Is.False);
            Assert.That(lifecycle.ErrorCode, Is.EqualTo("capture_lifecycle_interrupted"));
            Assert.That(barrier.IsValid, Is.False);
            Assert.That(barrier.ErrorCode, Is.EqualTo("capture_lifecycle_interrupted"));
        }

        [Test]
        public void CompletionValidationRejectsActiveSceneChange()
        {
            var result = GameViewCapturePolicy.ValidateContext(true, true, true, true, true, 10, "Assets/A.unity", 11, "Assets/B.unity", 800, 600, 800, 600, 800, 600, 800, 600);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("capture_scene_changed"));
            Assert.That(result.FinalGameViewResized, Is.False);
        }

        [Test]
        public void CompletionValidationReportsScheduleSubmissionOrCompletionResize()
        {
            var result = GameViewCapturePolicy.ValidateContext(true, true, true, true, true, 10, "Assets/A.unity", 10, "Assets/A.unity", 800, 600, 1024, 768, 1024, 768, 1024, 768);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("capture_dimensions_changed"));
            Assert.That(result.FinalGameViewResized, Is.True);
        }

        [Test]
        public void AlphaZeroAloneDoesNotFailValidRgbSamples()
        {
            Assert.That(GameViewCapturePolicy.HasStrongFidelityFailure(64, 0, 64, 1, 64, 0), Is.False);
            Assert.That(GameViewCapturePolicy.HasStrongFidelityFailure(64, 64, 64, 64, 1, 0), Is.True);
            Assert.That(GameViewCapturePolicy.HasStrongFidelityFailure(64, 0, 64, 64, 1, 64), Is.True);
        }

        [Test]
        public void FilesystemErrorsAreSanitized()
        {
            var message = GameViewCapturePolicy.SanitizeFilesystemFailure(new IOException("C:/private/project/output.png access denied"));

            Assert.That(message, Is.EqualTo(GameViewCapturePolicy.PublishFailureMessage));
            Assert.That(message, Does.Not.Contain("C:/private"));
        }

        [TestCase("gameview-20260716204410000-abcdef123456", true)]
        [TestCase("gameview-20260716204410000-ABCDEF123456", false)]
        [TestCase("gameview-20260716204410000-abcdef123456-extra", false)]
        [TestCase("gameview-../../private", false)]
        [TestCase("gameview-2026071620441000x-abcdef123456", false)]
        public void RecoveredCaptureIdsRequireExactGeneratedShape(string captureId, bool valid)
        {
            Assert.That(GameViewCapturePolicy.IsValidCaptureId(captureId), Is.EqualTo(valid));
            Assert.That(string.IsNullOrEmpty(GameViewCapturePolicy.BuildStagingPath("C:/project/output.png", captureId)), Is.EqualTo(!valid));
            if (valid)
            {
                Assert.That(GameViewCapturePolicy.IsExpectedStagingPath("C:/project/output.png", "C:/private/unrelated.tmp", captureId), Is.False);
            }
        }

        [Test]
        public void InvalidStagingDataCannotReplacePreexistingOutput()
        {
            var directory = Path.Combine(Path.GetTempPath(), "sgg-mcp-pr097-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var output = Path.Combine(directory, "capture.png");
                var captureId = "gameview-20260716204410000-abcdef123456";
                var staging = GameViewCapturePolicy.BuildStagingPath(output, captureId);
                File.WriteAllBytes(output, new byte[] { 1, 2, 3 });

                var published = GameViewCapturePolicy.TryStageAndPublish(directory, staging, output, new byte[] { 9, 9, 9 }, _ => false, out var errorCode, out var message);

                Assert.That(published, Is.False);
                Assert.That(errorCode, Is.EqualTo("capture_corrupt_artifact"));
                Assert.That(message, Is.EqualTo("Staged PNG validation failed before publication."));
                Assert.That(File.ReadAllBytes(output), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(File.Exists(staging), Is.False);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void SelectedPlayModeViewMustBeGameView()
        {
            var result = GameViewCapturePolicy.ValidateContext(false, true, true, true, true, 10, "Assets/A.unity", 10, "Assets/A.unity", 800, 600, 800, 600, 800, 600, 800, 600);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.ErrorCode, Is.EqualTo("capture_game_view_required"));
        }

        [Test]
        public void EditorAndScreenResolutionMustAgreeWithExpectation()
        {
            Assert.That(GameViewCapturePolicy.ResolutionMatches(800, 600, 800, 600, 800, 600), Is.True);
            Assert.That(GameViewCapturePolicy.ResolutionMatches(800, 600, 1024, 768, 800, 600), Is.False);
            Assert.That(GameViewCapturePolicy.ResolutionMatches(800, 600, 800, 600, 1024, 768), Is.False);
            var result = GameViewCapturePolicy.ValidateContext(true, false, true, true, true, 10, "Assets/A.unity", 10, "Assets/A.unity", 800, 600, 800, 600, 800, 600, 800, 600);
            Assert.That(result.ErrorCode, Is.EqualTo("capture_dimensions_changed"));
            Assert.That(result.FinalGameViewResized, Is.True);
        }

        [Test]
        public void RequestedResolutionChangeAndRestorationRequireExactEditorScreenAgreement()
        {
            Assert.That(GameViewCapturePolicy.ResolutionChangeRequired(1902, 915, 1902, 915, 1280, 720), Is.True);
            Assert.That(GameViewCapturePolicy.ResolutionChangeRequired(1280, 720, 1280, 720, 1280, 720), Is.False);
            Assert.That(GameViewCapturePolicy.ResolutionChangeRequired(1902, 915, 1902, 915, 0, 0), Is.False);
            Assert.That(GameViewCapturePolicy.ResolutionRestored(1902, 915, 1902, 915, 1902, 915), Is.True);
            Assert.That(GameViewCapturePolicy.ResolutionRestored(1902, 915, 1280, 720, 1902, 915), Is.False);
            Assert.That(GameViewCapturePolicy.ResolutionRestored(1280, 720, 1280, 720, 1902, 915), Is.False);
        }

        [Test]
        public void AbandonedOrLateGpuReadbackCanNeverBeAccepted()
        {
            Assert.That(GameViewCapturePolicy.ShouldAcceptReadbackCompletion("submitted", false, 200, 199), Is.True);
            Assert.That(GameViewCapturePolicy.ShouldAcceptReadbackCompletion("submitted", true, 200, 199), Is.False);
            Assert.That(GameViewCapturePolicy.ShouldAcceptReadbackCompletion("timed_out", false, 200, 199), Is.False);
            Assert.That(GameViewCapturePolicy.ShouldAcceptReadbackCompletion("submitted", false, 200, 200), Is.False);
        }

        [Test]
        public void LegacySceneHandlesConvertToStableUnsignedIdentity()
        {
            Assert.That(GameViewSceneIdentity.LegacyHandleIdentity(42), Is.EqualTo(42UL));
            Assert.That(GameViewSceneIdentity.LegacyHandleIdentity(-1), Is.EqualTo((ulong)uint.MaxValue));
        }

        [Test]
        public void LoadedSceneSetMismatchFailsEvenWhenActiveSceneMatches()
        {
            var result = GameViewCapturePolicy.ValidateContext(true, true, true, true, false, 10, "Assets/A.unity", 10, "Assets/A.unity", 800, 600, 800, 600, 800, 600, 800, 600);
            Assert.That(result.ErrorCode, Is.EqualTo("capture_scene_set_changed"));
            Assert.That(GameViewSceneIdentity.Matches(new LoadedSceneSetIdentity(2, "abc"), new LoadedSceneSetIdentity(2, "abc")), Is.True);
            Assert.That(GameViewSceneIdentity.Matches(new LoadedSceneSetIdentity(2, "abc"), new LoadedSceneSetIdentity(3, "abc")), Is.False);
        }

        [Test]
        public void PublicationFinalizeRequiresOwnerDeadlineAndContext()
        {
            Assert.That(GameViewCapturePolicy.FinalizePublication(true, true, true, true, true).ReportCompleted, Is.True);
            Assert.That(GameViewCapturePolicy.FinalizePublication(true, true, true, false, true).TerminalStatus, Is.EqualTo("timed_out"));
            Assert.That(GameViewCapturePolicy.FinalizePublication(false, true, true, true, true).Rollback, Is.True);
            Assert.That(GameViewCapturePolicy.FinalizePublication(true, true, true, true, false).TerminalStatus, Is.EqualTo("interrupted"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PublicationRollbackDeletesNewOutputOrRestoresPreexistingOutput(bool preExisting)
        {
            var directory = Path.Combine(Path.GetTempPath(), "sgg-mcp-pr097-publish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var output = Path.Combine(directory, "capture.png");
                var staging = Path.Combine(directory, "capture.staging");
                if (preExisting) File.WriteAllBytes(output, new byte[] { 1, 2, 3 });
                File.WriteAllBytes(staging, new byte[] { 9, 8, 7 });

                Assert.That(GameViewCapturePolicy.TryPublishReserved(directory, staging, output, out var publication, out _, out _), Is.True);
                Assert.That(File.ReadAllBytes(output), Is.EqualTo(new byte[] { 9, 8, 7 }));
                Assert.That(GameViewCapturePolicy.RollbackPublication(publication, out _, out _), Is.True);
                if (preExisting) Assert.That(File.ReadAllBytes(output), Is.EqualTo(new byte[] { 1, 2, 3 }));
                else Assert.That(File.Exists(output), Is.False);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void CompletedArtifactIntegrityRejectsReusedOrChangedOutput()
        {
            var directory = Path.Combine(Path.GetTempPath(), "sgg-mcp-pr097-integrity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var output = Path.Combine(directory, "capture.png");
                var original = Encoding.UTF8.GetBytes("original-capture");
                File.WriteAllBytes(output, original);
                var expectedHash = Sha256(original);

                Assert.That(GameViewCapturePolicy.CompletedArtifactIntegrity(directory, output, original.LongLength, expectedHash), Is.EqualTo("verified"));

                File.WriteAllBytes(output, Encoding.UTF8.GetBytes("replacement-data"));
                Assert.That(GameViewCapturePolicy.CompletedArtifactIntegrity(directory, output, original.LongLength, expectedHash), Is.EqualTo("sha256_mismatch"));

                File.WriteAllBytes(output, new byte[] { 1, 2, 3 });
                Assert.That(GameViewCapturePolicy.CompletedArtifactIntegrity(directory, output, original.LongLength, expectedHash), Is.EqualTo("length_mismatch"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void ExistingReparsePointInOutputChainIsRejected()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sgg-mcp-pr097-root"));
            var link = Path.Combine(root, "CaptureArtifacts", "link");
            var output = Path.Combine(link, "capture.png");

            var rejected = GameViewCapturePolicy.HasReparsePointInExistingChain(
                output,
                root,
                current => string.Equals(current, link, StringComparison.OrdinalIgnoreCase),
                _ => FileAttributes.Directory | FileAttributes.ReparsePoint);

            Assert.That(rejected, Is.True);
        }

        [Test]
        public void CommitCleanupFailureCannotReportSuccess()
        {
            var directory = Path.Combine(Path.GetTempPath(), "sgg-mcp-pr097-commit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var output = Path.Combine(directory, "capture.png");
                var backup = Path.Combine(directory, "backup-directory");
                File.WriteAllBytes(output, new byte[] { 9, 8, 7 });
                Directory.CreateDirectory(backup);
                var publication = new PublicationTransaction(Path.Combine(directory, "staging"), output, backup, true, true);

                Assert.That(GameViewCapturePolicy.CommitPublication(publication, out var errorCode, out var message), Is.False);
                Assert.That(errorCode, Is.EqualTo(GameViewCapturePolicy.PublishFailureCode));
                Assert.That(message, Is.EqualTo(GameViewCapturePolicy.PublishFailureMessage));
                Assert.That(File.ReadAllBytes(output), Is.EqualTo(new byte[] { 9, 8, 7 }));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        static CaptureContextValidation Validate(bool canAdvance, bool barrierCurrent)
        {
            return GameViewCapturePolicy.ValidateContext(true, true, canAdvance, barrierCurrent, true, 10, "Assets/A.unity", 10, "Assets/A.unity", 800, 600, 800, 600, 800, 600, 800, 600);
        }

        static string Sha256(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++) builder.Append(digest[index].ToString("x2"));
                return builder.ToString();
            }
        }
    }
}
