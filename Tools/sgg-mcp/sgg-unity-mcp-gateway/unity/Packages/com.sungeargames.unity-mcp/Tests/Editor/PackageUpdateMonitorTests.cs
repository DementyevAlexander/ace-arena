using System;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class PackageUpdateMonitorTests
    {
        string m_root;
        string m_manifestPath;

        [SetUp]
        public void SetUp()
        {
            m_root = Path.Combine(Path.GetTempPath(), "sgg-unity-mcp-package-monitor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_root);
            m_manifestPath = Path.Combine(m_root, "manifest.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(m_root) && Directory.Exists(m_root))
            {
                Directory.Delete(m_root, true);
            }
        }

        [Test]
        public void SameContentWithChangedTimestampKeepsFingerprint()
        {
            File.WriteAllText(m_manifestPath, "{\"dependencies\":{}}");
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var before), Is.True);

            File.SetLastWriteTimeUtc(m_manifestPath, DateTime.UtcNow.AddMinutes(1));
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var after), Is.True);

            Assert.That(after, Is.EqualTo(before));
        }

        [Test]
        public void SameContentRewriteKeepsFingerprint()
        {
            const string content = "{\"dependencies\":{\"com.example\":\"1.0.0\"}}";
            File.WriteAllText(m_manifestPath, content);
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var before), Is.True);

            File.WriteAllText(m_manifestPath, content);
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var after), Is.True);

            Assert.That(after, Is.EqualTo(before));
        }

        [Test]
        public void ContentChangeChangesFingerprint()
        {
            File.WriteAllText(m_manifestPath, "{\"dependencies\":{}}");
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var before), Is.True);

            File.WriteAllText(m_manifestPath, "{\"dependencies\":{\"com.example\":\"1.0.0\"}}");
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var after), Is.True);

            Assert.That(after, Is.Not.EqualTo(before));
        }

        [Test]
        public void EqualLengthContentChangeWithRestoredTimestampChangesFingerprint()
        {
            File.WriteAllText(m_manifestPath, "{}");
            var timestamp = File.GetLastWriteTimeUtc(m_manifestPath);
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var before), Is.True);

            File.WriteAllText(m_manifestPath, "[]");
            File.SetLastWriteTimeUtc(m_manifestPath, timestamp);
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var after), Is.True);

            Assert.That(after, Is.Not.EqualTo(before));
        }

        [Test]
        public void RequiredManifestMustBePresent()
        {
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("missing_file"));
            File.WriteAllText(m_manifestPath, "{}");
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out var present), Is.True);

            Assert.That(present, Does.Contain("|file|"));
        }

        [Test]
        public void OptionalLockFileMayBeMissing()
        {
            var lockPath = Path.Combine(m_root, "packages-lock.json");
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(lockPath, "Packages/packages-lock.json", out var missing), Is.True);
            Assert.That(missing, Does.Contain("|missing|"));
        }

        [Test]
        public void DirectoryAtManifestPathIsUnavailable()
        {
            Directory.CreateDirectory(m_manifestPath);
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("path_security_rejected"));
        }

        [Test]
        public void OversizedProjectPackageFileIsUnavailable()
        {
            using (var stream = new FileStream(m_manifestPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(4L * 1024L * 1024L + 1L);
            }

            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("size_limit_exceeded"));
        }

        [Test]
        public void ExpiredBudgetReturnsTypedFailureWithoutReadingContent()
        {
            var projectRoot = CreateProjectWithManifest("{}");

            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(
                projectRoot,
                "Packages/manifest.json",
                Stopwatch.StartNew(),
                -1,
                null,
                out _,
                out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("budget_exhausted"));
        }

        [Test]
        public void ReplacementBetweenStableReadsReturnsTypedFailure()
        {
            var projectRoot = CreateProjectWithManifest("{\"generation\":1}");
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");
            var displacedPath = Path.Combine(projectRoot, "Packages", "manifest.previous.json");

            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(
                projectRoot,
                "Packages/manifest.json",
                Stopwatch.StartNew(),
                5000,
                () => File.Move(manifestPath, displacedPath),
                out _,
                out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("replacement_race"));
        }

        [Test]
        public void RewriteBetweenStableReadsReturnsTypedFailure()
        {
            var projectRoot = CreateProjectWithManifest("{\"generation\":1}");
            var manifestPath = Path.Combine(projectRoot, "Packages", "manifest.json");

            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(
                projectRoot,
                "Packages/manifest.json",
                Stopwatch.StartNew(),
                5000,
                () => File.WriteAllText(manifestPath, "{\"generation\":22}"),
                out _,
                out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("unstable_metadata"));
        }

        [Test]
        public void SharingViolationReturnsTypedReadException()
        {
            File.WriteAllText(m_manifestPath, "{}");
            using (new FileStream(m_manifestPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprint(m_manifestPath, "Packages/manifest.json", out _, out var reason), Is.False);
                Assert.That(reason, Is.EqualTo("read_exception"));
            }
        }

        [Test]
        public void NativeContainmentAcceptsRootAndOrdinaryChildWithTrailingSeparators()
        {
            var child = Path.Combine(m_root, "Packages", "manifest.json");
            var rootWithTrailingSeparator = m_root + Path.DirectorySeparatorChar;
            var fileSystemRoot = Path.GetPathRoot(m_root);

            Assert.That(PackageUpdateMonitor.IsSameOrChildPath(m_root, rootWithTrailingSeparator), Is.True);
            Assert.That(PackageUpdateMonitor.IsSameOrChildPath(child, rootWithTrailingSeparator), Is.True);
            Assert.That(PackageUpdateMonitor.IsSameOrChildPath(fileSystemRoot, fileSystemRoot), Is.True);
            Assert.That(PackageUpdateMonitor.IsSameOrChildPath(m_root, fileSystemRoot), Is.True);

            if (Path.DirectorySeparatorChar == '\\')
            {
                Assert.That(PackageUpdateMonitor.IsSameOrChildPath(child, m_root.Replace('\\', '/')), Is.True);
            }
            else
            {
                Assert.That(PackageUpdateMonitor.IsSameOrChildPath(child, m_root.ToUpperInvariant()), Is.False);
            }
        }

        [Test]
        public void NativeContainmentRejectsSiblingPrefixAndTraversalEscape()
        {
            var sibling = m_root + "-sibling";
            var traversal = Path.Combine(m_root, "..", Path.GetFileName(sibling), "manifest.json");

            Assert.That(PackageUpdateMonitor.IsSameOrChildPath(sibling, m_root), Is.False);
            Assert.That(PackageUpdateMonitor.IsSameOrChildPath(traversal, m_root), Is.False);
        }

        [Test]
        public void RootAwareFingerprintUsesCanonicalContainmentPipeline()
        {
            var projectRoot = Path.Combine(m_root, "project");
            var packagesRoot = Path.Combine(projectRoot, "Packages");
            Directory.CreateDirectory(packagesRoot);
            File.WriteAllText(Path.Combine(packagesRoot, "manifest.json"), "{\"dependencies\":{}}");
            var trustedRoot = Path.DirectorySeparatorChar == '\\' ? projectRoot.Replace('\\', '/') : projectRoot;

            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(trustedRoot, "Packages/manifest.json", out var fingerprint), Is.True);
            Assert.That(fingerprint, Does.Contain("Packages/manifest.json|file|"));
        }

        [Test]
        public void RootAwareFingerprintRejectsTraversalOutsideTrustedRoot()
        {
            var projectRoot = Path.Combine(m_root, "project");
            Directory.CreateDirectory(projectRoot);
            File.WriteAllText(m_manifestPath, "{\"dependencies\":{}}");

            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, "../manifest.json", out _), Is.False);
            Assert.That(PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, m_manifestPath, out _), Is.False);
        }

        [Test]
        public void FailedMonitorScanInvalidatesPreviousLocalInventoryEvidence()
        {
            var state = new BridgeRuntimeState();
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Available(1, 1, 1, 0, 0, string.Empty, "source", "source", "state"));
            var monitor = new PackageUpdateMonitor(Path.Combine(m_root, "missing-package-root"));

            try
            {
                monitor.Update(state, 1.0);
            }
            finally
            {
                monitor.Dispose();
            }

            Assert.That(state.PackageRefreshPending, Is.True);
            Assert.That(state.PackageFingerprintFailureClass, Is.EqualTo("package_runtime"));
            Assert.That(state.PackageFingerprintFailureReason, Is.EqualTo("missing_file"));
            Assert.That(state.PackageFingerprintFailureFile, Is.EqualTo("package_root"));
        }

        [Test]
        public void ScanPolicyDefersDirtyAndFallbackWorkDuringPlayMode()
        {
            Assert.That(PackageUpdateMonitor.ShouldStartScan(true, false, true, false, false, 10.0, 0.0), Is.False);
            Assert.That(PackageUpdateMonitor.ShouldStartFallbackScan(false, false, true, false, false, 120.0, 120.0), Is.False);
            Assert.That(PackageUpdateMonitor.ShouldStartScan(true, false, false, false, false, 10.0, 0.0), Is.True);
            Assert.That(PackageUpdateMonitor.ShouldStartFallbackScan(false, false, false, false, false, 120.0, 120.0), Is.True);
        }

        [Test]
        public void ScanPolicyNeverStartsAnotherWorkerWhileOneIsRunning()
        {
            Assert.That(PackageUpdateMonitor.ShouldStartScan(true, true, false, false, false, 10.0, 0.0), Is.False);
            Assert.That(PackageUpdateMonitor.ShouldStartFallbackScan(false, true, false, false, false, 120.0, 120.0), Is.False);
        }

        [Test]
        public void ExplicitRefreshRetriesTransientStaleInventoryWithinBound()
        {
            Assert.That(PackageUpdateMonitor.ShouldRetryExplicitRefreshInventory(true, true, false, 0), Is.True);
            Assert.That(PackageUpdateMonitor.ShouldRetryExplicitRefreshInventory(true, false, true, 0), Is.True);
            Assert.That(PackageUpdateMonitor.ShouldRetryExplicitRefreshInventory(false, true, true, 0), Is.False);
            Assert.That(PackageUpdateMonitor.ShouldRetryExplicitRefreshInventory(true, false, false, 0), Is.False);
            Assert.That(PackageUpdateMonitor.ShouldRetryExplicitRefreshInventory(true, true, true, int.MaxValue), Is.False);
        }

        [Test]
        public void UnchangedUpdateTraceIsSampledWithoutDroppingTransitionsOrFingerprintChanges()
        {
            Assert.That(PackageUpdateMonitor.ShouldRetainUpdateTrace("package_refresh_required", "package_refresh_required", "pending", "pending", 10.0, 60.0), Is.False);
            Assert.That(PackageUpdateMonitor.ShouldRetainUpdateTrace("unavailable", "unavailable", "pending", "pending", 10.0, 60.0), Is.False);
            Assert.That(PackageUpdateMonitor.ShouldRetainUpdateTrace("package_refresh_required", "package_refresh_required", "pending", "pending", 60.0, 60.0), Is.True);
            Assert.That(PackageUpdateMonitor.ShouldRetainUpdateTrace("package_refresh_required", "current", "pending", string.Empty, 10.0, 60.0), Is.True);
            Assert.That(PackageUpdateMonitor.ShouldRetainUpdateTrace("package_refresh_required", "package_refresh_required", "pending", "new-pending", 10.0, 60.0), Is.True);
            Assert.That(PackageUpdateMonitor.ShouldRetainUpdateTrace("package_refresh_required", "package_refresh_required", "pending", "pending", 10.0, 60.0, true), Is.True);
        }

        string CreateProjectWithManifest(string content)
        {
            var projectRoot = Path.Combine(m_root, "project-" + Guid.NewGuid().ToString("N"));
            var packagesRoot = Path.Combine(projectRoot, "Packages");
            Directory.CreateDirectory(packagesRoot);
            File.WriteAllText(Path.Combine(packagesRoot, "manifest.json"), content);
            return projectRoot;
        }
    }
}
