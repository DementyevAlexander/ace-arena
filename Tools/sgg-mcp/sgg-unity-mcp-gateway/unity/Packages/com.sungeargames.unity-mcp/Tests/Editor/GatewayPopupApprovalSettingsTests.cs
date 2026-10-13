using System;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;

namespace SGG.UnityMCP.Editor.Tests
{
    // Pure text helpers and dedicated temporary configs only; never access actual user config.
    public sealed class GatewayPopupApprovalSettingsTests
    {
        const string Key = "auto_allow_known_unity_popups";

        [TestCase("{}")]
        [TestCase("{\"approvals\":{}}")]
        [TestCase("{\"approvals\":{\"auto_allow_ask\":true}}")]
        [TestCase("{\"auto_allow_known_unity_popups\":true}")]
        [TestCase("{\"other\":{\"approvals\":{\"auto_allow_known_unity_popups\":true}}}")]
        [TestCase("{\"approvals\":{\"other\":{\"auto_allow_known_unity_popups\":true}}}")]
        public void MissingOwnedSettingDefaultsOff(string source)
        {
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(source), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReadsOnlyAnExplicitOwnedBoolean(bool enabled)
        {
            var source = "{\"approvals\":{\"" + Key + "\":" + BooleanText(enabled) + "}}";
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(source), Is.EqualTo(enabled));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TogglePreservesEveryUnrelatedTokenIncludingNestedSameNames(bool enabled)
        {
            const string template = "{\r\n"
                + "  \"auto_allow_known_unity_popups\": true,\r\n"
                + "  \"other\": {\"approvals\":{\"auto_allow_known_unity_popups\":true}, \"auto_allow_ask\":false},\r\n"
                + "  \"approvals\": {\r\n"
                + "    \"auto_allow_ask\": true,\r\n"
                + "    \"dir\": \"C:\\\\custom\\\\approvals\",\r\n"
                + "    \"default_ttl_ms\": 12345,\r\n"
                + "    \"future\": {\"auto_allow_known_unity_popups\":false,\"auto_allow_ask\":true},\r\n"
                + "    \"auto_allow_known_unity_popups\" : OWNED\r\n"
                + "  },\r\n"
                + "  \"unknown\": [null, false, -0, 1.2300e+99, {\"text\":\"} \\\"approvals\\\" \\\\ \\u263a\"}],\r\n"
                + "  \"features\": {\"editor_utilities\":false},\r\n"
                + "  \"note\": \"Русский текст\"\r\n"
                + "}\r\n";
            var source = template.Replace("OWNED", BooleanText(!enabled));
            var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, enabled);

            Assert.That(updated, Is.EqualTo(template.Replace("OWNED", BooleanText(enabled))));
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(updated), Is.EqualTo(enabled));
            Assert.That(GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(updated, !enabled), Is.EqualTo(source));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnchangedSettingIsAByteIdenticalNoOp(bool enabled)
        {
            var source = " { \"approvals\" : { \"auto_allow_ask\":false, \"" + Key + "\" : " + BooleanText(enabled) + " } } \n";
            Assert.That(GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, enabled), Is.EqualTo(source));
        }

        [TestCase("{}", false)]
        [TestCase("{}", true)]
        [TestCase("{ \"other\":{\"approvals\":{\"auto_allow_known_unity_popups\":true}}\n}", false)]
        [TestCase("{ \"other\":{\"approvals\":{\"auto_allow_known_unity_popups\":true}}\n}", true)]
        public void AbsentApprovalsAddsOnlyANewObjectWithRequiredDefaultDir(string source, bool enabled)
        {
            var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, enabled);
            var added = (source.Contains("\"other\"") ? "," : string.Empty)
                + " \"approvals\": { \"dir\": \"%LOCALAPPDATA%/SGG/UnityMcpGateway/approvals\", \"" + Key + "\": " + BooleanText(enabled) + " }";

