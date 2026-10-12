using System;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class ProfilerFrameReadPolicyTests
    {
        [TestCase(false, true, 10, 20, -1, ProfilerFrameReadPolicy.Unavailable, -1)]
        [TestCase(true, false, 10, 20, -1, ProfilerFrameReadPolicy.Unavailable, -1)]
        [TestCase(true, true, -1, -1, -1, ProfilerFrameReadPolicy.Unsettled, -1)]
        [TestCase(true, true, 10, 20, 9, ProfilerFrameReadPolicy.Invalid, 9)]
        [TestCase(true, true, 10, 20, 21, ProfilerFrameReadPolicy.Invalid, 21)]
        [TestCase(true, true, 10, 20, -1, ProfilerFrameReadPolicy.Selected, 20)]
        [TestCase(true, true, 10, 20, 15, ProfilerFrameReadPolicy.Selected, 15)]
        public void FrameSelectionDistinguishesAvailabilityRangeAndExactIndex(
            bool apiAvailable,
            bool rangeAvailable,
            int firstFrameIndex,
            int lastFrameIndex,
            int requestedFrameIndex,
            string expectedState,
            int expectedSelectedFrame)
        {
            var state = ProfilerFrameReadPolicy.SelectFrame(
                apiAvailable, rangeAvailable, firstFrameIndex, lastFrameIndex, requestedFrameIndex, out var selectedFrameIndex);

            Assert.That(state, Is.EqualTo(expectedState));
            Assert.That(selectedFrameIndex, Is.EqualTo(expectedSelectedFrame));
        }

        [Test]
        public void ThreadDataAndBoundedEmptyStatesRemainDistinct()
        {
            Assert.That(ProfilerFrameReadPolicy.ClassifyData(ProfilerFrameReadPolicy.Selected, false, 0), Is.EqualTo(ProfilerFrameReadPolicy.Unavailable));
            Assert.That(ProfilerFrameReadPolicy.ClassifyData(ProfilerFrameReadPolicy.Selected, true, 0), Is.EqualTo(ProfilerFrameReadPolicy.Unsettled));
            Assert.That(ProfilerFrameReadPolicy.ClassifyData(ProfilerFrameReadPolicy.Selected, true, 2), Is.EqualTo(ProfilerFrameReadPolicy.Authoritative));
            Assert.That(ProfilerFrameReadPolicy.ResultState(ProfilerFrameReadPolicy.Authoritative, false, 0), Is.EqualTo("authoritative_empty"));
            Assert.That(ProfilerFrameReadPolicy.ResultState(ProfilerFrameReadPolicy.Authoritative, true, 0), Is.EqualTo("authoritative_bounded_empty"));
            Assert.That(ProfilerFrameReadPolicy.ResultState(ProfilerFrameReadPolicy.Authoritative, false, 1), Is.EqualTo("authoritative_nonempty"));
        }

        [Test]
        public void NormalTraversalEndAndNonFiniteTimingFailClosedWithoutFalseBounds()
        {
            Assert.That(ProfilerFrameReadPolicy.TraversalLimitReached(5000, 5000, true, false), Is.False);
            Assert.That(ProfilerFrameReadPolicy.TraversalLimitReached(5000, 5000, false, false), Is.True);
            Assert.That(ProfilerFrameReadPolicy.TraversalLimitReached(5000, 5000, false, true), Is.False);
            Assert.That(ProfilerFrameReadPolicy.IsFinite(1.25), Is.True);
            Assert.That(ProfilerFrameReadPolicy.IsFinite(double.NaN), Is.False);
            Assert.That(ProfilerFrameReadPolicy.IsFinite(double.PositiveInfinity), Is.False);
            Assert.That(ProfilerFrameReadPolicy.IsAuthoritativeResultEvidence(ProfilerFrameReadPolicy.Authoritative, "authoritative_empty"), Is.True);
            Assert.That(ProfilerFrameReadPolicy.IsAuthoritativeResultEvidence(ProfilerFrameReadPolicy.Authoritative, "unavailable"), Is.False);
            Assert.That(ProfilerFrameReadPolicy.IsAuthoritativeResultEvidence(ProfilerFrameReadPolicy.Unavailable, "authoritative_nonempty"), Is.False);
        }

        [Test]
        public void InstalledProfilerIteratorExposesTheBoundMemberShape()
        {
            var iteratorType = Type.GetType("UnityEditorInternal.ProfilerFrameDataIterator, UnityEditor", false)
                ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditorInternal.ProfilerFrameDataIterator", false);
            Assert.That(iteratorType, Is.Not.Null);
            Assert.That(iteratorType.GetMethod("GetThreadCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(int) }, null), Is.Not.Null);
            Assert.That(iteratorType.GetMethod("GetThreadName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null), Is.Not.Null);
            Assert.That(iteratorType.GetProperty("name", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Not.Null);
            Assert.That(iteratorType.GetProperty("durationMS", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Not.Null);
            Assert.That(iteratorType.GetProperty("depth", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Not.Null);
        }
    }
}
