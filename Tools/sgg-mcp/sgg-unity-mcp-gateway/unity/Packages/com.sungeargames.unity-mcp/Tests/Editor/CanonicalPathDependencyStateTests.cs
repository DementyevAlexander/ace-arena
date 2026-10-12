using System.IO;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.State;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class CanonicalPathDependencyStateTests
    {
        const string OlderVersion = "2026.10.9-1";
        const string RequiredVersion = "2026.10.10-4";
        const string NewerVersion = "2026.10.10-5";

        [TestCase(OlderVersion, true, "Update")]
        [TestCase(OlderVersion, false, "Update")]
        [TestCase("2026.10.10-1", true, "Update")]
        [TestCase("2026.10.10-1", false, "Update")]
        [TestCase("2026.10.10-2", true, "Update")]
        [TestCase("2026.10.10-2", false, "Update")]
        [TestCase("2026.10.10-3", true, "Update")]
        [TestCase("2026.10.10-3", false, "Update")]
        [TestCase(RequiredVersion, true, "None")]
        [TestCase(RequiredVersion, false, "Reload")]
        [TestCase(NewerVersion, true, "None")]
        [TestCase(NewerVersion, false, "Reload")]
        [TestCase("2026.10.11-1", true, "None")]
        [TestCase("2026.10.11-1", false, "Reload")]
        public void RegistryVersionAndProviderChooseUpdateReadinessOrReload(string installedVersion, bool providerAvailable, string expectedAction)
        {
            AssertState(Evaluate(installedVersion: installedVersion, providerAvailable: providerAvailable), expectedAction);
        }

        [TestCase("2026.10.10-2", "2026.10.10-10", "Update")]
        [TestCase("2026.10.10-10", "2026.10.10-2", "None")]
        [TestCase("2026.10.10-9", "2026.10.10-10", "Update")]
        [TestCase("2026.10.10-10", "2026.10.10-9", "None")]
        [TestCase("1.2.9", "1.2.10", "Update")]
        [TestCase("1.2.10", "1.2.9", "None")]
        [TestCase("1.0.0-beta.2", "1.0.0-beta.10", "Update")]
        [TestCase("1.0.0-beta.10", "1.0.0-beta.2", "None")]
        [TestCase("1.0.0-rc.1", "1.0.0", "Update")]
        [TestCase("1.0.0", "1.0.0-rc.1", "None")]
        [TestCase(RequiredVersion + "+build.1", RequiredVersion + "+build.2", "None")]
        public void ComparisonUsesNumericRevisionsPrereleasesAndTheSuppliedRequiredVersion(string installedVersion, string requiredVersion, string expectedAction)
        {
            var state = Evaluate(installedVersion: installedVersion, requiredVersion: requiredVersion);

            AssertState(state, expectedAction);
            Assert.That(state.RequiredVersion, Is.EqualTo(requiredVersion));
        }

        [TestCase(OlderVersion + "+build.1", OlderVersion + "+build.1", "Update")]
        [TestCase(OlderVersion + "+build.1", OlderVersion + "+build.2", "Manual")]
        [TestCase(OlderVersion + "+build.1", OlderVersion, "Manual")]
        [TestCase(OlderVersion, OlderVersion + "+build.1", "Manual")]
        [TestCase(OlderVersion + "+build.1", "", "Update")]
        public void UpdateRequiresExactDeclarationIdentityIncludingBuildMetadata(string installedVersion, string declaredVersion, string expectedAction)
        {
            AssertState(Evaluate(installedVersion: installedVersion, declaredVersion: declaredVersion), expectedAction);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("1.0")]
        [TestCase("v1.0.0")]
        [TestCase("1.0.0-01")]
        [TestCase("1.0.0-")]
        [TestCase("2026.10.10-1 ")]
        [TestCase(" 2026.10.10-1")]
        [TestCase("file:../canonical-path")]
        public void RegisteredMalformedInstalledVersionIsInvalidNeverAbsent(string installedVersion)
        {
            AssertState(Evaluate(installedVersion: installedVersion), "Invalid");
            AssertState(Evaluate(installedVersion: installedVersion, source: "Local"), "Invalid");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("1.0")]
        [TestCase("v1.0.0")]
        [TestCase("1.0.0-01")]
        [TestCase("^2026.10.10-1")]
        [TestCase("2026.10.10-1\n")]
        public void InvalidRequiredVersionFailsClosedForRegisteredAndMissingPackages(string requiredVersion)
        {
            AssertState(Evaluate(requiredVersion: requiredVersion), "Invalid");
            AssertState(Evaluate(registered: false, requiredVersion: requiredVersion), "Invalid");
        }

        [TestCase("Local")]
        [TestCase("Embedded")]
        [TestCase("Git")]
        [TestCase("Unknown")]
        [TestCase(null)]
        [TestCase("")]
        [TestCase("registry")]
        [TestCase("Registry ")]
        public void NonRegistrySourcesNeverAllowMutationAndNeedAnAvailableCurrentProvider(string source)
        {
            AssertState(Evaluate(source: source), "Manual");
            AssertState(Evaluate(source: source, providerAvailable: false), "Manual");
            AssertState(Evaluate(source: source, declaredVersion: RequiredVersion), "Manual");
            AssertState(Evaluate(source: source, installedVersion: RequiredVersion), "None");
            AssertState(Evaluate(source: source, installedVersion: NewerVersion), "None");
            AssertState(Evaluate(source: source, installedVersion: RequiredVersion, providerAvailable: false), "Manual");
            AssertState(Evaluate(source: source, installedVersion: NewerVersion, providerAvailable: false), "Manual");
        }

        [TestCase(null, "Update")]
        [TestCase("", "Update")]
        [TestCase(OlderVersion, "Update")]
        [TestCase(RequiredVersion, "Resolving")]
        [TestCase(NewerVersion, "Resolving")]
        [TestCase(RequiredVersion + "+pending.1", "Resolving")]
        [TestCase("2026.10.9-2", "Manual")]
        [TestCase("2026.10.8-1", "Manual")]
        [TestCase("^2026.10.10-1", "Manual")]
        [TestCase("~2026.10.10-1", "Manual")]
        [TestCase("*", "Manual")]
        [TestCase("latest", "Manual")]
        [TestCase("malformed", "Manual")]
        [TestCase("file:../canonical-path", "Manual")]
        [TestCase("https://example.invalid/canonical-path.git#main", "Manual")]
        [TestCase("2026.10.10-1 ", "Manual")]
        public void OlderRegistryDeclarationOnlyAllowsMatchingOrTransitiveUpdate(string declaredVersion, string expectedAction)
        {
            AssertState(Evaluate(declaredVersion: declaredVersion), expectedAction);
            AssertState(Evaluate(declaredVersion: declaredVersion, providerAvailable: false), expectedAction);
        }

        [TestCase("2026.10.10-2", "2026.10.10-10", "2026.10.10-10", "Resolving")]
        [TestCase("2026.10.10-2", "2026.10.10-9", "2026.10.10-10", "Manual")]
        [TestCase("2026.10.10-2", "2026.10.10-10", "2026.10.10-9", "Resolving")]
        public void PendingDeclarationUsesStrictNumericComparisonAgainstRequiredAndInstalled(string installedVersion, string declaredVersion, string requiredVersion, string expectedAction)
        {
            AssertState(Evaluate(installedVersion: installedVersion, declaredVersion: declaredVersion, requiredVersion: requiredVersion), expectedAction);
        }

        [TestCase(null, "Install")]
        [TestCase("", "Install")]
        [TestCase(OlderVersion, "Resolving")]
        [TestCase(RequiredVersion, "Resolving")]
        [TestCase(NewerVersion, "Resolving")]
        [TestCase("^2026.10.10-1", "Resolving")]
        [TestCase("malformed", "Resolving")]
        [TestCase(" ", "Resolving")]
        [TestCase("file:../canonical-path", "Manual")]
        [TestCase("FILE:../canonical-path", "Manual")]
        [TestCase("git://example.invalid/canonical-path.git", "Manual")]
        [TestCase("git+https://example.invalid/canonical-path.git", "Manual")]
        [TestCase("git@example.invalid:canonical-path.git", "Manual")]
        [TestCase("ssh://example.invalid/canonical-path.git", "Manual")]
        [TestCase("http://example.invalid/canonical-path.git", "Manual")]
        [TestCase("https://example.invalid/canonical-path.git#main", "Manual")]
        public void MissingPackageNeverRepeatsAddForAnExistingDeclaration(string declaredVersion, string expectedAction)
        {
            AssertState(Evaluate(registered: false, installedVersion: null, source: null, declaredVersion: declaredVersion), expectedAction);
            AssertState(Evaluate(registered: false, installedVersion: null, source: null, declaredVersion: declaredVersion, providerAvailable: false), expectedAction);
        }

        [Test]
        public void RunningRequestTakesPriorityOverAllOtherEvidenceAndDisablesApply()
        {
            AssertState(Evaluate(requestRunning: true), "Busy");
            AssertState(Evaluate(installedVersion: RequiredVersion, requestRunning: true), "Busy");
            AssertState(Evaluate(source: "Local", requestRunning: true), "Busy");
            AssertState(Evaluate(registered: false, requestRunning: true), "Busy");
            AssertState(Evaluate(registered: false, declaredVersion: RequiredVersion, requestRunning: true), "Busy");
            AssertState(Evaluate(installedVersion: null, requiredVersion: null, requestRunning: true, providerAvailable: false, manifestReadable: false), "Busy");
        }

        [Test]
        public void UnreadableManifestFailsClosedEvenForCurrentOrMissingPackages()
        {
            AssertState(Evaluate(manifestReadable: false), "Invalid");
            AssertState(Evaluate(installedVersion: RequiredVersion, manifestReadable: false), "Invalid");
            AssertState(Evaluate(installedVersion: NewerVersion, manifestReadable: false), "Invalid");
            AssertState(Evaluate(registered: false, manifestReadable: false), "Invalid");
            AssertState(Evaluate(registered: false, declaredVersion: RequiredVersion, manifestReadable: false), "Invalid");
        }

        [TestCase(null)]
        [TestCase(OlderVersion)]
        [TestCase(RequiredVersion)]
        [TestCase("file:../canonical-path")]
        public void CurrentRegistryVersionNeverDowngradesOrReinstallsForDeclarationOrProviderMismatch(string declaredVersion)
        {
            AssertState(Evaluate(installedVersion: RequiredVersion, declaredVersion: declaredVersion), "None");
            AssertState(Evaluate(installedVersion: NewerVersion, declaredVersion: declaredVersion), "None");
            AssertState(Evaluate(installedVersion: RequiredVersion, declaredVersion: declaredVersion, providerAvailable: false), "Reload");
            AssertState(Evaluate(installedVersion: NewerVersion, declaredVersion: declaredVersion, providerAvailable: false), "Reload");
        }

        [Test]
        public void VersionParsingIsBoundedWithoutTruncatingEvidence()
        {
            var oversized = "1.0.0+" + new string('b', UnityCliUpdateCheck.MaxSemVerCharacters);

            var installed = Evaluate(installedVersion: oversized);
            AssertState(installed, "Invalid");
            Assert.That(installed.InstalledVersion, Is.EqualTo(oversized));
            AssertState(Evaluate(requiredVersion: oversized), "Invalid");
            AssertState(Evaluate(declaredVersion: oversized), "Manual");
            AssertState(Evaluate(registered: false, declaredVersion: oversized), "Resolving");
        }

        [Test]
        public void PropertiesPreserveEveryInputWithoutTrimmingOrCaseNormalization()
        {
            const string installedVersion = " 2026.10.9-1 ";
            const string source = " Registry ";
            const string declaredVersion = "file:../canonical-path";
            var state = Evaluate(
                installedVersion: installedVersion,
                source: source,
                declaredVersion: declaredVersion,
                providerAvailable: false);

            AssertState(state, "Invalid");
            Assert.That(state.Registered, Is.True);
            Assert.That(state.InstalledVersion, Is.EqualTo(installedVersion));
            Assert.That(state.Source, Is.EqualTo(source));
            Assert.That(state.DeclaredVersion, Is.EqualTo(declaredVersion));
            Assert.That(state.RequiredVersion, Is.EqualTo(RequiredVersion));
            Assert.That(state.RequestRunning, Is.False);
            Assert.That(state.ProviderAvailable, Is.False);
            Assert.That(state.ManifestReadable, Is.True);
        }

        [Test]
        public void SameEvidenceAcceptsIdenticalInputsAndSelfButRejectsNull()
        {
            var first = Evaluate();
            var second = Evaluate();

            Assert.That(first.SameEvidence(second), Is.True);
            Assert.That(second.SameEvidence(first), Is.True);
            Assert.That(first.SameEvidence(first), Is.True);
            Assert.That(first.SameEvidence(null), Is.False);
        }

        [Test]
        public void NullStringsNormalizeOnlyToEmptyAndCompareAsTheSameEvidence()
        {
            var nulls = Evaluate(installedVersion: null, source: null, declaredVersion: null, requiredVersion: null);
            var empty = Evaluate(installedVersion: "", source: "", declaredVersion: "", requiredVersion: "");

            Assert.That(nulls.InstalledVersion, Is.Empty);
            Assert.That(nulls.Source, Is.Empty);
            Assert.That(nulls.DeclaredVersion, Is.Empty);
            Assert.That(nulls.RequiredVersion, Is.Empty);
            Assert.That(nulls.SameEvidence(empty), Is.True);
            Assert.That(empty.SameEvidence(nulls), Is.True);
        }

        [TestCase("Registered")]
        [TestCase("InstalledVersion")]
        [TestCase("Source")]
        [TestCase("DeclaredVersion")]
        [TestCase("RequiredVersion")]
        [TestCase("RequestRunning")]
        [TestCase("ProviderAvailable")]
        [TestCase("ManifestReadable")]
        public void SameEvidenceRejectsAChangeToEachOfTheEightInputs(string changedInput)
        {
            var original = Evaluate();
            var changed = Evaluate(
                registered: changedInput != "Registered",
                installedVersion: changedInput == "InstalledVersion" ? "2026.10.9-2" : OlderVersion,
                source: changedInput == "Source" ? "registry" : "Registry",
                declaredVersion: changedInput == "DeclaredVersion" ? OlderVersion : "",
                requiredVersion: changedInput == "RequiredVersion" ? NewerVersion : RequiredVersion,
                requestRunning: changedInput == "RequestRunning",
                providerAvailable: changedInput != "ProviderAvailable",
                manifestReadable: changedInput != "ManifestReadable");

            Assert.That(original.SameEvidence(changed), Is.False);
            Assert.That(changed.SameEvidence(original), Is.False);
        }

        [Test]
        public void SameActionOrSemVerPrecedenceDoesNotReplaceExactRawEvidence()
        {
            var first = Evaluate(installedVersion: OlderVersion + "+build.1");
            var second = Evaluate(installedVersion: OlderVersion + "+build.2");
            AssertState(first, "Update");
            AssertState(second, "Update");
            Assert.That(first.SameEvidence(second), Is.False);

            var requiredBuildOne = Evaluate(requiredVersion: RequiredVersion + "+build.1");
            var requiredBuildTwo = Evaluate(requiredVersion: RequiredVersion + "+build.2");
            AssertState(requiredBuildOne, "Update");
            AssertState(requiredBuildTwo, "Update");
            Assert.That(requiredBuildOne.SameEvidence(requiredBuildTwo), Is.False);

            var declarationOne = Evaluate(declaredVersion: RequiredVersion + "+build.1");
            var declarationTwo = Evaluate(declaredVersion: RequiredVersion + "+build.2");
            AssertState(declarationOne, "Resolving");
            AssertState(declarationTwo, "Resolving");
            Assert.That(declarationOne.SameEvidence(declarationTwo), Is.False);

            var sourceOne = Evaluate(source: "Local", installedVersion: RequiredVersion);
            var sourceTwo = Evaluate(source: "local", installedVersion: RequiredVersion);
            AssertState(sourceOne, "None");
            AssertState(sourceTwo, "None");
            Assert.That(sourceOne.SameEvidence(sourceTwo), Is.False);
            Assert.That(Evaluate(declaredVersion: "malformed").SameEvidence(Evaluate(declaredVersion: " malformed ")), Is.False);
        }

        [TestCase("{}", true, "")]
        [TestCase("{\"dependencies\":{}}", true, "")]
        [TestCase("{\"dependencies\":{\"com.romanilyin.canonicalpath\":\"\"}}", false, "")]
        [TestCase("{\"dependencies\":{\"com.romanilyin.canonicalpath\":\"2026.10.9-1\"}}", true, OlderVersion)]
        [TestCase("{\"dependencies\":{\"com.romanilyin.canonicalpath\":null}}", false, "")]
        [TestCase("{", false, "")]
        public void BoundedManifestReaderDistinguishesAbsenceFromInvalidDeclarations(string json, bool expected, string version)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                Assert.That(UnityMcpSetupWindow.TryReadCanonicalPathManifestDependency(stream, out var declared), Is.EqualTo(expected));
                Assert.That(declared, Is.EqualTo(version));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BoundedManifestReaderAcceptsExactByteLimitAndRejectsOverflow(bool overflow)
        {
            var bytes = Encoding.UTF8.GetBytes("{}" + new string(' ', UnityMcpSetupWindow.CanonicalPathManifestMaxBytes - 2 + (overflow ? 8 : 0)));
            using (var stream = new MemoryStream(bytes))
            {
                Assert.That(UnityMcpSetupWindow.TryReadCanonicalPathManifestDependency(stream, out var declared), Is.EqualTo(!overflow));
                Assert.That(declared, Is.Empty);
                Assert.That(stream.Position, Is.EqualTo(overflow ? UnityMcpSetupWindow.CanonicalPathManifestMaxBytes + 1 : bytes.Length));
            }
        }

        [Test]
        public void BoundedManifestReaderAcceptsUtf8BomButRejectsInvalidUtf8AndUnavailableStream()
        {
            using (var stream = new MemoryStream(new byte[] { 0xef, 0xbb, 0xbf, 0x7b, 0x7d }))
                Assert.That(UnityMcpSetupWindow.TryReadCanonicalPathManifestDependency(stream, out _), Is.True);
            using (var stream = new MemoryStream(new byte[] { 0x7b, 0x7d, 0xff }))
                Assert.That(UnityMcpSetupWindow.TryReadCanonicalPathManifestDependency(stream, out _), Is.False);
            Assert.That(UnityMcpSetupWindow.TryReadCanonicalPathManifestDependency(null, out _), Is.False);
        }

        static CanonicalPathDependencyState Evaluate(
            bool registered = true,
            string installedVersion = OlderVersion,
            string source = "Registry",
            string declaredVersion = "",
            string requiredVersion = RequiredVersion,
            bool requestRunning = false,
            bool providerAvailable = true,
            bool manifestReadable = true)
        {
            return CanonicalPathDependencyState.Evaluate(
                registered, installedVersion, source, declaredVersion, requiredVersion,
                requestRunning, providerAvailable, manifestReadable);
        }

        static void AssertState(CanonicalPathDependencyState state, string expectedAction)
        {
            Assert.That(state.Action.ToString(), Is.EqualTo(expectedAction));
            Assert.That(state.CanApply, Is.EqualTo(expectedAction == "Install" || expectedAction == "Update"));
            Assert.That(state.IsUpdate, Is.EqualTo(expectedAction == "Update"));
            Assert.That(state.IsReady, Is.EqualTo(expectedAction == "None"));
            Assert.That(state.NeedsAttention, Is.EqualTo(expectedAction != "None"));
            Assert.That(state.IsBusy, Is.EqualTo(expectedAction == "Busy"));
        }
    }
}
