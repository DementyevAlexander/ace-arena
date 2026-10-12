using System;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class PrefabYamlIntegrityTests
    {
        const string FixtureAssetPath = "Assets/Prefabs/StaleTransformChild.prefab";
        const string FixtureYaml =
            "%YAML 1.1\n"
            + "%TAG !u! tag:unity3d.com,2011:\n"
            + "--- !u!224 &100100\n"
            + "RectTransform:\n"
            + "  m_GameObject: {fileID: 100101}\n"
            + "  m_Father: {fileID: 0}\n"
            + "  m_Children:\n"
            + "  - {fileID: 100200}\n"
            + "--- !u!1 &100101\n"
            + "GameObject:\n"
            + "  m_Name: Canvas\n"
            + "  m_Component:\n"
            + "  - component: {fileID: 100100}\n"
            + "--- !u!224 &100200\n"
            + "RectTransform:\n"
            + "  m_GameObject: {fileID: 100201}\n"
            + "  m_Father: {fileID: 100100}\n"
            + "  m_Children:\n"
            + "  - {fileID: 999999}\n"
            + "--- !u!1 &100201\n"
            + "GameObject:\n"
            + "  m_Name: Panel\n"
            + "  m_Component:\n"
            + "  - component: {fileID: 100200}\n"
            + "--- !u!1001 &100300\n"
            + "PrefabInstance:\n"
            + "  m_SourcePrefab: {fileID: 100100000, guid: 0123456789abcdef0123456789abcdef, type: 3}\n";

        [Test]
        public void FixtureReportsMissingTransformFileIdWithParentPathAndReferenceLine()
        {
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, FixtureYaml, 64, 262144);
            var json = result.ToJson(false, "raw importer exception");

            Assert.That(result.FileExists, Is.True);
            Assert.That(result.AnalysisComplete, Is.True);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
            Assert.That(result.IssueCount, Is.EqualTo(1));
            Assert.That(json, Does.Contain("\"schema_version\":1"));
            Assert.That(json, Does.Contain("\"read_only\":true"));
            Assert.That(json, Does.Contain("\"missing_transform_file_id\""));
            Assert.That(json, Does.Contain("\"parent_transform_file_id\":\"100200\""));
            Assert.That(json, Does.Contain("\"parent_transform_type\":\"RectTransform\""));
            Assert.That(json, Does.Contain("\"parent_path\":\"/Canvas/Panel\""));
            Assert.That(json, Does.Contain("\"stale_child_file_id\":\"999999\""));
            Assert.That(json, Does.Contain("\"child_reference_line\":19"));
            Assert.That(json, Does.Not.Contain("raw importer exception"));
            Assert.That(json, Does.Contain("\"asset_load_error\":\"asset_load_failed\""));
        }

        [Test]
        public void KnownNonTransformChildProducesChildNotTransformIssue()
        {
            var yaml = FixtureYaml.Replace("999999", "100201");
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 64, 262144);
            var json = result.ToJson(true, "ignored");

            Assert.That(result.AnalysisComplete, Is.True);
            Assert.That(result.IssueCount, Is.EqualTo(1));
            Assert.That(json, Does.Contain("\"kind\":\"child_not_transform\""));
            Assert.That(json, Does.Contain("\"stale_child_file_id\":\"100201\""));
            Assert.That(json, Does.Contain("\"actual_class_id\":1"));
            Assert.That(json, Does.Contain("\"asset_load_error\":\"\""));
        }

        [Test]
        public void IssueLimitMarksDiagnosticOutputTruncated()
        {
            var children = new StringBuilder();
            for (var index = 0; index < 8; index++)
            {
                children.Append("  - {fileID: ").Append(900000 + index).Append("}\n");
            }

            var yaml = FixtureYaml.Replace("  - {fileID: 999999}\n", children.ToString());
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 1, 262144);

            Assert.That(result.IssueCount, Is.EqualTo(1));
            Assert.That(result.HasIssues, Is.True);
            Assert.That(result.ScanTruncated, Is.False);
            Assert.That(result.IssuesTruncated, Is.True);
            Assert.That(result.AnalysisComplete, Is.True);
        }

        [Test]
        public void ByteTruncationDoesNotAuthoritativelyReportMissingTransformFileId()
        {
            var maxScanBytes = Encoding.UTF8.GetByteCount(FixtureYaml) - 1;
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, FixtureYaml, 64, maxScanBytes);
            var json = result.ToJson(false, "asset_import_failed");

            Assert.That(result.ScanTruncated, Is.True);
            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(json, Does.Not.Contain("missing_transform_file_id"));
            Assert.That(json, Does.Contain("\"asset_load_error\":\"asset_import_failed\""));
        }

        [Test]
        public void PrefabInstanceGuidEvidenceIsBoundedAndReadOnly()
        {
            const string guid = "0123456789abcdef0123456789abcdef";
            var json = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, FixtureYaml, 64, 262144).ToJson(true, string.Empty);

            Assert.That(json, Does.Contain("\"suspected_nested_prefab_evidence\":[{"));
            Assert.That(json, Does.Contain("\"source_guid\":\"" + guid + "\""));
            Assert.That(json, Does.Contain("\"read_only\":true"));
        }

        [Test]
        public void ImportDiagnosticRequiresExactTransformChildPhraseAndRedactsMessages()
        {
            const string rawMessage = "Transform child [0] of [Assets/Prefabs/StaleTransformChild.prefab] can't be loaded";
            var json = PrefabYamlIntegrity.PrefabImportDiagnosticsJson(new[] { rawMessage });

            Assert.That(json, Does.Contain("\"has_transform_child_cant_be_loaded\":true"));
            Assert.That(json, Does.Contain("\"error_code\":\"asset_import_failed\""));
            Assert.That(json, Does.Contain("\"asset_paths\":[\"Assets/Prefabs/StaleTransformChild.prefab\"]"));
            Assert.That(json, Does.Contain("\"next_valid_actions\":[\"prefab.write_risk_report\",\"prefab.edit.preview\",\"logs.console.query\"]"));
            Assert.That(json, Does.Not.Contain(rawMessage));
        }

        [Test]
        public void OrdinaryCompilerErrorsDoNotProduceImportDiagnostic()
        {
            var json = PrefabYamlIntegrity.PrefabImportDiagnosticsJson(new[]
            {
                "Assets/Scripts/Example.cs(12,3): error CS1002: ; expected",
                "error CS0246: The type or namespace name was not found"
            });

            Assert.That(json, Is.Empty);
        }

        [Test]
        public void StructurallyInvalidTextIsIncompleteAndSkipsUnityLoad()
        {
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, "not a Unity YAML prefab", 64, 262144);

            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
            Assert.That(result.ToJson(false, string.Empty), Does.Contain("\"read_error\":\"invalid_prefab_yaml\""));
        }

        [Test]
        public void PersistedImportObservationIsOperationAndTimestampBound()
        {
            var operationId = new string('a', 32);
            Assert.That(PrefabYamlIntegrity.TryBuildPrefabImportObservationPayload("Transform child cant be loaded in Assets/Prefabs/StaleTransformChild.prefab", out var payload), Is.True);
            var diagnostics = PrefabYamlIntegrity.PrefabImportDiagnosticsJsonFromObservationPayload(payload);
            var blob = ConsoleLogBuffer.PrefabImportObservationBlobForTests(operationId, 200, payload);

            Assert.That(blob, Is.Not.Empty);
            Assert.That(ConsoleLogBuffer.PrefabImportObservationJsonForTests(blob, operationId, 200), Is.EqualTo(diagnostics));
            Assert.That(ConsoleLogBuffer.PrefabImportObservationJsonForTests(blob, new string('b', 32), 200), Is.Empty);
            Assert.That(ConsoleLogBuffer.PrefabImportObservationJsonForTests(blob, operationId, 201), Is.Empty);
            Assert.That(ConsoleLogBuffer.PrefabImportObservationJsonForTests("v1|broken", operationId, 0), Is.Empty);
            var futureBlob = ConsoleLogBuffer.PrefabImportObservationBlobForTests(operationId, DateTime.UtcNow.AddSeconds(30).Ticks, payload);
            Assert.That(ConsoleLogBuffer.PrefabImportObservationJsonForTests(futureBlob, operationId, 0), Is.Empty);
            var markerSpoof = ConsoleLogBuffer.PrefabImportObservationBlobForTests(operationId, 200, "{\"has_transform_child_cant_be_loaded\":true,\"error_code\":\"asset_import_failed\"}");
            Assert.That(ConsoleLogBuffer.PrefabImportObservationJsonForTests(markerSpoof, operationId, 0), Is.Empty);
        }

        [Test]
        public void PersistedImportObservationMergesBoundedAssetPaths()
        {
            Assert.That(PrefabYamlIntegrity.TryBuildPrefabImportObservationPayload("Transform child cant be loaded in Assets/Prefabs/Canvas.prefab", out var first), Is.True);
            Assert.That(PrefabYamlIntegrity.TryBuildPrefabImportObservationPayload("Transform child can't be loaded in Assets/Prefabs/Panel.prefab", out var second), Is.True);
            var merged = PrefabYamlIntegrity.MergePrefabImportObservationPayloads(first, second);
            var diagnostics = PrefabYamlIntegrity.PrefabImportDiagnosticsJsonFromObservationPayload(merged);

            Assert.That(diagnostics, Does.Contain("Assets/Prefabs/Canvas.prefab"));
            Assert.That(diagnostics, Does.Contain("Assets/Prefabs/Panel.prefab"));
        }

        [Test]
        public void MaximumBoundedObservationPayloadSurvivesParsing()
        {
            var payload = string.Empty;
            for (var index = 0; index < 16; index++)
            {
                var path = "Assets/" + new string((char)('a' + index), 247) + "/"
                    + new string((char)('a' + index), 246) + index + ".prefab";
                Assert.That(PrefabYamlIntegrity.TryBuildPrefabImportObservationPayload("Transform child cant be loaded in " + path, out var next), Is.True);
                payload = PrefabYamlIntegrity.MergePrefabImportObservationPayloads(payload, next);
            }
            Assert.That(payload.Length, Is.GreaterThan(8192));
            var operationId = new string('c', 32);
            var blob = ConsoleLogBuffer.PrefabImportObservationBlobForTests(operationId, 200, payload);
            var diagnostics = ConsoleLogBuffer.PrefabImportObservationJsonForTests(blob, operationId, 200);

            Assert.That(diagnostics, Is.Not.Empty);
            Assert.That(diagnostics, Does.Contain(new string('p', 246) + "15.prefab"));
        }

        [Test]
        public void IncompleteStructureDoesNotEmitMissingIdIssue()
        {
            const string partial = "%YAML 1.1\n--- !u!224 &100100\nRectTransform:\n  m_GameObject: {fileID: 100101}\n  m_Father: {fileID: 0}\n  m_Children:\n  - {fileID: 999999}\n";
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, partial, 64, 262144);

            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(result.IssueCount, Is.Zero);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
        }

        [Test]
        public void StrippedNestedTransformDoesNotMakeCompletePrefabInvalid()
        {
            var yaml = FixtureYaml
                + "--- !u!4 &100400 stripped\n"
                + "Transform:\n"
                + "  m_CorrespondingSourceObject: {fileID: 400000, guid: 0123456789abcdef0123456789abcdef, type: 3}\n"
                + "  m_PrefabInstance: {fileID: 100300}\n"
                + "  m_PrefabAsset: {fileID: 0}\n";
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 64, 262144);

            Assert.That(result.AnalysisComplete, Is.True);
            Assert.That(result.IssueCount, Is.EqualTo(1));
        }

        [Test]
        public void MissingFatherTransformMakesAnalysisIncomplete()
        {
            var yaml = FixtureYaml.Replace("m_Father: {fileID: 100100}", "m_Father: {fileID: 777777}");
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 64, 262144);

            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(result.IssueCount, Is.Zero);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
        }

        [Test]
        public void MalformedChildReferenceMakesAnalysisIncomplete()
        {
            var yaml = FixtureYaml.Replace("- {fileID: 999999}", "- {guid: invalid}");
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 64, 262144);

            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(result.IssueCount, Is.Zero);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
        }

        [Test]
        public void MalformedChildFileIdSuffixMakesAnalysisIncomplete()
        {
            var yaml = FixtureYaml.Replace("- {fileID: 999999}", "- {fileID: 100200junk}");
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 64, 262144);

            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(result.IssueCount, Is.Zero);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
        }

        [Test]
        public void ChildWithDifferentFatherMakesAnalysisIncomplete()
        {
            var yaml = FixtureYaml.Replace("- {fileID: 999999}", "- {fileID: 100100}");
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 64, 262144);

            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(result.IssueCount, Is.Zero);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
        }

        [Test]
        public void FatherCycleMakesAnalysisIncomplete()
        {
            var yaml = FixtureYaml.Replace("m_Father: {fileID: 0}", "m_Father: {fileID: 100200}");
            var result = PrefabYamlIntegrity.AnalyzeYamlForTests(FixtureAssetPath, yaml, 64, 262144);

            Assert.That(result.AnalysisComplete, Is.False);
            Assert.That(result.IssueCount, Is.Zero);
            Assert.That(result.ShouldSkipUnityLoad, Is.True);
        }
    }
}
