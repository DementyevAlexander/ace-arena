using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityCliSkillPinnedSourceTests
    {
        static readonly string[] ExpectedFiles =
        {
            "CHANGELOG.md", "SECURITY.md", "SKILL.md",
            "references/auth-license-cloud.md", "references/build-run-test.md",
            "references/cloud-automation.md", "references/collaboration.md",
            "references/config-hub.md", "references/diagnostics-maintenance.md",
            "references/editors-install.md", "references/integration-advanced.md",
            "references/playmode-verification-loop.md", "references/projects-templates.md",
            "references/version-control.md",
        };

        [Test]
        public void CurrentOfficialSourceAndHashesAreTheObservedImmutableFourteenFileArtifact()
        {
            Assert.That(UnityCliProjectSkillInstaller.ReviewedSourceCommit, Is.EqualTo("cb1dccb8f5adffcca43a5a26993fdeb8eae59433"));
            Assert.That(UnityCliProjectSkillInstaller.ReviewedSource, Is.EqualTo("https://raw.githubusercontent.com/Unity-Technologies/skills/cb1dccb8f5adffcca43a5a26993fdeb8eae59433/skills/unity-cli/"));
            Assert.That(UnityCliProjectSkillInstaller.ReviewedContentHash, Is.EqualTo("20cce0a4ca795c0f2d192da90d32c27a27686210da435c3b758487c62055768b"));
            Assert.That(UnityCliProjectSkillInstaller.ReviewedSkillsHash, Is.EqualTo("3dc226a7f8fdb938126606c6f7ed4c97a1eee541e9f0ae1a38e8d7918f37964e"));
            Assert.That(UnityCliReviewedSkillDownload.RelativePaths, Is.EqualTo(ExpectedFiles));
            Assert.That(UnityCliReviewedSkillDownload.RelativePaths.Distinct().Count(), Is.EqualTo(14));
            Assert.That(UnityCliReviewedSkillDownload.RelativePaths.All(path => path.EndsWith(".md", StringComparison.Ordinal) && !path.Contains("..") && !path.Contains(":")), Is.True);
        }

        [Test]
        public void AllThreeOlderOfficialGenerationsRemainAvailableForDetection()
        {
            Assert.That(UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit, Is.EqualTo("87fac23d66a1f44f5e06c2935eccce0b40b9715a"));
            Assert.That(UnityCliProjectSkillInstaller.PreviousReviewedContentHash, Is.EqualTo("7c8336ee41ec0f5d02df2d49ca2e6e008333180423e1fb074135931af52335fb"));
            Assert.That(UnityCliProjectSkillInstaller.EarlierReviewedSourceCommit, Is.EqualTo("344cf1b826633409baa1363720eab8e8144f0058"));
            Assert.That(UnityCliProjectSkillInstaller.EarlierReviewedContentHash, Is.EqualTo("be5efc00023c130b0fd5a5c5c5348761e9027cdb6c6bc768050a63626a29d667"));
            Assert.That(UnityCliProjectSkillInstaller.LegacyReviewedSourceCommit, Is.EqualTo("f4339cbf3e2293f6faaa0642d9cc3d161b909467"));
        }

        [Test]
        public void DownloadArgumentsAreNonInteractiveAndLiteralPathTokensCannotInjectScript()
        {
            RequireWindows();
            var root = Path.Combine(Path.GetTempPath(), "skill O'Neil $() {{FILES}} {{SOURCE}} {{ROOT}}");
            var args = UnityCliReviewedSkillDownload.BuildArguments(root);
            Assert.That(args.Take(3), Is.EqualTo(new[] { "-NoProfile", "-NonInteractive", "-Command" }));
            Assert.That(args, Has.Length.EqualTo(4));
            Assert.That(args[3], Does.Contain("[IO.Path]::GetFullPath('" + Path.GetFullPath(root).Replace("'", "''") + "')"));
            Assert.That(args[3], Does.Contain("$source='" + UnityCliProjectSkillInstaller.ReviewedSource + "'"));
            Assert.That(args[3], Does.Not.Contain("Invoke-Expression"));
            Assert.That(args[3], Does.Not.Contain("Start-Process"));
            Assert.That(args[3], Does.Not.Contain("DownloadString"));
            Assert.That(args[3], Does.Not.Contain("Expand-Archive"));
            Assert.That(args[3], Does.Not.Contain("npm"));
            Assert.That(args[3], Does.Not.Contain("git clone"));
        }

        [TestCase("")]
        [TestCase("relative/staging")]
        [TestCase("\\\\server\\share\\staging")]
        [TestCase("C:\\")]
        [TestCase("C:\\stage\nother")]
        public void DownloadArgumentsRejectNonLocalRootRelativeAndControlCharacterTargets(string root)
        {
            RequireWindows();
            Assert.Throws<ArgumentException>(() => UnityCliReviewedSkillDownload.BuildArguments(root));
        }

        [Test]
        public void ScriptRejectsRedirectsCredentialsNon200UnboundedDataAndExistingDestinations()
        {
            RequireWindows();
            var script = UnityCliReviewedSkillDownload.BuildArguments(Path.Combine(Path.GetTempPath(), "skill-source-contract"))[3];
            Assert.That(script, Does.Contain("$request.AllowAutoRedirect=$false"));
            Assert.That(script, Does.Contain("$request.UseDefaultCredentials=$false"));
            Assert.That(script, Does.Contain("$request.Proxy=$null"));
            Assert.That(script, Does.Contain("$response.StatusCode -ne [Net.HttpStatusCode]::OK"));
            Assert.That(script, Does.Contain("$response.ResponseUri.AbsoluteUri -cne $url"));
            Assert.That(script, Does.Contain("$request.Timeout=[Math]::Min($requestTimeout,(RemainingMilliseconds $fileBudget))"));
            Assert.That(script, Does.Contain("$inputStream.ReadTimeout=[Math]::Min($readTimeout,$remaining)"));
            Assert.That(script, Does.Contain("$fileBytes -gt $maxFileBytes -or $totalBytes -gt $maxTotalBytes"));
            Assert.That(script, Does.Contain("[IO.FileMode]::CreateNew"));
            Assert.That(script, Does.Contain("[IO.FileShare]::None"));
            Assert.That(script, Does.Contain("[IO.FileAttributes]::ReparsePoint"));
            Assert.That(script, Does.Contain("$current=$current.Parent"));
            Assert.That(script, Does.Contain("Staging root must be newly created and empty"));
            Assert.That(script, Does.Not.Contain("Directory]::Delete"));
            Assert.That(UnityCliReviewedSkillDownload.MaxFileBytes, Is.EqualTo(131072));
            Assert.That(UnityCliProjectSkillInstaller.MaxSkillBytes, Is.EqualTo(1048576));
            Assert.That(UnityCliProjectSkillInstaller.MaxSkillFiles, Is.EqualTo(64));
            Assert.That(UnityCliReviewedSkillDownload.TotalTimeoutMilliseconds, Is.LessThan(UnityCliReviewedSkillDownload.ProcessTimeout.TotalMilliseconds));
        }

        [Test]
        public void FullByteHashIsRequiredBeforeAcquisitionMetadataIsWritten()
        {
            RequireWindows();
            var script = UnityCliReviewedSkillDownload.BuildArguments(Path.Combine(Path.GetTempPath(), "skill-hash-contract"))[3];
            Assert.That(script, Does.Contain("[Array]::Sort($ordered,[StringComparer]::Ordinal)"));
            Assert.That(script, Does.Contain("[Text.Encoding]::UTF8.GetBytes($relative+[char]10)"));
            Assert.That(script, Does.Contain("$fileStream.CopyTo($hashBuffer,4096)"));
            Assert.That(script, Does.Contain("$hashBuffer.WriteByte(10)"));
            Assert.That(script.IndexOf("if (-not [string]::Equals($contentHash,$expectedHash,", StringComparison.Ordinal),
                Is.LessThan(script.IndexOf("$lockStream=[IO.File]::Open", StringComparison.Ordinal)));
            Assert.That(script, Does.Contain("gateway_reviewed_acquisition_metadata"));
            Assert.That(UnityCliReviewedSkillDownload.CanonicalReviewedLock,
                Is.EqualTo("{\"version\":1,\"skills\":{\"unity-cli\":{\"source\":\"Unity-Technologies/skills\",\"sourceType\":\"github\",\"skillPath\":\"skills/unity-cli/SKILL.md\",\"computedHash\":\"" + UnityCliProjectSkillInstaller.ReviewedSkillsHash + "\"}}}"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreexistingStagingIsNeverReusedOverwrittenOrRemoved(bool nonempty)
        {
            RequireWindows();
            WithOwnedRoot(root =>
            {
                Assert.That(UnityCliProjectSkillInstaller.TryCreateReviewedStagingDirectory(root, out var stage, out var error), Is.True, error);
                var marker = Path.Combine(stage, "user.txt");
                if (nonempty) File.WriteAllText(marker, "keep exact bytes");
                Assert.That(UnityCliProjectSkillInstaller.TryCreateReviewedStagingDirectory(root, out _, out error), Is.False);
                Assert.That(error, Does.Contain("already exists"));
                Assert.That(Directory.Exists(stage), Is.True);
                if (nonempty) Assert.That(File.ReadAllText(marker), Is.EqualTo("keep exact bytes"));
                else Assert.That(Directory.GetFileSystemEntries(stage), Is.Empty);
            });
        }

        [Test]
        public void StagingDoesNotTreatExistingFileOrNonDirectoryAncestorAsAbsent()
        {
            RequireWindows();
            WithOwnedRoot(root =>
            {
                var library = Path.Combine(root, "Library");
                File.WriteAllText(library, "keep ancestor");
                Assert.That(UnityCliProjectSkillInstaller.TryCreateReviewedStagingDirectory(root, out _, out _), Is.False);
                Assert.That(File.ReadAllText(library), Is.EqualTo("keep ancestor"));
            });
            WithOwnedRoot(root =>
            {
                var stage = Path.Combine(root, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-staging");
                Directory.CreateDirectory(Path.GetDirectoryName(stage));
                File.WriteAllText(stage, "keep staging file");
                Assert.That(UnityCliProjectSkillInstaller.TryCreateReviewedStagingDirectory(root, out _, out _), Is.False);
                Assert.That(File.ReadAllText(stage), Is.EqualTo("keep staging file"));
            });
        }

        [Test]
        public void CanonicalMetadataAloneCannotAuthorizeTamperedStagedFiles()
        {
            WithOwnedRoot(root =>
            {
                var stage = Path.Combine(root, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-staging");
                var skill = Path.Combine(stage, ".agents", "skills", "unity-cli");
                Directory.CreateDirectory(skill);
                File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: unity-cli\n---\nnot official bytes\n");
                File.WriteAllText(Path.Combine(stage, "skills-lock.json"), UnityCliReviewedSkillDownload.CanonicalReviewedLock);
                Assert.That(UnityCliProjectSkillInstaller.TryFinalizeInstall(root, out var message), Is.False);
                Assert.That(message, Does.Contain("content hash"));
                Assert.That(Directory.Exists(Path.Combine(root, ".agents", "skills", "unity-cli")), Is.False);
            });
        }

        static void RequireWindows()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Windows-only native staging contract.");
        }

        [Test]
        public void PublicationAtomicallyMovesStagedTreeAndNeverMergesAnAppearedTarget()
        {
            RequireWindows();
            WithOwnedRoot(root =>
            {
                var source = CreatePublicationFixture(root);
                Assert.That(UnityCliProjectSkillInstaller.TryPublishStagedInstall(root, source, out var error), Is.True, error);
                Assert.That(Directory.Exists(source), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(root, ".agents", "skills", "unity-cli", "SKILL.md")), Is.EqualTo("owned staged bytes"));
            });
            WithOwnedRoot(root =>
            {
                var source = CreatePublicationFixture(root);
                var target = Path.Combine(root, ".agents", "skills", "unity-cli");
                Assert.That(UnityCliProjectSkillInstaller.TryPublishStagedInstallForTests(root, source, () =>
                {
                    Directory.CreateDirectory(target);
                    File.WriteAllText(Path.Combine(target, "foreign.txt"), "foreign exact bytes");
                }, out _), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(target, "foreign.txt")), Is.EqualTo("foreign exact bytes"));
                Assert.That(File.Exists(Path.Combine(target, "SKILL.md")), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(source, "SKILL.md")), Is.EqualTo("owned staged bytes"));
            });
        }

        [Test]
        public void PublicationRefusesTargetFileAndChangedNonDirectoryAncestorWithoutCleanup()
        {
            RequireWindows();
            WithOwnedRoot(root =>
            {
                var source = CreatePublicationFixture(root);
                var parent = Path.Combine(root, ".agents", "skills");
                Directory.CreateDirectory(parent);
                var target = Path.Combine(parent, "unity-cli");
                File.WriteAllText(target, "foreign file");
                Assert.That(UnityCliProjectSkillInstaller.TryPublishStagedInstall(root, source, out _), Is.False);
                Assert.That(File.ReadAllText(target), Is.EqualTo("foreign file"));
                Assert.That(Directory.Exists(source), Is.True);
            });
            WithOwnedRoot(root =>
            {
                var source = CreatePublicationFixture(root);
                File.WriteAllText(Path.Combine(root, ".agents"), "changed ancestor");
                Assert.That(UnityCliProjectSkillInstaller.TryPublishStagedInstall(root, source, out _), Is.False);
                Assert.That(File.ReadAllText(Path.Combine(root, ".agents")), Is.EqualTo("changed ancestor"));
                Assert.That(Directory.Exists(source), Is.True);
            });
        }

        [Test]
        public void PublicationRefusesAnAncestorChangedToJunctionAfterStaging()
        {
            RequireWindows();
            WithOwnedRoot(root =>
            {
                var source = CreatePublicationFixture(root);
                var outside = Path.Combine(root, "outside-owned-test");
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside exact bytes");
                var link = Path.Combine(root, ".agents");
                CreateOwnedJunction(link, outside);
                try
                {
                    Assert.That(UnityCliProjectSkillInstaller.TryPublishStagedInstall(root, source, out _), Is.False);
                    Assert.That(Directory.GetFileSystemEntries(outside).Length, Is.EqualTo(1));
                    Assert.That(File.ReadAllText(Path.Combine(outside, "keep.txt")), Is.EqualTo("outside exact bytes"));
                    Assert.That(Directory.Exists(source), Is.True);
                }
                finally { Directory.Delete(link, false); }
            });
        }

        [Test]
        public void HeldPublicationParentsCannotBeRenamedDuringFinalMove()
        {
            RequireWindows();
            WithOwnedRoot(root =>
            {
                var source = CreatePublicationFixture(root);
                var parent = Path.Combine(root, ".agents", "skills");
                Assert.That(UnityCliProjectSkillInstaller.TryPublishStagedInstallForTests(root, source, () =>
                {
                    Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-swapped"));
                }, out var error), Is.True, error);
                Assert.That(Directory.Exists(parent + "-swapped"), Is.False);
            });
        }

        [Test]
        public void HandleRelativePublicationCannotFollowALateParentJunction()
        {
            RequireWindows();
            WithOwnedRoot(root =>
            {
                var source = CreatePublicationFixture(root);
                var outside = Path.Combine(root, "outside-owned-late-test");
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside exact bytes");
                var parent = Path.Combine(root, ".agents", "skills");
                try
                {
                    Assert.That(UnityCliProjectSkillInstaller.TryPublishStagedInstallForTests(root, source, null, out var error,
                        () => CreateOwnedJunction(parent, outside)), Is.False);
                    Assert.That(error, Does.Contain("Atomic skill move refused"));
                    Assert.That(Directory.GetFileSystemEntries(outside).Length, Is.EqualTo(1));
                    Assert.That(File.ReadAllText(Path.Combine(outside, "keep.txt")), Is.EqualTo("outside exact bytes"));
                    Assert.That(File.ReadAllText(Path.Combine(source, "SKILL.md")), Is.EqualTo("owned staged bytes"));
                }
                finally { if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) Directory.Delete(parent, false); }
            });
        }

        static string CreatePublicationFixture(string root)
        {
            var source = Path.Combine(root, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-staging", ".agents", "skills", "unity-cli");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "SKILL.md"), "owned staged bytes");
            return source;
        }

        static void CreateOwnedJunction(string path, string target)
        {
            Directory.CreateDirectory(path);
            var substitute = Encoding.Unicode.GetBytes("\\??\\" + Path.GetFullPath(target));
            var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
            var buffer = new byte[20 + substitute.Length + print.Length];
            using (var stream = new MemoryStream(buffer))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0xA0000003u); // IO_REPARSE_TAG_MOUNT_POINT.
                writer.Write((ushort)(buffer.Length - 8));
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)substitute.Length);
                writer.Write((ushort)(substitute.Length + 2));
                writer.Write((ushort)print.Length);
                writer.Write(substitute);
                writer.Write((ushort)0);
                writer.Write(print);
                writer.Write((ushort)0);
            }
            using (var handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
            {
                Assert.That(handle.IsInvalid, Is.False);
                Assert.That(DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero), Is.True,
                    "Failed to create an isolated junction: " + Marshal.GetLastWin32Error());
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

        static void WithOwnedRoot(Action<string> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "SGG-SkillPinned-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
