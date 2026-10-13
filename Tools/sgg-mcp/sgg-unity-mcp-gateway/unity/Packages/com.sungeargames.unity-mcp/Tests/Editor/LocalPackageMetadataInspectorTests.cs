using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class LocalPackageMetadataInspectorTests
    {
        string m_root;

        [SetUp]
        public void SetUp()
        {
            m_root = Path.Combine(Path.GetTempPath(), "sgg-unity-mcp-local-metadata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(m_root);
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
        public void MatchingLocalManifestIsCurrent()
        {
            WriteManifest("com.example.local", "1.2.3");

            var evidence = LocalPackageMetadataInspector.Inspect("Local", m_root, "com.example.local", "1.2.3");

            Assert.That(evidence.Status, Is.EqualTo("current"));
            Assert.That(evidence.NameMatches, Is.True);
            Assert.That(evidence.VersionMatches, Is.True);
            Assert.That(evidence.IsStale, Is.False);
        }

        [Test]
        public void MatchingEmbeddedManifestIsCurrent()
        {
            WriteManifest("com.example.embedded", "2.0.0");

            var evidence = LocalPackageMetadataInspector.Inspect("Embedded", m_root, "com.example.embedded", "2.0.0");

            Assert.That(evidence.Status, Is.EqualTo("current"));
            Assert.That(evidence.IsLocal, Is.True);
        }

        [Test]
        public void LocalVersionMismatchIsStalePackageResolution()
        {
            WriteManifest("com.example.local", "1.2.4");

            var evidence = LocalPackageMetadataInspector.Inspect("Local", m_root, "com.example.local", "1.2.3");

            Assert.That(evidence.Status, Is.EqualTo("stale_package_resolution"));
            Assert.That(evidence.UnityReportedVersion, Is.EqualTo("1.2.3"));
            Assert.That(evidence.LocalManifestVersion, Is.EqualTo("1.2.4"));
            Assert.That(evidence.NameMatches, Is.True);
            Assert.That(evidence.VersionMatches, Is.False);
            Assert.That(evidence.IsStale, Is.True);
        }

        [Test]
        public void LocalNameMismatchIsStalePackageResolution()
        {
            WriteManifest("com.example.other", "1.2.3");

            var evidence = LocalPackageMetadataInspector.Inspect("Local", m_root, "com.example.local", "1.2.3");

            Assert.That(evidence.Status, Is.EqualTo("stale_package_resolution"));
            Assert.That(evidence.NameMatches, Is.False);
            Assert.That(evidence.VersionMatches, Is.True);
        }

        [TestCase("{not-json", "local_manifest_invalid")]
        [TestCase("{}", "local_manifest_invalid")]
        [TestCase("{\"name\":42,\"version\":\"1.2.3\"}", "local_manifest_invalid")]
        [TestCase("{\"name\":\"com.example.local\",\"version\":false}", "local_manifest_invalid")]
        [TestCase("{\"name\":\"com.example.local\",\"name\":\"com.example.other\",\"version\":\"1.2.3\"}", "local_manifest_invalid")]
        [TestCase("{\"name\":\"com.example.local\",\"version\":\"1.2.3\",\"version\":\"1.2.4\"}", "local_manifest_invalid")]
        public void InvalidLocalManifestIsUnavailable(string json, string expectedError)
        {
            File.WriteAllText(Path.Combine(m_root, "package.json"), json);

            var evidence = LocalPackageMetadataInspector.Inspect("Local", m_root, "com.example.local", "1.2.3");

            Assert.That(evidence.Status, Is.EqualTo("unavailable"));
            Assert.That(evidence.ErrorCode, Is.EqualTo(expectedError));
        }

        [Test]
        public void CapturedLocalManifestCanBeInspectedFromWorkerThread()
        {
            WriteManifest("com.example.local", "1.2.3");
            var packages = new[]
            {
                new LocalPackageMetadataInput("Local", m_root, "com.example.local", "1.2.3"),
            };

            var summary = Task.Run(() => LocalPackageMetadataInspector.InspectCapturedPackages(packages)).GetAwaiter().GetResult();

            Assert.That(summary.Status, Is.EqualTo("current"));
            Assert.That(summary.CheckedCount, Is.EqualTo(1));
            Assert.That(summary.StaleCount, Is.Zero);
            Assert.That(summary.UnavailableCount, Is.Zero);
        }

        [Test]
        public void OversizedLocalManifestIsUnavailable()
        {
            File.WriteAllText(Path.Combine(m_root, "package.json"), new string('x', 65537));

            var evidence = LocalPackageMetadataInspector.Inspect("Local", m_root, "com.example.local", "1.2.3");

            Assert.That(evidence.Status, Is.EqualTo("unavailable"));
            Assert.That(evidence.ErrorCode, Is.EqualTo("local_manifest_size_invalid"));
        }

        [Test]
        public void NonLocalPackageDoesNotReadManifest()
        {
            var missingRoot = Path.Combine(m_root, "missing");

            var evidence = LocalPackageMetadataInspector.Inspect("Registry", missingRoot, "com.example.registry", "1.2.3");

            Assert.That(evidence.Status, Is.EqualTo("not_applicable"));
            Assert.That(evidence.IsLocal, Is.False);
        }

        [Test]
        public void MissingLocalManifestIsUnavailable()
        {
            var evidence = LocalPackageMetadataInspector.Inspect("Local", m_root, "com.example.local", "1.2.3");

            Assert.That(evidence.Status, Is.EqualTo("unavailable"));
            Assert.That(evidence.ErrorCode, Is.EqualTo("local_manifest_missing"));
        }

        [Test]
        public void QueryTruncationIsUnavailableWithoutInventingAnUnavailablePackage()
        {
            var summary = LocalPackageMetadataSummary.FromEvidence(new LocalPackageMetadataEvidence[0], true, false);

            Assert.That(summary.Status, Is.EqualTo("unavailable"));
            Assert.That(summary.UnavailableCount, Is.Zero);
            Assert.That(summary.QueryTruncated, Is.True);
            Assert.That(summary.InspectionTruncated, Is.False);
            Assert.That(summary.Truncated, Is.True);
        }

        void WriteManifest(string packageName, string version)
        {
            File.WriteAllText(Path.Combine(m_root, "package.json"), "{\"name\":\"" + packageName + "\",\"version\":\"" + version + "\"}");
        }
    }
}
