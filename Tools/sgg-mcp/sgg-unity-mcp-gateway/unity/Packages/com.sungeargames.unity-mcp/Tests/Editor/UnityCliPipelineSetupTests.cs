using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityCliPipelineSetupTests
    {
        const string CurrentCliVersion = "1.0.0-beta.2";
        const string TargetCliVersion = "1.0.0-beta.6";
        const string TargetPipelineVersion = "0.5.0-exp.1";
        const string SignatureThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";
        const string ExecutableSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string CliCheckResponse = "{\"success\":true,\"command\":\"upgrade\",\"data\":{\"currentVersion\":\"1.0.0-beta.2\",\"latestVersion\":\"1.0.0-beta.6\",\"platform\":\"win32-x64\"},\"errors\":[],\"warnings\":[]}";
        const string PipelineVersionsResponse = "{\"success\":true,\"command\":\"pipeline list-versions\",\"data\":{\"packageId\":\"com.unity.pipeline\",\"latest\":\"0.5.0-exp.1\",\"versions\":[\"0.4.0-exp.1\",\"0.5.0-exp.1\"]},\"errors\":[],\"warnings\":[]}";
        const string CliUpgradeHelp = "Usage: unity upgrade [options]\nOptions:\n  --target <version>  Target CLI version\n  -y, --yes           Skip confirmation\nGlobal Options:\n  --format <format>   Output format\n  --non-interactive   Disable prompts\n  --no-banner         Suppress the banner\n";
        const string PipelineInstallHelp = "Usage: unity pipeline install [options]\nOptions:\n  --project-path <path>       Unity project path\n  --package-version <version>  Exact package version\nGlobal Options:\n  --format <format>           Output format\n  --non-interactive           Disable prompts\n  --no-banner                 Suppress the banner\n";
        const string ValidSignatureResponse = "{\"status\":\"Valid\",\"subject\":\"CN=Unity Technologies SF, O=Unity Technologies SF\",\"thumbprint\":\"0123456789ABCDEF0123456789ABCDEF01234567\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}";
        const string InvalidSignatureResponse = "{\"status\":\"NotSigned\",\"subject\":\"CN=Unity Technologies SF, O=Unity Technologies SF\",\"thumbprint\":\"0123456789ABCDEF0123456789ABCDEF01234567\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}";
        const string SkillRefResponse = UnityCliProjectSkillInstaller.ReviewedSourceCommit + "\t" + UnityCliUpdateCheck.SkillRepositoryPath + "\n";

        static DateTime TestNow => new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

        [SetUp]
        public void ResetSharedUpdateState()
        {
            UnityCliUpdateCheck.ResetCacheForTests();
            UnityCliPipelineSetup.SetWindowsStatus(UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked());
            WindowsCliUpdateCoordinator.ClearLocalRefreshRequirement();
            UnityPipelineUpdateOperation.ResetForTests();
        }

        [TestCase("2022.3.40f1", false)]
        [TestCase("6000.0.0f1", true)]
        [TestCase("6000.5.3f1", true)]
        [TestCase("", false)]
        public void PipelineSupportRequiresUnitySixOrNewer(string version, bool expected)
        {
            Assert.That(UnityCliPipelineSetup.IsPipelineEditorSupported(version), Is.EqualTo(expected));
        }

        [TestCase("Unity CLI 1.0.0-beta.2", "1.0.0-beta.2")]
        [TestCase("v2.3.4", "2.3.4")]
        public void CliVersionParserAcceptsCurrentVersionShapes(string output, string expected)
        {
            Assert.That(UnityCliPipelineSetup.TryExtractCliVersion(output, out var version), Is.True);
            Assert.That(version, Is.EqualTo(expected));
        }

        [Test]
        public void CliVersionParserRejectsUnboundedOrMalformedLocalMetadata()
        {
            Assert.That(UnityCliPipelineSetup.TryExtractCliVersion("1.0.0-" + new string('a', 200), out _), Is.False);
            Assert.That(UnityCliPipelineSetup.TryExtractCliVersion("1.0.0-01", out _), Is.False);
            Assert.That(UnityCliPipelineSetup.TryExtractCliVersion("1.0.0garbage", out _), Is.False);
            Assert.That(UnityCliPipelineSetup.TryExtractCliVersion("1.0.0 2.0.0", out _), Is.False);
            Assert.That(UnityCliPipelineSetup.TryExtractCliVersion("Unity CLI 1.0.0+build.1", out var buildVersion), Is.True);
            Assert.That(buildVersion, Is.EqualTo("1.0.0+build.1"));
        }

        [Test]
        public void OfficialWindowsInstallPathUsesUnityBinUnderLocalAppData()
        {
            Assert.That(UnityCliPipelineSetup.OfficialWindowsInstallPath.Replace('\\', '/'), Does.EndWith("/Unity/bin/unity.exe"));
        }

        [Test]
        public void AuthenticodeParserAcceptsStrictValidUnitySignature()
        {
            Assert.That(WindowsCliAuthenticode.TryParseProbeResponse(ValidSignatureResponse, out var signature, out var error), Is.True, error);
            Assert.That(signature.State, Is.EqualTo(WindowsCliAuthenticode.ValidState));
            Assert.That(signature.Signer, Is.EqualTo(WindowsCliAuthenticode.UnitySigner));
            Assert.That(signature.Thumbprint, Is.EqualTo(SignatureThumbprint));
            Assert.That(signature.FileSha256, Is.EqualTo(ExecutableSha256));
        }

        [Test]
        public void AuthenticodeParserRejectsMalformedUntrustedAndUnboundedResponses()
        {
            var invalidResponses = new[]
            {
                "not-json",
                InvalidSignatureResponse,
                ValidSignatureResponse.Replace("CN=Unity Technologies SF", "CN=Other Signer"),
                ValidSignatureResponse.Replace("O=Unity Technologies SF", "O=Other Organization"),
                ValidSignatureResponse.Replace(SignatureThumbprint, new string('A', 39)),
                ValidSignatureResponse.Replace(ExecutableSha256, new string('a', 63)),
                new string('x', 4097),
            };

            for (var index = 0; index < invalidResponses.Length; index++)
            {
                Assert.That(WindowsCliAuthenticode.TryParseProbeResponse(invalidResponses[index], out var signature, out var error), Is.False, "invalid signature case " + index);
                Assert.That(signature, Is.Null, error);
                Assert.That(error, Is.Not.Empty);
            }
        }

        [Test]
        public void AuthenticodeProbeUsesBoundAbsolutePathsAndModuleQualifiedCommand()
        {
            var script = WindowsCliAuthenticode.BuildProbeArguments(UnityCliPipelineSetup.OfficialWindowsInstallPath).Last();

            Assert.That(script, Does.Contain(WindowsCliAuthenticode.SecurityModulePath.Replace("'", "''")));
            Assert.That(script, Does.Contain(UnityCliPipelineSetup.OfficialWindowsInstallPath.Replace("'", "''")));
            Assert.That(script, Does.Contain("Microsoft.PowerShell.Security\\Get-AuthenticodeSignature"));
            Assert.That(script, Does.Not.Contain("$env:WINDIR"));
            Assert.That(script, Does.Not.Contain("$env:LOCALAPPDATA"));
        }

        [Test]
        public void BoundCliReadRevalidatesIdentityBeforeExecutingExactArguments()
        {
            var script = WindowsCliAuthenticode.BuildBoundCommandArguments(
                UnityCliPipelineSetup.OfficialWindowsInstallPath,
                SignatureThumbprint,
                ExecutableSha256,
                new[] { "pipeline", "install", "--help", "owner's value" }).Last();

            Assert.That(script, Does.Contain("Get-AuthenticodeSignature -LiteralPath $path"));
            Assert.That(script, Does.Contain("$expectedThumbprint='" + SignatureThumbprint + "'"));
            Assert.That(script, Does.Contain("$expectedSha256='" + ExecutableSha256 + "'"));
            Assert.That(script, Does.Contain("$arguments=@('pipeline','install','--help','owner''s value')"));
            Assert.That(script, Does.Contain("& $path @arguments"));
            Assert.That(script, Does.Contain("Assert-NoReparsePath $path"));
            Assert.That(script.IndexOf("$stream=[IO.File]::Open", StringComparison.Ordinal), Is.LessThan(script.IndexOf("& $path @arguments", StringComparison.Ordinal)));
            Assert.That(script.IndexOf("& $path @arguments", StringComparison.Ordinal), Is.LessThan(script.IndexOf("finally{$stream.Dispose()}", StringComparison.Ordinal)));
            Assert.That(script, Does.Not.Contain("Start-Process"));
            Assert.That(script, Does.Not.Contain("Invoke-Expression"));
        }

        [Test]
        public void WindowsCliMutationArgumentsAreExactAndDoNotCarryOtherActions()
        {
            var arguments = WindowsCliUpdateCoordinator.BuildMutationArguments(TargetCliVersion).ToArray();

            Assert.That(arguments, Is.EqualTo(new[]
            {
                "upgrade", "--target", TargetCliVersion, "--yes", "--format", "json", "--non-interactive", "--no-banner",
            }));
            Assert.That(arguments.Count(value => value == "upgrade"), Is.EqualTo(1));
            Assert.That(arguments, Does.Not.Contain("--channel"));
            Assert.That(arguments, Does.Not.Contain("auth"));
            Assert.That(arguments, Does.Not.Contain("login"));
            Assert.That(arguments, Does.Not.Contain("pipeline"));
            Assert.That(arguments, Does.Not.Contain("rollback"));
            Assert.That(arguments, Does.Not.Contain("dry-run"));
        }

        [Test]
        public void WindowsCliInstallerUsesBoundedOfficialDownloadWithoutInvokeExpression()
        {
            var script = UnityCliPipelineSetup.BuildWindowsCliInstallScript();

            Assert.That(script, Does.Contain(UnityCliPipelineSetup.WindowsCliInstallerUrl));
            Assert.That(script, Does.Contain("-Channel '" + UnityCliPipelineSetup.WindowsCliInstallerChannel + "'"));
            Assert.That(script, Does.Contain("Invoke-WebRequest"));
            Assert.That(script, Does.Contain("-OutFile $installer"));
            Assert.That(script, Does.Contain("Remove-Item -LiteralPath $installer"));
            Assert.That(script, Does.Contain("SecurityProtocolType]::Tls12"));
            Assert.That(script, Does.Not.Contain("Invoke-Expression"));
            Assert.That(script, Does.Not.Contain("| iex"));
        }

        [Test]
        public void WslProbeKeepsWindowsWrapperAndLinuxBinaryDistinct()
        {
            var wrapper = UnityCliPipelineSetup.ParseWslProbe("kind=windows_wrapper\npath=/home/user/.local/bin/unity\n1.0.0-beta.2");
            var linux = UnityCliPipelineSetup.ParseWslProbe("kind=linux_binary\npath=/usr/local/bin/unity\n1.0.0-beta.2");

            Assert.That(wrapper.ResolvesToWindowsCli, Is.True);
            Assert.That(wrapper.Kind, Is.EqualTo("Wrapper to Windows CLI"));
            Assert.That(linux.ResolvesToWindowsCli, Is.False);
            Assert.That(linux.Kind, Is.EqualTo("Separate Linux CLI"));
        }

        [Test]
        public void PipelineListVerificationRequiresExactCurrentProjectPath()
        {
            const string json = "{\"success\":true,\"data\":{\"instances\":[{\"projectName\":\"Game\",\"projectPath\":\"C:\\\\Work\\\\Unity\\\\Game\",\"isRunning\":true,\"hasPipelinePackage\":true,\"pipelineVersion\":\"0.3.1-exp.1\",\"pipelineServer\":{\"port\":7800,\"isReachable\":true}}]}}";

            Assert.That(UnityCliPipelineSetup.PipelineListContainsProject(json, "C:\\Work\\Unity\\Game"), Is.True);
            Assert.That(UnityCliPipelineSetup.PipelineListContainsProject(json, "C:\\Work\\Unity\\Game-Other"), Is.False);
            Assert.That(UnityCliPipelineSetup.PipelineListContainsProject(json.Replace("\"isReachable\":true", "\"isReachable\":false"), "C:\\Work\\Unity\\Game"), Is.False);
            Assert.That(UnityCliPipelineSetup.PipelineListContainsProject(json.Replace("\"hasPipelinePackage\":true", "\"hasPipelinePackage\":false"), "C:\\Work\\Unity\\Game"), Is.False);
        }

        [Test]
        public void WslDirectExecutableMustMatchValidatedWindowsPath()
        {
            Assert.That(UnityCliPipelineSetup.WslDirectExecutableMatchesWindows("/mnt/c/Tools/Unity CLI/unity.exe", "C:\\Tools\\Unity CLI\\unity.exe"), Is.True);
            Assert.That(UnityCliPipelineSetup.WslDirectExecutableMatchesWindows("/mnt/c/Other/unity.exe", "C:\\Tools\\Unity CLI\\unity.exe"), Is.False);
        }

        [Test]
        public void WindowsCliPathConvertsToExactWslMountPath()
        {
            Assert.That(UnityCliPipelineSetup.WindowsPathToWsl("C:\\Users\\roman\\AppData\\Local\\Unity\\bin\\unity.exe"), Is.EqualTo("/mnt/c/Users/roman/AppData/Local/Unity/bin/unity.exe"));
            Assert.That(UnityCliPipelineSetup.WindowsPathToWsl("relative/unity.exe"), Is.Empty);
            Assert.That(UnityCliPipelineSetup.IsSafeWslWrapperTarget("/mnt/c/Users/roman/AppData/Local/Unity/bin/unity.exe"), Is.True);
            Assert.That(UnityCliPipelineSetup.IsSafeWslWrapperTarget("/mnt/c/Users/user$/Unity/bin/unity.exe"), Is.False);
        }

        [Test]
        public void ProcessArgumentsQuoteWhitespaceQuotesAndTrailingBackslashes()
        {
            var arguments = BoundedProcessOperation.BuildArguments(new[] { "plain", "C:\\Project Files\\", "a\"b" });

            Assert.That(arguments, Is.EqualTo("plain \"C:\\Project Files\\\\\" \"a\\\"b\""));
        }

        [Test]
        public void SkillInstallCommandIsPinnedProjectScopedAndCopyBased()
        {
            var arguments = UnityCliProjectSkillInstaller.BuildInstallArguments("C:/node/npm-cli.js");

            Assert.That(arguments, Does.Contain("skills@" + UnityCliProjectSkillInstaller.SkillsCliVersion));
            Assert.That(arguments, Does.Contain(UnityCliProjectSkillInstaller.Repository));
            Assert.That(arguments, Does.Contain("opencode"));
            Assert.That(arguments, Does.Contain("codex"));
            Assert.That(arguments, Does.Contain("--copy"));
            Assert.That(arguments, Does.Not.Contain("--global"));
            Assert.That(UnityCliProjectSkillInstaller.ReviewedSourceCommit, Has.Length.EqualTo(40));
            Assert.That(UnityCliProjectSkillInstaller.ReviewedSkillsHash, Has.Length.EqualTo(64));
        }

        [Test]
        public void SkillTargetContainmentRejectsSiblingPath()
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-unity-cli-root-" + Guid.NewGuid().ToString("N"));
            var inside = Path.Combine(root, ".agents", "skills", "unity-cli");
            var sibling = root + "-other" + Path.DirectorySeparatorChar + "unity-cli";

            Assert.That(UnityCliProjectSkillInstaller.IsTargetWithinProject(root, inside), Is.True);
            Assert.That(UnityCliProjectSkillInstaller.IsTargetWithinProject(root, sibling), Is.False);
        }

        [Test]
        public void SkillStatusDoesNotTreatUnknownExistingFolderAsInstalled()
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-unity-cli-status-" + Guid.NewGuid().ToString("N"));
            var skillRoot = Path.Combine(root, ".agents", "skills", "unity-cli");
            try
            {
                Directory.CreateDirectory(skillRoot);
                File.WriteAllText(Path.Combine(skillRoot, "SKILL.md"), "# Unknown\n");

                var status = UnityCliProjectSkillInstaller.GetStatus(root);

                Assert.That(status.Exists, Is.True);
                Assert.That(status.Valid, Is.False);
                Assert.That(status.LocalState, Is.EqualTo(UnityCliProjectSkillInstaller.LocalStateUnknown));
                Assert.That(status.InstalledReviewedCommit, Is.Empty);
                Assert.That(status.InstalledReviewedHash, Is.Empty);
                Assert.That(status.Message, Does.Contain("Manual review required"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [TestCase("1.0.0-beta.2", "1.0.0-beta.6", -1)]
        [TestCase("1.0.0-rc.1", "1.0.0", -1)]
        [TestCase("1.0.0+build.1", "1.0.0+build.2", 0)]
        [TestCase("2.0.0", "1.0.0", 1)]
        [TestCase("2026.8.9-2", "2026.8.9-1", 1)]
        [TestCase("2026.8.9-2", "2026.8.8-4", 1)]
        public void SemVerComparisonUsesPrereleaseRulesAndDoesNotDowngrade(string local, string available, int expectedSign)
        {
            Assert.That(UnityCliUpdateCheck.TryCompareSemVer(local, available, out var comparison), Is.True);
            Assert.That(Math.Sign(comparison), Is.EqualTo(expectedSign));
        }

        [TestCase("1.0")]
        [TestCase("1.0.0-01")]
        [TestCase("v1.0.0")]
        [TestCase("1.0.0-")]
        public void SemVerComparisonRejectsMalformedVersions(string value)
        {
            Assert.That(UnityCliUpdateCheck.TryCompareSemVer(value, "1.0.0", out _), Is.False);
        }

        [Test]
        public void NewerLocalVersionRemainsCurrentAndNeverEnablesDowngrade()
        {
            var status = UnityCliUpdateCheck.BuildVersionUpdateStatus(
                "2.0.0",
                "1.0.0",
                "beta",
                "official",
                "fixed",
                true,
                true,
                DateTime.UtcNow);

            Assert.That(status.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateCurrent));
            Assert.That(status.LastErrorCode, Is.EqualTo("local_newer_no_downgrade"));
        }

        [Test]
        public void CliUpdateParserAcceptsExactOfficialWindowsSchema()
        {
            const string json = CliCheckResponse;

            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(json, out var metadata, out var error), Is.True, error);
            Assert.That(metadata.CurrentVersion, Is.EqualTo("1.0.0-beta.2"));
            Assert.That(metadata.LatestVersion, Is.EqualTo(TargetCliVersion));
            Assert.That(metadata.Platform, Is.EqualTo(UnityCliUpdateCheck.CliPlatform));
        }

        [TestCase("command", "upgrade")]
        [TestCase("platform", "win32-arm64")]
        public void CliUpdateParserRejectsWrongCommandOrPlatform(string field, string wrongValue)
        {
            var json = CliCheckResponse;
            json = field == "command" ? json.Replace("\"command\":\"upgrade\"", "\"command\":\"wrong\"") : json.Replace("win32-x64", wrongValue);

            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(json, out _, out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void CliUpdateParserRejectsMalformedVersionAndMissingEnvelopeArrays()
        {
            const string json = "{\"success\":true,\"command\":\"upgrade\",\"data\":{\"currentVersion\":\"1.0\",\"latestVersion\":\"1.0.0-beta.6\",\"platform\":\"win32-x64\"},\"errors\":[],\"warnings\":[]}";
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(json, out _, out _), Is.False);
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(json.Replace(",\"errors\":[],\"warnings\":[]", string.Empty), out _, out _), Is.False);
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(CliCheckResponse.Replace(CurrentCliVersion, "1.0.0"), out _, out var channelError), Is.False);
            Assert.That(channelError, Is.EqualTo("cli_channel_mismatch"));
        }

        [Test]
        public void MutationHelpParsersAcceptOnlyExactRequiredCommandSurfaces()
        {
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(CliUpgradeHelp), Is.True);
            Assert.That(UnityCliUpdateCheck.TryValidatePipelineInstallHelp(PipelineInstallHelp), Is.True);

            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(CliUpgradeHelp.Replace("--target <version>", "--target-version <version>")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(CliUpgradeHelp.Replace("--no-banner", "--banner")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(CliUpgradeHelp.Replace("-y, --yes           Skip confirmation", "-y, --yes <value>   Skip confirmation")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(CliUpgradeHelp.Replace("  --target <version>  Target CLI version\n", string.Empty).Replace("Global Options:\n", "Global Options:\n  --target <version>  Target CLI version\n")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(CliUpgradeHelp.Replace("Global Options:\n", "Removed Options:\n  --target <version>  Removed\nGlobal Options:\n")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidatePipelineInstallHelp(PipelineInstallHelp.Replace("--project-path <path>", "--project <path>")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidatePipelineInstallHelp(PipelineInstallHelp.Replace("--non-interactive           Disable prompts", "--non-interactive <mode>    Disable prompts")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidatePipelineInstallHelp(PipelineInstallHelp.Replace("Global Options:\n", "  --package-version <version>  Duplicate\nGlobal Options:\n")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidatePipelineInstallHelp(PipelineInstallHelp.Replace("  --no-banner                 Suppress the banner\n", string.Empty) + "Examples:\n  --no-banner                 Not an option declaration\n"), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidatePipelineInstallHelp(PipelineInstallHelp.Replace("  --no-banner                 Suppress the banner\n", string.Empty) + "  Examples:\n  --no-banner                 Not an option declaration\n"), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidatePipelineInstallHelp(new string('x', UnityCliUpdateCheck.MaxMetadataResponseCharacters + 1)), Is.False);
        }

        [Test]
        public void PipelineUpdateParserRequiresExactPackageLatestAndBoundedVersionList()
        {
            const string json = "{\"success\":true,\"command\":\"pipeline list-versions\",\"data\":{\"packageId\":\"com.unity.pipeline\",\"latest\":\"0.5.0-exp.1\",\"versions\":[\"0.3.0\",\"0.4.0-exp.1\",\"0.5.0-exp.1\"]},\"errors\":[],\"warnings\":[]}";
            Assert.That(UnityCliUpdateCheck.TryParsePipelineListVersionsResponse(json, out var metadata, out var error), Is.True, error);
            Assert.That(metadata.PackageId, Is.EqualTo(UnityCliPipelineSetup.PipelinePackageId));
            Assert.That(metadata.LatestVersion, Is.EqualTo(TargetPipelineVersion));

            Assert.That(UnityCliUpdateCheck.TryParsePipelineListVersionsResponse(
                json.Replace("com.unity.pipeline", "com.unity.other"), out _, out _), Is.False);
            Assert.That(UnityCliUpdateCheck.TryParsePipelineListVersionsResponse(
                json.Replace("\"latest\":\"0.5.0-exp.1\"", "\"latest\":\"0.6.0-exp.1\""), out _, out _), Is.False);

            var tooManyVersions = string.Join(",", Enumerable.Repeat("\"0.1.0\"", UnityCliUpdateCheck.MaxMetadataEntries + 1));
            var boundedJson = "{\"success\":true,\"command\":\"pipeline list-versions\",\"data\":{\"packageId\":\"com.unity.pipeline\",\"latest\":\"0.1.0\",\"versions\":[" + tooManyVersions + "]},\"errors\":[],\"warnings\":[]}";
            Assert.That(UnityCliUpdateCheck.TryParsePipelineListVersionsResponse(boundedJson, out _, out _), Is.False);
            Assert.That(UnityCliUpdateCheck.TryParsePipelineListVersionsResponse(
                json.Replace(",\"errors\":[],\"warnings\":[]", string.Empty), out _, out _), Is.False);
        }

        [Test]
        public void SkillPathCommitParserDistinguishesReviewedAndUnreviewedChanges()
        {
            var script = UnityCliUpdateCheck.BuildSkillLatestCheckArguments().Last();
            Assert.That(script, Does.Contain("+[char]9+'" + UnityCliUpdateCheck.SkillRepositoryPath + "'"));
            Assert.That(script, Does.Not.Contain("'`t"));

            var same = UnityCliProjectSkillInstaller.ReviewedSourceCommit + "\t" + UnityCliUpdateCheck.SkillRepositoryPath + "\n";
            Assert.That(UnityCliUpdateCheck.TryParseSkillLatestCommitResponse(same, out var sameCommit, out var sameError), Is.True, sameError);

            var current = new UnityCliProjectSkillInstaller.UnityCliSkillStatus
            {
                Exists = true,
                Valid = true,
                ProvenanceVerified = true,
                LocalState = UnityCliProjectSkillInstaller.LocalStateCurrentReviewed,
                InstalledReviewedCommit = UnityCliProjectSkillInstaller.ReviewedSourceCommit,
                InstalledReviewedHash = UnityCliProjectSkillInstaller.ReviewedSkillsHash,
                ContentHash = UnityCliProjectSkillInstaller.ReviewedContentHash,
            };
            UnityCliUpdateCheck.ApplySkillGitSignal(current, sameCommit, DateTime.UtcNow);
            Assert.That(current.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateCurrent));
            Assert.That(current.UpstreamChangeDetected, Is.False);

            var previous = CreatePreviousReviewedSkillStatus(Path.Combine(Path.GetTempPath(), "sgg-skill-binding"));
            UnityCliUpdateCheck.ApplySkillGitSignal(previous, sameCommit, DateTime.UtcNow);
            Assert.That(previous.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateAvailable));
            Assert.That(previous.Update.InstalledVersion, Is.EqualTo(UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit));
            Assert.That(previous.Update.AvailableVersion, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));
            var skillSnapshot = new UnityCliUpdateCheckSnapshot(
                UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked(),
                new UnityCliPipelineSetup.UnityPipelineStatus { Update = ComponentUpdateStatus.NotChecked() },
                previous)
            {
                IsComplete = true,
            };
            Assert.That(UnityCliProjectSkillInstaller.TryCreateUpdateBinding(skillSnapshot, previous, DateTime.UtcNow, out var binding, out var bindingError), Is.True, bindingError);
            Assert.That(binding.InstalledReviewedCommit, Is.EqualTo(UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit));
            Assert.That(binding.TargetReviewedCommit, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));

            var different = new string('a', 40) + "\t" + UnityCliUpdateCheck.SkillRepositoryPath + "\n";
            Assert.That(UnityCliUpdateCheck.TryParseSkillLatestCommitResponse(different, out var differentCommit, out _), Is.True);
            UnityCliUpdateCheck.ApplySkillGitSignal(current, differentCommit, DateTime.UtcNow);
            Assert.That(current.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateUpstreamReview));
            Assert.That(current.AvailableReviewedCommit, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));
            Assert.That(current.AvailableReviewedHash, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSkillsHash));
            Assert.That(current.Update.AvailableVersion, Is.EqualTo(differentCommit));
            Assert.That(current.Update.AuthoritativeForMutation, Is.False);
            Assert.That(current.UpstreamChangeDetected, Is.True);

            Assert.That(UnityCliUpdateCheck.TryParseSkillLatestCommitResponse("not-a-ref\n", out _, out var malformedError), Is.False);
            Assert.That(UnityCliUpdateCheck.TryParseSkillLatestCommitResponse(
                UnityCliProjectSkillInstaller.ReviewedSourceCommit + "\tother-skill\n", out _, out _), Is.False);
            Assert.That(malformedError, Is.Not.Empty);
        }

        [Test]
        public void InstalledOnlyCheckStartsNoProcessesWhenEveryOptionalComponentIsMissing()
        {
            var snapshot = UnityCliUpdateCheck.CreateInitialSnapshotForTests(
                UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked(),
                false,
                new UnityCliPipelineSetup.UnityPipelineStatus(),
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus(),
                UnityToolUpdateComponents.None,
                TestNow);
            var starter = new FakeProcessStarter();

            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Assert.That(coordinator.IsComplete, Is.True);
                Assert.That(starter.StartedFiles, Is.Empty);
                Assert.That(snapshot.WindowsCli.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(snapshot.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(snapshot.Skill.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
            }
        }

        [Test]
        public void InstalledOnlyCheckRunsOnlyCliLocalTrustAndRemoteStagesForCliOnly()
        {
            var snapshot = UnityCliUpdateCheck.CreateInitialSnapshotForTests(
                UnityCliPipelineSetup.WindowsUnityCliStatus.Ready(UnityCliPipelineSetup.OfficialWindowsInstallPath, CurrentCliVersion),
                true,
                new UnityCliPipelineSetup.UnityPipelineStatus(),
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus(),
                UnityToolUpdateComponents.WindowsCli,
                TestNow);
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                new FakeProcess(0, CliCheckResponse));

            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                PollToCompletion(coordinator);

                Assert.That(starter.StartedFiles, Is.EqualTo(new[]
                {
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                }));
                Assert.That(starter.StartedArguments[2].Last(), Does.Contain("$arguments=@('upgrade','--help','--no-banner')"));
                Assert.That(starter.StartedArguments.Any(arguments => arguments.Contains("pipeline")), Is.False);
                Assert.That(starter.StartedArguments.Any(arguments => arguments.Any(argument => argument.Contains(UnityCliUpdateCheck.SkillLatestApiUrl))), Is.False);
                Assert.That(snapshot.WindowsCli.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateAvailable));
                Assert.That(snapshot.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(snapshot.Skill.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
            }
        }

        [Test]
        public void InstalledOnlyCheckRunsOnlyRelevantPathLookupForSkillOnly()
        {
            var skill = new UnityCliProjectSkillInstaller.UnityCliSkillStatus
            {
                Exists = true,
                Valid = true,
                ProvenanceVerified = true,
                LocalState = UnityCliProjectSkillInstaller.LocalStateCurrentReviewed,
                InstalledReviewedCommit = UnityCliProjectSkillInstaller.ReviewedSourceCommit,
                InstalledReviewedHash = UnityCliProjectSkillInstaller.ReviewedSkillsHash,
                ContentHash = UnityCliProjectSkillInstaller.ReviewedContentHash,
            };
            var snapshot = UnityCliUpdateCheck.CreateInitialSnapshotForTests(
                UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked(),
                false,
                new UnityCliPipelineSetup.UnityPipelineStatus(),
                skill,
                UnityToolUpdateComponents.Skill,
                TestNow);
            var starter = new FakeProcessStarter(new FakeProcess(0, SkillRefResponse));

            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                PollToCompletion(coordinator);

                Assert.That(starter.StartedFiles, Is.EqualTo(new[] { WindowsCliAuthenticode.PowerShellPath }));
                Assert.That(starter.StartedArguments[0], Is.EqualTo(UnityCliUpdateCheck.BuildSkillLatestCheckArguments()));
                Assert.That(snapshot.WindowsCli.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(snapshot.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(snapshot.Skill.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateCurrent));
            }
        }

        [Test]
        public void InstalledPipelineWithoutCliGetsTypedTransportBlockerWithoutProcessOrInstallSuggestion()
        {
            var pipeline = new UnityCliPipelineSetup.UnityPipelineStatus
            {
                EditorSupported = true,
                Registered = true,
                ManifestVersion = "0.4.0-exp.1",
                Version = "0.4.0-exp.1",
                Source = "Registry",
                ProjectPackagesIdentitySha256 = new string('a', 64),
            };
            var snapshot = UnityCliUpdateCheck.CreateInitialSnapshotForTests(
                UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked(),
                false,
                pipeline,
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus(),
                UnityToolUpdateComponents.Pipeline,
                TestNow);
            var starter = new FakeProcessStarter();

            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Assert.That(coordinator.IsComplete, Is.True);
                Assert.That(starter.StartedFiles, Is.Empty);
                Assert.That(snapshot.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateCheckFailed));
                Assert.That(snapshot.Pipeline.Update.LastErrorCode, Is.EqualTo("check_blocked_update_transport_unavailable"));
                Assert.That(snapshot.Pipeline.Update.LastErrorSummary, Does.Not.Contain("Install Windows CLI"));
                Assert.That(snapshot.Pipeline.Update.LastErrorSummary, Does.Not.Contain("install the CLI"));
            }
        }

        [Test]
        public void AutomaticFreshnessUsesTwentyFourHourCadenceAndOneHourFailureBackoff()
        {
            var record = new UnityToolFreshnessRecord
            {
                installedIdentity = "identity",
                checkedAtUtc = TestNow.AddHours(-23).ToString("O"),
            };

            Assert.That(UnityToolFreshnessStore.NeedsAutomaticCheck(record, "identity", TestNow), Is.False);
            record.checkedAtUtc = TestNow.AddHours(-24).ToString("O");
            Assert.That(UnityToolFreshnessStore.NeedsAutomaticCheck(record, "identity", TestNow), Is.True);
            record.lastFailureAtUtc = TestNow.AddMinutes(-30).ToString("O");
            Assert.That(UnityToolFreshnessStore.NeedsAutomaticCheck(record, "identity", TestNow), Is.False);
            record.lastFailureAtUtc = TestNow.AddMinutes(-60).ToString("O");
            Assert.That(UnityToolFreshnessStore.NeedsAutomaticCheck(record, "identity", TestNow), Is.True);
            Assert.That(UnityToolFreshnessStore.NeedsAutomaticCheck(record, "different", TestNow), Is.True);
            Assert.That(UnityCliUpdateCheck.MaximumAllComponentsStageBudget, Is.EqualTo(TimeSpan.FromSeconds(135)));
            Assert.That(UnityCliUpdateCheck.TotalTimeout, Is.GreaterThan(UnityCliUpdateCheck.MaximumAllComponentsStageBudget));
        }

        [Test]
        public void AutomaticFreshnessRejectsObsoleteSkillSourceContract()
        {
            var installed = new UnityToolInstalledSet
            {
                SkillInstalled = true,
                SkillIdentity = "identity",
            };
            var records = new UnityToolFreshnessRecords
            {
                skill = new UnityToolFreshnessRecord
                {
                    installedIdentity = "identity",
                    channel = "main",
                    sourceKind = "official_git_remote_ref",
                    checkedAtUtc = TestNow.AddMinutes(-1).ToString("O"),
                    lastFailureAtUtc = TestNow.AddMinutes(-1).ToString("O"),
                },
            };

            var components = UnityToolFreshnessStore.ComponentsNeedingAutomaticCheck(installed, records, TestNow);

            Assert.That(components, Is.EqualTo(UnityToolUpdateComponents.Skill));

            var failed = ComponentUpdateStatus.Failed(
                UnityCliProjectSkillInstaller.LegacyReviewedSourceCommit,
                UnityCliUpdateCheck.SkillChannel,
                UnityCliUpdateCheck.SkillSourceKind,
                UnityCliUpdateCheck.SkillSourceBaseIdentity(),
                "skill_process_failed",
                "failed",
                TestNow);
            records.skill = UnityToolFreshnessStore.RecordComponent(
                records.skill,
                true,
                installed.SkillIdentity,
                true,
                failed,
                TestNow);

            Assert.That(records.skill.channel, Is.EqualTo(UnityCliUpdateCheck.SkillChannel));
            Assert.That(records.skill.sourceKind, Is.EqualTo(UnityCliUpdateCheck.SkillSourceKind));
            Assert.That(UnityToolFreshnessStore.ComponentsNeedingAutomaticCheck(
                installed, records, TestNow.AddMinutes(30)), Is.EqualTo(UnityToolUpdateComponents.None));
            Assert.That(UnityToolFreshnessStore.ComponentsNeedingAutomaticCheck(
                installed, records, TestNow.AddHours(1)), Is.EqualTo(UnityToolUpdateComponents.Skill));
        }

        [Test]
        public void FailedFreshnessCheckDoesNotRebindOldDisplayEvidenceToNewIdentity()
        {
            var previous = new UnityToolFreshnessRecord
            {
                installedIdentity = "old-identity",
                installedVersion = CurrentCliVersion,
                availableVersion = TargetCliVersion,
                sourceIdentitySha256 = UnityCliUpdateCheck.CliSourceIdentity(CurrentCliVersion, TargetCliVersion),
                updateState = UnityCliUpdateCheck.UpdateStateAvailable,
                checkedAtUtc = TestNow.AddHours(-2).ToString("O"),
            };
            var failed = ComponentUpdateStatus.Failed(
                CurrentCliVersion,
                UnityCliUpdateCheck.CliChannel,
                "official_unity_cli_upgrade_check",
                UnityCliUpdateCheck.CliSourceIdentity(CurrentCliVersion, TargetCliVersion),
                "cli_process_failed",
                "failed",
                TestNow);

            var recorded = UnityToolFreshnessStore.RecordComponent(previous, true, "new-identity", true, failed, TestNow);

            Assert.That(recorded.installedIdentity, Is.EqualTo("new-identity"));
            Assert.That(recorded.installedVersion, Is.Empty);
            Assert.That(recorded.availableVersion, Is.Empty);
            Assert.That(recorded.sourceIdentitySha256, Is.Empty);
            Assert.That(recorded.updateState, Is.Empty);
            Assert.That(recorded.checkedAtUtc, Is.Empty);
            Assert.That(recorded.lastFailureAtUtc, Is.Not.Empty);

            var checkedStatus = ComponentUpdateStatus.Checked(
                CurrentCliVersion,
                TargetCliVersion,
                UnityCliUpdateCheck.CliChannel,
                "official_unity_cli_upgrade_check",
                UnityCliUpdateCheck.CliSourceIdentity(CurrentCliVersion, TargetCliVersion),
                TestNow);
            checkedStatus.UpdateState = UnityCliUpdateCheck.UpdateStateAvailable;
            var changedDuringSuccess = UnityToolFreshnessStore.RecordComponent(
                previous,
                true,
                "new-identity",
                true,
                checkedStatus,
                TestNow,
                "old-identity");
            Assert.That(changedDuringSuccess.installedIdentity, Is.EqualTo("new-identity"));
            Assert.That(changedDuringSuccess.checkedAtUtc, Is.Empty);
            Assert.That(changedDuringSuccess.lastFailureAtUtc, Is.Not.Empty);
        }

        [Test]
        public void PersistedFreshnessIsDisplayOnlyAndCannotAuthorizeMutation()
        {
            var record = new UnityToolFreshnessRecord
            {
                installedIdentity = "identity",
                installedVersion = CurrentCliVersion,
                availableVersion = TargetCliVersion,
                channel = UnityCliUpdateCheck.CliChannel,
                sourceKind = "official_unity_cli_upgrade_check",
                sourceIdentitySha256 = UnityCliUpdateCheck.CliSourceIdentity(CurrentCliVersion, TargetCliVersion),
                updateState = UnityCliUpdateCheck.UpdateStateAvailable,
                checkedAtUtc = TestNow.AddMinutes(-1).ToString("O"),
            };

            Assert.That(UnityToolFreshnessStore.TryRestore(record, "identity", TestNow, out var restored), Is.True);
            Assert.That(restored.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateAvailable));
            Assert.That(restored.AuthoritativeForMutation, Is.False);

            var fixture = CreateBindingFixture(TestNow, TargetCliVersion);
            fixture.Snapshot.WindowsCli.Update = restored;
            AssertBindingRejected(fixture);
        }

        [Test]
        public void CoordinatorContinuesAfterIndependentProcessFailure()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                new FakeProcess(1, string.Empty),
                new FakeProcess(0, PipelineInstallHelp),
                new FakeProcess(0, PipelineVersionsResponse),
                new FakeProcess(0, SkillRefResponse));
            var snapshot = CreateCoordinatorSnapshot();
            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", snapshot, starter, () => DateTime.UtcNow, TimeSpan.FromMinutes(1)))
            {
                PollToCompletion(coordinator);

                Assert.That(starter.StartedFiles.Count, Is.EqualTo(7));
                Assert.That(starter.StartedFiles, Is.All.EqualTo(WindowsCliAuthenticode.PowerShellPath));
                Assert.That(starter.StartedArguments[0], Is.EqualTo(WindowsCliAuthenticode.BuildProbeArguments(UnityCliPipelineSetup.OfficialWindowsInstallPath).ToArray()));
                Assert.That(starter.StartedArguments[1].Last(), Does.Contain("$arguments=@('--version','--no-banner')"));
                Assert.That(starter.StartedArguments[2].Last(), Does.Contain("$arguments=@('upgrade','--help','--no-banner')"));
                Assert.That(starter.StartedArguments[3].Last(), Does.Contain("$arguments=@('upgrade','--check','--channel','beta','--format','json','--non-interactive','--no-banner')"));
                Assert.That(starter.StartedArguments[4].Last(), Does.Contain("$arguments=@('pipeline','install','--help','--no-banner')"));
                Assert.That(starter.StartedArguments[5].Last(), Does.Contain("$arguments=@('pipeline','list-versions','--format','json','--non-interactive','--no-banner')"));
                Assert.That(starter.StartedArguments[6], Is.EqualTo(UnityCliUpdateCheck.BuildSkillLatestCheckArguments()));
                Assert.That(starter.StartedArguments[6].Last(), Does.Contain("path=skills%2Funity-cli&per_page=1"));
                Assert.That(starter.StartedArguments[6].Last(), Does.Not.Contain("refs/heads/main"));
                Assert.That(coordinator.Status.IsComplete, Is.True);
                Assert.That(coordinator.Status.WindowsCli.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateCheckFailed));
                Assert.That(coordinator.Status.WindowsCli.ExecutableSignatureState, Is.EqualTo(WindowsCliAuthenticode.ValidState));
                Assert.That(coordinator.Status.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
                Assert.That(coordinator.Status.Skill.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
            }
        }

        [Test]
        public void CoordinatorMarksCompleteAfterAllProcessesSucceed()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                new FakeProcess(0, CliCheckResponse),
                new FakeProcess(0, PipelineInstallHelp),
                new FakeProcess(0, PipelineVersionsResponse),
                new FakeProcess(0, SkillRefResponse));
            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", CreateCoordinatorSnapshot(), starter, () => DateTime.UtcNow, TimeSpan.FromMinutes(1)))
            {
                PollToCompletion(coordinator);

                Assert.That(starter.StartedFiles.Count, Is.EqualTo(7));
                Assert.That(coordinator.Status.IsComplete, Is.True);
                Assert.That(coordinator.IsCancelled, Is.False);
                Assert.That(coordinator.Status.WindowsCli.ExecutableSignatureState, Is.EqualTo(WindowsCliAuthenticode.ValidState));
                Assert.That(coordinator.Status.WindowsCli.ExecutableSigner, Is.EqualTo(WindowsCliAuthenticode.UnitySigner));
                Assert.That(coordinator.Status.WindowsCli.ExecutableSignatureThumbprint, Is.EqualTo(SignatureThumbprint));
            }
        }

        [Test]
        public void ChangedMutationHelpKeepsFreshnessDisplayOnlyAndCannotAuthorizeUpdates()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp.Replace("--target <version>", "--target-version <version>")),
                new FakeProcess(0, CliCheckResponse),
                new FakeProcess(0, PipelineInstallHelp.Replace("--package-version <version>", "--version <version>")),
                new FakeProcess(0, PipelineVersionsResponse),
                new FakeProcess(0, SkillRefResponse));
            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", CreateCoordinatorSnapshot(), starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                PollToCompletion(coordinator);

                Assert.That(coordinator.Status.WindowsCli.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
                Assert.That(coordinator.Status.WindowsCli.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateUnsupported));
                Assert.That(coordinator.Status.WindowsCli.Update.AuthoritativeForMutation, Is.False);
                Assert.That(coordinator.Status.WindowsCli.Update.LastErrorCode, Is.EqualTo("cli_update_surface_unavailable"));
                Assert.That(coordinator.Status.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
                Assert.That(coordinator.Status.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateUnsupported));
                Assert.That(coordinator.Status.Pipeline.Update.AuthoritativeForMutation, Is.False);
                Assert.That(coordinator.Status.Pipeline.Update.LastErrorCode, Is.EqualTo("pipeline_update_surface_unavailable"));
            }
        }

        [Test]
        public void InvalidCliSignatureSkipsAllCliCommandsButStillChecksIndependentSkill()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, InvalidSignatureResponse),
                new FakeProcess(0, SkillRefResponse));
            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", CreateCoordinatorSnapshot(), starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                PollToCompletion(coordinator);

                Assert.That(starter.StartedFiles, Is.EqualTo(new[] { WindowsCliAuthenticode.PowerShellPath, WindowsCliAuthenticode.PowerShellPath }));
                Assert.That(starter.StartedArguments[1], Is.EqualTo(UnityCliUpdateCheck.BuildSkillLatestCheckArguments()));
                Assert.That(starter.StartedArguments.Any(arguments => arguments.Contains("upgrade")), Is.False);
                Assert.That(starter.StartedArguments.Any(arguments => arguments.Contains("pipeline")), Is.False);
                Assert.That(coordinator.Status.WindowsCli.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(coordinator.Status.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateCheckFailed));
                Assert.That(coordinator.Status.Skill.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
            }
        }

        [Test]
        public void CliBindingAcceptsOnlyFreshExactBetaTargetAtOfficialPathWithMatchingSignature()
        {
            var fixture = CreateBindingFixture(TestNow, TargetCliVersion);

            Assert.That(
                WindowsCliUpdateCoordinator.TryCreateBinding(fixture.Snapshot, fixture.LocalStatus, TestNow, out var binding, out var error),
                Is.True,
                error);
            Assert.That(binding.InstalledVersion, Is.EqualTo(CurrentCliVersion));
            Assert.That(binding.TargetVersion, Is.EqualTo(TargetCliVersion));
            Assert.That(binding.Channel, Is.EqualTo(UnityCliUpdateCheck.CliChannel));
            Assert.That(binding.ExecutablePath, Is.EqualTo(UnityCliPipelineSetup.OfficialWindowsInstallPath));
            Assert.That(binding.SourceIdentitySha256, Is.EqualTo(UnityCliUpdateCheck.CliSourceIdentity(CurrentCliVersion, TargetCliVersion)));
            Assert.That(binding.SignatureState, Is.EqualTo(WindowsCliAuthenticode.ValidState));
            Assert.That(binding.SignatureThumbprint, Is.EqualTo(SignatureThumbprint));
            Assert.That(binding.ExecutableSha256, Is.EqualTo(ExecutableSha256));
            Assert.That(binding.BindingIdentitySha256, Has.Length.EqualTo(64));
        }

        [Test]
        public void CliBindingRejectsExpiredEqualOrOlderTargetAndWrongSource()
        {
            var expired = CreateBindingFixture(TestNow, TargetCliVersion);
            expired.Snapshot.WindowsCli.Update.ExpiresAtUtc = TestNow;
            AssertBindingRejected(expired);

            var equal = CreateBindingFixture(TestNow, CurrentCliVersion);
            AssertBindingRejected(equal);

            var older = CreateBindingFixture(TestNow, "1.0.0-beta.1");
            AssertBindingRejected(older);

            var wrongSource = CreateBindingFixture(TestNow, TargetCliVersion);
            wrongSource.Snapshot.WindowsCli.Update.SourceKind = "untrusted_source";
            AssertBindingRejected(wrongSource);

            var wrongChannel = CreateBindingFixture(TestNow, TargetCliVersion);
            wrongChannel.Snapshot.WindowsCli.Update.Channel = "stable";
            AssertBindingRejected(wrongChannel);

            var differentInstalledChannel = CreateBindingFixture(TestNow, TargetCliVersion);
            differentInstalledChannel.Snapshot.WindowsCli.Update.InstalledVersion = "1.0.0";
            differentInstalledChannel.Snapshot.WindowsCli.Version = "1.0.0";
            differentInstalledChannel.LocalStatus.Version = "1.0.0";
            differentInstalledChannel.Snapshot.WindowsCli.Update.SourceIdentitySha256 = UnityCliUpdateCheck.CliSourceIdentity("1.0.0", TargetCliVersion);
            AssertBindingRejected(differentInstalledChannel);

            var wrongIdentity = CreateBindingFixture(TestNow, TargetCliVersion);
            wrongIdentity.Snapshot.WindowsCli.Update.SourceIdentitySha256 = UnityCliUpdateCheck.SourceIdentity("different-check");
            AssertBindingRejected(wrongIdentity);
        }

        [Test]
        public void CliBindingRejectsWrongPathSignatureAndLocalDrift()
        {
            var wrongCheckedPath = CreateBindingFixture(TestNow, TargetCliVersion);
            wrongCheckedPath.Snapshot.WindowsCli.ExecutablePath = "C:/Other/unity.exe";
            AssertBindingRejected(wrongCheckedPath);

            var wrongLocalPath = CreateBindingFixture(TestNow, TargetCliVersion);
            wrongLocalPath.LocalStatus.ExecutablePath = "C:/Other/unity.exe";
            AssertBindingRejected(wrongLocalPath);

            var invalidSignature = CreateBindingFixture(TestNow, TargetCliVersion);
            invalidSignature.LocalStatus.ExecutableSignatureState = WindowsCliAuthenticode.InvalidState;
            AssertBindingRejected(invalidSignature);

            var mismatchedSignature = CreateBindingFixture(TestNow, TargetCliVersion);
            mismatchedSignature.LocalStatus.ExecutableSignatureThumbprint = new string('A', 40);
            AssertBindingRejected(mismatchedSignature);

            var mismatchedHash = CreateBindingFixture(TestNow, TargetCliVersion);
            mismatchedHash.LocalStatus.ExecutableSha256 = new string('a', 64);
            AssertBindingRejected(mismatchedHash);

            var localDrift = CreateBindingFixture(TestNow, TargetCliVersion);
            localDrift.LocalStatus.Version = "1.0.0-beta.1";
            AssertBindingRejected(localDrift);

            var missingExpiry = CreateBindingFixture(TestNow, TargetCliVersion);
            missingExpiry.Snapshot.WindowsCli.Update.ExpiresAtUtc = DateTime.MinValue;
            AssertBindingRejected(missingExpiry);

            var missingCheckedAt = CreateBindingFixture(TestNow, TargetCliVersion);
            missingCheckedAt.Snapshot.WindowsCli.Update.CheckedAtUtc = DateTime.MinValue;
            AssertBindingRejected(missingCheckedAt);
        }

        [Test]
        public void CliUpdateCoordinatorRunsExactPreflightMutationAndPostconditionSequence()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                new FakeProcess(0, "update complete"),
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + TargetCliVersion));

            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                PollToCompletion(coordinator);

                CollectionAssert.AreEqual(new[]
                {
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                    WindowsCliAuthenticode.PowerShellPath,
                }, starter.StartedFiles);
                Assert.That(starter.StartedArguments[0], Is.EqualTo(WindowsCliAuthenticode.BuildProbeArguments(UnityCliPipelineSetup.OfficialWindowsInstallPath).ToArray()));
                Assert.That(starter.StartedArguments[1].Last(), Does.Contain("$arguments=@('--version','--no-banner')"));
                Assert.That(starter.StartedArguments[2].Last(), Does.Contain("$arguments=@('upgrade','--help','--no-banner')"));
                Assert.That(starter.StartedArguments[3].Last(), Does.Contain("Start-Process -FilePath $path"));
                Assert.That(starter.StartedArguments[3].Last(), Does.Contain("-NoNewWindow -PassThru"));
                Assert.That(starter.StartedArguments[3].Last(), Does.Contain("}finally{$stream.Dispose()};$process.WaitForExit()"));
                Assert.That(starter.StartedArguments[3].Last(), Does.Contain("$target='" + TargetCliVersion + "'"));
                Assert.That(starter.StartedArguments[3].Last(), Does.Contain("$expectedSha256='" + ExecutableSha256 + "'"));
                Assert.That(starter.StartedArguments[4], Is.EqualTo(WindowsCliAuthenticode.BuildProbeArguments(UnityCliPipelineSetup.OfficialWindowsInstallPath).ToArray()));
                Assert.That(starter.StartedArguments[5].Last(), Does.Contain("$arguments=@('--version','--no-banner')"));
                Assert.That(starter.StartedArguments.Count(IsTrackedMutationArguments), Is.EqualTo(1));
                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateCurrent));
                Assert.That(coordinator.Result.Success, Is.True);
                Assert.That(coordinator.Result.MutationStarted, Is.True);
                Assert.That(coordinator.Result.MutationExitSucceeded, Is.True);
                Assert.That(coordinator.Result.VersionVerified, Is.True);
                Assert.That(coordinator.Result.SignatureVerified, Is.True);
                Assert.That(coordinator.Result.ObservedVersion, Is.EqualTo(TargetCliVersion));
                Assert.That(coordinator.Result.SignatureState, Is.EqualTo(WindowsCliAuthenticode.ValidState));
            }
        }

        [Test]
        public void CliUpdateCoordinatorDoesNotReportSuccessForNonzeroMutationTimeoutVersionMismatchOrSignatureFailure()
        {
            var nonzeroMutation = RunWindowsCliUpdate(new FakeProcess(23, string.Empty));
            Assert.That(nonzeroMutation.Success, Is.False);
            Assert.That(nonzeroMutation.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
            WindowsCliUpdateCoordinator.ClearLocalRefreshRequirement();

            var timedOutMutation = RunWindowsCliUpdate(new FakeProcess(-1, string.Empty, true, true));
            Assert.That(timedOutMutation.Success, Is.False);
            Assert.That(timedOutMutation.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateOutcomeUnknown));
            Assert.That(WindowsCliUpdateCoordinator.RequiresLocalRefresh, Is.True);
            AssertBindingRejected(CreateBindingFixture(TestNow, TargetCliVersion));
            WindowsCliUpdateCoordinator.ClearLocalRefreshRequirement();

            var versionMismatch = RunWindowsCliUpdate(new FakeProcess(0, "mutation complete"), "1.0.0-beta.4");
            Assert.That(versionMismatch.Success, Is.False);
            Assert.That(versionMismatch.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
            WindowsCliUpdateCoordinator.ClearLocalRefreshRequirement();

            var signatureFailure = RunWindowsCliUpdate(new FakeProcess(0, "mutation complete"), TargetCliVersion, InvalidSignatureResponse);
            Assert.That(signatureFailure.Success, Is.False);
            Assert.That(signatureFailure.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
            Assert.That(UnityCliPipelineSetup.WindowsStatus.ExecutableSignatureState, Is.EqualTo(WindowsCliAuthenticode.InvalidState));
            Assert.That(UnityCliPipelineSetup.WindowsStatus.ExecutableSha256, Is.Empty);
        }

        [Test]
        public void CliUpdateTimeoutDoesNotLaunchAutomaticPostflightAndRequiresLocalRefresh()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                new FakeProcess(-1, string.Empty, true, true),
                new FakeProcess(0, "Unity CLI " + TargetCliVersion));
            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                PollToCompletion(coordinator);

                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateOutcomeUnknown));
                Assert.That(starter.StartedFiles.Count, Is.EqualTo(4));
                Assert.That(starter.StartedArguments.Count(IsTrackedMutationArguments), Is.EqualTo(1));
                Assert.That(WindowsCliUpdateCoordinator.RequiresLocalRefresh, Is.True);
            }
        }

        [Test]
        public void UnconfirmedUpdaterTerminationBlocksLocalRefreshWhileTrackedPidIsAlive()
        {
            var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                new FakeProcess(-1, string.Empty, true, true, false, currentProcessId));
            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                PollToCompletion(coordinator);

                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateOutcomeUnknown));
                Assert.That(WindowsCliUpdateCoordinator.CanRefreshLocalState(out var error), Is.False);
                Assert.That(error, Does.Contain("still alive"));
            }
        }

        [Test]
        public void UnconfirmedPreflightTerminationAlsoBlocksFurtherProcesses()
        {
            var currentProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse, true, false, false, currentProcessId));
            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                PollToCompletion(coordinator);

                Assert.That(coordinator.Result.MutationStarted, Is.False);
                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
                Assert.That(starter.StartedFiles.Count, Is.EqualTo(1));
                Assert.That(WindowsCliUpdateCoordinator.CanRefreshLocalState(out var error), Is.False);
                Assert.That(error, Does.Contain("still alive"));
            }
        }

        [Test]
        public void CliUpdateCoordinatorRejectsInvalidPreflightSignatureBeforeMutation()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, InvalidSignatureResponse),
                new FakeProcess(0, "unused"));

            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                PollToCompletion(coordinator);

                Assert.That(coordinator.Result.Success, Is.False);
                Assert.That(coordinator.Result.MutationStarted, Is.False);
                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
                Assert.That(starter.StartedArguments.Count(IsTrackedMutationArguments), Is.Zero);
            }
        }

        [Test]
        public void CliUpdateCoordinatorRejectsChangedCommandSurfaceBeforeMutation()
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp.Replace("--target <version>", "--target-version <version>")),
                new FakeProcess(0, "unused"));

            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                PollToCompletion(coordinator);

                Assert.That(coordinator.Result.Success, Is.False);
                Assert.That(coordinator.Result.MutationStarted, Is.False);
                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
                Assert.That(coordinator.Result.ErrorCode, Is.EqualTo("cli_update_preflight_surface_changed"));
                Assert.That(starter.StartedArguments.Count(IsTrackedMutationArguments), Is.Zero);
                Assert.That(starter.StartedFiles.Count, Is.EqualTo(3));
            }
        }

        [Test]
        public void CliUpdateCancellationBeforeMutationDisposesProcessAndNeverStartsMutation()
        {
            var pendingPreflight = new FakeProcess(-1, string.Empty, false);
            var starter = new FakeProcessStarter(pendingPreflight);
            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                coordinator.Cancel();
                coordinator.Poll();

                Assert.That(pendingPreflight.Disposed, Is.True);
                Assert.That(coordinator.Result.MutationStarted, Is.False);
                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
                Assert.That(starter.StartedArguments.Count(IsTrackedMutationArguments), Is.Zero);
                Assert.That(starter.StartedFiles.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void CliUpdateCancellationAfterMutationIsOutcomeUnknownAndNeverReplays()
        {
            var pendingMutation = new FakeProcess(-1, string.Empty, false);
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                pendingMutation);

            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                Assert.That(coordinator.Poll(), Is.False);
                Assert.That(coordinator.Poll(), Is.False);
                Assert.That(coordinator.Poll(), Is.False);
                Assert.That(coordinator.Result.MutationStarted, Is.True);

                coordinator.Cancel();
                coordinator.Poll();

                Assert.That(pendingMutation.Disposed, Is.True);
                Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateOutcomeUnknown));
                Assert.That(coordinator.Result.Success, Is.False);
                Assert.That(starter.StartedArguments.Count(IsTrackedMutationArguments), Is.EqualTo(1));
                Assert.That(starter.StartedFiles.Count, Is.EqualTo(4));
                Assert.That(WindowsCliUpdateCoordinator.RequiresLocalRefresh, Is.True);
            }
        }

        [Test]
        public void PipelineBindingRequiresFreshExactMetadataCurrentProjectAndRegistryEvidence()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                    fixture.Snapshot,
                    fixture.LocalPipeline,
                    fixture.LocalCli,
                    fixture.ProjectRoot,
                    fixture.ProjectRoot,
                    TestNow,
                    out var binding,
                    out var error), Is.True, error);

                Assert.That(binding.InstalledVersion, Is.EqualTo("0.3.0"));
                Assert.That(binding.TargetVersion, Is.EqualTo(TargetPipelineVersion));
                Assert.That(binding.CanonicalProjectRoot, Is.Not.Empty);
                Assert.That(binding.WindowsProjectRoot, Is.Not.Empty);
                Assert.That(binding.PreManifestSha256, Has.Length.EqualTo(64));
                Assert.That(binding.PreLockExists, Is.True);
                Assert.That(binding.PreLockSha256, Has.Length.EqualTo(64));
                Assert.That(binding.BindingIdentitySha256, Has.Length.EqualTo(64));
            }
        }

        [Test]
        public void PipelineBindingRejectsExpiredWrongSourceMetadataAndProjectDrift()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                fixture.Snapshot.Pipeline.Update.ExpiresAtUtc = TestNow;
                AssertPipelineBindingRejected(fixture);
            }
            using (var fixture = CreatePipelineBindingFixture())
            {
                fixture.LocalPipeline.Source = "Git";
                AssertPipelineBindingRejected(fixture);
            }
            using (var fixture = CreatePipelineBindingFixture())
            {
                fixture.Snapshot.Pipeline.Update.SourceIdentitySha256 = UnityCliUpdateCheck.SourceIdentity("wrong");
                AssertPipelineBindingRejected(fixture);
            }
            using (var fixture = CreatePipelineBindingFixture())
            {
                var otherRoot = fixture.ProjectRoot + "-other";
                Directory.CreateDirectory(otherRoot);
                try
                {
                    Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                        fixture.Snapshot,
                        fixture.LocalPipeline,
                        fixture.LocalCli,
                        fixture.ProjectRoot,
                        otherRoot,
                        TestNow,
                        out _,
                        out _), Is.False);
                }
                finally
                {
                    Directory.Delete(otherRoot, true);
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SkillUpdateFinalizationReplacesAnyShippedReviewedTarget(bool legacyGeneration)
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-unity-cli-update-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var sourceCommit = legacyGeneration
                    ? UnityCliProjectSkillInstaller.LegacyReviewedSourceCommit
                    : UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit;
                var sourceHash = legacyGeneration
                    ? UnityCliProjectSkillInstaller.LegacyReviewedSkillsHash
                    : UnityCliProjectSkillInstaller.PreviousReviewedSkillsHash;
                WriteReviewedSkillTarget(root, "Previous", sourceCommit, sourceHash);
                var previousContentHash = UnityCliProjectSkillInstaller.ComputeContentHash(TargetSkillRoot(root));
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(
                    string.Empty,
                    legacyGeneration ? string.Empty : previousContentHash,
                    legacyGeneration ? previousContentHash : string.Empty);
                var previous = UnityCliProjectSkillInstaller.GetStatus(root);
                var snapshot = CreateSkillUpdateSnapshot(previous);
                Assert.That(UnityCliProjectSkillInstaller.TryCreateUpdateBinding(snapshot, previous, DateTime.UtcNow, out var binding, out var bindingError), Is.True, bindingError);
                WriteStagedSkill(root, "Current", true);
                var expectedContentHash = UnityCliProjectSkillInstaller.ComputeContentHash(StagedSkillRoot(root));
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(
                    expectedContentHash,
                    legacyGeneration ? string.Empty : previousContentHash,
                    legacyGeneration ? previousContentHash : string.Empty);

                Assert.That(UnityCliProjectSkillInstaller.TryFinalizeUpdateForTests(root, binding, expectedContentHash, out var message), Is.True, message);

                var status = UnityCliProjectSkillInstaller.GetStatus(root);
                Assert.That(status.LocalState, Is.EqualTo(UnityCliProjectSkillInstaller.LocalStateCurrentReviewed));
                Assert.That(status.InstalledReviewedCommit, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));
                Assert.That(File.ReadAllText(Path.Combine(status.SkillRoot, "SKILL.md")), Does.Contain("# Current"));
                Assert.That(Directory.Exists(Path.Combine(root, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-backup")), Is.False);
                var archives = Directory.GetDirectories(Path.Combine(root, "Library", "SGGUnityMcp", "setup"), "unity-cli-skill-backup-completed-*");
                Assert.That(archives, Has.Length.EqualTo(1));
                Assert.That(UnityCliProjectSkillInstaller.ComputeContentHash(archives[0]), Is.EqualTo(previousContentHash));
                Assert.That(File.ReadAllText(Path.Combine(archives[0], "SKILL.md")), Does.Contain("# Previous"));
            }
            finally
            {
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(string.Empty, string.Empty, string.Empty);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void SkillUpdateFinalizationRollsBackWhenReplacementFailsValidation()
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-unity-cli-rollback-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                WriteReviewedSkillTarget(root, "Previous", UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit, UnityCliProjectSkillInstaller.PreviousReviewedSkillsHash);
                var previousContentHash = UnityCliProjectSkillInstaller.ComputeContentHash(TargetSkillRoot(root));
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(string.Empty, previousContentHash);
                var previous = UnityCliProjectSkillInstaller.GetStatus(root);
                var snapshot = CreateSkillUpdateSnapshot(previous);
                Assert.That(UnityCliProjectSkillInstaller.TryCreateUpdateBinding(snapshot, previous, DateTime.UtcNow, out var binding, out var bindingError), Is.True, bindingError);
                WriteStagedSkill(root, "Invalid", false);
                var expectedContentHash = UnityCliProjectSkillInstaller.ComputeContentHash(StagedSkillRoot(root));
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(expectedContentHash, previousContentHash);

                Assert.That(UnityCliProjectSkillInstaller.TryFinalizeUpdateForTests(root, binding, expectedContentHash, out var message), Is.False);
                Assert.That(message, Does.Contain("restored"));

                var status = UnityCliProjectSkillInstaller.GetStatus(root);
                Assert.That(status.LocalState, Is.EqualTo(UnityCliProjectSkillInstaller.LocalStatePreviousReviewed));
                Assert.That(File.ReadAllText(Path.Combine(status.SkillRoot, "SKILL.md")), Does.Contain("# Previous"));
                var quarantines = Directory.GetDirectories(Path.Combine(root, "Library", "SGGUnityMcp", "setup"), "unity-cli-skill-failed-*");
                Assert.That(quarantines, Has.Length.EqualTo(1));
                Assert.That(File.ReadAllText(Path.Combine(quarantines[0], "SKILL.md")), Does.Contain("# Invalid"));
            }
            finally
            {
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(string.Empty, string.Empty);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void SkillUpdateFinalizationRejectsStagedContentWithoutReviewedHash()
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-unity-cli-content-hash-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                WriteReviewedSkillTarget(root, "Previous", UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit, UnityCliProjectSkillInstaller.PreviousReviewedSkillsHash);
                var previousContentHash = UnityCliProjectSkillInstaller.ComputeContentHash(TargetSkillRoot(root));
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(string.Empty, previousContentHash);
                var previous = UnityCliProjectSkillInstaller.GetStatus(root);
                var snapshot = CreateSkillUpdateSnapshot(previous);
                Assert.That(UnityCliProjectSkillInstaller.TryCreateUpdateBinding(snapshot, previous, DateTime.UtcNow, out var binding, out var bindingError), Is.True, bindingError);
                WriteStagedSkill(root, "Unreviewed", true);

                Assert.That(UnityCliProjectSkillInstaller.TryFinalizeUpdate(root, binding, out var message), Is.False);
                Assert.That(message, Does.Contain("content hash"));

                var status = UnityCliProjectSkillInstaller.GetStatus(root);
                Assert.That(status.LocalState, Is.EqualTo(UnityCliProjectSkillInstaller.LocalStatePreviousReviewed));
                Assert.That(File.ReadAllText(Path.Combine(status.SkillRoot, "SKILL.md")), Does.Contain("# Previous"));
            }
            finally
            {
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(string.Empty, string.Empty);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SkillUpdateNeverDeletesAConcurrentForeignTargetAndPreservesPreviousBackup(bool targetFile)
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-unity-cli-concurrent-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                WriteReviewedSkillTarget(root, "Previous", UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit, UnityCliProjectSkillInstaller.PreviousReviewedSkillsHash);
                var previousHash = UnityCliProjectSkillInstaller.ComputeContentHash(TargetSkillRoot(root));
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(string.Empty, previousHash);
                var previous = UnityCliProjectSkillInstaller.GetStatus(root);
                Assert.That(UnityCliProjectSkillInstaller.TryCreateUpdateBinding(CreateSkillUpdateSnapshot(previous), previous, DateTime.UtcNow,
                    out var binding, out var error), Is.True, error);
                WriteStagedSkill(root, "Current", true);
                var currentHash = UnityCliProjectSkillInstaller.ComputeContentHash(StagedSkillRoot(root));
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(currentHash, previousHash);
                var foreignPath = targetFile ? TargetSkillRoot(root) : Path.Combine(TargetSkillRoot(root), "foreign.txt");
                Assert.That(UnityCliProjectSkillInstaller.TryFinalizeUpdateForTests(root, binding, currentHash, out var message, () =>
                {
                    if (!targetFile) Directory.CreateDirectory(TargetSkillRoot(root));
                    File.WriteAllText(foreignPath, "foreign exact bytes");
                }), Is.False);
                Assert.That(message, Does.Contain("inspect the preserved"));
                Assert.That(File.ReadAllText(foreignPath), Is.EqualTo("foreign exact bytes"));
                var backup = Path.Combine(root, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-backup");
                Assert.That(UnityCliProjectSkillInstaller.ComputeContentHash(backup), Is.EqualTo(previousHash));
                Assert.That(UnityCliProjectSkillInstaller.ComputeContentHash(StagedSkillRoot(root)), Is.EqualTo(currentHash));
            }
            finally
            {
                UnityCliProjectSkillInstaller.SetReviewedContentHashOverridesForTests(string.Empty, string.Empty);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void PipelineMutationArgumentsAreExactProjectScopedAndExcludeOtherActions()
        {
            var binding = new UnityPipelineUpdateBinding
            {
                WindowsProjectRoot = "C:\\Work\\Unity\\Game",
                TargetVersion = TargetPipelineVersion,
            };
            var arguments = UnityPipelineUpdateOperation.BuildMutationArguments(binding).ToArray();

            Assert.That(arguments, Is.EqualTo(new[]
            {
                "pipeline", "install", "--project-path", "C:\\Work\\Unity\\Game",
                "--package-version", TargetPipelineVersion, "--format", "json", "--non-interactive", "--no-banner",
            }));
            Assert.That(arguments, Does.Not.Contain("--force"));
            Assert.That(arguments, Does.Not.Contain("auth"));
            Assert.That(arguments, Does.Not.Contain("login"));
            Assert.That(arguments, Does.Not.Contain("uninstall"));
            Assert.That(arguments, Does.Not.Contain("rollback"));
        }

        [Test]
        public void PipelineTrackedMutationRevalidatesCliManifestAndLockBeforeOnePinnedCommand()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                    fixture.Snapshot,
                    fixture.LocalPipeline,
                    fixture.LocalCli,
                    fixture.ProjectRoot,
                    fixture.ProjectRoot,
                    TestNow,
                    out var binding,
                    out var error), Is.True, error);
                var script = UnityPipelineUpdateOperation.BuildTrackedMutationArguments(binding).Last();

                Assert.That(script, Does.Contain("$expectedThumbprint"));
                Assert.That(script, Does.Contain("$expectedCliSha"));
                Assert.That(script, Does.Contain("$expectedManifestSha"));
                Assert.That(script, Does.Contain("$expectedLockSha"));
                Assert.That(script, Does.Contain("Assert-NoReparsePath $cli"));
                Assert.That(script, Does.Contain("Assert-NoReparsePath $project"));
                Assert.That(script, Does.Contain("Assert-NoReparsePath $packages"));
                Assert.That(script, Does.Contain("Start-Process -FilePath $cli"));
                var startIndex = script.IndexOf("Start-Process -FilePath $cli", StringComparison.Ordinal);
                Assert.That(script.IndexOf("$cliStream=[IO.File]::Open", StringComparison.Ordinal), Is.LessThan(startIndex));
                Assert.That(script.IndexOf("$manifestStream=[IO.File]::Open", StringComparison.Ordinal), Is.LessThan(startIndex));
                Assert.That(script.IndexOf("$lockStream=[IO.File]::Open", StringComparison.Ordinal), Is.LessThan(startIndex));
                Assert.That(startIndex, Is.LessThan(script.IndexOf("finally{if($null -ne $lockStream)", StringComparison.Ordinal)));
                Assert.That(script, Does.Contain("--package-version"));
                Assert.That(script, Does.Contain(BoundedProcessOperation.BuildArguments(UnityPipelineUpdateOperation.BuildMutationArguments(binding)).Replace("'", "''")));
                Assert.That(script, Does.Not.Contain("--force"));
                Assert.That(script, Does.Not.Contain("Invoke-Expression"));
            }
        }

        [Test]
        public void PipelineTrackedMutationQuotesProjectPathsWithSpacesAndApostrophes()
        {
            var binding = new UnityPipelineUpdateBinding
            {
                WindowsProjectRoot = "C:\\Work Files\\Owner's Game",
                TargetVersion = TargetPipelineVersion,
                CliExecutablePath = "C:\\Users\\Owner's Name\\Unity\\bin\\unity.exe",
            };
            var expected = BoundedProcessOperation.BuildArguments(UnityPipelineUpdateOperation.BuildMutationArguments(binding)).Replace("'", "''");
            var script = UnityPipelineUpdateOperation.BuildTrackedMutationArguments(binding).Last();

            Assert.That(expected, Does.Contain("\"C:\\Work Files\\Owner''s Game\""));
            Assert.That(script, Does.Contain(expected));
            Assert.That(script, Does.Contain("Owner''s Game"));
            Assert.That(script, Does.Contain("Owner''s Name"));
        }

        [Test]
        public void PipelineEvidenceParserRequiresExactDependencyAndLockPaths()
        {
            Assert.That(PipelineJsonEvidenceParser.TryReadStringPath(
                "{\"dependencies\":{\"com.unity.pipeline\":\"0.4.0-exp.1\"}}",
                new[] { "dependencies", "com.unity.pipeline" },
                out var manifestVersion), Is.True);
            Assert.That(manifestVersion, Is.EqualTo("0.4.0-exp.1"));
            Assert.That(PipelineJsonEvidenceParser.TryReadStringPath(
                "{\"other\":{\"com.unity.pipeline\":\"0.4.0-exp.1\"},\"dependencies\":{}}",
                new[] { "dependencies", "com.unity.pipeline" },
                out _), Is.False);
            Assert.That(PipelineJsonEvidenceParser.TryReadStringPath(
                "{\"dependencies\":{\"com.unity.pipeline\":\"0.3.0\",\"com.unity.pipeline\":\"0.4.0-exp.1\"}}",
                new[] { "dependencies", "com.unity.pipeline" },
                out _), Is.False);
            Assert.That(PipelineJsonEvidenceParser.TryReadStringPath(
                "{\"dependencies\":{\"com.unity.pipeline\":{\"version\":\"0.4.0-exp.1\"}}}",
                new[] { "dependencies", "com.unity.pipeline", "version" },
                out var lockVersion), Is.True);
            Assert.That(lockVersion, Is.EqualTo("0.4.0-exp.1"));
        }

        [TestCase("{}", "")]
        [TestCase("{\"dependencies\":{}}", "")]
        [TestCase("{\"other\":{\"com.romanilyin.canonicalpath\":\"old\"},\"dependencies\":{}}", "")]
        [TestCase("{\"dependencies\":{\"com.romanilyin.canonicalpath\":\"2026.10.10-1\"}}", "2026.10.10-1")]
        public void OptionalDependencyEvidenceDistinguishesValidAbsenceFromRegisteredVersion(string json, string expected)
        {
            var path = new[] { "dependencies", "com.romanilyin.canonicalpath" };
            Assert.That(PipelineJsonEvidenceParser.TryReadOptionalStringPath(json, path, out var version), Is.True);
            Assert.That(version, Is.EqualTo(expected));
            Assert.That(PipelineJsonEvidenceParser.TryReadStringPath(json, path, out _), Is.EqualTo(expected != string.Empty));
        }

        [TestCase("{\"dependencies\":{}")]
        [TestCase("{\"dependencies\":{}} trailing")]
        [TestCase("{\"dependencies\":[]}")]
        [TestCase("{\"dependencies\":{},\"dependencies\":{}}")]
        [TestCase("{\"dependencies\":{\"com.romanilyin.canonicalpath\":null}}")]
        [TestCase("{\"dependencies\":{\"com.romanilyin.canonicalpath\":\"old\",\"com.romanilyin.canonicalpath\":\"new\"}}")]
        public void OptionalDependencyEvidenceRejectsMalformedAmbiguousOrNonStringDeclarations(string json)
        {
            Assert.That(PipelineJsonEvidenceParser.TryReadOptionalStringPath(json,
                new[] { "dependencies", "com.romanilyin.canonicalpath" }, out _), Is.False);
        }

        [Test]
        public void OptionalDependencyEvidencePreservesParserSizeAndDepthLimits()
        {
            var path = new[] { "dependencies", "com.romanilyin.canonicalpath" };
            Assert.That(PipelineJsonEvidenceParser.TryReadOptionalStringPath("{}" + new string(' ', 4 * 1024 * 1024), path, out _), Is.False);
            var nested = "{\"other\":" + new string('[', 34) + "0" + new string(']', 34) + "}";
            Assert.That(PipelineJsonEvidenceParser.TryReadOptionalStringPath(nested, path, out _), Is.False);
            Assert.That(PipelineJsonEvidenceParser.TryReadStringPath(nested, path, out _), Is.False);
        }

        [Test]
        public void PipelineCoordinatorPersistsBeforeExactlyOneDispatchAndAwaitsReloadVerification()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                    fixture.Snapshot,
                    fixture.LocalPipeline,
                    fixture.LocalCli,
                    fixture.ProjectRoot,
                    fixture.ProjectRoot,
                    TestNow,
                    out var binding,
                    out var error), Is.True, error);
                var starter = new FakeProcessStarter(
                    new FakeProcess(0, PipelineInstallHelp),
                    new FakeProcess(0, "updated"));
                using (var coordinator = new UnityPipelineUpdateCoordinator(
                    fixture.Snapshot,
                    binding,
                    starter,
                    () => fixture.LocalPipeline,
                    _ => true,
                    () => TestNow,
                    TimeSpan.FromMinutes(1)))
                {
                    Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(
                        UnityPipelineUpdateOperation.PersistedStateForTests(), out var prepared), Is.True);
                    Assert.That(prepared.dispatchCount, Is.Zero);
                    Assert.That(prepared.mutationStarted, Is.False);
                    Assert.That(prepared.phase, Is.EqualTo(UnityPipelineUpdateOperation.PhasePrepared));

                    Assert.That(coordinator.Poll(), Is.False);
                    Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(
                        UnityPipelineUpdateOperation.PersistedStateForTests(), out var running), Is.True);
                    Assert.That(running.dispatchCount, Is.EqualTo(1));
                    Assert.That(running.mutationStarted, Is.True);
                    Assert.That(running.phase, Is.EqualTo(UnityPipelineUpdateOperation.PhaseMutationRunning));

                    Assert.That(coordinator.Poll(), Is.True);

                    Assert.That(starter.StartedFiles, Is.EqualTo(new[]
                    {
                        WindowsCliAuthenticode.PowerShellPath,
                        WindowsCliAuthenticode.PowerShellPath,
                    }));
                    Assert.That(starter.StartedArguments[0].Last(), Does.Contain("$arguments=@('pipeline','install','--help','--no-banner')"));
                    Assert.That(starter.StartedArguments[1].Last(), Does.Contain("Start-Process -FilePath $cli"));
                    Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(
                        UnityPipelineUpdateOperation.PersistedStateForTests(), out var awaiting), Is.True);
                    Assert.That(awaiting.dispatchCount, Is.EqualTo(1));
                    Assert.That(awaiting.mutationExitSucceeded, Is.True);
                    Assert.That(awaiting.phase, Is.EqualTo(UnityPipelineUpdateOperation.PhaseAwaitingPackage));
                    Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateInProgress));
                }
            }
        }

        [Test]
        public void PipelineCoordinatorFailureAndCancellationNeverReplayMutation()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                var coordinator = CreatePipelineUpdateCoordinator(fixture, new FakeProcess(17, string.Empty), out var starter);
                using (coordinator)
                {
                    Assert.That(coordinator.Poll(), Is.False);
                    Assert.That(coordinator.Poll(), Is.True);
                    Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
                    Assert.That(starter.StartedFiles.Count, Is.EqualTo(2));
                }
            }

            UnityPipelineUpdateOperation.ResetForTests();
            using (var fixture = CreatePipelineBindingFixture())
            {
                var pending = new FakeProcess(-1, string.Empty, false);
                var coordinator = CreatePipelineUpdateCoordinator(fixture, pending, out var starter);
                using (coordinator)
                {
                    Assert.That(coordinator.Poll(), Is.False);
                    coordinator.Cancel();
                    Assert.That(pending.Disposed, Is.True);
                    Assert.That(coordinator.Result.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateOutcomeUnknown));
                    Assert.That(starter.StartedFiles.Count, Is.EqualTo(2));
                    Assert.That(UnityPipelineUpdateOperation.RequiresLocalRefresh, Is.True);
                }
            }
        }

        [Test]
        public void PipelineCoordinatorRejectsChangedCommandSurfaceAndLocalDriftBeforeDispatch()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                var coordinator = CreatePipelineUpdateCoordinator(
                    fixture,
                    new FakeProcess(0, "unused"),
                    out var starter,
                    PipelineInstallHelp.Replace("--package-version <version>", "--version <version>"));
                using (coordinator)
                {
                    Assert.That(coordinator.Poll(), Is.True);
                    Assert.That(coordinator.Result.MutationStarted, Is.False);
                    Assert.That(coordinator.Result.ErrorCode, Is.EqualTo("pipeline_update_preflight_surface_changed"));
                    Assert.That(starter.StartedFiles.Count, Is.EqualTo(1));
                }
            }

            UnityPipelineUpdateOperation.ResetForTests();
            using (var fixture = CreatePipelineBindingFixture())
            {
                var coordinator = CreatePipelineUpdateCoordinator(fixture, new FakeProcess(0, "unused"), out var starter);
                using (coordinator)
                {
                    fixture.LocalPipeline.Version = "0.2.0";
                    Assert.That(coordinator.Poll(), Is.True);
                    Assert.That(coordinator.Result.MutationStarted, Is.False);
                    Assert.That(coordinator.Result.ErrorCode, Is.EqualTo("pipeline_update_preflight_changed"));
                    Assert.That(starter.StartedFiles.Count, Is.EqualTo(1));
                }
            }
        }

        [Test]
        public void PipelinePreparedStateBecomesTerminalAfterReloadWithoutDispatchOrRefreshBlocker()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                    fixture.Snapshot,
                    fixture.LocalPipeline,
                    fixture.LocalCli,
                    fixture.ProjectRoot,
                    fixture.ProjectRoot,
                    TestNow,
                    out var binding,
                    out var error), Is.True, error);
                var state = UnityPipelineUpdateOperation.CreateState(binding, TestNow);
                UnityPipelineUpdateOperation.Persist(state);

                Assert.That(UnityPipelineUpdateOperation.FinishInterruptedPreparedStateForTests(state, TestNow.AddSeconds(1)), Is.True);
                Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(
                    UnityPipelineUpdateOperation.PersistedStateForTests(), out var completed), Is.True);
                Assert.That(completed.phase, Is.EqualTo(UnityPipelineUpdateOperation.PhaseComplete));
                Assert.That(completed.dispatchCount, Is.Zero);
                Assert.That(completed.mutationStarted, Is.False);
                Assert.That(completed.updateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateVerificationRequired));
                Assert.That(completed.errorCode, Is.EqualTo("pipeline_update_preflight_interrupted"));
                Assert.That(completed.localRefreshCompleted, Is.True);
                Assert.That(UnityPipelineUpdateOperation.HasActiveOperation, Is.False);
                Assert.That(UnityPipelineUpdateOperation.RequiresLocalRefresh, Is.False);
            }
        }

        [Test]
        public void PipelinePersistedStateRejectsMalformedOrReplayableRecords()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                    fixture.Snapshot,
                    fixture.LocalPipeline,
                    fixture.LocalCli,
                    fixture.ProjectRoot,
                    fixture.ProjectRoot,
                    TestNow,
                    out var binding,
                    out var error), Is.True, error);
                var state = UnityPipelineUpdateOperation.CreateState(binding, TestNow);
                state.dispatchCount = 1;
                state.phase = UnityPipelineUpdateOperation.PhaseMutationRunning;
                state.mutationStarted = true;
                state.processId = 12345;
                state.deliveryCertainty = "dispatched_once";
                state.terminationConfirmed = false;
                var serialized = state.Serialize();

                Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(serialized, out var parsed), Is.True);
                Assert.That(parsed.bindingIdentitySha256, Is.EqualTo(binding.BindingIdentitySha256));
                Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(serialized.Replace("\"dispatchCount\":1", "\"dispatchCount\":2"), out _), Is.False);
                Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(serialized.Replace(binding.TargetVersion, "latest"), out _), Is.False);
                Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(serialized.Replace(binding.CanonicalProjectRoot, binding.CanonicalProjectRoot + "-other"), out _), Is.False);
                Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests("not-json", out _), Is.False);
            }
        }

        [Test]
        public void PipelineRecoveryRecordsRefreshWithoutErasingNoReplayState()
        {
            using (var fixture = CreatePipelineBindingFixture())
            {
                Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                    fixture.Snapshot,
                    fixture.LocalPipeline,
                    fixture.LocalCli,
                    fixture.ProjectRoot,
                    fixture.ProjectRoot,
                    TestNow,
                    out var binding,
                    out var error), Is.True, error);
                var state = UnityPipelineUpdateOperation.CreateState(binding, TestNow);
                state.phase = UnityPipelineUpdateOperation.PhaseComplete;
                state.updateState = UnityCliUpdateCheck.UpdateStateOutcomeUnknown;
                state.dispatchCount = 1;
                state.mutationStarted = true;
                state.deliveryCertainty = "uncertain";
                state.terminationConfirmed = true;
                state.actualManifestVersion = "0.3.0";
                state.actualRegisteredVersion = "0.3.0";
                state.actualRegisteredSource = "Registry";
                UnityPipelineUpdateOperation.Persist(state);

                Assert.That(UnityPipelineUpdateOperation.RequiresLocalRefresh, Is.True);
                state.localRefreshCompleted = true;
                state.errorCode = "pipeline_update_local_state_refreshed";
                state.summary = "Actual state was refreshed; a new manual check is required.";
                UnityPipelineUpdateOperation.Persist(state);

                Assert.That(UnityPipelineUpdateOperation.RequiresLocalRefresh, Is.False);
                Assert.That(UnityPipelineUpdateOperation.PersistedStateForTests(), Is.Not.Empty);
                Assert.That(UnityPipelineUpdateOperation.TryParseStateForTests(UnityPipelineUpdateOperation.PersistedStateForTests(), out var persisted), Is.True);
                Assert.That(persisted.noReplay, Is.True);
                Assert.That(persisted.deliveryCertainty, Is.EqualTo("uncertain"));
                Assert.That(persisted.actualRegisteredVersion, Is.EqualTo("0.3.0"));
            }
        }

        [Test]
        public void PipelineVerificationRequiresExactRegistryPackageAndCleanPostReloadBarrier()
        {
            var status = new UnityCliPipelineSetup.UnityPipelineStatus
            {
                Registered = true,
                ManifestVersion = TargetPipelineVersion,
                Version = TargetPipelineVersion,
                Source = "Registry",
            };

            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, true, true, false, true, false), Is.EqualTo("current"));
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, true, false, false, true, false), Is.EqualTo("wait"));
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, false, true, false, true, false), Is.EqualTo("wait"));
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, true, true, false, true, true), Is.EqualTo("verification_required"));
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, true, false, true, true, true), Is.EqualTo("compilation_failed"));
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(null, TargetPipelineVersion, true, false, true, true, true), Is.EqualTo("compilation_failed"));
            status.Source = "Git";
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, true, false, true, true, true), Is.EqualTo("compilation_failed"));
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, true, true, false, true, false), Is.EqualTo("verification_required"));
            status.Source = "Registry";
            status.Version = "0.3.0";
            Assert.That(UnityPipelineUpdateOperation.EvaluateVerification(status, TargetPipelineVersion, true, true, false, true, false), Is.EqualTo("verification_required"));
        }

        [Test]
        public void StartReusesActiveCoordinatorWithoutAutomaticReplacement()
        {
            var firstProcess = new FakeProcess(-1, string.Empty, false);
            var firstStarter = new FakeProcessStarter(firstProcess);
            var first = UnityCliUpdateCheck.StartForTests(
                "C:/Project",
                CreateCoordinatorSnapshot(),
                firstStarter,
                () => DateTime.UtcNow,
                TimeSpan.FromMinutes(1));

            try
            {
                var replacementStarter = new FakeProcessStarter(new FakeProcess(0, string.Empty));
                var second = UnityCliUpdateCheck.StartForTests(
                    "C:/OtherProject",
                    CreateCoordinatorSnapshot(),
                    replacementStarter,
                    () => DateTime.UtcNow,
                    TimeSpan.FromMinutes(1));

                Assert.That(second, Is.SameAs(first));
                Assert.That(firstProcess.Disposed, Is.False);
                Assert.That(replacementStarter.StartedFiles, Is.Empty);
            }
            finally
            {
                first.Cancel();
                UnityCliUpdateCheck.ResetCacheForTests();
            }
        }

        [Test]
        public void CoordinatorCancellationDisposesCurrentProcessAndDoesNotContinue()
        {
            var pending = new FakeProcess(-1, string.Empty, false);
            var starter = new FakeProcessStarter(pending);
            using (var coordinator = new UnityCliUpdateCheckCoordinator("C:/Project", CreateCoordinatorSnapshot(), starter, () => DateTime.UtcNow, TimeSpan.FromMinutes(1)))
            {
                coordinator.Cancel();

                Assert.That(pending.Disposed, Is.True);
                Assert.That(starter.StartedFiles.Count, Is.EqualTo(1));
                Assert.That(coordinator.IsCancelled, Is.True);
                Assert.That(coordinator.Status.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateOutcomeUnknown));
            }
        }

        static UnityCliUpdateCheckSnapshot CreateCoordinatorSnapshot()
        {
            return new UnityCliUpdateCheckSnapshot(
                new UnityCliPipelineSetup.WindowsUnityCliStatus
                {
                    Update = ComponentUpdateStatus.Checking("1.0.0-beta.2", UnityCliUpdateCheck.CliChannel, "official_unity_cli_upgrade_check", ""),
                },
                new UnityCliPipelineSetup.UnityPipelineStatus
                {
                    EditorSupported = true,
                    Registered = true,
                    Version = "0.3.0",
                    Update = ComponentUpdateStatus.Checking("0.3.0", "", "official_unity_pipeline_list_versions", ""),
                },
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus
                {
                    Exists = true,
                    Valid = true,
                    ProvenanceVerified = true,
                    LocalState = UnityCliProjectSkillInstaller.LocalStateCurrentReviewed,
                    InstalledReviewedCommit = UnityCliProjectSkillInstaller.ReviewedSourceCommit,
                    InstalledReviewedHash = UnityCliProjectSkillInstaller.ReviewedSkillsHash,
                    ContentHash = UnityCliProjectSkillInstaller.ReviewedContentHash,
                    Update = ComponentUpdateStatus.Checking(
                        UnityCliProjectSkillInstaller.ReviewedSourceCommit,
                        UnityCliUpdateCheck.SkillChannel,
                        UnityCliUpdateCheck.SkillSourceKind,
                        ""),
                });
        }

        static UnityCliProjectSkillInstaller.UnityCliSkillStatus CreatePreviousReviewedSkillStatus(string projectRoot)
        {
            var root = Path.GetFullPath(projectRoot);
            var skillRoot = Path.Combine(root, ".agents", "skills", UnityCliProjectSkillInstaller.SkillName);
            return new UnityCliProjectSkillInstaller.UnityCliSkillStatus
            {
                Exists = true,
                Valid = true,
                ProvenanceVerified = true,
                ValidFrontmatter = true,
                SkillRoot = skillRoot,
                SkillPath = Path.Combine(skillRoot, "SKILL.md"),
                LocalState = UnityCliProjectSkillInstaller.LocalStatePreviousReviewed,
                SourceProvenance = "previous_reviewed",
                InstalledReviewedCommit = UnityCliProjectSkillInstaller.PreviousReviewedSourceCommit,
                InstalledReviewedHash = UnityCliProjectSkillInstaller.PreviousReviewedSkillsHash,
                ContentHash = UnityCliProjectSkillInstaller.PreviousReviewedContentHash,
                Update = ComponentUpdateStatus.NotChecked(),
            };
        }

        static UnityCliUpdateCheckSnapshot CreateSkillUpdateSnapshot(UnityCliProjectSkillInstaller.UnityCliSkillStatus previous)
        {
            var update = ComponentUpdateStatus.Checked(
                previous.InstalledReviewedCommit,
                UnityCliProjectSkillInstaller.ReviewedSourceCommit,
                UnityCliUpdateCheck.SkillChannel,
                UnityCliUpdateCheck.SkillSourceKind,
                UnityCliUpdateCheck.SkillSourceIdentity(UnityCliProjectSkillInstaller.ReviewedSourceCommit),
                DateTime.UtcNow);
            update.UpdateState = UnityCliUpdateCheck.UpdateStateAvailable;
            previous.Update = update;
            return new UnityCliUpdateCheckSnapshot(
                UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked(),
                new UnityCliPipelineSetup.UnityPipelineStatus { Update = ComponentUpdateStatus.NotChecked() },
                previous)
            {
                IsComplete = true,
            };
        }

        static void WriteReviewedSkillTarget(string projectRoot, string title, string commit, string skillsHash)
        {
            var skillRoot = TargetSkillRoot(projectRoot);
            WriteSkillFiles(skillRoot, title, true);
            var contentHash = UnityCliProjectSkillInstaller.ComputeContentHash(skillRoot);
            File.WriteAllText(Path.Combine(skillRoot, ".sgg-unity-cli-source"), commit + "\n" + skillsHash + "\n" + contentHash + "\n");
        }

        static string TargetSkillRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".agents", "skills", UnityCliProjectSkillInstaller.SkillName);
        }

        static void WriteStagedSkill(string projectRoot, string title, bool validFrontmatter)
        {
            var stagingRoot = Path.Combine(projectRoot, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-staging");
            var skillRoot = StagedSkillRoot(projectRoot);
            WriteSkillFiles(skillRoot, title, validFrontmatter);
            File.WriteAllText(Path.Combine(stagingRoot, "skills-lock.json"),
                "{\"version\":1,\"skills\":{\"unity-cli\":{\"source\":\"Unity-Technologies/skills\",\"sourceType\":\"github\",\"skillPath\":\"skills/unity-cli/SKILL.md\",\"computedHash\":\""
                + UnityCliProjectSkillInstaller.ReviewedSkillsHash
                + "\"}}}");
        }

        static string StagedSkillRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "SGGUnityMcp", "setup", "unity-cli-skill-staging", ".agents", "skills", UnityCliProjectSkillInstaller.SkillName);
        }

        static void WriteSkillFiles(string skillRoot, string title, bool validFrontmatter)
        {
            Directory.CreateDirectory(skillRoot);
            var text = validFrontmatter
                ? "---\nname: unity-cli\n---\n# " + title + "\n"
                : "---\nname: other\n---\n# " + title + "\n";
            File.WriteAllText(Path.Combine(skillRoot, "SKILL.md"), text);
        }

        static BindingFixture CreateBindingFixture(DateTime now, string targetVersion)
        {
            var sourceIdentity = UnityCliUpdateCheck.CliSourceIdentity(CurrentCliVersion, targetVersion);
            var update = ComponentUpdateStatus.Checked(
                CurrentCliVersion,
                targetVersion,
                UnityCliUpdateCheck.CliChannel,
                "official_unity_cli_upgrade_check",
                sourceIdentity,
                now.AddMinutes(-1));
            update.UpdateState = UnityCliUpdateCheck.UpdateStateAvailable;

            var checkedStatus = CreateTrustedWindowsStatus(CurrentCliVersion);
            checkedStatus.Update = update;
            var localStatus = CreateTrustedWindowsStatus(CurrentCliVersion);
            var snapshot = new UnityCliUpdateCheckSnapshot(
                checkedStatus,
                new UnityCliPipelineSetup.UnityPipelineStatus { Update = ComponentUpdateStatus.NotChecked() },
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus { Update = ComponentUpdateStatus.NotChecked() })
            {
                IsComplete = true,
            };

            return new BindingFixture
            {
                Snapshot = snapshot,
                LocalStatus = localStatus,
            };
        }

        static PipelineBindingFixture CreatePipelineBindingFixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-pipeline-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Assets"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{\"dependencies\":{\"com.unity.pipeline\":\"0.3.0\"}}");
            File.WriteAllText(Path.Combine(root, "Packages", "packages-lock.json"), "{\"dependencies\":{\"com.unity.pipeline\":{\"version\":\"0.3.0\"}}}");

            var versionsIdentity = UnityCliUpdateCheck.PipelineVersionsIdentity(new[] { "0.3.0", "0.4.0-exp.1", TargetPipelineVersion });
            var sourceIdentity = UnityCliUpdateCheck.PipelineSourceIdentity("0.3.0", TargetPipelineVersion, versionsIdentity, CurrentCliVersion, UnityEngine.Application.unityVersion);
            var update = ComponentUpdateStatus.Checked(
                "0.3.0",
                TargetPipelineVersion,
                string.Empty,
                "official_unity_pipeline_list_versions",
                sourceIdentity,
                TestNow.AddMinutes(-1));
            update.UpdateState = UnityCliUpdateCheck.UpdateStateAvailable;
            var checkedPipeline = new UnityCliPipelineSetup.UnityPipelineStatus
            {
                EditorSupported = true,
                Registered = true,
                ManifestVersion = "0.3.0",
                Version = "0.3.0",
                Source = "Registry",
                Update = update,
                TargetPackageVersion = TargetPipelineVersion,
                EditorCompatibilityState = "supported",
                CliCompatibilityState = "supported",
                AvailableVersionsIdentitySha256 = versionsIdentity,
                CheckedCliVersion = CurrentCliVersion,
                CheckedEditorVersion = UnityEngine.Application.unityVersion,
            };
            var checkedCli = CreateTrustedWindowsStatus(CurrentCliVersion);
            var localCli = CreateTrustedWindowsStatus(CurrentCliVersion);
            var localPipeline = new UnityCliPipelineSetup.UnityPipelineStatus
            {
                EditorSupported = true,
                Registered = true,
                ManifestVersion = "0.3.0",
                Version = "0.3.0",
                Source = "Registry",
            };
            var snapshot = new UnityCliUpdateCheckSnapshot(
                checkedCli,
                checkedPipeline,
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus { Update = ComponentUpdateStatus.NotChecked() })
            {
                IsComplete = true,
            };
            return new PipelineBindingFixture
            {
                ProjectRoot = root,
                Snapshot = snapshot,
                LocalPipeline = localPipeline,
                LocalCli = localCli,
            };
        }

        static void AssertPipelineBindingRejected(PipelineBindingFixture fixture)
        {
            Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                fixture.Snapshot,
                fixture.LocalPipeline,
                fixture.LocalCli,
                fixture.ProjectRoot,
                fixture.ProjectRoot,
                TestNow,
                out _,
                out var error), Is.False);
            Assert.That(error, Is.Not.Empty);
        }

        static UnityPipelineUpdateCoordinator CreatePipelineUpdateCoordinator(
            PipelineBindingFixture fixture,
            FakeProcess process,
            out FakeProcessStarter starter,
            string helpOutput = PipelineInstallHelp)
        {
            Assert.That(UnityPipelineUpdateOperation.TryCreateBinding(
                fixture.Snapshot,
                fixture.LocalPipeline,
                fixture.LocalCli,
                fixture.ProjectRoot,
                fixture.ProjectRoot,
                TestNow,
                out var binding,
                out var error), Is.True, error);
            starter = new FakeProcessStarter(new FakeProcess(0, helpOutput), process);
            return new UnityPipelineUpdateCoordinator(
                fixture.Snapshot,
                binding,
                starter,
                () => fixture.LocalPipeline,
                _ => true,
                () => TestNow,
                TimeSpan.FromMinutes(1));
        }

        static UnityCliPipelineSetup.WindowsUnityCliStatus CreateTrustedWindowsStatus(string version)
        {
            return new UnityCliPipelineSetup.WindowsUnityCliStatus
            {
                Checked = true,
                Installed = true,
                ExecutablePath = UnityCliPipelineSetup.OfficialWindowsInstallPath,
                Version = version,
                Message = "Installed " + version,
                ExecutableSignatureState = WindowsCliAuthenticode.ValidState,
                ExecutableSigner = WindowsCliAuthenticode.UnitySigner,
                ExecutableSignatureThumbprint = SignatureThumbprint,
                ExecutableSha256 = ExecutableSha256,
                Update = ComponentUpdateStatus.NotChecked(),
            };
        }

        static void AssertBindingRejected(BindingFixture fixture)
        {
            Assert.That(
                WindowsCliUpdateCoordinator.TryCreateBinding(fixture.Snapshot, fixture.LocalStatus, TestNow, out var binding, out var error),
                Is.False,
                "Unexpected binding: " + (binding == null ? string.Empty : binding.TargetVersion));
            Assert.That(error, Is.Not.Empty);
        }

        static WindowsCliUpdateCoordinator CreateWindowsCliUpdateCoordinator(FakeProcessStarter starter)
        {
            var fixture = CreateBindingFixture(TestNow, TargetCliVersion);
            Assert.That(
                WindowsCliUpdateCoordinator.TryCreateBinding(fixture.Snapshot, fixture.LocalStatus, TestNow, out var binding, out var error),
                Is.True,
                error);
            return new WindowsCliUpdateCoordinator(fixture.Snapshot, binding, starter, () => TestNow, TimeSpan.FromMinutes(1));
        }

        static WindowsCliUpdateResult RunWindowsCliUpdate(
            FakeProcess mutationProcess,
            string postVersion = TargetCliVersion,
            string postSignature = ValidSignatureResponse)
        {
            var starter = new FakeProcessStarter(
                new FakeProcess(0, ValidSignatureResponse),
                new FakeProcess(0, "Unity CLI " + CurrentCliVersion),
                new FakeProcess(0, CliUpgradeHelp),
                mutationProcess,
                new FakeProcess(0, postSignature),
                new FakeProcess(0, "Unity CLI " + postVersion));
            using (var coordinator = CreateWindowsCliUpdateCoordinator(starter))
            {
                PollToCompletion(coordinator);
                return coordinator.Result;
            }
        }

        static bool IsTrackedMutationArguments(string[] arguments)
        {
            return arguments != null && arguments.Any(value => value != null && value.Contains("Start-Process -FilePath $path"));
        }

        static void PollToCompletion(WindowsCliUpdateCoordinator coordinator)
        {
            for (var index = 0; index < 16 && !coordinator.Poll(); index++)
            {
            }

            Assert.That(coordinator.IsComplete, Is.True, "Windows CLI update coordinator did not complete within the bounded test polls.");
        }

        static void PollToCompletion(UnityCliUpdateCheckCoordinator coordinator)
        {
            for (var index = 0; index < 16 && !coordinator.Poll(); index++)
            {
            }

            Assert.That(coordinator.IsComplete, Is.True, "Unity CLI update-check coordinator did not complete within the bounded test polls.");
        }

        sealed class BindingFixture
        {
            public UnityCliUpdateCheckSnapshot Snapshot;
            public UnityCliPipelineSetup.WindowsUnityCliStatus LocalStatus;
        }

        sealed class PipelineBindingFixture : IDisposable
        {
            public string ProjectRoot;
            public UnityCliUpdateCheckSnapshot Snapshot;
            public UnityCliPipelineSetup.UnityPipelineStatus LocalPipeline;
            public UnityCliPipelineSetup.WindowsUnityCliStatus LocalCli;

            public void Dispose()
            {
                UnityPipelineUpdateOperation.ResetForTests();
                if (!string.IsNullOrEmpty(ProjectRoot) && Directory.Exists(ProjectRoot)) Directory.Delete(ProjectRoot, true);
            }
        }

        sealed class FakeProcessStarter : IUpdateCheckProcessStarter
        {
            readonly Queue<FakeProcess> m_processes;
            public readonly List<string> StartedFiles = new List<string>();
            public readonly List<string[]> StartedArguments = new List<string[]>();

            public FakeProcessStarter(params FakeProcess[] processes)
            {
                m_processes = new Queue<FakeProcess>(processes);
            }

            public bool TryStart(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, out IUpdateCheckProcess operation, out string error)
            {
                StartedFiles.Add(fileName);
                StartedArguments.Add(arguments.ToArray());
                error = string.Empty;
                if (m_processes.Count == 0)
                {
                    operation = null;
                    error = "no fake process";
                    return false;
                }

                operation = m_processes.Dequeue();
                return true;
            }
        }

        sealed class FakeProcess : IUpdateCheckProcess
        {
            readonly bool m_completes;
            readonly bool m_timedOut;
            readonly bool m_terminationConfirmed;
            readonly int m_processId;
            public readonly int ExitCodeValue;
            public readonly string Output;
            public bool Disposed;

            public FakeProcess(int exitCode, string output, bool completes = true, bool timedOut = false, bool terminationConfirmed = true, int processId = 12345)
            {
                ExitCodeValue = exitCode;
                Output = output;
                m_completes = completes;
                m_timedOut = timedOut;
                m_terminationConfirmed = terminationConfirmed;
                m_processId = processId;
            }

            public int ExitCode => ExitCodeValue;
            public string StandardOutput => Output;
            public string StandardError => string.Empty;
            public bool TimedOut => m_timedOut;
            public bool TerminationConfirmed => m_terminationConfirmed;
            public int ProcessId => m_processId;
            public bool Poll() => m_completes;
            public void Dispose() => Disposed = true;
        }
    }
}
