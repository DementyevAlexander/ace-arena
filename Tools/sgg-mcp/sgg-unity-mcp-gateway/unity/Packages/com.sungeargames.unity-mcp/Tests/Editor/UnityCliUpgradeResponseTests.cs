using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityCliUpgradeResponseTests
    {
        // Captured from signed official beta.9 with upgrade --check --channel beta
        // on 2026-10-11. The target was reported only; no update was executed.
        const string Beta9Response = "{\"success\":true,\"command\":\"self-update\",\"data\":{\"currentVersion\":\"1.0.0-beta.9\",\"latestVersion\":\"1.0.0-beta.13\",\"platform\":\"win32-x64\"},\"errors\":[],\"warnings\":[]}";
        // Relevant verbatim declarations from beta.9 upgrade --help --no-banner.
        const string Beta9Help = "Usage: unity self-update|upgrade [options]\n\nUpdate the unity CLI to the latest version\n\nOptions:\n  --check              Check for updates without installing\n  -y, --yes            Skip confirmation prompt\n  --channel <channel>  Update channel: stable or beta\n  --target <version>   Update to a specific version\n\nGlobal Options:\n  --format <format>    Output format: human, json, tsv, ndjson, github (env:\n                       UNITY_FORMAT)\n  --no-banner          Suppress the startup banner (env: UNITY_NO_BANNER)\n  --non-interactive    Disable interactive prompts. Useful in CI/CD\n                       environments. (env: UNITY_NON_INTERACTIVE)\n";
        const string PipelineHelp = "Usage: unity pipeline install [options]\nOptions:\n  --project-path <path>  Unity project path\n  --package-version <version>  Exact package version\nGlobal Options:\n  --format <format>  Output format\n  --non-interactive  Disable prompts\n  --no-banner  Suppress the banner\n";
        const string PipelineResponse = "{\"success\":true,\"command\":\"pipeline list-versions\",\"data\":{\"packageId\":\"com.unity.pipeline\",\"latest\":\"0.5.0-exp.1\",\"versions\":[\"0.4.0-exp.1\",\"0.5.0-exp.1\"]},\"errors\":[],\"warnings\":[]}";
        const string Beta9PipelineResponse = "{\"success\":true,\"command\":\"pipeline list-versions\",\"data\":{\"packageId\":\"com.unity.pipeline\",\"latest\":\"0.8.0-exp.1\",\"versions\":[\"0.8.0-exp.1\",\"0.7.0-exp.1\",\"0.6.0-exp.1\",\"0.5.0-exp.1\",\"0.4.0-exp.1\",\"0.3.1-exp.1\",\"0.3.0-exp.1\",\"0.2.0-exp.2\"]},\"errors\":[],\"warnings\":[]}";
        const string SignatureResponse = "{\"status\":\"Valid\",\"subject\":\"CN=Unity Technologies SF, O=Unity Technologies SF\",\"thumbprint\":\"0123456789ABCDEF0123456789ABCDEF01234567\",\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}";
        static DateTime TestNow => new DateTime(2026, 10, 11, 19, 5, 0, DateTimeKind.Utc);
        UnityCliPipelineSetup.WindowsUnityCliStatus m_previousWindowsStatus;

        [SetUp]
        public void SaveSharedState()
        {
            m_previousWindowsStatus = UnityCliPipelineSetup.WindowsStatus;
            UnityCliUpdateCheck.ResetCacheForTests();
        }

        [TearDown]
        public void RestoreSharedState()
        {
            UnityCliUpdateCheck.ResetCacheForTests();
            UnityCliPipelineSetup.SetWindowsStatus(m_previousWindowsStatus);
        }

        [Test]
        public void SignedBeta9CanonicalResponseAndHelpExposeExactTargetContract()
        {
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(Beta9Response, out var metadata, out var error), Is.True, error);
            Assert.That(metadata.CurrentVersion, Is.EqualTo("1.0.0-beta.9"));
            Assert.That(metadata.LatestVersion, Is.EqualTo("1.0.0-beta.13"));
            Assert.That(metadata.Platform, Is.EqualTo(UnityCliUpdateCheck.CliPlatform));
            Assert.That(metadata.Channel, Is.EqualTo(UnityCliUpdateCheck.CliChannel));
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(Beta9Help), Is.True);
        }

        [Test]
        public void LegacyUpgradeResponseAndHelpRemainSupported()
        {
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(
                Beta9Response.Replace("self-update", "upgrade"), out _, out var error), Is.True, error);
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(
                Beta9Help.Replace("self-update|upgrade", "upgrade")), Is.True);
        }

        [Test]
        public void VerifiedSchemaAllowsMemberOrderWhitespaceAndStandardJsonEscapes()
        {
            const string reordered = "{\"warnings\":[],\"errors\":[],\"data\":{\"platform\":\"win32-x64\",\"latestVersion\":\"1.0.0-beta.13\",\"currentVersion\":\"1.0.0-beta.9\"},\"com\\u006dand\":\"self\\u002dupdate\",\"success\":true}";
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(reordered, out var metadata, out var error), Is.True, error);
            Assert.That(metadata.LatestVersion, Is.EqualTo("1.0.0-beta.13"));
        }

        [TestCase("\"success\":true", "\"success\":\"true\"")]
        [TestCase("\"success\":true", "\"success\":1")]
        [TestCase("\"success\":true", "\"success\":null")]
        [TestCase("\"success\":true", "\"success\":true,\"success\":true")]
        [TestCase("\"command\":\"self-update\"", "\"command\":\"upgrade-check\"")]
        [TestCase("\"command\":\"self-update\"", "\"command\":\"wrong\",\"com\\u006dand\":\"self-update\"")]
        [TestCase("\"currentVersion\":\"1.0.0-beta.9\"", "\"currentVersion\":9")]
        [TestCase("\"currentVersion\":\"1.0.0-beta.9\"", "\"currentVersion\":\"1.0.0-beta.9\",\"currentVersion\":\"1.0.0-beta.9\"")]
        [TestCase("\"latestVersion\":\"1.0.0-beta.13\"", "\"latestVersion\":null")]
        [TestCase("\"latestVersion\":\"1.0.0-beta.13\"", "\"latestVersion\":\"latest\"")]
        [TestCase("\"latestVersion\":\"1.0.0-beta.13\"", "\"latestVersion\":\"1.0.0-beta.013\"")]
        [TestCase("win32-x64", "win32-arm64")]
        [TestCase("\"platform\":\"win32-x64\"", "\"platform\":null")]
        [TestCase("\"errors\":[]", "\"errors\":null")]
        [TestCase("\"errors\":[]", "\"errors\":[1]")]
        [TestCase("\"errors\":[]", "\"errors\":[{}]")]
        [TestCase("\"warnings\":[]", "\"warnings\":false")]
        [TestCase("\"warnings\":[]", "\"warnings\":[null]")]
        [TestCase(",\"warnings\":[]", "")]
        [TestCase(",\"platform\":\"win32-x64\"", "")]
        [TestCase("\"success\":true", "\"success\":true,\"schemaVersion\":2")]
        [TestCase("\"platform\":\"win32-x64\"", "\"platform\":\"win32-x64\",\"channel\":\"beta\"")]
        [TestCase("\"warnings\":[]", "\"warnings\":[],")]
        public void ResponseRejectsUnverifiedSchemaDuplicateKeysAndWrongTypes(string before, string after)
        {
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(Beta9Response.Replace(before, after), out var metadata, out var error), Is.False);
            Assert.That(metadata, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [TestCase("1.0.0-beta.9", "1.0.0")]
        [TestCase("1.0.0-beta.13", "1.0.0-alpha.13")]
        public void BothCurrentAndTargetMustRemainInTheReviewedBetaChannel(string before, string after)
        {
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(Beta9Response.Replace(before, after), out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo("cli_channel_mismatch"));
        }

        [Test]
        public void ResponseRequiresOneCompleteSuccessfulBoundedDocument()
        {
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(" \r\n" + Beta9Response + "\t\n", out _, out var validError), Is.True, validError);
            foreach (var output in new[] { string.Empty, "null", "[]", Beta9Response + Beta9Response, "banner\n" + Beta9Response, Beta9Response + "\0", Beta9Response.Substring(0, Beta9Response.Length - 1) })
            {
                Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(output, out _, out _), Is.False, output);
            }
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(new string(' ', UnityCliUpdateCheck.MaxMetadataResponseCharacters + 1), out _, out var oversizedError), Is.False);
            Assert.That(oversizedError, Is.EqualTo("cli_response_oversized"));
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(Beta9Response.Replace("\"success\":true", "\"success\":false"), out _, out var failedError), Is.False);
            Assert.That(failedError, Is.EqualTo("cli_check_unsuccessful"));
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(Beta9Response.Replace("\"errors\":[]", "\"errors\":[\"failed\"]"), out _, out var errorsError), Is.False);
            Assert.That(errorsError, Is.EqualTo("cli_response_errors"));
        }

        [Test]
        public void MessagesKeepExactCountLengthAndNullBounds()
        {
            var maximum = string.Join(",", Enumerable.Repeat("\"warning\"", UnityCliUpdateCheck.MaxMetadataEntries));
            Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(Beta9Response.Replace("\"warnings\":[]", "\"warnings\":[" + maximum + "]"), out _, out var error), Is.True, error);
            var tooMany = maximum + ",\"warning\"";
            var tooLong = "\"" + new string('x', UnityCliUpdateCheck.MaxMetadataEntryCharacters + 1) + "\"";
            foreach (var messages in new[] { tooMany, tooLong, "\"invalid\\u0000warning\"" })
            {
                Assert.That(UnityCliUpdateCheck.TryParseCliCheckResponse(Beta9Response.Replace("\"warnings\":[]", "\"warnings\":[" + messages + "]"), out _, out _), Is.False);
            }
        }

        [TestCase("self-update|upgrade", "self-update")]
        [TestCase("self-update|upgrade", "upgrade|self-update")]
        [TestCase("--target <version>", "--target-version <version>")]
        [TestCase("-y, --yes            Skip", "-y, --yes <value>    Skip")]
        [TestCase("--non-interactive    Disable", "--non-interactive <mode>  Disable")]
        public void CanonicalAliasHelpDoesNotRelaxExactMutationOptions(string before, string after)
        {
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(Beta9Help.Replace(before, after)), Is.False);
        }

        [Test]
        public void HelpRejectsAmbiguousUsageAndOptionSections()
        {
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(Beta9Help + "Usage: unity upgrade [options]\n"), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(Beta9Help.Replace("Global Options:", "  --target <version>   Duplicate\nGlobal Options:")), Is.False);
            Assert.That(UnityCliUpdateCheck.TryValidateCliUpgradeHelp(Beta9Help.Replace("  --target <version>   Update to a specific version\n", string.Empty) + "Examples:\n  --target <version>   Not an option\n"), Is.False);
        }

        [TestCase((int)UnityToolUpdateComponents.None, false, false, false)]
        [TestCase((int)UnityToolUpdateComponents.WindowsCli, true, false, false)]
        [TestCase((int)UnityToolUpdateComponents.Pipeline, false, true, false)]
        [TestCase((int)UnityToolUpdateComponents.Skill, false, false, true)]
        [TestCase((int)UnityToolUpdateComponents.All, true, true, true)]
        public void SelectionInitializesOnlyRequestedInstalledComponents(int components, bool cli, bool pipeline, bool skill)
        {
            var snapshot = CreateSnapshot((UnityToolUpdateComponents)components);
            Assert.That(snapshot.CheckWindowsCli, Is.EqualTo(cli));
            Assert.That(snapshot.CheckPipeline, Is.EqualTo(pipeline));
            Assert.That(snapshot.CheckSkill, Is.EqualTo(skill));
            Assert.That(snapshot.WindowsCli.Update.CheckState, Is.EqualTo(cli ? UnityCliUpdateCheck.CheckStateChecking : UnityCliUpdateCheck.CheckStateNotChecked));
            Assert.That(snapshot.Pipeline.Update.CheckState, Is.EqualTo(pipeline ? UnityCliUpdateCheck.CheckStateChecking : UnityCliUpdateCheck.CheckStateNotChecked));
            Assert.That(snapshot.Skill.Update.CheckState, Is.EqualTo(skill ? UnityCliUpdateCheck.CheckStateChecking : UnityCliUpdateCheck.CheckStateNotChecked));
            Assert.That(snapshot.WindowsCli.Update.AuthoritativeForMutation, Is.False);
            Assert.That(snapshot.Pipeline.Update.AuthoritativeForMutation, Is.False);
            Assert.That(snapshot.Skill.Update.AuthoritativeForMutation, Is.False);
        }

        [Test]
        public void SelectionNeverChecksMissingComponentsOrUnknownFlags()
        {
            var snapshot = UnityCliUpdateCheck.CreateInitialSnapshotForTests(null, false, null, null, UnityToolUpdateComponents.All, TestNow);
            Assert.That(snapshot.CheckWindowsCli || snapshot.CheckPipeline || snapshot.CheckSkill, Is.False);
            Assert.That(snapshot.WindowsCli.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateSkippedNotInstalled));
            Assert.That(snapshot.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateSkippedNotInstalled));
            Assert.That(snapshot.Skill.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateSkippedNotInstalled));
            var unknown = CreateSnapshot((UnityToolUpdateComponents)128);
            Assert.That(unknown.CheckWindowsCli || unknown.CheckPipeline || unknown.CheckSkill, Is.False);
        }

        [Test]
        public void PipelineOnlyChecksLocalCliPrerequisitesButNoCliOrSkillRemoteMetadata()
        {
            var snapshot = CreateSnapshot(UnityToolUpdateComponents.Pipeline);
            var starter = new FakeStarter(SignatureResponse, "1.0.0-beta.9\n", PipelineHelp, PipelineResponse);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(starter.Arguments.Count, Is.EqualTo(4));
                Assert.That(starter.Arguments[0].Last(), Does.Contain("Get-AuthenticodeSignature"));
                Assert.That(starter.Arguments[1].Last(), Does.Contain("$arguments=@('--version','--no-banner')"));
                Assert.That(starter.Arguments[2].Last(), Does.Contain("$arguments=@('pipeline','install','--help','--no-banner')"));
                Assert.That(starter.Arguments[3].Last(), Does.Contain("$arguments=@('pipeline','list-versions','--format','json','--non-interactive','--no-banner')"));
                Assert.That(starter.Arguments.SelectMany(value => value).Any(value => value.Contains("'upgrade'") || value.Contains(UnityCliUpdateCheck.SkillLatestApiUrl)), Is.False);
                Assert.That(snapshot.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
                Assert.That(snapshot.WindowsCli.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateNotChecked));
                Assert.That(snapshot.Skill.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateNotChecked));
                Assert.That(snapshot.WindowsCli.Update.AuthoritativeForMutation, Is.False);
            }
        }

        [Test]
        public void CliOnlyBeta9CheckBindsExactTargetAndRetainsAuthorizationTtl()
        {
            var snapshot = CreateSnapshot(UnityToolUpdateComponents.WindowsCli);
            var starter = new FakeStarter(SignatureResponse, "1.0.0-beta.9\n", Beta9Help, Beta9Response);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(starter.Arguments.Count, Is.EqualTo(4));
                Assert.That(starter.Arguments[3].Last(), Does.Contain("$arguments=@('upgrade','--check','--channel','beta','--format','json','--non-interactive','--no-banner')"));
                Assert.That(snapshot.WindowsCli.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateAvailable));
                Assert.That(snapshot.WindowsCli.Update.AvailableVersion, Is.EqualTo("1.0.0-beta.13"));
                Assert.That(snapshot.WindowsCli.Update.SourceIdentitySha256, Is.EqualTo(UnityCliUpdateCheck.CliSourceIdentity("1.0.0-beta.9", "1.0.0-beta.13")));
                Assert.That(snapshot.WindowsCli.Update.ExpiresAtUtc, Is.EqualTo(TestNow.Add(UnityCliUpdateCheck.CacheTtl)));
                Assert.That(snapshot.WindowsCli.Update.AuthoritativeForMutation, Is.True);
                Assert.That(snapshot.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateNotChecked));
                Assert.That(snapshot.Skill.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateNotChecked));
            }
        }

        [Test]
        public void CanonicalResponseCannotAuthorizeChangedLocalVersionOrMissingTargetSurface()
        {
            foreach (var changedLocal in new[] { true, false })
            {
                var snapshot = CreateSnapshot(UnityToolUpdateComponents.WindowsCli);
                var starter = new FakeStarter(SignatureResponse, changedLocal ? "1.0.0-beta.8\n" : "1.0.0-beta.9\n", changedLocal ? Beta9Help : Beta9Help.Replace("--target <version>", "--target-version <version>"), Beta9Response);
                using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
                {
                    Complete(coordinator);
                    Assert.That(starter.Arguments.Count, Is.EqualTo(4));
                    Assert.That(snapshot.WindowsCli.Update.LastErrorCode, Is.EqualTo(changedLocal ? "cli_current_version_changed" : "cli_update_surface_unavailable"));
                    Assert.That(snapshot.WindowsCli.Update.UpdateState, Is.Not.EqualTo(UnityCliUpdateCheck.UpdateStateAvailable));
                    if (!changedLocal) Assert.That(snapshot.WindowsCli.Update.AuthoritativeForMutation, Is.False);
                }
            }
        }

        [Test]
        public void SkillOnlyDoesNotStartAnyCliProcessAndNoneStartsNoProcess()
        {
            var skill = CreateSnapshot(UnityToolUpdateComponents.Skill);
            var starter = new FakeStarter(UnityCliProjectSkillInstaller.ReviewedSourceCommit + "\t" + UnityCliUpdateCheck.SkillRepositoryPath);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(skill, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(starter.Arguments.Count, Is.EqualTo(1));
                Assert.That(starter.Arguments[0].Last(), Does.Contain(UnityCliUpdateCheck.SkillLatestApiUrl));
                Assert.That(skill.WindowsCli.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateNotChecked));
                Assert.That(skill.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateNotChecked));
            }
            var noneStarter = new FakeStarter();
            using (var coordinator = new UnityCliUpdateCheckCoordinator(CreateSnapshot(UnityToolUpdateComponents.None), noneStarter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(noneStarter.Arguments, Is.Empty);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void UnselectedFreshnessRecordIsPreservedWithoutMutationAuthority(bool installed)
        {
            var record = new UnityToolFreshnessRecord { installedIdentity = "previous", availableVersion = "1.0.0-beta.13", checkedAtUtc = TestNow.ToString("O"), lastFailureAtUtc = "previous failure" };
            var result = UnityToolFreshnessStore.RecordComponent(record, installed, "different identity", false, ComponentUpdateStatus.NotChecked(), TestNow);
            Assert.That(result, Is.SameAs(record));
            Assert.That(result.installedIdentity, Is.EqualTo("previous"));
            Assert.That(result.lastFailureAtUtc, Is.EqualTo("previous failure"));
        }

        [Test]
        public void SelectedMissingComponentStillClearsItsOwnFreshnessRecord()
        {
            var record = new UnityToolFreshnessRecord { installedIdentity = "previous" };
            var result = UnityToolFreshnessStore.RecordComponent(record, false, string.Empty, true, ComponentUpdateStatus.NotChecked(), TestNow);
            Assert.That(result, Is.Not.SameAs(record));
            Assert.That(result.installedIdentity, Is.Empty);
        }

        [Test]
        public void ExplicitMissingPipelineAvailabilityUsesOnlyTrustedCliPipelineReadStages()
        {
            var snapshot = CreateMissingAvailabilitySnapshot(UnityToolUpdateComponents.Pipeline, true);
            var starter = new FakeStarter(SignatureResponse, "1.0.0-beta.9\n", PipelineHelp, Beta9PipelineResponse);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(starter.Arguments.Count, Is.EqualTo(4));
                Assert.That(starter.Arguments[0].Last(), Does.Contain("Get-AuthenticodeSignature"));
                Assert.That(starter.Arguments[1].Last(), Does.Contain("$arguments=@('--version','--no-banner')"));
                Assert.That(starter.Arguments[2].Last(), Does.Contain("$arguments=@('pipeline','install','--help','--no-banner')"));
                Assert.That(starter.Arguments[3].Last(), Does.Contain("$arguments=@('pipeline','list-versions','--format','json','--non-interactive','--no-banner')"));
                Assert.That(starter.Arguments.SelectMany(value => value).Any(value => value.Contains("'upgrade'") || value.Contains(UnityCliUpdateCheck.SkillLatestApiUrl)), Is.False);
                Assert.That(snapshot.Pipeline.Update.AvailableVersion, Is.EqualTo("0.8.0-exp.1"));
                Assert.That(snapshot.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
                Assert.That(snapshot.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(snapshot.Pipeline.Update.AuthoritativeForMutation, Is.False);
                Assert.That(snapshot.CheckWindowsCli || snapshot.CheckSkill, Is.False);
                Assert.That(snapshot.WindowsCli.Update.AuthoritativeForMutation || snapshot.Skill.Update.AuthoritativeForMutation, Is.False);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExplicitMissingSkillAvailabilityIsOneNetworkReadAndNeverMutationAuthority(bool upstreamChanged)
        {
            var snapshot = CreateMissingAvailabilitySnapshot(UnityToolUpdateComponents.Skill, false);
            var remote = upstreamChanged ? new string('a', 40) : UnityCliProjectSkillInstaller.ReviewedSourceCommit;
            var starter = new FakeStarter(remote + "\t" + UnityCliUpdateCheck.SkillRepositoryPath);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(starter.Arguments.Count, Is.EqualTo(1));
                Assert.That(starter.Arguments[0].Last(), Does.Contain(UnityCliUpdateCheck.SkillLatestApiUrl));
                Assert.That(snapshot.CheckWindowsCli || snapshot.CheckPipeline, Is.False);
                Assert.That(snapshot.Skill.AvailableReviewedCommit, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));
                Assert.That(snapshot.Skill.Update.AvailableVersion, Is.EqualTo(remote));
                Assert.That(snapshot.Skill.UpstreamChangeDetected, Is.EqualTo(upstreamChanged));
                Assert.That(snapshot.Skill.Update.UpdateState, Is.EqualTo(upstreamChanged ? UnityCliUpdateCheck.UpdateStateUpstreamReview : UnityCliUpdateCheck.UpdateStateNotInstalled));
                Assert.That(snapshot.Skill.Update.AuthoritativeForMutation, Is.False);
                Assert.That(UnityCliProjectSkillInstaller.TryCreateUpdateBinding(snapshot, snapshot.Skill, TestNow, out _, out _), Is.False);
            }
        }

        [Test]
        public void MissingPipelineAvailabilityWithoutTrustedCliHasNoNetworkOrInstallFallback()
        {
            var snapshot = CreateMissingAvailabilitySnapshot(UnityToolUpdateComponents.Pipeline, false);
            var starter = new FakeStarter();
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(starter.Arguments, Is.Empty);
                Assert.That(snapshot.Pipeline.Update.LastErrorCode, Is.EqualTo("check_blocked_update_transport_unavailable"));
                Assert.That(snapshot.Pipeline.Update.AuthoritativeForMutation, Is.False);
                Assert.That(snapshot.Pipeline.Update.AvailableVersion, Is.Empty);
            }
        }

        [TestCase("unknown")]
        [TestCase("unreadable")]
        [TestCase("declared")]
        [TestCase("registered")]
        public void ExplicitPipelineAvailabilityDoesNotCallUnknownOrUnresolvedTargetAbsent(string kind)
        {
            var pipeline = MissingPipeline();
            if (kind == "unknown") pipeline.EditorCompatibilityState = "unknown";
            if (kind == "unreadable") pipeline.ProjectPackagesIdentitySha256 = string.Empty;
            if (kind == "declared") pipeline.ManifestVersion = "0.8.0-exp.1";
            if (kind == "registered") pipeline.Registered = true;
            var snapshot = UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(
                UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked(), true, pipeline, MissingSkill(), UnityToolUpdateComponents.Pipeline, TestNow, true);
            var starter = new FakeStarter(SignatureResponse, "1.0.0-beta.9\n", PipelineHelp, Beta9PipelineResponse);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(snapshot.PipelineAbsenceVerified, Is.False);
                Assert.That(snapshot.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateUnsupported));
                Assert.That(snapshot.Pipeline.Update.LastErrorCode, Is.EqualTo("pipeline_local_state_unverified"));
                Assert.That(snapshot.Pipeline.Update.AvailableVersion, Is.EqualTo("0.8.0-exp.1"));
                Assert.That(snapshot.Pipeline.Update.AuthoritativeForMutation, Is.False);
            }
        }

        [TestCase("unknown_provenance", false)]
        [TestCase("incomplete", true)]
        [TestCase("reparse_point", true)]
        [TestCase("modified", true)]
        public void ExplicitSkillAvailabilityDoesNotCallUnknownUnreadableOrUnsafeTargetAbsent(string localState, bool exists)
        {
            var skill = new UnityCliProjectSkillInstaller.UnityCliSkillStatus { Exists = exists, LocalState = localState, SourceProvenance = "unknown", ModifiedOrUnknown = true, ReparsePoint = localState == UnityCliProjectSkillInstaller.LocalStateReparse };
            var snapshot = UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(null, false, MissingPipeline(), skill, UnityToolUpdateComponents.Skill, TestNow);
            var starter = new FakeStarter(new string('a', 40) + "\t" + UnityCliUpdateCheck.SkillRepositoryPath);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(snapshot.SkillAbsenceVerified, Is.False);
                Assert.That(snapshot.Skill.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateUnsupported));
                Assert.That(snapshot.Skill.Update.LastErrorCode, Is.EqualTo("skill_local_state_unverified"));
                Assert.That(snapshot.Skill.Update.AuthoritativeForMutation, Is.False);
                Assert.That(snapshot.Skill.UpstreamChangeDetected, Is.True);
                Assert.That(snapshot.Skill.AvailableReviewedCommit, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));
            }
        }

        [TestCase("{}", true)]
        [TestCase("{\"dependencies\":{}}", true)]
        [TestCase("{\"dependencies\":{\"com.unity.pipeline\":\"\"}}", false)]
        [TestCase("{\"dependencies\":{\"com.unity.pipeline\":null}}", false)]
        [TestCase("{\"dependencies\":{\"com.unity.pipeline\":\"0.8.0-exp.1\"}}", false)]
        [TestCase("{\"dependencies\":null}", false)]
        [TestCase("{\"dependencies\":{},\"dependencies\":{}}", false)]
        [TestCase("{\"dependencies\":{\"com\\u002eunity.pipeline\":\"0.8.0-exp.1\"}}", false)]
        [TestCase("{\"dependencies\":{", false)]
        public void ConfirmedPipelineAbsenceRequiresReadableUniqueManifestWithoutAnyDeclaration(string json, bool expected)
        {
            Assert.That(UnityCliUpdateCheck.IsPipelineAbsentManifest(json), Is.EqualTo(expected));
        }

        [Test]
        public void ExplicitAvailabilityDoesNotWidenNormalOrAutomaticMissingComponentSelection()
        {
            var installed = UnityCliUpdateCheck.InspectInstalledSet(string.Empty, null, false, MissingPipeline(), MissingSkill());
            Assert.That(installed.Components, Is.EqualTo(UnityToolUpdateComponents.None));
            Assert.That(UnityToolFreshnessStore.ComponentsNeedingAutomaticCheck(installed, new UnityToolFreshnessRecords(), TestNow), Is.EqualTo(UnityToolUpdateComponents.None));
            var normal = UnityCliUpdateCheck.CreateInitialSnapshotForTests(null, false, MissingPipeline(), MissingSkill(), UnityToolUpdateComponents.All, TestNow);
            Assert.That(normal.CheckWindowsCli || normal.CheckPipeline || normal.CheckSkill, Is.False);
            var explicitNone = CreateMissingAvailabilitySnapshot((UnityToolUpdateComponents)128, false);
            Assert.That(explicitNone.CheckWindowsCli || explicitNone.CheckPipeline || explicitNone.CheckSkill, Is.False);
        }

        [Test]
        public void InstalledPipelineExplicitAvailabilityRetainsRegularExactSourceBinding()
        {
            var installed = CreateSnapshot(UnityToolUpdateComponents.Pipeline);
            var snapshot = UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(installed.WindowsCli, true, installed.Pipeline, installed.Skill, UnityToolUpdateComponents.Pipeline, TestNow);
            var starter = new FakeStarter(SignatureResponse, "1.0.0-beta.9\n", PipelineHelp, PipelineResponse);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(snapshot.PipelineAvailabilityDisplayOnly, Is.False);
                Assert.That(snapshot.Pipeline.Update.UpdateState, Is.EqualTo(UnityCliUpdateCheck.UpdateStateAvailable));
                Assert.That(snapshot.Pipeline.Update.AuthoritativeForMutation, Is.True);
                Assert.That(snapshot.Pipeline.Update.SourceKind, Is.EqualTo(UnityCliUpdateCheck.PipelineSourceKind));
                Assert.That(snapshot.Pipeline.Update.ExpiresAtUtc, Is.EqualTo(TestNow.Add(UnityCliUpdateCheck.CacheTtl)));
                Assert.That(snapshot.CheckWindowsCli || snapshot.CheckSkill, Is.False);
            }
        }

        [Test]
        public void ExplicitAvailabilityKeepsUnselectedPersistedStatusDisplayOnly()
        {
            var original = CreateSnapshot(UnityToolUpdateComponents.None);
            var installed = UnityCliUpdateCheck.InspectInstalledSet(string.Empty, original.WindowsCli, true, original.Pipeline, original.Skill);
            var records = new UnityToolFreshnessRecords
            {
                pipeline = new UnityToolFreshnessRecord
                {
                    installedIdentity = installed.PipelineIdentity,
                    installedVersion = original.Pipeline.Version,
                    availableVersion = "0.8.0-exp.1",
                    channel = string.Empty,
                    sourceKind = UnityCliUpdateCheck.PipelineSourceKind,
                    sourceIdentitySha256 = new string('c', 64),
                    updateState = UnityCliUpdateCheck.UpdateStateAvailable,
                    checkedAtUtc = TestNow.AddHours(-1).ToString("O"),
                },
            };
            var snapshot = UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(original.WindowsCli, true, original.Pipeline, MissingSkill(), UnityToolUpdateComponents.Skill, TestNow, false, records);
            Assert.That(snapshot.CheckPipeline || snapshot.CheckWindowsCli, Is.False);
            Assert.That(snapshot.Pipeline.Update.CheckState, Is.EqualTo(UnityCliUpdateCheck.CheckStateChecked));
            Assert.That(snapshot.Pipeline.Update.AvailableVersion, Is.EqualTo("0.8.0-exp.1"));
            Assert.That(snapshot.Pipeline.Update.AuthoritativeForMutation, Is.False);
            Assert.That(records.pipeline.availableVersion, Is.EqualTo("0.8.0-exp.1"));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SequentialMissingComponentChecksRetainBothAvailabilityRowsWithoutExtraProcesses(bool pipelineFirst)
        {
            var records = new UnityToolFreshnessRecords();
            foreach (var component in pipelineFirst
                ? new[] { UnityToolUpdateComponents.Pipeline, UnityToolUpdateComponents.Skill }
                : new[] { UnityToolUpdateComponents.Skill, UnityToolUpdateComponents.Pipeline })
            {
                var snapshot = UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(null, true,
                    MissingPipeline(), MissingSkill(), component, TestNow, true, records);
                var starter = component == UnityToolUpdateComponents.Pipeline
                    ? new FakeStarter(SignatureResponse, "1.0.0-beta.9\n", PipelineHelp, Beta9PipelineResponse)
                    : new FakeStarter(UnityCliProjectSkillInstaller.ReviewedSourceCommit + "\t" + UnityCliUpdateCheck.SkillRepositoryPath);
                using (var coordinator = new UnityCliUpdateCheckCoordinator(snapshot, starter, () => TestNow, TimeSpan.FromMinutes(1))) Complete(coordinator);
                Assert.That(starter.Arguments.Count, Is.EqualTo(component == UnityToolUpdateComponents.Pipeline ? 4 : 1));
                records.pipeline = UnityToolFreshnessStore.RecordComponent(records.pipeline, false, snapshot.PipelineInstalledIdentity,
                    snapshot.CheckPipeline, snapshot.Pipeline.Update, TestNow, snapshot.PipelineInstalledIdentity, snapshot.PipelineAvailabilityDisplayOnly);
                records.skill = UnityToolFreshnessStore.RecordComponent(records.skill, false, snapshot.SkillInstalledIdentity,
                    snapshot.CheckSkill, snapshot.Skill.Update, TestNow, snapshot.SkillInstalledIdentity, snapshot.SkillAvailabilityDisplayOnly);
                // Persistence roundtrip must retain display context, never live authority.
                records = UnityEngine.JsonUtility.FromJson<UnityToolFreshnessRecords>(UnityEngine.JsonUtility.ToJson(records));
            }
            var restored = UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(null, true,
                MissingPipeline(), MissingSkill(), UnityToolUpdateComponents.None, TestNow, true, records);
            Assert.That(restored.Pipeline.Update.AvailableVersion, Is.EqualTo("0.8.0-exp.1"));
            Assert.That(restored.Skill.Update.AvailableVersion, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));
            Assert.That(restored.Pipeline.Update.AuthoritativeForMutation || restored.Skill.Update.AuthoritativeForMutation, Is.False);
            Assert.That(restored.CheckWindowsCli || restored.CheckPipeline || restored.CheckSkill, Is.False);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(restored, new FakeStarter(), () => TestNow, TimeSpan.FromMinutes(1))) Complete(coordinator);
            var changedPipeline = MissingPipeline();
            changedPipeline.ProjectPackagesIdentitySha256 = new string('c', 64);
            var changed = UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(null, true,
                changedPipeline, MissingSkill(), UnityToolUpdateComponents.None, TestNow, true, records);
            Assert.That(changed.Pipeline.Update.AvailableVersion, Is.Empty, "Changed local package context invalidates retained display metadata.");
            Assert.That(changed.Skill.Update.AvailableVersion, Is.EqualTo(UnityCliProjectSkillInstaller.ReviewedSourceCommit));
        }

        [Test]
        public void MissingAvailabilityCannotPersistAnAuthoritativeSnapshot()
        {
            var snapshot = CreateMissingAvailabilitySnapshot(UnityToolUpdateComponents.Pipeline, true);
            var status = ComponentUpdateStatus.Checked("", "0.8.0-exp.1", "", UnityCliUpdateCheck.PipelineSourceKind, new string('a', 64), TestNow);
            status.UpdateState = UnityCliUpdateCheck.UpdateStateNotInstalled;
            var record = UnityToolFreshnessStore.RecordComponent(null, false, snapshot.PipelineInstalledIdentity, true,
                status, TestNow, snapshot.PipelineInstalledIdentity, true);
            Assert.That(record.availableVersion, Is.Empty);
            Assert.That(UnityToolFreshnessStore.TryRestore(record, snapshot.PipelineInstalledIdentity, TestNow, out _), Is.False);
        }

        [Test]
        public void FailedMissingAvailabilityResponsesNeverBecomeAuthoritative()
        {
            var pipeline = CreateMissingAvailabilitySnapshot(UnityToolUpdateComponents.Pipeline, true);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(pipeline, new FakeStarter(SignatureResponse, "1.0.0-beta.9\n", PipelineHelp, "invalid"), () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(pipeline.Pipeline.Update.AuthoritativeForMutation, Is.False);
            }
            var skill = CreateMissingAvailabilitySnapshot(UnityToolUpdateComponents.Skill, false);
            using (var coordinator = new UnityCliUpdateCheckCoordinator(skill, new FakeStarter("invalid"), () => TestNow, TimeSpan.FromMinutes(1)))
            {
                Complete(coordinator);
                Assert.That(skill.Skill.Update.AuthoritativeForMutation, Is.False);
            }
        }

        static UnityCliPipelineSetup.UnityPipelineStatus MissingPipeline()
        {
            return new UnityCliPipelineSetup.UnityPipelineStatus { EditorSupported = true, EditorCompatibilityState = "supported", ProjectPackagesIdentitySha256 = new string('b', 64) };
        }

        static UnityCliProjectSkillInstaller.UnityCliSkillStatus MissingSkill()
        {
            return new UnityCliProjectSkillInstaller.UnityCliSkillStatus { LocalState = UnityCliProjectSkillInstaller.LocalStateAbsent, SourceProvenance = "absent" };
        }

        static UnityCliUpdateCheckSnapshot CreateMissingAvailabilitySnapshot(UnityToolUpdateComponents components, bool cliCandidate)
        {
            return UnityCliUpdateCheck.CreateInitialAvailabilitySnapshotForTests(null, cliCandidate, MissingPipeline(), MissingSkill(), components, TestNow, true);
        }

        static UnityCliUpdateCheckSnapshot CreateSnapshot(UnityToolUpdateComponents components)
        {
            return UnityCliUpdateCheck.CreateInitialSnapshotForTests(
                UnityCliPipelineSetup.WindowsUnityCliStatus.Ready(UnityCliPipelineSetup.OfficialWindowsInstallPath, "1.0.0-beta.9"),
                true,
                new UnityCliPipelineSetup.UnityPipelineStatus { Registered = true, EditorSupported = true, Version = "0.4.0-exp.1", ManifestVersion = "0.4.0-exp.1", Source = "Registry", ProjectPackagesIdentitySha256 = new string('a', 64) },
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus { Exists = true, Valid = true, ProvenanceVerified = true, LocalState = UnityCliProjectSkillInstaller.LocalStateCurrentReviewed, InstalledReviewedCommit = UnityCliProjectSkillInstaller.ReviewedSourceCommit },
                components,
                TestNow);
        }

        static void Complete(UnityCliUpdateCheckCoordinator coordinator)
        {
            for (var index = 0; index < 12 && !coordinator.Poll(); index++) { }
            Assert.That(coordinator.IsComplete, Is.True);
        }

        sealed class FakeStarter : IUpdateCheckProcessStarter
        {
            readonly Queue<string> m_outputs;
            internal readonly List<string[]> Arguments = new List<string[]>();
            internal FakeStarter(params string[] outputs) { m_outputs = new Queue<string>(outputs); }
            public bool TryStart(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, out IUpdateCheckProcess operation, out string error)
            {
                Arguments.Add(arguments.ToArray());
                Assert.That(m_outputs.Count, Is.GreaterThan(0), "Unexpected extra process; component checks must not fan out or retry.");
                operation = new FakeProcess(m_outputs.Dequeue());
                error = string.Empty;
                return true;
            }
        }

        sealed class FakeProcess : IUpdateCheckProcess
        {
            internal FakeProcess(string output) { StandardOutput = output; }
            public int ExitCode => 0;
            public string StandardOutput { get; }
            public string StandardError => string.Empty;
            public bool TimedOut => false;
            public bool TerminationConfirmed => true;
            public int ProcessId => 0;
            public bool Poll() => true;
            public void Dispose() { }
        }
    }
}
