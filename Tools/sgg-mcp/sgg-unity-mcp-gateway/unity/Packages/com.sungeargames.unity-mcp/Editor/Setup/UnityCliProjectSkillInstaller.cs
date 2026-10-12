using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class UnityCliProjectSkillInstaller
    {
        public const string SkillsCliVersion = "1.5.20";
        public const string Repository = "Unity-Technologies/skills";
        public const string RepositoryUrl = "https://github.com/Unity-Technologies/skills.git";
        public const string SkillName = "unity-cli";
        public const string ReviewedSourceCommit = "cb1dccb8f5adffcca43a5a26993fdeb8eae59433";
        public const string ReviewedSource = "https://raw.githubusercontent.com/Unity-Technologies/skills/" + ReviewedSourceCommit + "/skills/unity-cli/";
        public const string ReviewedSkillsHash = "3dc226a7f8fdb938126606c6f7ed4c97a1eee541e9f0ae1a38e8d7918f37964e";
        internal const string ReviewedContentHash = "20cce0a4ca795c0f2d192da90d32c27a27686210da435c3b758487c62055768b";
        internal const string PreviousReviewedSourceCommit = "87fac23d66a1f44f5e06c2935eccce0b40b9715a";
        internal const string PreviousReviewedSkillsHash = "c9aa42894d016587157929468030728b9549a99f1e399c77d05057107a1b24df";
        internal const string PreviousReviewedContentHash = "7c8336ee41ec0f5d02df2d49ca2e6e008333180423e1fb074135931af52335fb";
        internal const string EarlierReviewedSourceCommit = "344cf1b826633409baa1363720eab8e8144f0058";
        internal const string EarlierReviewedSkillsHash = "6e95b2883ce0a5fab92bd7f847b8b2e82d6fefcf53c149dabfeacc1ff77aadc8";
        internal const string EarlierReviewedContentHash = "be5efc00023c130b0fd5a5c5c5348761e9027cdb6c6bc768050a63626a29d667";
        internal const string LegacyReviewedSourceCommit = "f4339cbf3e2293f6faaa0642d9cc3d161b909467";
        internal const string LegacyReviewedSkillsHash = "257ddc99eb016131e7358ba953f6b45972aeaed73d16a9d002f32a8f0a29aa74";
        internal const string LegacyReviewedContentHash = "a2a598007b1633f66c1cc5bc03d22e170119303224af076d11a75cd4676f621f";

        internal const string LocalStateAbsent = "absent";
        internal const string LocalStateCurrentReviewed = "valid_current_reviewed";
        internal const string LocalStatePreviousReviewed = "valid_previous_reviewed";
        internal const string LocalStateModified = "modified";
        internal const string LocalStateUnknown = "unknown_provenance";
        internal const string LocalStateIncomplete = "incomplete";
        internal const string LocalStateReparse = "reparse_point";

        const string ProvenanceFileName = ".sgg-unity-cli-source";
        internal const int MaxSkillFiles = 64;
        internal const long MaxSkillBytes = 1048576;
        const long MaxLockBytes = 4096;
        static string s_reviewedContentHashOverrideForTests = string.Empty;
        static string s_previousReviewedContentHashOverrideForTests = string.Empty;
        static string s_legacyReviewedContentHashOverrideForTests = string.Empty;
        static readonly ReviewedSkillGeneration[] s_reviewedGenerations =
        {
            new ReviewedSkillGeneration(ReviewedSourceCommit, ReviewedSkillsHash, ReviewedContentHash),
            new ReviewedSkillGeneration(PreviousReviewedSourceCommit, PreviousReviewedSkillsHash, PreviousReviewedContentHash),
            new ReviewedSkillGeneration(EarlierReviewedSourceCommit, EarlierReviewedSkillsHash, EarlierReviewedContentHash),
            new ReviewedSkillGeneration(LegacyReviewedSourceCommit, LegacyReviewedSkillsHash, LegacyReviewedContentHash),
        };

        public static UnityCliSkillStatus GetStatus(string projectRoot)
        {
            var root = Path.GetFullPath(projectRoot);
            var skillRoot = Path.Combine(root, ".agents", "skills", SkillName);
            var skillPath = Path.Combine(skillRoot, "SKILL.md");
            var status = new UnityCliSkillStatus
            {
                SkillRoot = skillRoot,
                SkillPath = skillPath,
                LocalState = LocalStateAbsent,
                SourceProvenance = "absent",
                Update = ComponentUpdateStatus.NotChecked(
                    "",
                    UnityCliUpdateCheck.SkillChannel,
                    UnityCliUpdateCheck.SkillSourceKind,
                    UnityCliUpdateCheck.SkillSourceBaseIdentity()),
            };
            try
            {
                var attributes = File.GetAttributes(skillRoot);
                status.Exists = true;
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    status.ModifiedOrUnknown = true;
                    status.LocalState = LocalStateUnknown;
                    status.SourceProvenance = "unknown";
                    status.Message = "Existing skill target is not a directory and will not be overwritten.";
                    return status;
                }
            }
            catch (FileNotFoundException)
            {
                status.Message = "Not installed. Project target: .agents/skills/unity-cli.";
                return status;
            }
            catch (DirectoryNotFoundException)
            {
                status.Message = "Not installed. Project target: .agents/skills/unity-cli.";
                return status;
            }
            catch
            {
                status.Exists = true;
                status.ModifiedOrUnknown = true;
                status.LocalState = LocalStateUnknown;
                status.SourceProvenance = "unknown";
                status.Message = "Skill installation state could not be read. Manual review required; Install is disabled.";
                return status;
            }

            status.Exists = true;
            if (HasReparsePoint(skillRoot))
            {
                status.ReparsePoint = true;
                status.ModifiedOrUnknown = true;
                status.LocalState = LocalStateReparse;
                status.SourceProvenance = "reparse_point";
                status.Message = "Existing skill target is a reparse point and will not be modified.";
                return status;
            }

            if (!File.Exists(skillPath))
            {
                status.ModifiedOrUnknown = true;
                status.LocalState = LocalStateIncomplete;
                status.SourceProvenance = "unknown";
                status.Message = "Existing target is incomplete; SKILL.md is missing. Manual review required.";
                return status;
            }

            try
            {
                if (new FileInfo(skillPath).Length > 131072)
                {
                    status.ModifiedOrUnknown = true;
                    status.LocalState = LocalStateModified;
                    status.SourceProvenance = "modified";
                    status.Message = "Existing target SKILL.md is too large. Manual review required.";
                    return status;
                }

                var text = File.ReadAllText(skillPath);
                var provenancePath = Path.Combine(skillRoot, ProvenanceFileName);
                var provenance = File.Exists(provenancePath) && new FileInfo(provenancePath).Length <= 512 ? File.ReadAllText(provenancePath) : string.Empty;
                var parsedProvenance = TryParseReviewedProvenance(provenance, out var provenanceCommit, out var provenanceHash, out var recordedContentHash);
                var hasReviewedProvenance = parsedProvenance && IsKnownReviewedPair(provenanceCommit, provenanceHash);
                ValidateStagedFiles(skillRoot);
                status.ContentHash = ComputeContentHash(skillRoot);
                status.ProvenanceVerified = hasReviewedProvenance
                    && string.Equals(recordedContentHash, status.ContentHash, StringComparison.OrdinalIgnoreCase)
                    && IsExpectedReviewedContentHash(provenanceCommit, provenanceHash, status.ContentHash);
                status.ValidFrontmatter = Regex.IsMatch(text, "^---\\s*$(?:(?!^---\\s*$)[\\s\\S])*^name:\\s*unity-cli\\s*$(?:(?!^---\\s*$)[\\s\\S])*^---\\s*$", RegexOptions.Multiline);
                status.Valid = status.ProvenanceVerified && status.ValidFrontmatter;
                if (status.ProvenanceVerified)
                {
                    status.InstalledReviewedCommit = provenanceCommit;
                    status.InstalledReviewedHash = provenanceHash;
                    status.SourceProvenance = IsCurrentReviewedPair(provenanceCommit, provenanceHash) ? "reviewed" : "previous_reviewed";
                }

                if (status.Valid)
                {
                    status.LocalState = IsCurrentReviewedPair(status.InstalledReviewedCommit, status.InstalledReviewedHash)
                        ? LocalStateCurrentReviewed
                        : LocalStatePreviousReviewed;
                }
                else
                {
                    status.ModifiedOrUnknown = true;
                    status.LocalState = hasReviewedProvenance ? LocalStateModified : LocalStateUnknown;
                    status.SourceProvenance = hasReviewedProvenance ? "modified" : "unknown";
                }
            }
            catch (Exception exception)
            {
                status.ModifiedOrUnknown = true;
                status.ReparsePoint = exception.Message.IndexOf("reparse", StringComparison.OrdinalIgnoreCase) >= 0;
                status.LocalState = status.ReparsePoint ? LocalStateReparse : LocalStateUnknown;
                status.SourceProvenance = status.ReparsePoint ? "reparse_point" : "unknown";
                status.Message = "Existing target could not be validated: " + BoundedProcessOperation.Sanitize(exception.Message);
                return status;
            }

            status.Message = status.Valid
                ? string.Equals(status.LocalState, LocalStateCurrentReviewed, StringComparison.Ordinal)
                    ? "Installed from the current reviewed official artifact at .agents/skills/unity-cli. Restart agent sessions to rediscover it."
                    : "Installed from an older Gateway-reviewed official artifact at .agents/skills/unity-cli. Run the manual update check before updating it."
                : "Existing target has unknown provenance, modified content, or invalid SKILL.md. Manual review required.";
            status.Update = ComponentUpdateStatus.NotChecked(
                status.InstalledReviewedCommit,
                UnityCliUpdateCheck.SkillChannel,
                UnityCliUpdateCheck.SkillSourceKind,
                UnityCliUpdateCheck.SkillSourceBaseIdentity());
            return status;
        }

        public static bool TryStartInstall(string projectRoot, out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            error = string.Empty;
            var status = GetStatus(projectRoot);
            if (status.Exists)
            {
                error = status.Valid ? "unity-cli is already installed for this project." : "An existing unity-cli target requires manual review and will not be overwritten.";
                return false;
            }

            return TryStartStaging(projectRoot, out operation, out error);
        }

        public static bool TryStartUpdate(string projectRoot, UnityCliSkillUpdateBinding binding, out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            var root = Path.GetFullPath(projectRoot);
            if (HasPendingUpdateBackup(root))
            {
                error = "A previous skill backup already exists; inspect or remove it manually before retrying the update.";
                return false;
            }

            if (!TryValidateUpdateBinding(root, binding, GetStatus(root), DateTime.UtcNow, out error))
            {
                return false;
            }

            return TryStartStaging(root, out operation, out error);
        }

        static bool TryStartStaging(string projectRoot, out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            if (!UnityCliReviewedSkillDownload.TryGetPowerShellPath(out var powerShellPath, out error))
            {
                return false;
            }

            if (!TryCreateReviewedStagingDirectory(projectRoot, out var stagingRoot, out error)) return false;
            return BoundedProcessOperation.TryStart(
                powerShellPath,
                UnityCliReviewedSkillDownload.BuildArguments(stagingRoot),
                stagingRoot,
                UnityCliReviewedSkillDownload.ProcessTimeout,
                out operation,
                out error);
        }

        internal static bool CanStageReviewedSkill(out string error)
        {
            return UnityCliReviewedSkillDownload.TryGetPowerShellPath(out _, out error);
        }

        internal static bool TryCreateReviewedStagingDirectory(string projectRoot, out string stagingRoot, out string error)
        {
            stagingRoot = string.Empty;
            error = string.Empty;
            try
            {
                var root = Path.GetFullPath(projectRoot);
                if (!Directory.Exists(root))
                {
                    error = "Unity project root does not exist.";
                    return false;
                }

                var target = Path.Combine(root, ".agents", "skills", SkillName);
                if (!IsTargetWithinProject(root, target) || HasExistingReparsePoint(root, target))
                {
                    error = "Project skill target must stay under the project root and cannot traverse reparse points.";
                    return false;
                }

                var candidate = StagingRoot(root);
                if (!IsTargetWithinProject(root, candidate))
                {
                    error = "Skill staging must stay under the project root.";
                    return false;
                }
                // Validate local path syntax before creating any staging directories.
                UnityCliReviewedSkillDownload.BuildArguments(candidate);
                var current = new DirectoryInfo(candidate);
                while (current != null)
                {
                    try
                    {
                        var attributes = File.GetAttributes(current.FullName);
                        if (string.Equals(current.FullName, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            error = "Skill staging already exists; inspect it manually before retrying. Nothing was deleted or reused.";
                            return false;
                        }
                        if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                        {
                            error = "Skill staging cannot traverse a reparse point or non-directory ancestor.";
                            return false;
                        }
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    current = current.Parent;
                }
                Directory.CreateDirectory(candidate);
                stagingRoot = candidate;
                return true;
            }
            catch (Exception exception)
            {
                error = "Skill staging could not be safely created: " + BoundedProcessOperation.Sanitize(exception.Message);
                return false;
            }
        }

        internal static bool HasPendingUpdateBackup(string projectRoot)
        {
            return Directory.Exists(BackupRoot(Path.GetFullPath(projectRoot)));
        }

        internal static void SetReviewedContentHashOverridesForTests(
            string currentReviewedContentHash,
            string previousReviewedContentHash,
            string legacyReviewedContentHash = null)
        {
            s_reviewedContentHashOverrideForTests = currentReviewedContentHash ?? string.Empty;
            s_previousReviewedContentHashOverrideForTests = previousReviewedContentHash ?? string.Empty;
            s_legacyReviewedContentHashOverrideForTests = legacyReviewedContentHash ?? string.Empty;
        }

        internal static string[] BuildInstallArguments(string npmCliPath)
        {
            // Legacy diagnostic contract only; production staging downloads the
            // immutable ReviewedSource without Node, npm, Git or the skills CLI.
            return new[]
            {
                npmCliPath,
                "exec",
                "--yes",
                "skills@" + SkillsCliVersion,
                "--",
                "add",
                Repository,
                "--skill",
                SkillName,
                "--agent",
                "opencode",
                "--agent",
                "codex",
                "--copy",
                "--yes",
            };
        }

        public static bool TryFinalizeInstall(string projectRoot, out string message)
        {
            message = string.Empty;
            var root = Path.GetFullPath(projectRoot);
            var stagingRoot = StagingRoot(root);
            var stagedSkillRoot = Path.Combine(stagingRoot, ".agents", "skills", SkillName);
            var lockPath = Path.Combine(stagingRoot, "skills-lock.json");
            var targetRoot = Path.Combine(root, ".agents", "skills", SkillName);
            var installed = false;
            try
            {
                if (Directory.Exists(targetRoot))
                {
                    message = "The project skill target appeared during installation and was not overwritten.";
                    return false;
                }

                if (!TryPrepareReviewedStagedSkill(stagedSkillRoot, lockPath, ReviewedContentHash, out message))
                {
                    return false;
                }

                if (!TryPublishStagedInstall(root, stagedSkillRoot, out message)) return false;
                var status = GetStatus(root);
                if (!IsCurrentReviewedStatus(status))
                {
                    message = "Installed files failed final provenance validation. The target was preserved for inspection; nothing was deleted.";
                    return false;
                }

                message = status.Message;
                installed = true;
                return true;
            }
            catch (Exception exception)
            {
                message = "Skill finalization failed: " + BoundedProcessOperation.Sanitize(exception.Message);
                return false;
            }
            finally
            {
                if (installed) TryArchiveCompletedInstallStaging(stagingRoot, lockPath);
            }
        }

        internal static bool TryPublishStagedInstall(string projectRoot, string stagedSkillRoot, out string message)
        {
            return TryPublishStagedInstall(projectRoot, stagedSkillRoot, null, out message);
        }

        internal static bool TryPublishStagedInstallForTests(string projectRoot, string stagedSkillRoot, Action beforeMove, out string message, Action beforeNativeRename = null)
        {
            return TryPublishStagedInstall(projectRoot, stagedSkillRoot, beforeMove, out message, beforeNativeRename);
        }

        static bool TryPublishStagedInstall(string projectRoot, string stagedSkillRoot, Action beforeMove, out string message, Action beforeNativeRename = null)
        {
            message = string.Empty;
            try
            {
                var root = Path.GetFullPath(projectRoot);
                var source = Path.GetFullPath(stagedSkillRoot);
                var expectedSource = Path.Combine(StagingRoot(root), ".agents", "skills", SkillName);
                var agents = Path.Combine(root, ".agents");
                var parent = Path.Combine(agents, "skills");
                var target = Path.Combine(parent, SkillName);
                if (!SamePath(source, expectedSource) || !UnityCliPipelineSetup.IsPathWithoutReparsePoints(source)
                    || !UnityCliPipelineSetup.IsPathWithoutReparsePoints(target)
                    || !UnityCliPipelineSetup.TryGetCliPresence(target, out var present) || present)
                {
                    message = "The target appeared or its ancestors became unsafe; nothing was published or deleted.";
                    return false;
                }
                using (var custody = new PublicationDirectoryCustody())
                {
                    // Hold existing parent chains against rename, removal and reparse
                    // mutation while creating parents and atomically publishing.
                    custody.HoldChain(root);
                    Directory.CreateDirectory(agents);
                    custody.HoldChain(agents);
                    Directory.CreateDirectory(parent);
                    custody.HoldChain(parent);
                    custody.HoldChain(Path.GetDirectoryName(source));
                    var sourceIdentity = custody.HoldMovable(source);
                    beforeMove?.Invoke();
                    if (!UnityCliPipelineSetup.IsPathWithoutReparsePoints(target)
                        || !UnityCliPipelineSetup.TryGetCliPresence(target, out present) || present)
                    {
                        message = "The project skill target appeared during installation; nothing was overwritten or deleted.";
                        return false;
                    }
                    // Handle-relative, no-replace rename cannot follow a changed
                    // target-parent junction or merge a concurrently appeared target.
                    custody.MoveDirectory(source, target, sourceIdentity, beforeNativeRename);
                }
                return true;
            }
            catch (Exception exception)
            {
                message = "Skill publication refused; existing files were preserved: " + BoundedProcessOperation.Sanitize(exception.Message);
                return false;
            }
        }

        static void TryArchiveCompletedInstallStaging(string stagingRoot, string lockPath)
        {
            try
            {
                using (var custody = new PublicationDirectoryCustody())
                {
                    custody.HoldChain(Path.GetDirectoryName(stagingRoot));
                    if (!UnityCliPipelineSetup.IsPathWithoutReparsePoints(lockPath)
                        || !File.Exists(lockPath) || new FileInfo(lockPath).Length > MaxLockBytes
                        || RemoveJsonWhitespaceOutsideStrings(File.ReadAllText(lockPath)) != UnityCliReviewedSkillDownload.CanonicalReviewedLock) return;
                    var agents = Path.Combine(stagingRoot, ".agents");
                    var skills = Path.Combine(agents, "skills");
                    if (!UnityCliPipelineSetup.IsPathWithoutReparsePoints(skills)
                        || Directory.GetFiles(stagingRoot).Length != 1 || Directory.GetDirectories(stagingRoot).Length != 1
                        || Directory.GetFileSystemEntries(agents).Length != 1 || Directory.GetFileSystemEntries(skills).Length != 0) return;
                    // Preserve the acquisition receipt; never recursively delete a
                    // target or uncertain staging tree. A unique archive frees the
                    // fixed staging slot for a later explicitly confirmed update.
                    custody.MoveDirectory(stagingRoot, stagingRoot + "-completed-" + Guid.NewGuid().ToString("N"), custody.HoldMovable(stagingRoot));
                }
            }
            catch { /* Preserve an uncertain staging path for manual inspection. */ }
        }

        sealed class PublicationDirectoryCustody : IDisposable
        {
            readonly Dictionary<string, SafeFileHandle> m_handles = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            readonly List<SafeFileHandle> m_movableHandles = new List<SafeFileHandle>();
            public void HoldChain(string path)
            {
                if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new InvalidOperationException("Safe skill publication requires Windows directory custody.");
                var current = Path.GetFullPath(path);
                while (!string.IsNullOrEmpty(current))
                {
                    if (!m_handles.ContainsKey(current))
                    {
                        // LIST_DIRECTORY | READ_ATTRIBUTES, SHARE_READ | SHARE_WRITE,
                        // OPEN_EXISTING, BACKUP_SEMANTICS | OPEN_REPARSE_POINT.
                        // Deny delete/rename; child moves require write sharing.
                        // Publication uses a bound parent handle, never path traversal.
                        // Attribute-only handles do not enforce delete sharing on
                        // Windows. LIST_DIRECTORY is required for rename custody.
                        var handle = CreateFile(current, 0x81, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                            || (info.Attributes & (uint)FileAttributes.Directory) == 0
                            || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                        {
                            handle.Dispose();
                            throw new IOException("Could not hold a safe publication directory.");
                        }
                        m_handles.Add(current, handle);
                    }
                    var parent = Path.GetDirectoryName(current);
                    if (parent == current) break;
                    current = parent;
                }
                // A parent could have moved before its handle was acquired.
                // Once the whole chain is held, bind every pathname back to the
                // original handle, not just its lexical spelling.
                foreach (var entry in m_handles)
                {
                    if (!GetFileInformationByHandle(entry.Value, out var info)
                        || !Matches(entry.Key, new DirectoryIdentity { Volume = info.Volume, IndexHigh = info.IndexHigh, IndexLow = info.IndexLow, Valid = true }))
                        throw new IOException("Publication ancestor identity changed while custody was acquired.");
                }
            }
            public void Dispose()
            {
                foreach (var handle in m_handles.Values) handle.Dispose();
                foreach (var handle in m_movableHandles) handle.Dispose();
                m_handles.Clear();
                m_movableHandles.Clear();
            }
            public DirectoryIdentity HoldMovable(string path)
            {
                var handle = CreateFile(path, 0x81, 5, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero); // READ | DELETE sharing for atomic rename.
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                    || (info.Attributes & (uint)FileAttributes.Directory) == 0
                    || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    handle.Dispose();
                    throw new IOException("Could not bind a safe publication directory identity.");
                }
                m_movableHandles.Add(handle);
                return new DirectoryIdentity { Volume = info.Volume, IndexHigh = info.IndexHigh, IndexLow = info.IndexLow, Valid = true };
            }
            public bool Matches(string path, DirectoryIdentity expected)
            {
                if (!expected.Valid) return false;
                using (var handle = CreateFile(path, 0x81, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
                {
                    return !handle.IsInvalid && GetFileInformationByHandle(handle, out var info)
                        && (info.Attributes & (uint)FileAttributes.Directory) != 0 && (info.Attributes & (uint)FileAttributes.ReparsePoint) == 0
                        && info.Volume == expected.Volume && info.IndexHigh == expected.IndexHigh && info.IndexLow == expected.IndexLow;
                }
            }
            public void MoveDirectory(string source, string target, DirectoryIdentity expected, Action beforeNativeRename = null)
            {
                var parentPath = Path.GetDirectoryName(Path.GetFullPath(target));
                var name = Path.GetFileName(target);
                if (!expected.Valid || string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOfAny(new[] { ':', '/', '\\', '\0' }) >= 0
                    || !m_handles.TryGetValue(parentPath, out var parent)
                    || !GetFileInformationByHandle(parent, out var parentInfo)
                    || (parentInfo.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Safe publication parent custody is unavailable.");
                using (var handle = CreateFile(source, 0x10081, 5, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) // DELETE | LIST_DIRECTORY | READ_ATTRIBUTES.
                {
                    if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
                        || (info.Attributes & (uint)FileAttributes.Directory) == 0 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
                        || info.Volume != expected.Volume || info.IndexHigh != expected.IndexHigh || info.IndexLow != expected.IndexLow)
                        throw new IOException("Publication source identity changed; all files were preserved.");
                    var bytes = Encoding.Unicode.GetBytes(name);
                    var rootOffset = IntPtr.Size;
                    var lengthOffset = rootOffset + IntPtr.Size;
                    var nameOffset = lengthOffset + 4;
                    var length = nameOffset + bytes.Length;
                    var buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        for (var index = 0; index < length; index++) Marshal.WriteByte(buffer, index, 0); // ReplaceIfExists = false.
                        Marshal.WriteIntPtr(buffer, rootOffset, parent.DangerousGetHandle());
                        Marshal.WriteInt32(buffer, lengthOffset, bytes.Length);
                        Marshal.Copy(bytes, 0, IntPtr.Add(buffer, nameOffset), bytes.Length);
                        beforeNativeRename?.Invoke();
                        var result = NtSetInformationFile(handle, out _, buffer, (uint)length, 10); // FileRenameInformation, relative to bound parent.
                        if (result < 0) throw new IOException("Atomic skill move refused (Windows error " + RtlNtStatusToDosError(result) + "); all files were preserved.");
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            [StructLayout(LayoutKind.Sequential)]
            struct FileInformation
            {
                public uint Attributes;
                public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
                public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
            [StructLayout(LayoutKind.Sequential)]
            struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
            [DllImport("ntdll.dll")]
            static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr buffer, uint length, int informationClass);
            [DllImport("ntdll.dll")]
            static extern uint RtlNtStatusToDosError(int status);
        }

        struct DirectoryIdentity
        {
            public uint Volume, IndexHigh, IndexLow;
            public bool Valid;
        }

        public static bool TryFinalizeUpdate(string projectRoot, UnityCliSkillUpdateBinding binding, out string message)
        {
            return TryFinalizeUpdate(projectRoot, binding, CurrentReviewedContentHash(), out message);
        }

        internal static bool TryFinalizeUpdateForTests(string projectRoot, UnityCliSkillUpdateBinding binding, string expectedContentHash, out string message, Action beforePublish = null)
        {
            return TryFinalizeUpdate(projectRoot, binding, expectedContentHash, out message, beforePublish);
        }

        static bool TryFinalizeUpdate(string projectRoot, UnityCliSkillUpdateBinding binding, string expectedContentHash, out string message, Action beforePublish = null)
        {
            message = string.Empty;
            var root = Path.GetFullPath(projectRoot);
            var stagingRoot = StagingRoot(root);
            var stagedSkillRoot = Path.Combine(stagingRoot, ".agents", "skills", SkillName);
            var lockPath = Path.Combine(stagingRoot, "skills-lock.json");
            var targetRoot = Path.Combine(root, ".agents", "skills", SkillName);
            var backupRoot = BackupRoot(root);
            var targetBackedUp = false;
            var updated = false;
            var previousIdentity = default(DirectoryIdentity);
            var replacementIdentity = default(DirectoryIdentity);
            try
            {
                if (!TryValidateUpdateBinding(root, binding, GetStatus(root), DateTime.UtcNow, out message))
                {
                    return false;
                }

                if (!TryPrepareReviewedStagedSkill(stagedSkillRoot, lockPath, expectedContentHash, out message))
                {
                    return false;
                }

                if (!TryValidateUpdateBinding(root, binding, GetStatus(root), DateTime.UtcNow, out message))
                {
                    return false;
                }

                if (!UnityCliPipelineSetup.IsPathWithoutReparsePoints(backupRoot)
                    || !UnityCliPipelineSetup.TryGetCliPresence(backupRoot, out var backupPresent) || backupPresent)
                {
                    message = "A previous skill backup already exists; inspect or remove it manually before retrying the update.";
                    return false;
                }

                using (var custody = new PublicationDirectoryCustody())
                {
                    custody.HoldChain(Path.GetDirectoryName(targetRoot));
                    custody.HoldChain(Path.GetDirectoryName(stagedSkillRoot));
                    custody.HoldChain(Path.GetDirectoryName(backupRoot));
                    if (!TryValidateUpdateBinding(root, binding, GetStatus(root), DateTime.UtcNow, out message)) return false;
                    previousIdentity = custody.HoldMovable(targetRoot);
                    replacementIdentity = custody.HoldMovable(stagedSkillRoot);
                    custody.MoveDirectory(targetRoot, backupRoot, previousIdentity);
                    targetBackedUp = true;
                    if (!custody.Matches(backupRoot, previousIdentity)) throw new IOException("Backup identity changed; all files were preserved.");
                    beforePublish?.Invoke();
                    custody.MoveDirectory(stagedSkillRoot, targetRoot, replacementIdentity);

                    var status = GetStatus(root);
                    if (!custody.Matches(targetRoot, replacementIdentity) || !IsCurrentReviewedStatus(status))
                    {
                        var rollbackMessage = RollbackUpdate(root, targetRoot, backupRoot, binding, previousIdentity, replacementIdentity)
                            ? " The previous verified target was restored; the failed replacement was preserved."
                            : " Rollback could not be completed automatically; inspect the preserved project skill target and backup before retrying.";
                        message = "Updated files failed final provenance validation." + rollbackMessage;
                        return false;
                    }
                    // Retain the exact old generation rather than recursively
                    // deleting a directory that could acquire foreign files.
                    custody.MoveDirectory(backupRoot, backupRoot + "-completed-" + Guid.NewGuid().ToString("N"), previousIdentity);
                }
                updated = true;
                message = "Unity CLI project skill updated to official commit " + ReviewedSourceCommit + ". Previous content was retained in an isolated backup. Restart agent sessions to rediscover it.";
                return true;
            }
            catch (Exception exception)
            {
                var rollbackMessage = targetBackedUp
                    ? RollbackUpdate(root, targetRoot, backupRoot, binding, previousIdentity, replacementIdentity)
                        ? " The previous verified target was restored; any failed replacement was preserved."
                        : " Rollback could not be completed automatically; inspect the preserved project skill target and backup before retrying."
                    : string.Empty;
                message = "Skill update failed: " + BoundedProcessOperation.Sanitize(exception.Message) + rollbackMessage;
                return false;
            }
            finally
            {
                if (updated) TryArchiveCompletedInstallStaging(stagingRoot, lockPath);
            }
        }

        internal static bool TryCreateUpdateBinding(
            UnityCliUpdateCheckSnapshot snapshot,
            UnityCliSkillStatus localStatus,
            DateTime utcNow,
            out UnityCliSkillUpdateBinding binding,
            out string error)
        {
            binding = null;
            error = "Run a fresh manual update check before updating the Unity CLI skill.";
            if (snapshot == null || !snapshot.IsComplete || snapshot.IsCancelled || snapshot.Skill == null)
            {
                return false;
            }

            var checkedSkill = snapshot.Skill;
            var update = checkedSkill.Update;
            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            if (update == null
                || !string.Equals(update.CheckState, UnityCliUpdateCheck.CheckStateChecked, StringComparison.Ordinal)
                || !update.AuthoritativeForMutation
                || !string.Equals(update.UpdateState, UnityCliUpdateCheck.UpdateStateAvailable, StringComparison.Ordinal)
                || update.CheckedAtUtc == DateTime.MinValue
                || update.ExpiresAtUtc == DateTime.MinValue
                || update.CheckedAtUtc > now
                || update.ExpiresAtUtc <= update.CheckedAtUtc
                || update.ExpiresAtUtc - update.CheckedAtUtc > UnityCliUpdateCheck.CacheTtl
                || update.IsExpired(now))
            {
                error = "The manual skill update check is missing, expired, or does not prove the Gateway-reviewed update.";
                return false;
            }

            localStatus = localStatus ?? new UnityCliSkillStatus();
            if (!IsPreviousReviewedStatus(localStatus)
                || !IsPreviousReviewedStatus(checkedSkill)
                || !string.Equals(localStatus.InstalledReviewedCommit, checkedSkill.InstalledReviewedCommit, StringComparison.Ordinal)
                || !string.Equals(localStatus.InstalledReviewedHash, checkedSkill.InstalledReviewedHash, StringComparison.Ordinal)
                || !string.Equals(update.InstalledVersion, localStatus.InstalledReviewedCommit, StringComparison.Ordinal)
                || !string.Equals(update.AvailableVersion, ReviewedSourceCommit, StringComparison.Ordinal)
                || !string.Equals(update.Channel, UnityCliUpdateCheck.SkillChannel, StringComparison.Ordinal)
                || !string.Equals(update.SourceKind, UnityCliUpdateCheck.SkillSourceKind, StringComparison.Ordinal)
                || !string.Equals(update.SourceIdentitySha256, UnityCliUpdateCheck.SkillSourceIdentity(ReviewedSourceCommit), StringComparison.Ordinal))
            {
                error = "Only an unmodified older Gateway-reviewed unity-cli skill can be updated to the bundled reviewed artifact.";
                return false;
            }

            if (!string.Equals(localStatus.ContentHash, checkedSkill.ContentHash, StringComparison.OrdinalIgnoreCase)
                || !SamePath(localStatus.SkillRoot, checkedSkill.SkillRoot))
            {
                error = "The project skill content or target path changed after the manual update check.";
                return false;
            }

            binding = new UnityCliSkillUpdateBinding
            {
                InstalledReviewedCommit = localStatus.InstalledReviewedCommit,
                InstalledReviewedHash = localStatus.InstalledReviewedHash,
                TargetReviewedCommit = ReviewedSourceCommit,
                TargetReviewedHash = ReviewedSkillsHash,
                SourceIdentitySha256 = update.SourceIdentitySha256,
                SkillRoot = localStatus.SkillRoot,
                PreContentHash = localStatus.ContentHash,
                CheckedAtUtc = update.CheckedAtUtc,
                ExpiresAtUtc = update.ExpiresAtUtc,
            };
            binding.BindingIdentitySha256 = ComputeBindingIdentity(binding);
            error = string.Empty;
            return true;
        }

        internal static bool IsTargetWithinProject(string projectRoot, string targetPath)
        {
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(targetPath);
            return target.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        static bool HasReparsePoint(string path)
        {
            var current = new DirectoryInfo(path);
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        static bool HasExistingReparsePoint(string projectRoot, string targetPath)
        {
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = new DirectoryInfo(Path.GetDirectoryName(targetPath));
            while (current != null && current.FullName.Length >= root.Length)
            {
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = current.Parent;
            }

            return false;
        }

        static string StagingRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-staging");
        }

        static string BackupRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-backup");
        }

        static bool TryPrepareReviewedStagedSkill(string stagedSkillRoot, string lockPath, string expectedContentHash, out string message)
        {
            message = string.Empty;
            if (!Directory.Exists(stagedSkillRoot) || !File.Exists(lockPath) || HasReparsePoint(stagedSkillRoot))
            {
                message = "The staged skill output is missing or unsafe.";
                return false;
            }

            var lockInfo = new FileInfo(lockPath);
            if ((lockInfo.Attributes & FileAttributes.ReparsePoint) != 0 || lockInfo.Length > MaxLockBytes)
            {
                message = "The staged skill lock file is missing, oversized, or unsafe.";
                return false;
            }

            var lockText = RemoveJsonWhitespaceOutsideStrings(File.ReadAllText(lockPath));
            var expectedLock = "{\"version\":1,\"skills\":{\"unity-cli\":{\"source\":\"Unity-Technologies/skills\",\"sourceType\":\"github\",\"skillPath\":\"skills/unity-cli/SKILL.md\",\"computedHash\":\"" + ReviewedSkillsHash + "\"}}}";
            if (!string.Equals(lockText, expectedLock, StringComparison.Ordinal))
            {
                message = "The downloaded skill does not match reviewed source commit " + ReviewedSourceCommit + ". Nothing was installed into the project.";
                return false;
            }

            ValidateStagedFiles(stagedSkillRoot);
            var contentHash = ComputeContentHash(stagedSkillRoot);
            if (!Regex.IsMatch(expectedContentHash ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                || !string.Equals(contentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase))
            {
                message = "The staged skill content hash does not match reviewed source commit " + ReviewedSourceCommit + ". Nothing was installed into the project.";
                return false;
            }

            File.WriteAllText(Path.Combine(stagedSkillRoot, ProvenanceFileName), ReviewedSourceCommit + "\n" + ReviewedSkillsHash + "\n" + contentHash + "\n", Encoding.UTF8);
            ValidateStagedFiles(stagedSkillRoot);
            return true;
        }

        static void ValidateStagedFiles(string root)
        {
            var fileCount = 0;
            long totalBytes = 0;
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    var info = new DirectoryInfo(directory);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("Staged skill contains a reparse point.");
                    }

                    if (!IsTargetWithinProject(root, info.FullName))
                    {
                        throw new InvalidOperationException("Staged skill contains an unsafe directory path.");
                    }

                    pending.Push(info.FullName);
                }

                foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    fileCount++;
                    if (fileCount > MaxSkillFiles)
                    {
                        throw new InvalidOperationException("Staged skill exceeds the maximum file count.");
                    }

                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("Staged skill contains a reparse point.");
                    }

                    if (!IsTargetWithinProject(root, info.FullName))
                    {
                        throw new InvalidOperationException("Staged skill contains an unsafe file path.");
                    }

                    if (string.Equals(info.Name, ProvenanceFileName, StringComparison.Ordinal)
                        && !SamePath(info.FullName, Path.Combine(Path.GetFullPath(root), ProvenanceFileName)))
                    {
                        throw new InvalidOperationException("Staged skill contains a nested provenance file.");
                    }

                    totalBytes += info.Length;
                    if (totalBytes > MaxSkillBytes)
                    {
                        throw new InvalidOperationException("Staged skill exceeds the maximum allowed size.");
                    }
                }
            }

            if (fileCount == 0)
            {
                throw new InvalidOperationException("Staged skill contains no files.");
            }
        }


        internal static string ComputeContentHash(string root)
        {
            var files = new List<string>(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            var rootProvenancePath = Path.Combine(Path.GetFullPath(root), ProvenanceFileName);
            files.RemoveAll(path => SamePath(path, rootProvenancePath));
            files.Sort(StringComparer.Ordinal);
            using (var hash = SHA256.Create())
            using (var stream = new MemoryStream())
            {
                for (var index = 0; index < files.Count; index++)
                {
                    var relative = RelativePath(root, files[index]).Replace('\\', '/');
                    var pathBytes = Encoding.UTF8.GetBytes(relative + "\n");
                    stream.Write(pathBytes, 0, pathBytes.Length);
                    var bytes = File.ReadAllBytes(files[index]);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.WriteByte((byte)'\n');
                }

                stream.Position = 0;
                var digest = hash.ComputeHash(stream);
                var result = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++) result.Append(digest[index].ToString("x2"));
                return result.ToString();
            }
        }

        static string RelativePath(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        static string RemoveJsonWhitespaceOutsideStrings(string value)
        {
            var builder = new StringBuilder();
            var inString = false;
            var escaped = false;
            for (var index = 0; index < (value ?? string.Empty).Length; index++)
            {
                var character = value[index];
                if (inString)
                {
                    builder.Append(character);
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    builder.Append(character);
                }
                else if (!char.IsWhiteSpace(character))
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        static bool TryParseReviewedProvenance(string provenance, out string commit, out string skillsHash, out string contentHash)
        {
            commit = string.Empty;
            skillsHash = string.Empty;
            contentHash = string.Empty;
            var lines = (provenance ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length != 4 || lines[3].Length != 0)
            {
                return false;
            }

            commit = lines[0];
            skillsHash = lines[1];
            contentHash = lines[2];
            return Regex.IsMatch(commit, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
                && Regex.IsMatch(skillsHash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                && Regex.IsMatch(contentHash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        }

        static bool IsKnownReviewedPair(string commit, string skillsHash)
        {
            return TryGetReviewedGeneration(commit, skillsHash, out _);
        }

        static bool IsCurrentReviewedPair(string commit, string skillsHash)
        {
            return string.Equals(commit, ReviewedSourceCommit, StringComparison.Ordinal)
                && string.Equals(skillsHash, ReviewedSkillsHash, StringComparison.Ordinal);
        }

        static bool IsPreviousReviewedPair(string commit, string skillsHash)
        {
            return TryGetReviewedGeneration(commit, skillsHash, out _)
                && !IsCurrentReviewedPair(commit, skillsHash);
        }

        static bool IsExpectedReviewedContentHash(string commit, string skillsHash, string contentHash)
        {
            return TryGetReviewedGeneration(commit, skillsHash, out var generation)
                && string.Equals(contentHash, ReviewedContentHashValue(generation), StringComparison.OrdinalIgnoreCase);
        }

        static string CurrentReviewedContentHash()
        {
            return string.IsNullOrEmpty(s_reviewedContentHashOverrideForTests) ? ReviewedContentHash : s_reviewedContentHashOverrideForTests;
        }

        static string PreviousReviewedContentHashValue()
        {
            return string.IsNullOrEmpty(s_previousReviewedContentHashOverrideForTests) ? PreviousReviewedContentHash : s_previousReviewedContentHashOverrideForTests;
        }

        static string LegacyReviewedContentHashValue()
        {
            return string.IsNullOrEmpty(s_legacyReviewedContentHashOverrideForTests) ? LegacyReviewedContentHash : s_legacyReviewedContentHashOverrideForTests;
        }

        static string ReviewedContentHashValue(ReviewedSkillGeneration generation)
        {
            if (generation == null)
            {
                return string.Empty;
            }
            if (string.Equals(generation.Commit, ReviewedSourceCommit, StringComparison.Ordinal))
            {
                return CurrentReviewedContentHash();
            }
            if (string.Equals(generation.Commit, PreviousReviewedSourceCommit, StringComparison.Ordinal))
            {
                return PreviousReviewedContentHashValue();
            }
            if (string.Equals(generation.Commit, LegacyReviewedSourceCommit, StringComparison.Ordinal))
            {
                return LegacyReviewedContentHashValue();
            }

            return generation.ContentHash;
        }

        static bool TryGetReviewedGeneration(string commit, string skillsHash, out ReviewedSkillGeneration generation)
        {
            for (var index = 0; index < s_reviewedGenerations.Length; index++)
            {
                var candidate = s_reviewedGenerations[index];
                if (string.Equals(commit, candidate.Commit, StringComparison.Ordinal)
                    && string.Equals(skillsHash, candidate.SkillsHash, StringComparison.Ordinal))
                {
                    generation = candidate;
                    return true;
                }
            }

            generation = null;
            return false;
        }

        static bool IsCurrentReviewedStatus(UnityCliSkillStatus status)
        {
            return status != null
                && status.Valid
                && status.ProvenanceVerified
                && string.Equals(status.LocalState, LocalStateCurrentReviewed, StringComparison.Ordinal)
                && IsCurrentReviewedPair(status.InstalledReviewedCommit, status.InstalledReviewedHash)
                && string.Equals(status.ContentHash, CurrentReviewedContentHash(), StringComparison.OrdinalIgnoreCase);
        }

        static bool IsPreviousReviewedStatus(UnityCliSkillStatus status)
        {
            return status != null
                && status.Valid
                && status.ProvenanceVerified
                && string.Equals(status.LocalState, LocalStatePreviousReviewed, StringComparison.Ordinal)
                && TryGetReviewedGeneration(status.InstalledReviewedCommit, status.InstalledReviewedHash, out var generation)
                && !IsCurrentReviewedPair(status.InstalledReviewedCommit, status.InstalledReviewedHash)
                && string.Equals(status.ContentHash, ReviewedContentHashValue(generation), StringComparison.OrdinalIgnoreCase);
        }

        static bool TryValidateUpdateBinding(string projectRoot, UnityCliSkillUpdateBinding binding, UnityCliSkillStatus localStatus, DateTime utcNow, out string error)
        {
            error = string.Empty;
            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            var targetRoot = Path.Combine(projectRoot, ".agents", "skills", SkillName);
            if (binding == null
                || !string.Equals(binding.BindingIdentitySha256, ComputeBindingIdentity(binding), StringComparison.Ordinal)
                || !IsPreviousReviewedPair(binding.InstalledReviewedCommit, binding.InstalledReviewedHash)
                || !string.Equals(binding.TargetReviewedCommit, ReviewedSourceCommit, StringComparison.Ordinal)
                || !string.Equals(binding.TargetReviewedHash, ReviewedSkillsHash, StringComparison.Ordinal)
                || !string.Equals(binding.SourceIdentitySha256, UnityCliUpdateCheck.SkillSourceIdentity(ReviewedSourceCommit), StringComparison.Ordinal)
                || binding.CheckedAtUtc == DateTime.MinValue
                || binding.ExpiresAtUtc == DateTime.MinValue
                || binding.CheckedAtUtc > now
                || binding.ExpiresAtUtc <= binding.CheckedAtUtc
                || binding.ExpiresAtUtc - binding.CheckedAtUtc > UnityCliUpdateCheck.CacheTtl
                || now >= binding.ExpiresAtUtc)
            {
                error = "The skill update binding is missing, expired, or no longer matches the bundled reviewed artifact.";
                return false;
            }

            if (!IsTargetWithinProject(projectRoot, targetRoot)
                || HasExistingReparsePoint(projectRoot, targetRoot)
                || !IsPreviousReviewedStatus(localStatus)
                || !string.Equals(binding.InstalledReviewedCommit, localStatus.InstalledReviewedCommit, StringComparison.Ordinal)
                || !string.Equals(binding.InstalledReviewedHash, localStatus.InstalledReviewedHash, StringComparison.Ordinal)
                || !SamePath(binding.SkillRoot, targetRoot)
                || !SamePath(localStatus.SkillRoot, targetRoot)
                || !string.Equals(binding.PreContentHash, localStatus.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                error = "The existing unity-cli target is no longer the exact older Gateway-reviewed artifact.";
                return false;
            }

            return true;
        }

        static string ComputeBindingIdentity(UnityCliSkillUpdateBinding binding)
        {
            binding = binding ?? new UnityCliSkillUpdateBinding();
            return UnityCliUpdateCheck.SourceIdentity(
                binding.InstalledReviewedCommit + "|" + binding.InstalledReviewedHash + "|"
                + binding.TargetReviewedCommit + "|" + binding.TargetReviewedHash + "|"
                + binding.SourceIdentitySha256 + "|" + binding.SkillRoot + "|"
                + binding.PreContentHash + "|" + binding.CheckedAtUtc.Ticks);
        }

        static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        static bool RollbackUpdate(string projectRoot, string targetRoot, string backupRoot,
            UnityCliSkillUpdateBinding binding, DirectoryIdentity previousIdentity, DirectoryIdentity replacementIdentity)
        {
            try
            {
                using (var custody = new PublicationDirectoryCustody())
                {
                    custody.HoldChain(Path.GetDirectoryName(targetRoot));
                    custody.HoldChain(Path.GetDirectoryName(backupRoot));
                    if (!UnityCliPipelineSetup.IsPathWithoutReparsePoints(targetRoot)
                        || !UnityCliPipelineSetup.IsPathWithoutReparsePoints(backupRoot)
                        || !custody.Matches(backupRoot, previousIdentity)
                        || !string.Equals(ComputeContentHash(backupRoot), binding.PreContentHash, StringComparison.OrdinalIgnoreCase)) return false;
                    if (!TryGetPathPresence(targetRoot, out var present)) return false;
                    if (present)
                    {
                        if (!custody.Matches(targetRoot, replacementIdentity)) return false;
                        // Never delete a failed or concurrently modified replacement.
                        custody.MoveDirectory(targetRoot, Path.Combine(Path.GetDirectoryName(backupRoot), "unity-cli-skill-failed-" + Guid.NewGuid().ToString("N")), replacementIdentity);
                    }
                    custody.MoveDirectory(backupRoot, targetRoot, previousIdentity); // A newly appeared target is refused atomically.
                    var restored = GetStatus(projectRoot);
                    return IsPreviousReviewedStatus(restored)
                        && restored.InstalledReviewedCommit == binding.InstalledReviewedCommit
                        && string.Equals(restored.ContentHash, binding.PreContentHash, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        static bool TryGetPathPresence(string path, out bool present)
        {
            present = false;
            if (!UnityCliPipelineSetup.IsPathWithoutReparsePoints(path)) return false;
            try { File.GetAttributes(path); present = true; return true; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { return false; }
        }

        internal sealed class UnityCliSkillUpdateBinding
        {
            public string InstalledReviewedCommit = string.Empty;
            public string InstalledReviewedHash = string.Empty;
            public string TargetReviewedCommit = string.Empty;
            public string TargetReviewedHash = string.Empty;
            public string SourceIdentitySha256 = string.Empty;
            public string BindingIdentitySha256 = string.Empty;
            public string SkillRoot = string.Empty;
            public string PreContentHash = string.Empty;
            public DateTime CheckedAtUtc = DateTime.MinValue;
            public DateTime ExpiresAtUtc = DateTime.MinValue;
        }

        sealed class ReviewedSkillGeneration
        {
            public ReviewedSkillGeneration(string commit, string skillsHash, string contentHash)
            {
                Commit = commit;
                SkillsHash = skillsHash;
                ContentHash = contentHash;
            }

            public string Commit { get; }
            public string SkillsHash { get; }
            public string ContentHash { get; }
        }

        internal sealed class UnityCliSkillStatus
        {
            public bool Exists;
            public bool Valid;
            public bool ProvenanceVerified;
            public bool ValidFrontmatter;
            public bool ModifiedOrUnknown;
            public bool ReparsePoint;
            public string SkillRoot = string.Empty;
            public string SkillPath = string.Empty;
            public string LocalState = LocalStateAbsent;
            public string SourceProvenance = "unknown";
            public string InstalledReviewedCommit = string.Empty;
            public string InstalledReviewedHash = string.Empty;
            public string AvailableReviewedCommit = string.Empty;
            public string AvailableReviewedHash = string.Empty;
            public string ContentHash = string.Empty;
            public bool UpstreamChangeDetected;
            public ComponentUpdateStatus Update = ComponentUpdateStatus.NotChecked();
            public string Message = string.Empty;

            public string UpdateState => Update == null ? UnityCliUpdateCheck.UpdateStateNotChecked : Update.UpdateState;
        }
    }
}
