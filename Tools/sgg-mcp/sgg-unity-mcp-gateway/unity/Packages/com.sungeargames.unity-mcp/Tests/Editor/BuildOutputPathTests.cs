using System;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class BuildOutputPathTests
    {
        [Test]
        public void ExternalBuildOutputRootRequiresSafeAbsolutePathOutsideProject()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows path contract");
            var requested = Path.Combine(Path.GetTempPath(), "SGGUnityMcpBuilds", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(requested);
            try
            {
                Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot(requested, out var normalized, out var error), Is.True, error);
                Assert.That(normalized, Is.EqualTo(Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
                Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot("relative/builds", out _, out _), Is.False);
                Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot(Path.GetPathRoot(requested), out _, out _), Is.False);
                Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot(Path.Combine(ProjectPaths.ProjectRoot, "BuildArtifacts"), out _, out _), Is.False);
            }
            finally
            {
                Directory.Delete(requested, true);
            }
        }

        [Test]
        public void ExternalBuildOutputRootRejectsExistingFiles()
        {
            var existingFile = Path.GetTempFileName();
            try
            {
                Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot(existingFile, out _, out _), Is.False);
            }
            finally
            {
                File.Delete(existingFile);
            }
        }

        [Test]
        public void ExternalBuildOutputRootRejectsUncAndUnsafeWindowsSegments()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows path contract");

            Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot("\\\\server\\share\\builds", out _, out _), Is.False);
            Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot("C:\\builds\\CON", out _, out _), Is.False);
            Assert.That(BuildTestAutomationCommands.TryNormalizeExternalBuildOutputRoot("C:\\builds\\trailing. ", out _, out _), Is.False);
        }

        [Test]
        public void ExternalBuildReceiptPreservesConfirmedRootAndDottedBuildName()
        {
            if (Path.DirectorySeparatorChar != '\\') Assert.Ignore("Windows path contract");
            const string operationId = "build-20260727-120000-deadbeef";
            const string artifactPath = "Library/SGGUnityMcp/job-artifacts/build-runs/build-20260727-120000-deadbeef.json";
            var outputRoot = Path.Combine(Path.GetTempPath(), "SGGUnityMcpBuilds", Guid.NewGuid().ToString("N"));
            var outputPath = Path.Combine(outputRoot, operationId, "Game.v1.exe");
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllBytes(outputPath, new byte[] { 1 });
            var content = "{\"artifact_schema_version\":2,\"operation_id\":\"" + operationId
                + "\",\"status\":\"completed\",\"success\":true,\"result\":\"Succeeded\",\"artifact_path\":\"" + artifactPath
                + "\",\"build_target\":\"StandaloneWindows64\",\"build_name\":\"Game.v1\",\"build_output_path\":" + JsonUtil.String(outputPath)
                + ",\"build_output_root\":" + JsonUtil.String(outputRoot)
                + ",\"output_selection\":\"caller_absolute_root\",\"development\":true,\"total_errors\":0,\"total_warnings\":3,\"output_exists\":true,\"output_kind\":\"file\",\"terminal_evidence_authoritative\":true}";

            try
            {
                var normalized = BuildTestAutomationCommands.NormalizePersistedBuildRunArtifactForRead(content, operationId, artifactPath);

                Assert.That(normalized, Does.Contain("\"output_selection\":\"caller_absolute_root\""));
                Assert.That(normalized, Does.Contain("\"build_name\":\"Game.v1\""));
                Assert.That(normalized, Does.Contain("\"output_exists\":true"));
                Assert.That(normalized, Does.Contain("\"success\":true"));
                Assert.That(normalized, Does.Contain("\"receipt_consistency\":\"consistent\""));
                Assert.That(normalized, Does.Not.Contain("artifact.metadata"));
            }
            finally
            {
                Directory.Delete(outputRoot, true);
            }
        }

        [Test]
        public void PersistedSucceededReceiptWithErrorsIsDowngradedWithoutRewritingRawResult()
        {
            const string operationId = "build-20260830-082626-ac856c6a";
            const string artifactPath = "Library/SGGUnityMcp/job-artifacts/build-runs/build-20260830-082626-ac856c6a.json";
            var content = "{\"artifact_schema_version\":2,\"operation_id\":\"" + operationId
                + "\",\"status\":\"completed\",\"success\":true,\"result\":\"Succeeded\",\"artifact_path\":\"" + artifactPath
                + "\",\"build_target\":\"StandaloneWindows64\",\"build_name\":\"Game\",\"build_output_path\":\"BuildArtifacts/SGGUnityMcp/" + operationId
                + "/Game.exe\",\"build_output_root\":\"BuildArtifacts/SGGUnityMcp\",\"output_selection\":\"project_default\",\"total_errors\":14,\"total_warnings\":2,\"output_exists\":true,\"output_kind\":\"file\",\"terminal_evidence_authoritative\":true}";

            var normalized = BuildTestAutomationCommands.NormalizePersistedBuildRunArtifactForRead(content, operationId, artifactPath);

            Assert.That(normalized, Does.Contain("\"success\":false"));
            Assert.That(normalized, Does.Contain("\"persisted_success_claim\":true"));
            Assert.That(normalized, Does.Contain("\"result\":\"Succeeded\""));
            Assert.That(normalized, Does.Contain("\"receipt_consistency\":\"conflict\""));
            Assert.That(normalized, Does.Contain("\"receipt_consistency_conflict\":\"nonzero_total_errors\""));
            Assert.That(normalized, Does.Contain("\"receipt_conflicts\":[\"nonzero_total_errors\"]"));
            Assert.That(normalized, Does.Contain("\"failed\":true"));
            Assert.That(normalized, Does.Contain("logs.console.query"));
            Assert.That(content, Does.Contain("\"success\":true"));
        }

        [Test]
        public void PersistedFalseSuccessClaimIsNeverUpgraded()
        {
            const string operationId = "build-20260830-090000-deadbeef";
            const string artifactPath = "Library/SGGUnityMcp/job-artifacts/build-runs/build-20260830-090000-deadbeef.json";
            var content = "{\"artifact_schema_version\":2,\"operation_id\":\"" + operationId
                + "\",\"status\":\"completed\",\"success\":false,\"result\":\"Succeeded\",\"artifact_path\":\"" + artifactPath
                + "\",\"build_target\":\"StandaloneWindows64\",\"build_name\":\"Game\",\"build_output_path\":\"BuildArtifacts/SGGUnityMcp/" + operationId
                + "/Game.exe\",\"build_output_root\":\"BuildArtifacts/SGGUnityMcp\",\"output_selection\":\"project_default\",\"total_errors\":0,\"total_warnings\":4,\"output_exists\":true,\"output_kind\":\"file\",\"terminal_evidence_authoritative\":true}";

            var normalized = BuildTestAutomationCommands.NormalizePersistedBuildRunArtifactForRead(content, operationId, artifactPath);

            Assert.That(normalized, Does.Contain("\"success\":false"));
            Assert.That(normalized, Does.Not.Contain("\"success\":true"));
            Assert.That(normalized, Does.Contain("\"receipt_consistency\":\"consistent\""));
        }

    }
}
