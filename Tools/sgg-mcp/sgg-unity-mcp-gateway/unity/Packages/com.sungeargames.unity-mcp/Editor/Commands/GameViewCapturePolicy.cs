using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SGG.UnityMCP.Editor.Commands
{
    internal static class GameViewCapturePolicy
    {
        internal const string PublishFailureCode = "capture_publish_failed";
        internal const string PublishFailureMessage = "Screenshot staging or publication failed.";
        internal const string UnsafePathCode = "path_not_allowed";
        const long MaxCompletedArtifactBytes = 128L * 1024L * 1024L;

        internal static CaptureContextValidation ValidateContext(
            bool gameViewSelected,
            bool resolutionAgreement,
            bool canAdvance,
            bool barrierCurrent,
            bool loadedSceneSetMatches,
            ulong scheduledSceneHandle,
            string scheduledScenePath,
            ulong currentSceneHandle,
            string currentScenePath,
            int scheduleWidth,
            int scheduleHeight,
            int submissionWidth,
            int submissionHeight,
            int currentWidth,
            int currentHeight,
            int captureWidth,
            int captureHeight)
        {
            if (!gameViewSelected)
            {
                return CaptureContextValidation.Interrupted("capture_game_view_required", "The selected main Play Mode view is not Game View.", false);
            }
            if (!resolutionAgreement)
            {
                return CaptureContextValidation.Interrupted("capture_dimensions_changed", "Editor Game View and Screen resolutions disagree.", true);
            }
            if (!canAdvance || !barrierCurrent)
            {
                return CaptureContextValidation.Interrupted("capture_lifecycle_interrupted", "Play Mode capture lifecycle conditions changed.", false);
            }
            if (!loadedSceneSetMatches)
            {
                return CaptureContextValidation.Interrupted("capture_scene_set_changed", "The loaded scene set changed during Game View capture.", false);
            }
            if (!SceneMatches(scheduledSceneHandle, scheduledScenePath, currentSceneHandle, currentScenePath))
            {
                return CaptureContextValidation.Interrupted("capture_scene_changed", "The active scene changed during Game View capture.", false);
            }

            var submitted = submissionWidth > 0 && submissionHeight > 0;
            var captured = captureWidth > 0 && captureHeight > 0;
            var resized = currentWidth != scheduleWidth || currentHeight != scheduleHeight
                || (submitted && (submissionWidth != scheduleWidth || submissionHeight != scheduleHeight || currentWidth != submissionWidth || currentHeight != submissionHeight))
                || (captured && (captureWidth != currentWidth || captureHeight != currentHeight || (submitted && (captureWidth != submissionWidth || captureHeight != submissionHeight))));
            return resized
                ? CaptureContextValidation.Interrupted("capture_dimensions_changed", "Final Game View dimensions changed during capture.", true)
                : CaptureContextValidation.Valid();
        }

        internal static bool SceneMatches(ulong scheduledHandle, string scheduledPath, ulong currentHandle, string currentPath)
        {
            return scheduledHandle != 0
                && scheduledHandle == currentHandle
                && string.Equals(scheduledPath ?? string.Empty, currentPath ?? string.Empty, StringComparison.Ordinal);
        }

        internal static bool HasStrongFidelityFailure(int sampleCount, int zeroCount, int transparentCount, int uniformCount, int uniqueCount, int sentinelCount)
        {
            if (sampleCount <= 0) return false;
            var allRgbaZero = zeroCount == sampleCount;
            var allSentinel = transparentCount == sampleCount && sentinelCount == sampleCount && uniformCount == sampleCount && uniqueCount == 1;
            // Alpha alone is only a metric; RGB must corroborate an uninitialized target.
            return allRgbaZero || allSentinel;
        }

        internal static bool TerminalRequiresBridgeDisposal(string status)
        {
            return status == "completed" || status == "timed_out" || status == "corrupt"
                || status == "fidelity_suspect" || status == "interrupted" || status == "cancelled";
        }

        internal static bool ResolutionMatches(int editorWidth, int editorHeight, int screenWidth, int screenHeight, int expectedWidth, int expectedHeight)
        {
            return editorWidth > 0 && editorHeight > 0
                && editorWidth == screenWidth && editorHeight == screenHeight
                && (expectedWidth == 0 || (editorWidth == expectedWidth && editorHeight == expectedHeight));
        }

        internal static bool ResolutionChangeRequired(int editorWidth, int editorHeight, int screenWidth, int screenHeight, int requestedWidth, int requestedHeight)
        {
            return requestedWidth > 0 && requestedHeight > 0
                && !ResolutionMatches(editorWidth, editorHeight, screenWidth, screenHeight, requestedWidth, requestedHeight);
        }

        internal static bool ResolutionRestored(int editorWidth, int editorHeight, int screenWidth, int screenHeight, int originalWidth, int originalHeight)
        {
            return originalWidth > 0 && originalHeight > 0
                && ResolutionMatches(editorWidth, editorHeight, screenWidth, screenHeight, originalWidth, originalHeight);
        }

        internal static bool ShouldAcceptReadbackCompletion(string status, bool abandoned, long deadlineTicks, long completionTicks)
        {
            return IsActiveStatus(status) && !abandoned && deadlineTicks > 0 && completionTicks > 0 && completionTicks < deadlineTicks;
        }

        internal static bool IsActiveStatus(string status)
        {
            return status == "scheduled" || status == "settling" || status == "submitted";
        }

        internal static bool IsValidCaptureId(string captureId)
        {
            if (string.IsNullOrEmpty(captureId) || captureId.Length != 39 || !captureId.StartsWith("gameview-", StringComparison.Ordinal) || captureId[26] != '-') return false;
            for (var index = 9; index < 26; index++)
            {
                if (captureId[index] < '0' || captureId[index] > '9') return false;
            }
            for (var index = 27; index < 39; index++)
            {
                var character = captureId[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }
            return true;
        }

        internal static string BuildStagingPath(string fullPath, string captureId)
        {
            return !string.IsNullOrEmpty(fullPath) && IsValidCaptureId(captureId) ? fullPath + "." + captureId + ".staging" : string.Empty;
        }

        internal static bool IsExpectedStagingPath(string fullPath, string stagingPath, string captureId)
        {
            var expected = BuildStagingPath(fullPath, captureId);
            return !string.IsNullOrEmpty(expected) && string.Equals(expected, stagingPath, StringComparison.Ordinal);
        }

        internal static bool TryStageAndPublish(string projectRoot, string stagingPath, string fullPath, byte[] bytes, Func<byte[], bool> validator, out string errorCode, out string message)
        {
            if (!TryStage(projectRoot, stagingPath, fullPath, bytes, validator, out errorCode, out message)) return false;
            return TryPublishStaged(projectRoot, stagingPath, fullPath, out errorCode, out message);
        }

        internal static bool TryStage(string projectRoot, string stagingPath, string fullPath, byte[] bytes, Func<byte[], bool> validator, out string errorCode, out string message)
        {
            errorCode = string.Empty;
            message = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(stagingPath) || string.IsNullOrEmpty(fullPath) || bytes == null || validator == null || string.Equals(stagingPath, fullPath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException();
                }
                if (!IsConfinedOutputPath(projectRoot, fullPath) || !IsConfinedOutputPath(projectRoot, stagingPath))
                {
                    errorCode = UnsafePathCode;
                    message = "Screenshot output path contains a symbolic link or reparse point.";
                    return false;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                if (!IsConfinedOutputPath(projectRoot, fullPath) || !IsConfinedOutputPath(projectRoot, stagingPath))
                {
                    errorCode = UnsafePathCode;
                    message = "Screenshot output path contains a symbolic link or reparse point.";
                    return false;
                }
                TryDelete(stagingPath);
                File.WriteAllBytes(stagingPath, bytes);
                var staged = File.ReadAllBytes(stagingPath);
                if (!validator(staged))
                {
                    TryDelete(stagingPath);
                    errorCode = "capture_corrupt_artifact";
                    message = "Staged PNG validation failed before publication.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                TryDelete(stagingPath);
                errorCode = PublishFailureCode;
                message = SanitizeFilesystemFailure(exception);
                return false;
            }
        }

        internal static bool TryPublishStaged(string projectRoot, string stagingPath, string fullPath, out string errorCode, out string message)
        {
            if (!TryPublishReserved(projectRoot, stagingPath, fullPath, out var transaction, out errorCode, out message)) return false;
            return CommitPublication(transaction, out errorCode, out message);
        }

        internal static bool TryPublishReserved(string projectRoot, string stagingPath, string fullPath, out PublicationTransaction transaction, out string errorCode, out string message)
        {
            transaction = null;
            errorCode = string.Empty;
            message = string.Empty;
            try
            {
                if (string.IsNullOrEmpty(stagingPath) || string.IsNullOrEmpty(fullPath) || !File.Exists(stagingPath) || string.Equals(stagingPath, fullPath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException();
                }
                if (!IsConfinedOutputPath(projectRoot, fullPath) || !IsConfinedOutputPath(projectRoot, stagingPath))
                {
                    errorCode = UnsafePathCode;
                    message = "Screenshot output path contains a symbolic link or reparse point.";
                    return false;
                }
                var preExisting = File.Exists(fullPath);
                var backupPath = preExisting ? stagingPath + ".publish-backup" : string.Empty;
                if (preExisting)
                {
                    TryDelete(backupPath);
                    File.Replace(stagingPath, fullPath, backupPath);
                }
                else
                {
                    File.Move(stagingPath, fullPath);
                }
                transaction = new PublicationTransaction(stagingPath, fullPath, backupPath, preExisting, true);
                return true;
            }
            catch (Exception exception)
            {
                TryDelete(stagingPath);
                errorCode = PublishFailureCode;
                message = SanitizeFilesystemFailure(exception);
                return false;
            }
        }

        internal static bool CommitPublication(PublicationTransaction transaction, out string errorCode, out string message)
        {
            errorCode = string.Empty;
            message = string.Empty;
            if (transaction == null || !transaction.Published) return false;
            if (!transaction.OutputPreExisted || TryDelete(transaction.BackupPath)) return true;
            errorCode = PublishFailureCode;
            message = PublishFailureMessage;
            return false;
        }

        internal static bool RollbackPublication(PublicationTransaction transaction, out string errorCode, out string message)
        {
            errorCode = string.Empty;
            message = string.Empty;
            try
            {
                if (transaction == null || !transaction.Published) return true;
                if (!transaction.OutputPreExisted)
                {
                    if (File.Exists(transaction.FullPath)) File.Delete(transaction.FullPath);
                    return !File.Exists(transaction.FullPath);
                }
                if (!File.Exists(transaction.BackupPath)) throw new IOException();
                if (File.Exists(transaction.FullPath)) File.Replace(transaction.BackupPath, transaction.FullPath, null);
                else File.Move(transaction.BackupPath, transaction.FullPath);
                return true;
            }
            catch (Exception exception)
            {
                errorCode = PublishFailureCode;
                message = SanitizeFilesystemFailure(exception);
                return false;
            }
        }

        internal static PublicationFinalizeDecision FinalizePublication(bool sameOwner, bool active, bool reserved, bool beforeDeadline, bool contextValid)
        {
            if (!sameOwner || !active || !reserved) return PublicationFinalizeDecision.ForRollback("interrupted", "capture_publication_ownership_lost");
            if (!beforeDeadline) return PublicationFinalizeDecision.ForRollback("timed_out", "capture_timeout");
            if (!contextValid) return PublicationFinalizeDecision.ForRollback("interrupted", "capture_context_changed");
            return PublicationFinalizeDecision.Complete();
        }

        internal static string SanitizeFilesystemFailure(Exception exception)
        {
            return PublishFailureMessage;
        }

        internal static string CompletedArtifactIntegrity(string projectRoot, string fullPath, long expectedBytes, string expectedSha256)
        {
            if (string.IsNullOrEmpty(fullPath) || expectedBytes <= 0 || expectedBytes > MaxCompletedArtifactBytes || !IsLowerHexSha256(expectedSha256))
            {
                return "invalid_evidence";
            }
            if (!IsConfinedOutputPath(projectRoot, fullPath)) return "unsafe_path";

            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists) return "missing";
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return "unsafe_path";
                if (info.Length != expectedBytes) return "length_mismatch";

                byte[] digest;
                long lengthAfterRead;
                using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                using (var sha256 = SHA256.Create())
                {
                    if (stream.Length != expectedBytes) return "length_mismatch";
                    digest = sha256.ComputeHash(stream);
                    lengthAfterRead = stream.Length;
                }
                if (lengthAfterRead != expectedBytes || new FileInfo(fullPath).Length != expectedBytes) return "length_mismatch";
                return string.Equals(Hex(digest), expectedSha256, StringComparison.Ordinal) ? "verified" : "sha256_mismatch";
            }
            catch (FileNotFoundException)
            {
                return "missing";
            }
            catch (DirectoryNotFoundException)
            {
                return "missing";
            }
            catch (Exception)
            {
                return "unavailable";
            }
        }

        internal static bool IsConfinedOutputPath(string projectRoot, string candidate)
        {
            try
            {
                if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(candidate)) return false;
                var root = Path.GetFullPath(projectRoot);
                var fullPath = Path.GetFullPath(candidate);
                return IsSameOrChildPath(fullPath, root) && !HasReparsePointInExistingChain(fullPath, root);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool HasReparsePointInExistingChain(string path, string root)
        {
            return HasReparsePointInExistingChain(
                path,
                root,
                current => File.Exists(current) || Directory.Exists(current),
                File.GetAttributes);
        }

        internal static bool HasReparsePointInExistingChain(string path, string root, Func<string, bool> exists, Func<string, FileAttributes> attributes)
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root);
            if (!IsSameOrChildPath(fullPath, fullRoot)) return true;
            var current = fullPath;
            while (IsSameOrChildPath(current, fullRoot))
            {
                if (exists(current) && (attributes(current) & FileAttributes.ReparsePoint) != 0) return true;
                if (PathsEqual(current, fullRoot)) return false;
                var next = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(next) || PathsEqual(next, current)) return true;
                current = next;
            }
            return true;
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return PathsEqual(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot)
                || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static bool PathsEqual(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsLowerHexSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }
            return true;
        }

        static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var index = 0; index < bytes.Length; index++) builder.Append(bytes[index].ToString("x2"));
            return builder.ToString();
        }

        static bool TryDelete(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal sealed class PublicationTransaction
    {
        internal PublicationTransaction(string stagingPath, string fullPath, string backupPath, bool outputPreExisted, bool published)
        {
            StagingPath = stagingPath;
            FullPath = fullPath;
            BackupPath = backupPath;
            OutputPreExisted = outputPreExisted;
            Published = published;
        }

        internal string StagingPath { get; }
        internal string FullPath { get; }
        internal string BackupPath { get; }
        internal bool OutputPreExisted { get; }
        internal bool Published { get; }
    }

    internal readonly struct PublicationFinalizeDecision
    {
        PublicationFinalizeDecision(bool reportCompleted, bool rollback, string terminalStatus, string errorCode)
        {
            ReportCompleted = reportCompleted;
            Rollback = rollback;
            TerminalStatus = terminalStatus;
            ErrorCode = errorCode;
        }

        internal bool ReportCompleted { get; }
        internal bool Rollback { get; }
        internal string TerminalStatus { get; }
        internal string ErrorCode { get; }

        internal static PublicationFinalizeDecision Complete()
        {
            return new PublicationFinalizeDecision(true, false, string.Empty, string.Empty);
        }

        internal static PublicationFinalizeDecision ForRollback(string status, string errorCode)
        {
            return new PublicationFinalizeDecision(false, true, status, errorCode);
        }
    }

    internal readonly struct CaptureContextValidation
    {
        CaptureContextValidation(bool isValid, string errorCode, string message, bool finalGameViewResized)
        {
            IsValid = isValid;
            ErrorCode = errorCode;
            Message = message;
            FinalGameViewResized = finalGameViewResized;
        }

        internal bool IsValid { get; }
        internal string ErrorCode { get; }
        internal string Message { get; }
        internal bool FinalGameViewResized { get; }

        internal static CaptureContextValidation Valid()
        {
            return new CaptureContextValidation(true, string.Empty, string.Empty, false);
        }

        internal static CaptureContextValidation Interrupted(string errorCode, string message, bool resized)
        {
            return new CaptureContextValidation(false, errorCode, message, resized);
        }
    }
}
