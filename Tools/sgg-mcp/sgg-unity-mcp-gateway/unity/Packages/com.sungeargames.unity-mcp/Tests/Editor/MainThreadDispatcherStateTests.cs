using System.Collections;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using UnityEngine.TestTools;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class MainThreadDispatcherStateTests
    {
        [UnityTest]
        public IEnumerator EnqueuedWorkRunsOnEditorUpdate()
        {
            var executed = false;
            MainThreadDispatcher.Initialize();
            MainThreadDispatcher.Enqueue(() => executed = true);

            for (var frame = 0; frame < 10 && !executed; frame++)
            {
                yield return null;
            }

            Assert.That(executed, Is.True);
        }

        [Test]
        public void QueuedWorkCanBeCancelledBeforeStart()
        {
            var state = new MainThreadDispatchWorkState();

            Assert.That(state.StateName, Is.EqualTo("queued"));
            Assert.That(state.TryCancelBeforeStart(), Is.True);
            Assert.That(state.StateName, Is.EqualTo("cancelled_before_start"));
            Assert.That(state.TryStart(), Is.False);
        }

        [Test]
        public void RunningWorkCannotBeCancelledAndCompletesExplicitly()
        {
            var state = new MainThreadDispatchWorkState();

            Assert.That(state.TryStart(), Is.True);
            Assert.That(state.StateName, Is.EqualTo("running"));
            Assert.That(state.TryCancelBeforeStart(), Is.False);
            state.Complete();
            Assert.That(state.IsCompleted, Is.True);
            Assert.That(state.StateName, Is.EqualTo("completed"));
        }

        [Test]
        public void TimeoutEvidenceIsBoundedAndNonInterrupting()
        {
            var timeout = new MainThreadDispatchTimeoutException("running", -1, true);

            Assert.That(timeout.Message, Is.EqualTo("command_timeout"));
            Assert.That(timeout.WorkState, Is.EqualTo("running"));
            Assert.That(timeout.QueueDepthAtTimeout, Is.Zero);
            Assert.That(timeout.LateExecutionPossible, Is.True);
        }
    }
}
