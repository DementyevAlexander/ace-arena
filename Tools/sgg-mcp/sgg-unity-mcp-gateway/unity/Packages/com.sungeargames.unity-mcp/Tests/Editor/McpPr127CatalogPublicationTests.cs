using System.Linq;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr127CatalogPublicationTests
    {
        [Test]
        public void ReadyRequiresPublishedCatalogAndLiveListener()
        {
            var state = new BridgeRuntimeState();
            state.SetPackageUpdateStatus("current", "current", false, "fingerprint", string.Empty, 0, string.Empty);
            Assert.That(state.Status, Is.EqualTo("starting"));
            Assert.That(state.CatalogStatus, Is.EqualTo("pending"));

            state.PublishCatalog("catalog-a", 1);
            Assert.That(state.Status, Is.EqualTo("error"));
            Assert.That(state.CatalogStatus, Is.EqualTo("unavailable"));

            state.SetBridgeListening(true);
            Assert.That(state.Status, Is.EqualTo("ready"));
            Assert.That(state.CatalogStatus, Is.EqualTo("ready"));
            Assert.That(state.IsReady, Is.True);

            state.SetBridgeListening(false);
            Assert.That(state.Status, Is.EqualTo("error"));
            Assert.That(state.CatalogStatus, Is.EqualTo("unavailable"));
            Assert.That(state.IsReady, Is.False);
        }

        [Test]
        public void RebuildPublishesNewSnapshotWithoutMutatingPreviousCatalog()
        {
            var registry = CommandRegistry.Build(new BridgeRuntimeState());
            var first = registry.CaptureSnapshot();
            var firstIds = first.Descriptors.Select(descriptor => descriptor.Id).ToArray();
            var exposed = first.Descriptors.ToArray();
            var firstRisk = exposed[0].Risk;
            exposed[0].Risk = "destructive";
            exposed[0].Tags = new[] { "mutated" };

            registry.Rebuild();
            var second = registry.CaptureSnapshot();

            Assert.That(first.CatalogGeneration, Is.EqualTo(1));
            Assert.That(second.CatalogGeneration, Is.EqualTo(2));
            Assert.That(first.CommandsHash, Is.Not.Empty);
            Assert.That(second.CommandsHash, Is.EqualTo(first.CommandsHash));
            Assert.That(first.Descriptors.Select(descriptor => descriptor.Id), Is.EqualTo(firstIds));
            Assert.That(first.Descriptors[0].Risk, Is.EqualTo(firstRisk));
            Assert.That(first.Descriptors[0].Tags, Does.Not.Contain("mutated"));
            Assert.That(second.Descriptors.Select(descriptor => descriptor.Id), Is.EqualTo(firstIds));
        }
    }
}
