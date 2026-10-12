using System;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor.PackageManager;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class LocalPackageScriptImportCommandsTests
    {
        string m_root;

        [SetUp]
        public void SetUp()
        {
            m_root = Path.Combine(Path.GetTempPath(), "sgg-unity-mcp-local-package-import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(m_root, "Runtime"));
            File.WriteAllText(Path.Combine(m_root, "Runtime", "Weather.cs"), "public sealed class Weather {}");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(m_root)) Directory.Delete(m_root, true);
        }

        [TestCase(PackageSource.Local)]
        [TestCase(PackageSource.Embedded)]
        public void ExactMutablePackageScriptProducesBoundedPrecondition(PackageSource source)
        {
            var success = LocalPackageScriptImportCommands.TryResolveTargetForTests(
                Input(source, "com.example.weather", m_root),
                "Packages/com.example.weather/Runtime/Weather.cs",
                out var sha256,
                out var failure);

            Assert.That(success, Is.True, failure);
            Assert.That(sha256, Has.Length.EqualTo(64));
        }

        [Test]
        public void RegistryPackageFailsClosed()
        {
            var success = LocalPackageScriptImportCommands.TryResolveTargetForTests(
                Input(PackageSource.Registry, "com.example.weather", m_root),
                "Packages/com.example.weather/Runtime/Weather.cs",
                out _,
                out var failure);

            Assert.That(success, Is.False);
            Assert.That(failure, Is.EqualTo("unsupported_package_source"));
        }

        [TestCase("Packages/com.example.other/Runtime/Weather.cs")]
        [TestCase("Packages/com.example.weather/Runtime/Weather.asmdef")]
        [TestCase("Packages/com.example.weather/../Weather.cs")]
        public void MismatchedOrUnsupportedPathFailsClosed(string packagePath)
        {
            var success = LocalPackageScriptImportCommands.TryResolveTargetForTests(
                Input(PackageSource.Local, "com.example.weather", m_root),
                packagePath,
                out _,
                out var failure);

            Assert.That(success, Is.False);
            Assert.That(failure, Is.Not.Empty);
        }

        static LocalPackageCodeInventoryInput Input(PackageSource source, string name, string root)
        {
            return new LocalPackageCodeInventoryInput(source.ToString(), name, root);
        }
    }
}
