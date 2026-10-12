using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class ExpectedScriptImportInspectionTests
    {
        [Test]
        public void ValidatesAndNormalizesBoundedProjectScriptPaths()
        {
            var result = BuiltInCommands.ReadExpectedScriptPaths(
                "{\"expected_script_paths\":[\"Assets\\\\Tests//PlayMode/Expected.cs\"]}",
                "expected_script_paths",
                out var paths);

            Assert.That(result, Is.Null);
            Assert.That(paths, Is.EqualTo(new[] { "Assets/Tests/PlayMode/Expected.cs" }));
        }

        [TestCase("Packages/com.example/Test.cs")]
        [TestCase("Assets/Tests/Test.asmdef")]
        [TestCase("Assets/../Test.cs")]
        public void RejectsUnsupportedExpectedScriptPaths(string path)
        {
            var result = BuiltInCommands.ReadExpectedScriptPaths(
                "{\"expected_script_paths\":[" + JsonUtil.String(path) + "]}",
                "expected_script_paths",
                out _);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Status, Is.EqualTo("error"));
            StringAssert.Contains("\"path\":[\"expected_script_paths\",0]", result.ErrorDetailsJson);
        }

        [Test]
        public void RejectsCaseInsensitiveDuplicateExpectedScriptPaths()
        {
            var result = BuiltInCommands.ReadExpectedScriptPaths(
                "{\"expected_script_paths\":[\"Assets/Tests/Expected.cs\",\"Assets/Tests/EXPECTED.cs\"]}",
                "expected_script_paths",
                out _);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.ErrorCode, Is.EqualTo("invalid_field_value"));
            StringAssert.Contains("\"path\":[\"expected_script_paths\",1]", result.ErrorDetailsJson);
            StringAssert.Contains("\"issue_code\":\"relational_constraint_failed\"", result.ErrorDetailsJson);
        }

        [Test]
        public void EnforcesMaximumExpectedScriptPathCount()
        {
            var entries = new string[65];
            for (var index = 0; index < entries.Length; index++)
            {
                entries[index] = JsonUtil.String("Assets/Tests/Expected" + index + ".cs");
            }

            var accepted = BuiltInCommands.ReadExpectedScriptPaths(
                "{\"expected_script_paths\":[" + string.Join(",", entries, 0, 64) + "]}",
                "expected_script_paths",
                out var acceptedPaths);
            var rejected = BuiltInCommands.ReadExpectedScriptPaths(
                "{\"expected_script_paths\":[" + string.Join(",", entries) + "]}",
                "expected_script_paths",
                out _);

            Assert.That(accepted, Is.Null);
            Assert.That(acceptedPaths.Length, Is.EqualTo(64));
            Assert.That(rejected, Is.Not.Null);
            Assert.That(rejected.ErrorCode, Is.EqualTo("invalid_field_value"));
        }

        [TestCase("null")]
        [TestCase("42")]
        [TestCase("\"Assets/Tests/Expected.cs\"")]
        [TestCase("[null]")]
        [TestCase("[\"Assets/Tests/Expected.cs\",42]")]
        public void RejectsMalformedExpectedScriptArrays(string valueJson)
        {
            var result = BuiltInCommands.ReadExpectedScriptPaths(
                "{\"expected_script_paths\":" + valueJson + "}",
                "expected_script_paths",
                out _);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.ErrorCode, Is.EqualTo("invalid_field_value"));
        }

        [Test]
        public void ReportsOnlyCallerDeclaredMissingImports()
        {
            var paths = new[] { "Assets/Tests/Imported.cs", "Assets/Tests/Missing.cs" };
            var inspection = BuiltInCommands.InspectExpectedScriptImportsForTests(
                paths,
                path => path.EndsWith("Imported.cs") ? "0123456789abcdef0123456789abcdef" : string.Empty,
                path => path.EndsWith("Imported.cs"),
                path => path.EndsWith("Imported.cs"));

            Assert.That(inspection.Verified, Is.False);
            var json = inspection.ToJson();
            StringAssert.Contains("\"checked_count\":2", json);
            StringAssert.Contains("\"verified_count\":1", json);
            StringAssert.Contains("\"missing_count\":1", json);
            StringAssert.Contains("\"items\":[{\"asset_path\":\"Assets/Tests/Imported.cs\",\"verified\":true", json);
            StringAssert.Contains("\"asset_path\":\"Assets/Tests/Missing.cs\"", json);
            StringAssert.Contains("\"guid_present\":false", json);
            StringAssert.Contains("\"guid_valid\":false", json);
            StringAssert.Contains("\"mono_script_loaded\":false", json);
            StringAssert.Contains("\"compiler_source_present\":false", json);
            StringAssert.Contains("\"failure_causes\":[\"invalid_guid\",\"mono_script_missing\",\"compiler_source_missing\"]", json);
        }

        [TestCase("")]
        [TestCase("0123456789abcdef0123456789abcde")]
        [TestCase("0123456789abcdef0123456789abcdef0")]
        [TestCase("0123456789abcdef0123456789abcdeg")]
        public void RejectsMissingOrMalformedUnityGuids(string guid)
        {
            var inspection = BuiltInCommands.InspectExpectedScriptImportsForTests(
                new[] { "Assets/Tests/Expected.cs" },
                _ => guid,
                _ => true,
                _ => true);

            Assert.That(inspection.Verified, Is.False);
            var json = inspection.ToJson();
            StringAssert.Contains("\"guid_valid\":false", json);
            StringAssert.Contains("\"failure_causes\":[\"invalid_guid\"]", json);
        }

        [Test]
        public void RequiresMonoScriptMainAssetAndActiveCompilerSourceForEveryPath()
        {
            var inspection = BuiltInCommands.InspectExpectedScriptImportsForTests(
                new[] { "Assets/Tests/MainAssetMissing.cs", "Assets/Tests/CompilerSourceMissing.cs" },
                _ => "0123456789ABCDEF0123456789ABCDEF",
                path => !path.EndsWith("MainAssetMissing.cs"),
                path => !path.EndsWith("CompilerSourceMissing.cs"));

            Assert.That(inspection.Verified, Is.False);
            var json = inspection.ToJson();
            StringAssert.Contains("\"verified_count\":0", json);
            StringAssert.Contains("\"missing_count\":2", json);
            StringAssert.Contains("\"failure_causes\":[\"mono_script_missing\"]", json);
            StringAssert.Contains("\"failure_causes\":[\"compiler_source_missing\"]", json);
        }

        [Test]
        public void AcceptsOnlyCompleteExpectedScriptEvidence()
        {
            var inspection = BuiltInCommands.InspectExpectedScriptImportsForTests(
                new[] { "Assets/Tests/Expected.cs" },
                _ => "0123456789abcdef0123456789abcdef",
                _ => true,
                _ => true);

            Assert.That(inspection.Verified, Is.True);
            var json = inspection.ToJson();
            StringAssert.Contains("\"verified\":true", json);
            StringAssert.Contains("\"guid_valid\":true", json);
            StringAssert.Contains("\"mono_script_main_asset\":true", json);
            StringAssert.Contains("\"compiler_source_present\":true", json);
            StringAssert.Contains("\"failure_causes\":[]", json);
            StringAssert.Contains("\"failures\":[]", json);
        }
    }
}