            Assert.That(updated.Replace(added, string.Empty), Is.EqualTo(source));
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(updated), Is.EqualTo(enabled));
            Assert.That(updated, Does.Not.Contain("\"auto_allow_ask\""));
        }

        [TestCase("{\"approvals\":{}}", false)]
        [TestCase("{\"approvals\":{}}", true)]
        [TestCase("{\"approvals\":{\"auto_allow_ask\":false,\"future\":{\"auto_allow_known_unity_popups\":true}}}", false)]
        [TestCase("{\"approvals\":{\"auto_allow_ask\":false,\"future\":{\"auto_allow_known_unity_popups\":true}}}", true)]
        [TestCase("{\"approvals\":{ \"auto_allow_ask\":true,\"dir\":\"custom\" \n}}", false)]
        [TestCase("{\"approvals\":{ \"auto_allow_ask\":true,\"dir\":\"custom\" \n}}", true)]
        public void AbsentSettingAddsOnlyTheOwnedProperty(string source, bool enabled)
        {
            var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, enabled);
            var added = (source.Contains("auto_allow_ask") ? "," : string.Empty)
                + " \"" + Key + "\": " + BooleanText(enabled);

            Assert.That(updated.Replace(added, string.Empty), Is.EqualTo(source));
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(updated), Is.EqualTo(enabled));
        }

        [Test]
        public void DecodedPropertyNamesIdentifyTheOwnedObjectAndSetting()
        {
            const string source = "{\"\\u0061pprovals\":{\"auto_allow_known_unity_\\u0070opups\":true},\"other\":{\"auto_allow_known_unity_popups\":true}}";
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(source), Is.True);
            Assert.That(GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, false),
                Is.EqualTo(source.Replace("\\u0070opups\":true", "\\u0070opups\":false")));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("[]")]
        [TestCase("null")]
        [TestCase("{\"approvals\":null}")]
        [TestCase("{\"approvals\":[]}")]
        [TestCase("{\"approvals\":true}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":\"true\"}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":1}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":null}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":{}}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true,\"auto_allow_known_unity_popups\":false}}")]
        [TestCase("{\"approvals\":{},\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true,\"auto_allow_known_unity_\\u0070opups\":false}}")]
        [TestCase("{\"approvals\":{},\"\\u0061pprovals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true},\"unknown\":{\"x\":1,\"x\":2}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true},\"broken\":nope}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true},}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true}} trailing")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true}} {}")]
        [TestCase("{/* comment */\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"x\":[1,],\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"x\":01,\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"x\":1.,\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"x\":1e,\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"x\":\"\\q\",\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"x\":\"\\u00gg\",\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"x\":\"raw\nnewline\",\"approvals\":{\"auto_allow_known_unity_popups\":true}}")]
        [TestCase("{\"approvals\":{\"auto_allow_known_unity_popups\":true}")]
        public void InvalidOrAmbiguousConfigFailsClosedWithoutProducingAMutation(string source)
        {
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(source), Is.False);
            Assert.Throws<FormatException>(() => GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true));
            Assert.Throws<FormatException>(() => GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, false));
        }

        [Test]
        public void OversizedConfigFailsClosed()
        {
            var source = "{\"approvals\":{\"" + Key + "\":true},\"unknown\":\"" + new string('x', 1024 * 1024) + "\"}";
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(source), Is.False);
            Assert.Throws<FormatException>(() => GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true));
        }

        [Test]
        public void InsertionCannotGrowConfigBeyondTheSizeLimit()
        {
            var source = "{}" + new string(' ', 1024 * 1024 - 2);
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(source), Is.False);
            Assert.Throws<FormatException>(() => GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true));
        }

        [Test]
        public void ExcessiveNestingFailsClosed()
        {
            var source = "{\"approvals\":{\"" + Key + "\":true},\"unknown\":" + new string('[', 66) + "null" + new string(']', 66) + "}";
            Assert.That(GatewayPolicyManager.ReadAutoAllowKnownUnityPopups(source), Is.False);
            Assert.Throws<FormatException>(() => GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true));
        }

        [Test]
        public void AtomicReplacementPreservesUnrelatedValuesAndRemovesItsStagingFile()
        {
            WithTemporaryConfig(configPath =>
            {
                const string source = "{\"approvals\":{\"auto_allow_ask\":false,\"auto_allow_known_unity_popups\":false},\"note\":\"unchanged\"}";
                File.WriteAllText(configPath, source);
                var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true);
                GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated);
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(updated));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
            });
        }

        [Test]
        public void ChangedDestinationIsNotOverwritten()
        {
            WithTemporaryConfig(configPath =>
            {
                const string source = "{\"approvals\":{\"auto_allow_known_unity_popups\":false}}";
                const string changed = "{\"approvals\":{\"auto_allow_known_unity_popups\":false},\"other_writer\":true}";
                File.WriteAllText(configPath, changed);
                var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true);
                Assert.Throws<IOException>(() => GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated));
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(changed));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
            });
        }

        [Test]
        public void InPlaceWriterIsBlockedBetweenValidationAndReplacementWithoutRestoringUnrelatedFields()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Windows sharing-mode acceptance");
            WithTemporaryConfig(configPath =>
            {
                const string source = "{\"approvals\":{\"auto_allow_ask\":false,\"auto_allow_known_unity_popups\":false},\"other_writer\":\"current\"}";
                const string changed = "{\"approvals\":{\"auto_allow_ask\":false,\"auto_allow_known_unity_popups\":false},\"other_writer\":\"newer\"}";
                File.WriteAllText(configPath, source);
                var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true);
                var writerAttempted = false;
                GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated, () =>
                {
                    writerAttempted = true;
                    // The competing writer grants every sharing permission; the held destination
                    // read handle must still deny its write access at this exact interleaving.
                    Assert.Throws<IOException>(() =>
                    {
                        using (var writer = new FileStream(configPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        {
                            writer.SetLength(0);
                        }
                    });
                    Assert.That(File.ReadAllText(configPath), Is.EqualTo(source));
                });

                Assert.That(writerAttempted, Is.True);
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(updated));
                // After the protected handle closes, a newer unrelated update can proceed;
                // a stale consent transaction must not restore its previous unrelated value.
                File.WriteAllText(configPath, changed);
                Assert.Throws<IOException>(() => GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated));
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(changed));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
            });
        }

        [Test]
        public void CooperatingConfigWriterLockRefusesConcurrentConsentReplacement()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Windows sharing-mode acceptance");
            WithTemporaryConfig(configPath =>
            {
                const string source = "{\"approvals\":{\"auto_allow_known_unity_popups\":false},\"other_writer\":true}";
                File.WriteAllText(configPath, source);
                var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true);
                using (GatewayPolicyManager.AcquireGatewayConfigWriteLock(configPath))
                {
                    Assert.Throws<IOException>(() => GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated,
                        () => Assert.Fail("A concurrent cooperating writer must not reach replacement.")));
                    Assert.That(File.ReadAllText(configPath), Is.EqualTo(source));
                    Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
                }

                GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated);
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(updated));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
            });
        }

        [Test]
        public void AlreadyOpenInPlaceWriterRefusesConsentReplacement()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Windows sharing-mode acceptance");
            WithTemporaryConfig(configPath =>
            {
                const string source = "{\"approvals\":{\"auto_allow_known_unity_popups\":false},\"other_writer\":true}";
                File.WriteAllText(configPath, source);
                var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true);
                using (var writer = new FileStream(configPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    Assert.Throws<IOException>(() => GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated,
                        () => Assert.Fail("An already-open in-place writer must prevent destination validation.")));
                }
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(source));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
            });
        }

        [Test]
        public void NoOpConsentStillRejectsAChangedDestinationWithoutRewritingIt()
        {
            WithTemporaryConfig(configPath =>
            {
                const string source = "{\"approvals\":{\"auto_allow_known_unity_popups\":false}}";
                const string changed = "{\"approvals\":{\"auto_allow_known_unity_popups\":true},\"other_writer\":true}";
                File.WriteAllText(configPath, source);
                var bytes = File.ReadAllBytes(configPath);
                var lastWrite = File.GetLastWriteTimeUtc(configPath);
                GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, source,
                    () => Assert.Fail("An unchanged config must not reach replacement."));
                Assert.That(File.ReadAllBytes(configPath), Is.EqualTo(bytes));
                Assert.That(File.GetLastWriteTimeUtc(configPath), Is.EqualTo(lastWrite));

                File.WriteAllText(configPath, changed);
                Assert.Throws<IOException>(() => GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, source));
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(changed));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
            });
        }

        [Test]
        public void FailedReplacementDoesNotFallBackToOverwritingTheLockedDestination()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Windows sharing-mode acceptance");
            WithTemporaryConfig(configPath =>
            {
                const string source = "{\"approvals\":{\"auto_allow_known_unity_popups\":false}}";
                File.WriteAllText(configPath, source);
                var updated = GatewayPolicyManager.UpsertAutoAllowKnownUnityPopups(source, true);
                using (var held = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.Throws<IOException>(() => GatewayPolicyManager.AtomicWritePopupApprovalConfig(configPath, source, updated));
                }
                Assert.That(File.ReadAllText(configPath), Is.EqualTo(source));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(configPath), "gateway.json.tmp-*"), Is.Empty);
            });
        }

        static void WithTemporaryConfig(Action<string> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "sgg-popup-setting-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { test(Path.Combine(directory, "gateway.json")); }
            finally { Directory.Delete(directory, true); }
        }

        static string BooleanText(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
