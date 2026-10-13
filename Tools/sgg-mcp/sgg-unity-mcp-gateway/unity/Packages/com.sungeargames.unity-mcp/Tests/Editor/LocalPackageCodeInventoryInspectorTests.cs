using System;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor.PackageManager;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class LocalPackageCodeInventoryInspectorTests
    {
        string m_root;

        [SetUp]
        public void SetUp()
        {
            m_root = Path.Combine(Path.GetTempPath(), "sgg-unity-mcp-local-package-inventory-" + Guid.NewGuid().ToString("N"));
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
        public void MatchingSourceAndAssetDatabaseCodePathsAreCurrent()
        {
            Write("Runtime/Weather.cs", "public sealed class Weather {}");
            Write("Runtime/SGG.Weather.asmdef", "{}");

            var snapshot = Inspect(
                new[] { "Packages/com.example.weather/Runtime/Weather.cs", "Packages/com.example.weather/Runtime/SGG.Weather.asmdef" });

            Assert.That(snapshot.Status, Is.EqualTo("current"));
            Assert.That(snapshot.LocalPackageCount, Is.EqualTo(1));
            Assert.That(snapshot.SourceFileCount, Is.EqualTo(2));
            Assert.That(snapshot.AssetDatabaseFileCount, Is.EqualTo(2));
            Assert.That(snapshot.MissingAssetFileCount, Is.Zero);
            Assert.That(snapshot.UnexpectedAssetFileCount, Is.Zero);
            Assert.That(snapshot.SourceFingerprint, Has.Length.EqualTo(64));
            Assert.That(snapshot.AssetDatabaseFingerprint, Is.EqualTo(snapshot.SourceFingerprint));
        }

        [Test]
        public void NewSourceCodeMissingFromAssetDatabaseIsStale()
        {
            Write("Runtime/Existing.cs", "public sealed class Existing {}");
            Write("Runtime/NewContract.cs", "public sealed class NewContract {}");

            var snapshot = Inspect(new[] { "Packages/com.example.weather/Runtime/Existing.cs" });

            Assert.That(snapshot.Status, Is.EqualTo("stale"));
            Assert.That(snapshot.MissingAssetFileCount, Is.EqualTo(1));
            Assert.That(snapshot.UnexpectedAssetFileCount, Is.Zero);
            Assert.That(snapshot.MismatchPackage, Is.EqualTo("com.example.weather"));
            Assert.That(snapshot.MismatchDetails, Has.Length.EqualTo(1));
            Assert.That(snapshot.MismatchDetails[0].Kind, Is.EqualTo("missing_asset"));
            Assert.That(snapshot.MismatchDetails[0].Path, Is.EqualTo("Packages/com.example.weather/Runtime/NewContract.cs"));
            Assert.That(snapshot.MismatchDetails[0].MetadataReason, Is.EqualTo("meta_missing"));
            Assert.That(snapshot.MismatchDetailsTruncated, Is.False);
        }

        [TestCase(31)]
        [TestCase(33)]
        public void MissingAssetPathReportsMalformedGuidLengthWithoutReflectingGuid(int guidLength)
        {
            Write("Runtime/Broken.cs", "public sealed class Broken {}");
            WriteMeta("Runtime/Broken.cs", new string('a', guidLength));

            var snapshot = Inspect(new string[0]);
            var state = new BridgeRuntimeState();
            state.SetLocalPackageCodeInventory(snapshot);
            var diagnostics = state.LocalPackageCodeInventoryDiagnosticsJson();

            Assert.That(snapshot.Status, Is.EqualTo("stale"));
            Assert.That(snapshot.MismatchDetails, Has.Length.EqualTo(1));
            Assert.That(snapshot.MismatchDetails[0].MetadataReason, Is.EqualTo("guid_invalid_length"));
            Assert.That(snapshot.MismatchDetails[0].ObservedGuidLength, Is.EqualTo(guidLength));
            Assert.That(diagnostics, Does.Contain("sgg.unity_mcp.local_package_inventory_diagnostics.v1"));
            Assert.That(diagnostics, Does.Contain("\"path\":\"Packages/com.example.weather/Runtime/Broken.cs\""));
            Assert.That(diagnostics, Does.Contain("\"reason\":\"guid_invalid_length\""));
            Assert.That(diagnostics, Does.Contain("\"observed_guid_length\":" + guidLength));
            Assert.That(diagnostics, Does.Not.Contain(new string('a', guidLength)));
            Assert.That(diagnostics, Does.Contain("\"automatic_repair_attempted\":false"));
        }

        [Test]
        public void NestedImporterGuidDoesNotMasqueradeAsTopLevelMetadataGuid()
        {
            Write("Runtime/Broken.cs", "public sealed class Broken {}");
            Write("Runtime/Broken.cs.meta", "fileFormatVersion: 2\nMonoImporter:\n  guid: " + new string('a', 32) + "\n");

            var snapshot = Inspect(new string[0]);

            Assert.That(snapshot.Status, Is.EqualTo("stale"), snapshot.FailureReason);
            Assert.That(snapshot.MismatchDetails[0].MetadataReason, Is.EqualTo("guid_missing"));
        }

        [Test]
        public void OversizedMetadataHasBoundedReasonWithoutReadingGuid()
        {
            Write("Runtime/Broken.cs", "public sealed class Broken {}");
            Write("Runtime/Broken.cs.meta", "guid: " + new string('a', 65536));

            var snapshot = Inspect(new string[0]);

            Assert.That(snapshot.Status, Is.EqualTo("stale"), snapshot.FailureReason);
            Assert.That(snapshot.MismatchDetails[0].MetadataReason, Is.EqualTo("meta_size_limit_exceeded"));
            Assert.That(snapshot.MismatchDetails[0].ObservedGuidLength, Is.Zero);
        }

        [Test]
        public void LongGuidDoesNotReportAClampedLengthAsExact()
        {
            Write("Runtime/Broken.cs", "public sealed class Broken {}");
            WriteMeta("Runtime/Broken.cs", new string('a', 5000));
            var state = new BridgeRuntimeState();
            state.SetLocalPackageCodeInventory(Inspect(new string[0]));

            var diagnostics = state.LocalPackageCodeInventoryDiagnosticsJson();
            Assert.That(diagnostics, Does.Contain("\"reason\":\"guid_invalid_length\""));
            Assert.That(diagnostics, Does.Not.Contain("observed_guid_length"));
        }

        [TestCase("Packages/com.example.weather/../secret/Hidden.cs")]
        [TestCase("/Packages/com.example.weather/Runtime/Outside.cs")]
        [TestCase("Packages/com.example.weather/Runtime/Bad:Name.cs")]
        public void UnsafeAssetDatabasePathFailsClosedWithoutPathDisclosure(string path)
        {
            Write("Runtime/Weather.cs", "public sealed class Weather {}");

            var snapshot = Inspect(new[] { path });

            Assert.That(snapshot.Status, Is.EqualTo("unavailable"));
            Assert.That(snapshot.FailureReason, Is.EqualTo("path_security_rejected"));
            Assert.That(snapshot.MismatchDetails, Is.Empty);
            Assert.That(snapshot.FailureReason, Does.Not.Contain(path));
        }

        [Test]
        public void MatchingRawCountsCannotHideMissingCompilerSourcePaths()
        {
            Write("Runtime/Existing.cs", "public sealed class Existing {}");
            Write("Runtime/NewContract.cs", "public sealed class NewContract {}");

            var snapshot = LocalPackageCodeInventoryInspector.InspectForTests(
                new[] { Input(PackageSource.Local.ToString(), "com.example.weather", m_root) },
                new[] { "Packages/com.example.weather/Runtime/Existing.cs", "Packages/com.example.weather/Runtime/NewContract.cs" },
                5000,
                16384,
                new[] { "Packages/com.example.weather/Runtime/Existing.cs", "Packages/com.example.weather/Runtime/Removed.cs" });

            Assert.That(snapshot.Status, Is.EqualTo("stale"));
            Assert.That(snapshot.SourceFileCount, Is.EqualTo(snapshot.AssetDatabaseFileCount));
            Assert.That(snapshot.CompilerSourceFileCount, Is.EqualTo(snapshot.SourceFileCount));
            Assert.That(snapshot.MissingCompilerSourceFileCount, Is.EqualTo(1));
            Assert.That(snapshot.UnexpectedCompilerSourceFileCount, Is.EqualTo(1));
            Assert.That(snapshot.MismatchDetails, Has.Length.EqualTo(2));
            Assert.That(snapshot.MismatchDetails[0].Kind, Is.EqualTo("missing_compiler_source"));
            Assert.That(snapshot.MismatchDetails[0].Path, Is.EqualTo("Packages/com.example.weather/Runtime/NewContract.cs"));
            Assert.That(snapshot.MismatchDetails[1].Kind, Is.EqualTo("unexpected_compiler_source"));
            Assert.That(snapshot.MismatchDetails[1].Path, Is.EqualTo("Packages/com.example.weather/Runtime/Removed.cs"));
        }

        [Test]
        public void CompilerSourceInclusionConvergenceMakesInventoryCurrent()
        {
            Write("Runtime/Existing.cs", "public sealed class Existing {}");
            Write("Runtime/NewContract.cs", "public sealed class NewContract {}");
            var paths = new[] { "Packages/com.example.weather/Runtime/Existing.cs", "Packages/com.example.weather/Runtime/NewContract.cs" };

            var snapshot = LocalPackageCodeInventoryInspector.InspectForTests(
                new[] { Input(PackageSource.Local.ToString(), "com.example.weather", m_root) },
                paths,
                5000,
                16384,
                paths);

            Assert.That(snapshot.Status, Is.EqualTo("current"));
            Assert.That(snapshot.CompilerSourceFileCount, Is.EqualTo(2));
            Assert.That(snapshot.MissingCompilerSourceFileCount, Is.Zero);
            Assert.That(snapshot.CompilerSourceFingerprint, Has.Length.EqualTo(64));
        }

        [Test]
        public void SourcesFromInactiveAssembliesDoNotBlockCurrentInventory()
        {
            Write("Runtime/Weather.cs", "public sealed class Weather {}");
            Write("Tests/Editor/WeatherTests.cs", "public sealed class WeatherTests {}");
            var paths = new[]
            {
                "Packages/com.example.weather/Runtime/Weather.cs",
                "Packages/com.example.weather/Tests/Editor/WeatherTests.cs",
            };

            var snapshot = LocalPackageCodeInventoryInspector.InspectForTests(
                new[] { Input(PackageSource.Local.ToString(), "com.example.weather", m_root) },
                paths,
                5000,
                16384,
                new[] { paths[0] },
                new[] { paths[0] });

            Assert.That(snapshot.Status, Is.EqualTo("current"));
            Assert.That(snapshot.CompilerSourceFileCount, Is.EqualTo(1));
            Assert.That(snapshot.MissingCompilerSourceFileCount, Is.Zero);
        }

        [Test]
        public void AssetDatabaseCodePathMissingFromSourceIsStale()
        {
            Write("Runtime/Existing.cs", "public sealed class Existing {}");

            var snapshot = Inspect(new[]
            {
                "Packages/com.example.weather/Runtime/Existing.cs",
                "Packages/com.example.weather/Runtime/Removed.cs",
            });

            Assert.That(snapshot.Status, Is.EqualTo("stale"), snapshot.FailureReason);
            Assert.That(snapshot.MissingAssetFileCount, Is.Zero);
            Assert.That(snapshot.UnexpectedAssetFileCount, Is.EqualTo(1));
            Assert.That(snapshot.MismatchDetails, Has.Length.EqualTo(2));
            Assert.That(snapshot.MismatchDetails[0].Kind, Is.EqualTo("unexpected_asset"));
            Assert.That(snapshot.MismatchDetails[0].Path, Is.EqualTo("Packages/com.example.weather/Runtime/Removed.cs"));
            Assert.That(snapshot.MismatchDetails[1].Kind, Is.EqualTo("unexpected_compiler_source"));
            Assert.That(snapshot.MismatchDetails[1].Path, Is.EqualTo("Packages/com.example.weather/Runtime/Removed.cs"));
        }

        [Test]
        public void MismatchDetailsAreDeterministicallyBounded()
        {
            for (var index = 0; index < 17; index++) Write("Runtime/File" + index.ToString("D2") + ".cs", "public sealed class File" + index + " {}");

            var snapshot = Inspect(new string[0]);

            Assert.That(snapshot.MissingAssetFileCount, Is.EqualTo(17));
            Assert.That(snapshot.MismatchDetailTotalCount, Is.EqualTo(17));
            Assert.That(snapshot.MismatchDetails, Has.Length.EqualTo(16));
            Assert.That(snapshot.MismatchDetailsTruncated, Is.True);
            Assert.That(snapshot.MismatchDetails[0].Path, Does.EndWith("/File00.cs"));
            Assert.That(snapshot.MismatchDetails[15].Path, Does.EndWith("/File15.cs"));
        }

        [Test]
        public void SeparateMetadataRepairAndAssetImportConvergeWithoutInventoryRelaxation()
        {
            var assetPath = "Packages/com.example.weather/Runtime/Broken.cs";
            Write("Runtime/Broken.cs", "public sealed class Broken {}");
            WriteMeta("Runtime/Broken.cs", new string('a', 33));
            var stale = Inspect(new string[0]);

            WriteMeta("Runtime/Broken.cs", new string('b', 32));
            var current = Inspect(new[] { assetPath });

            Assert.That(stale.Status, Is.EqualTo("stale"));
            Assert.That(stale.MismatchDetails[0].MetadataReason, Is.EqualTo("guid_invalid_length"));
            Assert.That(current.Status, Is.EqualTo("current"));
            Assert.That(current.MissingAssetFileCount, Is.Zero);
            Assert.That(current.MismatchDetailTotalCount, Is.Zero);
            Assert.That(current.MismatchDetails, Is.Empty);
        }

        [Test]
        public void ContentChangeUpdatesSourceStateWithoutInventingInventoryMismatch()
        {
            var path = Write("Runtime/Weather.cs", "public sealed class Weather {}");
            var before = Inspect(new[] { "Packages/com.example.weather/Runtime/Weather.cs" });
            File.WriteAllText(path, "public sealed class Weather { public int Generation; }");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            var after = Inspect(new[] { "Packages/com.example.weather/Runtime/Weather.cs" });

            Assert.That(after.Status, Is.EqualTo("current"));
            Assert.That(after.SourceFingerprint, Is.EqualTo(before.SourceFingerprint));
            Assert.That(after.SourceStateFingerprint, Is.Not.EqualTo(before.SourceStateFingerprint));
        }

        [Test]
        public void NonCodeFilesAndNonLocalPackagesDoNotEnterInventory()
        {
            Write("Runtime/README.md", "ignored");
            var packages = new[]
            {
                Input(PackageSource.Local.ToString(), "com.example.weather", m_root),
                Input(PackageSource.Registry.ToString(), "com.example.registry", m_root),
            };

            var snapshot = LocalPackageCodeInventoryInspector.InspectForTests(packages, new[]
            {
                "Packages/com.example.weather/Runtime/README.md",
                "Packages/com.example.registry/Runtime/Registry.cs",
            });

            Assert.That(snapshot.Status, Is.EqualTo("current"));
            Assert.That(snapshot.LocalPackageCount, Is.EqualTo(1));
            Assert.That(snapshot.SourceFileCount, Is.Zero);
            Assert.That(snapshot.AssetDatabaseFileCount, Is.Zero);
        }

        [Test]
        public void UnityIgnoredTildeDirectoriesDoNotEnterInventory()
        {
            Write("Runtime/Weather.cs", "public sealed class Weather {}");
            Write("Samples~/BasicUsage/Sample.cs", "public sealed class Sample {}");

            var snapshot = Inspect(new[] { "Packages/com.example.weather/Runtime/Weather.cs" });

            Assert.That(snapshot.Status, Is.EqualTo("current"));
            Assert.That(snapshot.SourceFileCount, Is.EqualTo(1));
            Assert.That(snapshot.AssetDatabaseFileCount, Is.EqualTo(1));
        }

        [Test]
        public void BoundedFileLimitFailsClosedWithoutHostPathDisclosure()
        {
            Write("Runtime/One.cs", "public sealed class One {}");
            Write("Runtime/Two.cs", "public sealed class Two {}");

            var snapshot = LocalPackageCodeInventoryInspector.InspectForTests(
                new[] { Input(PackageSource.Local.ToString(), "com.example.weather", m_root) },
                new string[0],
                5000,
                1);

            Assert.That(snapshot.Status, Is.EqualTo("unavailable"));
            Assert.That(snapshot.FailureReason, Is.EqualTo("file_limit_exceeded"));
            Assert.That(snapshot.MismatchPackage, Is.Empty);
            Assert.That(snapshot.FailureReason, Does.Not.Contain(m_root));
        }

        [Test]
        public void MissingResolvedRootFailsClosed()
        {
            var snapshot = LocalPackageCodeInventoryInspector.InspectForTests(
                new[] { Input(PackageSource.Local.ToString(), "com.example.weather", Path.Combine(m_root, "missing")) },
                new string[0]);

            Assert.That(snapshot.Status, Is.EqualTo("unavailable"));
            Assert.That(snapshot.FailureReason, Is.EqualTo("path_security_rejected"));
        }

        [Test]
        public void UnavailableSnapshotCountsStayWithinBridgeProtocolBounds()
        {
            var snapshot = LocalPackageCodeInventorySnapshot.Unavailable(
                "package_limit_exceeded",
                int.MaxValue,
                int.MaxValue,
                int.MaxValue);

            Assert.That(snapshot.LocalPackageCount, Is.EqualTo(64));
            Assert.That(snapshot.SourceFileCount, Is.EqualTo(16384));
            Assert.That(snapshot.AssetDatabaseFileCount, Is.EqualTo(16384));
        }

        [Test]
        public void ExpiredBudgetFailsClosed()
        {
            Write("Runtime/Weather.cs", "public sealed class Weather {}");

            var snapshot = LocalPackageCodeInventoryInspector.InspectForTests(
                new[] { Input(PackageSource.Local.ToString(), "com.example.weather", m_root) },
                new[] { "Packages/com.example.weather/Runtime/Weather.cs" },
                -1);

            Assert.That(snapshot.Status, Is.EqualTo("unavailable"));
            Assert.That(snapshot.FailureReason, Is.EqualTo("budget_exhausted"));
        }

        [Test]
        public void ValidationActionsDoNotSuggestTestsForUntrustedInventory()
        {
            var state = new BridgeRuntimeState();
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Unavailable("read_exception"));

            var json = BuiltInCommands.ValidationStatusJson(state, "after_edit", true, false, 0, 1000);

            Assert.That(json, Does.Contain("inspect_local_package_code_inventory"));
            Assert.That(json, Does.Not.Contain("run_tests_or_target_validation"));
        }

        [Test]
        public void RuntimeStateStartsPackageValidationFailClosed()
        {
            var state = new BridgeRuntimeState();

            Assert.That(state.PackageRefreshPending, Is.True);
            Assert.That(state.PackageUpdateStatus, Is.EqualTo("unavailable"));
            Assert.That(state.LocalPackageCodeInventoryUnavailable, Is.True);
            Assert.That(state.PackageUpdateJson(), Does.Contain("\"refresh_pending\":true"));
            Assert.That(state.PackageUpdateJson(), Does.Contain("\"failure_reason\":\"not_scanned\""));
        }

        LocalPackageCodeInventorySnapshot Inspect(string[] assetPaths)
        {
            return LocalPackageCodeInventoryInspector.InspectForTests(
                new[] { Input(PackageSource.Local.ToString(), "com.example.weather", m_root) },
                assetPaths);
        }

        static LocalPackageCodeInventoryInput Input(string source, string name, string resolvedPath)
        {
            return new LocalPackageCodeInventoryInput(source, name, resolvedPath);
        }

        string Write(string relativePath, string content)
        {
            var path = Path.Combine(m_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        void WriteMeta(string sourceRelativePath, string guid)
        {
            Write(sourceRelativePath + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
        }
    }
}
