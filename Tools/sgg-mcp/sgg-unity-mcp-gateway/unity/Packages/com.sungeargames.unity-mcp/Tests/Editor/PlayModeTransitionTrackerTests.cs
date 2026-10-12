using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class PlayModeTransitionTrackerTests
    {
        [Test]
        public void RequestedTransitionExpectsDefaultDomainReload()
        {
            Assert.That(PlayModeTransitionTracker.ReloadExpectedForTransition(true, true, false, EnterPlayModeOptions.None), Is.True);
        }

        [Test]
        public void RequestedTransitionHonorsDisabledDomainReload()
        {
            Assert.That(PlayModeTransitionTracker.ReloadExpectedForTransition(true, true, true, EnterPlayModeOptions.DisableDomainReload), Is.False);
        }

        [Test]
        public void RequestedTransitionStillExpectsReloadWhenOnlySceneReloadIsDisabled()
        {
            Assert.That(PlayModeTransitionTracker.ReloadExpectedForTransition(true, true, true, EnterPlayModeOptions.DisableSceneReload), Is.True);
        }

        [Test]
        public void AlreadyReachedTargetDoesNotExpectReload()
        {
            Assert.That(PlayModeTransitionTracker.ReloadExpectedForTransition(false, true, false, EnterPlayModeOptions.None), Is.False);
        }

        [Test]
        public void ExitPlayModeDoesNotRequireAnEnterOnlyDomainReload()
        {
            Assert.That(PlayModeTransitionTracker.ReloadExpectedForTransition(true, false, false, EnterPlayModeOptions.None), Is.False);
        }

        [TestCase("playing", true, true, true)]
        [TestCase("playing", false, false, false)]
        [TestCase("editing", false, false, true)]
        [TestCase("editing", false, true, false)]
        public void RestoredTransitionReconcilesOnlyAnActuallyReachedTarget(string target, bool isPlaying, bool isPlayingOrWillChangePlaymode, bool expected)
        {
            Assert.That(PlayModeTransitionTracker.TargetReachedForRestoration(target, isPlaying, isPlayingOrWillChangePlaymode), Is.EqualTo(expected));
        }
    }
}
