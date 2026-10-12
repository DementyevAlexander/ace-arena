using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using System;
using System.Threading;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class ExplicitCompilationBarrierTests
    {
        const string OperationId = "0123456789abcdef0123456789abcdef";
        const string NextOperationId = "fedcba9876543210fedcba9876543210";

        [Test]
        public void AutomaticCompilationBeforeRequestCannotVerifyBarrier()
        {
            var pending = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100);
            var automaticStarted = pending.OnCompilationStarted(110);
            var automaticFinished = automaticStarted.OnCompilationFinished(120);

            Assert.That(automaticStarted.Started, Is.False);
            Assert.That(automaticFinished.Status, Is.EqualTo("pending"));

            var armed = automaticFinished.WithRequestIssued(120);
            var requestedStarted = armed.OnCompilationStarted(130);
            var verified = requestedStarted.OnCompilationFinished(140);

            Assert.That(requestedStarted.StartedGeneration, Is.EqualTo(requestedStarted.RequestGeneration));
            Assert.That(verified.Status, Is.EqualTo("verified"));
            Assert.That(verified.Started, Is.True);
            Assert.That(verified.Finished, Is.True);
        }

        [Test]
        public void RefreshCompilationAfterRequestCanVerifyBarrierWithoutSecondGeneration()
        {
            var armed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var startedByRefresh = armed.OnCompilationStarted(120);
            var finishedByRefresh = startedByRefresh.OnCompilationFinished(130);

            Assert.That(startedByRefresh.StartedGeneration, Is.EqualTo(armed.RequestGeneration));
            Assert.That(finishedByRefresh.Status, Is.EqualTo("verified"));
            Assert.That(finishedByRefresh.ObservedGeneration, Is.EqualTo(armed.RequestGeneration));
        }

        [Test]
        public void FinishWithoutPostRequestStartFailsClosed()
        {
            var state = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110).OnCompilationFinished(120);

            Assert.That(state.Status, Is.EqualTo("failed"));
            Assert.That(state.Failure, Is.EqualTo("finish_without_start"));
        }

        [Test]
        public void MultipleStartedGenerationsFailClosed()
        {
            var state = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(105)
                .OnCompilationStarted(110)
                .OnCompilationStarted(120);

            Assert.That(state.Status, Is.EqualTo("failed"));
            Assert.That(state.Failure, Is.EqualTo("multiple_generation_overlap"));
        }

        [Test]
        public void MultipleFinishFailsClosed()
        {
            var verified = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130);

            var duplicate = verified.OnCompilationFinished(OperationId, verified.StartedGeneration, 140);

            Assert.That(duplicate.Status, Is.EqualTo("failed"));
            Assert.That(duplicate.Failure, Is.EqualTo("multiple_finish"));
        }

        [Test]
        public void MalformedOrUnknownVersionStateIsUnavailable()
        {
            Assert.That(ExplicitCompilationBarrier.ParseForTests("malformed").Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(ValidPendingBlob().Replace("v=3", "v=4")).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(ValidPendingBlob().Replace("v=3", "v=2")).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(ValidPendingBlob().Replace("observed_generation=0", "observed_generation= 0")).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(ValidPendingBlob() + ";extra=1").Available, Is.False);
        }

        [Test]
        public void PendingStateRestoresAcrossReloadThenFinishes()
        {
            var started = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(105)
                .OnCompilationStarted(110);
            var restored = ExplicitCompilationBarrier.ParseForTests(started.Serialize());

            Assert.That(restored.Available, Is.True);
            Assert.That(restored.State.Status, Is.EqualTo("pending"));
            Assert.That(restored.State.Started, Is.True);

            var finished = restored.State.OnCompilationFinished(120);
            Assert.That(finished.Status, Is.EqualTo("verified"));
            Assert.That(ExplicitCompilationBarrier.ParseForTests(finished.Serialize()).State.Finished, Is.True);
        }

        [Test]
        public void StateRoundTripUsesOneStrictVersionedBlob()
        {
            var state = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var parsed = ExplicitCompilationBarrier.ParseForTests(state.Serialize());

            Assert.That(parsed.Available, Is.True);
            Assert.That(parsed.State.OperationId, Is.EqualTo(OperationId));
            Assert.That(parsed.State.SourceInstanceId, Is.EqualTo("test-instance-a"));
            Assert.That(parsed.State.RequestIssued, Is.True);
            Assert.That(parsed.State.Started, Is.False);
            Assert.That(parsed.State.Finished, Is.False);
        }

        [Test]
        public void SnapshotIsSafeFromBackgroundThread()
        {
            Exception failure = null;
            ExplicitCompilationBarrierSnapshot snapshot = null;
            var thread = new Thread(() =>
            {
                try
                {
                    snapshot = ExplicitCompilationBarrier.Snapshot(string.Empty, false);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });

            thread.Start();
            thread.Join();

            Assert.That(failure, Is.Null);
            Assert.That(snapshot, Is.Not.Null);
        }

        [Test]
        public void UnarmedPendingIsExplicitlyReplaced()
        {
            var prior = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100);
            var decision = ExplicitCompilationBarrier.BeginForTests(prior.Serialize(), NextOperationId, 200);

            Assert.That(decision.Success, Is.True);
            Assert.That(decision.State.OperationId, Is.EqualTo(NextOperationId));
            Assert.That(decision.State.Recovery, Is.EqualTo("replaced_unarmed_pending"));
            Assert.That(decision.State.PreviousFailure, Is.EqualTo("request_not_issued"));
        }

        [Test]
        public void FreshArmedPendingRejectsOverlap()
        {
            var prior = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var decision = ExplicitCompilationBarrier.BeginForTests(prior.Serialize(), NextOperationId, 110 + ExplicitCompilationBarrier.MaxOperationAgeTicks);

            Assert.That(decision.Success, Is.False);
            Assert.That(decision.ErrorCode, Is.EqualTo("compile_freshness_unverified"));
            Assert.That(decision.State.OperationId, Is.EqualTo(OperationId));
        }

        [Test]
        public void StaleArmedPendingIsAbandonedAndReplaced()
        {
            var prior = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var decision = ExplicitCompilationBarrier.BeginForTests(prior.Serialize(), NextOperationId, 111 + ExplicitCompilationBarrier.MaxOperationAgeTicks);

            Assert.That(decision.Success, Is.True);
            Assert.That(decision.State.OperationId, Is.EqualTo(NextOperationId));
            Assert.That(decision.State.Recovery, Is.EqualTo("abandoned_stale_armed"));
            Assert.That(decision.State.PreviousFailure, Is.EqualTo("operation_max_age_exceeded"));
        }

        [Test]
        public void StaleArmedPendingCannotResetWhileCompilationIsActive()
        {
            var prior = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var decision = ExplicitCompilationBarrier.BeginForTests(prior.Serialize(), NextOperationId, 111 + ExplicitCompilationBarrier.MaxOperationAgeTicks, true);

            Assert.That(decision.Success, Is.False);
            Assert.That(decision.ErrorCode, Is.EqualTo("unity_compiling"));
            Assert.That(decision.State.OperationId, Is.EqualTo(OperationId));
        }

        [Test]
        public void VerifiedBarrierCannotBeReplacedBeforePostReloadObservation()
        {
            var verified = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130);

            var blocked = ExplicitCompilationBarrier.BeginForTests(verified.Serialize(), NextOperationId, 140, false, false);
            var allowed = ExplicitCompilationBarrier.BeginForTests(verified.Serialize(), NextOperationId, 140, false, true);

            Assert.That(blocked.Success, Is.False);
            Assert.That(blocked.ErrorCode, Is.EqualTo("compile_freshness_unverified"));
            Assert.That(blocked.Failure, Is.EqualTo("explicit_compilation_reload_pending"));
            Assert.That(blocked.State.OperationId, Is.EqualTo(OperationId));
            Assert.That(allowed.Success, Is.True);
            Assert.That(allowed.State.OperationId, Is.EqualTo(NextOperationId));
        }

        [Test]
        public void VerifiedCompileFailureCanBeReplacedWithoutImpossibleReloadProof()
        {
            var verified = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(OperationId, 1, 130, 1);

            var decision = ExplicitCompilationBarrier.BeginForTests(verified.Serialize(), NextOperationId, 140, false, false, verified.TerminalCompileFailedWithoutReload);

            Assert.That(decision.Success, Is.True);
            Assert.That(decision.State.OperationId, Is.EqualTo(NextOperationId));
            Assert.That(decision.State.Recovery, Is.EqualTo("replaced_verified_compile_failed_no_reload"));
            Assert.That(decision.State.PreviousFailure, Is.EqualTo("verified"));
        }

        [Test]
        public void RememberedCompileFailureRequiresTheExactVerifiedOperation()
        {
            var verified = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130);
            var pending = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);

            Assert.That(ExplicitCompilationBarrier.MatchesExactVerifiedOperation(verified, OperationId), Is.True);
            Assert.That(ExplicitCompilationBarrier.MatchesExactVerifiedOperation(verified, NextOperationId), Is.False);
            Assert.That(ExplicitCompilationBarrier.MatchesExactVerifiedOperation(pending, OperationId), Is.False);
        }

        [Test]
        public void CompileFailureUsesExactPersistedResultAcrossIndependentLifecycleGenerations()
        {
            var verified = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(OperationId, 1, TimeSpan.TicksPerSecond * 20, 3);
            var independentGeneration = new CompilationLifecycleSnapshot(106, 1, new DateTime(TimeSpan.TicksPerSecond * 10, DateTimeKind.Utc), new DateTime(verified.FinishedTicks, DateTimeKind.Utc).AddMinutes(1));

            Assert.That(verified.TerminalCompileResult, Is.EqualTo("failed"));
            Assert.That(verified.TerminalCompileErrorCount, Is.EqualTo(3));
            Assert.That(ExplicitCompilationBarrier.CompileFailureMatchesBarrier(verified, independentGeneration, true), Is.True);
            Assert.That(ExplicitCompilationBarrier.CompileFailureMatchesBarrier(verified, independentGeneration, false), Is.False);
        }

        [Test]
        public void CleanCompilationRequiresExactBeforeMarkerAndDistinctReplacement()
        {
            var clean = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130);

            Assert.That(clean.PostReloadObserved, Is.False);
            var uncorrelatedReplacement = clean.WithReplacementObserved("test-instance-b", 150);
            Assert.That(uncorrelatedReplacement.Status, Is.EqualTo("abandoned"));
            Assert.That(uncorrelatedReplacement.Failure, Is.EqualTo("runtime_replaced_without_correlated_reload_proof"));
            Assert.That(clean.WithReloadStarted("wrong-instance", 140).ReloadStartedTicks, Is.Zero);

            var reloading = clean.WithReloadStarted("test-instance-a", 140);
            Assert.That(reloading.WithReplacementObserved("test-instance-a", 150).PostReloadObserved, Is.False);
            var replaced = reloading.WithReplacementObserved("test-instance-b", 150);
            Assert.That(replaced.PostReloadObserved, Is.True);
            Assert.That(replaced.ReplacementInstanceId, Is.EqualTo("test-instance-b"));
            Assert.That(ExplicitCompilationBarrier.ParseForTests(replaced.Serialize()).State.PostReloadObserved, Is.True);
        }

        [Test]
        public void ReplacementAbandonsAnOperationThatNeverStartedCompilation()
        {
            var armed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);

            var abandoned = armed.WithReplacementObserved("test-instance-b", 120);

            Assert.That(abandoned.Status, Is.EqualTo("abandoned"));
            Assert.That(abandoned.Failure, Is.EqualTo("runtime_replaced_before_compilation_started"));
            Assert.That(ExplicitCompilationBarrier.ParseForTests(abandoned.Serialize()).State.Status, Is.EqualTo("abandoned"));
        }

        [Test]
        public void SupersededBarrierFailsExactRecoveryButDoesNotBlockIndependentWork()
        {
            var abandoned = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .WithReplacementObserved("test-instance-b", 120);
            var exact = new ExplicitCompilationBarrierSnapshot("abandoned", abandoned, abandoned.Failure, true, false, 0);
            var independent = new ExplicitCompilationBarrierSnapshot("abandoned", abandoned, abandoned.Failure, false, false, 0);

            Assert.That(exact.Unavailable, Is.True);
            Assert.That(exact.Stable, Is.False);
            Assert.That(independent.Unavailable, Is.False);
            Assert.That(independent.Stable, Is.True);

            var replacement = ExplicitCompilationBarrier.BeginForTests(abandoned.Serialize(), NextOperationId, 130);
            Assert.That(replacement.Success, Is.True);
            Assert.That(replacement.State.OperationId, Is.EqualTo(NextOperationId));
        }

        [Test]
        public void UncorrelatedCleanReplacementAlsoUnblocksOnlyIndependentWork()
        {
            var clean = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130);
            var abandoned = clean.WithReplacementObserved("test-instance-b", 140);
            var parsed = ExplicitCompilationBarrier.ParseForTests(abandoned.Serialize());
            var exact = new ExplicitCompilationBarrierSnapshot("abandoned", parsed.State, parsed.State.Failure, true, false, 0);
            var independent = new ExplicitCompilationBarrierSnapshot("abandoned", parsed.State, parsed.State.Failure, false, false, 0);

            Assert.That(parsed.Available, Is.True);
            Assert.That(exact.Unavailable, Is.True);
            Assert.That(exact.Stable, Is.False);
            Assert.That(independent.Unavailable, Is.False);
            Assert.That(independent.Stable, Is.True);
        }

        [Test]
        public void LaterSameProcessReloadPreservesTheFirstVerifiedReplacement()
        {
            var replaced = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130)
                .WithReloadStarted("test-instance-a", 140)
                .WithReplacementObserved("test-instance-b", 150);

            var laterReload = replaced.WithReplacementObserved("test-instance-c", 160);

            Assert.That(laterReload, Is.SameAs(replaced));
            Assert.That(laterReload.Status, Is.EqualTo("verified"));
            Assert.That(laterReload.Failure, Is.EqualTo("none"));
            Assert.That(laterReload.ReplacementInstanceId, Is.EqualTo("test-instance-b"));
            Assert.That(laterReload.PostReloadObserved, Is.True);
        }

        [Test]
        public void PersistedLegacyReplacementMismatchRecoversItsExactTerminalProof()
        {
            var verified = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130)
                .WithReloadStarted("test-instance-a", 140)
                .WithReplacementObserved("test-instance-b", 150)
                .Serialize();
            var legacyFailure = verified
                .Replace("status=verified", "status=failed")
                .Replace(";failure=none;recovery=", ";failure=replacement_marker_mismatch;recovery=");

            var recovered = ExplicitCompilationBarrier.RecoverPersistedStateForTests(legacyFailure);

            Assert.That(recovered.Available, Is.True);
            Assert.That(recovered.State.Status, Is.EqualTo("verified"));
            Assert.That(recovered.State.Failure, Is.EqualTo("none"));
            Assert.That(recovered.State.Recovery, Is.EqualTo("preserved_verified_replacement"));
            Assert.That(recovered.State.PreviousFailure, Is.EqualTo("replacement_marker_mismatch"));
            Assert.That(recovered.State.OperationId, Is.EqualTo(OperationId));
            Assert.That(recovered.State.ReplacementInstanceId, Is.EqualTo("test-instance-b"));
            Assert.That(recovered.State.PostReloadObserved, Is.True);
        }

        [Test]
        public void CleanCompilationRejectsStaleReloadAndReplacementMarkers()
        {
            var clean = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130);
            var staleReloadTicks = clean.FinishedTicks + ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks + 1;

            Assert.That(clean.WithReloadStarted("test-instance-a", staleReloadTicks).ReloadStartedTicks, Is.Zero);

            var reloading = clean.WithReloadStarted("test-instance-a", 140);
            var staleReplacementTicks = reloading.ReloadStartedTicks + ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks + 1;
            Assert.That(reloading.WithReplacementObserved("test-instance-b", staleReplacementTicks).PostReloadObserved, Is.False);
        }

        [Test]
        public void ReplacementCreatedBeforeLateFinishReconcilesAfterExactFinish()
        {
            var started = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .WithReloadStarted("test-instance-a", 130);

            Assert.That(started.WithReplacementObserved("test-instance-b", 140).PostReloadObserved, Is.False);
            var finished = started.OnCompilationFinished(OperationId, started.StartedGeneration, 150, 0);
            var reconciled = finished.WithReplacementObserved("test-instance-b", 160);

            Assert.That(reconciled.TerminalCompileResult, Is.EqualTo("clean"));
            Assert.That(reconciled.PostReloadObserved, Is.True);
        }

        [Test]
        public void FailedCompilationCannotAcquireReloadOrReplacementProof()
        {
            var failed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(OperationId, 1, 130, 1);

            var afterReload = failed.WithReloadStarted("test-instance-a", 140).WithReplacementObserved("test-instance-b", 150);

            Assert.That(failed.TerminalCompileFailedWithoutReload, Is.True);
            Assert.That(afterReload.ReloadStartedTicks, Is.Zero);
            Assert.That(afterReload.PostReloadObserved, Is.False);
        }

        [Test]
        public void FailedCompilationRejectsAnImpossibleExistingReloadMarker()
        {
            var reloading = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .WithReloadStarted("test-instance-a", 130);

            var failed = reloading.OnCompilationFinished(OperationId, reloading.StartedGeneration, 140, 1);

            Assert.That(failed.Status, Is.EqualTo("failed"));
            Assert.That(failed.Failure, Is.EqualTo("reload_marker_mismatch"));
            Assert.That(failed.TerminalCompileResult, Is.EqualTo("unknown"));
            Assert.That(ExplicitCompilationBarrier.ParseForTests(failed.Serialize()).Available, Is.True);
        }

        [Test]
        public void InvalidStateIsObservablyResetByNewExplicitBegin()
        {
            var decision = ExplicitCompilationBarrier.BeginForTests("malformed", NextOperationId, 200);

            Assert.That(decision.Success, Is.True);
            Assert.That(decision.State.Recovery, Is.EqualTo("reset_invalid_state"));
            Assert.That(decision.State.PreviousFailure, Is.EqualTo("malformed_session_state"));
        }

        [Test]
        public void OldOperationCallbackCannotSatisfyReplacement()
        {
            var prior = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100);
            var replacement = ExplicitCompilationBarrier.BeginForTests(prior.Serialize(), NextOperationId, 200).State.WithRequestIssued(210);

            var afterOldStart = replacement.OnCompilationStarted(OperationId, 220);
            var afterOldFinish = afterOldStart.OnCompilationFinished(OperationId, 1, 230);

            Assert.That(afterOldFinish.OperationId, Is.EqualTo(NextOperationId));
            Assert.That(afterOldFinish.Started, Is.False);
            Assert.That(afterOldFinish.Finished, Is.False);
        }

        [Test]
        public void ProductionCallbackHandlerRejectsOldEpochAfterAllowedReset()
        {
            var stale = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var replacement = ExplicitCompilationBarrier.BeginForTests(stale.Serialize(), NextOperationId, 111 + ExplicitCompilationBarrier.MaxOperationAgeTicks).State.WithRequestIssued(200 + ExplicitCompilationBarrier.MaxOperationAgeTicks);
            var oldEpoch = new ExplicitCompilationRuntimeEpoch(OperationId, stale.RequestGeneration);

            var handled = ExplicitCompilationBarrierCallbacks.HandleStarted(replacement, oldEpoch, new object(), 210 + ExplicitCompilationBarrier.MaxOperationAgeTicks);

            Assert.That(handled.State.Status, Is.EqualTo("failed"));
            Assert.That(handled.State.Failure, Is.EqualTo("runtime_epoch_unavailable"));
            Assert.That(handled.State.Finished, Is.False);
        }

        [Test]
        public void ProductionCallbackHandlerPairsContextWithinDomain()
        {
            var armed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var epoch = new ExplicitCompilationRuntimeEpoch(OperationId, armed.RequestGeneration);
            var context = new object();

            var started = ExplicitCompilationBarrierCallbacks.HandleStarted(armed, epoch, context, 120);
            var finished = ExplicitCompilationBarrierCallbacks.HandleFinished(started.State, started.Epoch, context, 130);

            Assert.That(finished.State.Status, Is.EqualTo("verified"));
            Assert.That(finished.State.Finished, Is.True);
        }

        [Test]
        public void DistinctEqualContextTokensAreRejected()
        {
            var armed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var epoch = new ExplicitCompilationRuntimeEpoch(OperationId, armed.RequestGeneration);
            var started = ExplicitCompilationBarrierCallbacks.HandleStarted(armed, epoch, new EqualToken(7), 120);

            var finished = ExplicitCompilationBarrierCallbacks.HandleFinished(started.State, started.Epoch, new EqualToken(7), 130);

            Assert.That(finished.State.Status, Is.EqualTo("failed"));
            Assert.That(finished.State.Failure, Is.EqualTo("callback_context_mismatch"));
        }

        [Test]
        public void ProductionCallbackHandlerAcceptsPersistedStartedFinishAfterReload()
        {
            var armed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var epoch = new ExplicitCompilationRuntimeEpoch(OperationId, armed.RequestGeneration);
            var started = ExplicitCompilationBarrierCallbacks.HandleStarted(armed, epoch, new object(), 120).State;
            var restored = ExplicitCompilationBarrier.ParseForTests(started.Serialize()).State;

            var finished = ExplicitCompilationBarrierCallbacks.HandleFinished(restored, null, new object(), 130);

            Assert.That(finished.State.Status, Is.EqualTo("verified"));
            Assert.That(finished.State.Finished, Is.True);
        }

        [Test]
        public void TimestampAndGenerationImpossibleCombinationsAreMalformed()
        {
            var verified = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130)
                .Serialize();
            var replaced = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100)
                .WithRequestIssued(110)
                .OnCompilationStarted(120)
                .OnCompilationFinished(130)
                .WithReloadStarted("test-instance-a", 140)
                .WithReplacementObserved("test-instance-b", 150)
                .Serialize();
            var staleTerminalTicks = 130 + ExplicitCompilationBarrier.MaxReloadEdgeAgeTicks + 1;

            Assert.That(ExplicitCompilationBarrier.ParseForTests(verified.Replace("request_issued_ticks=110", "request_issued_ticks=99")).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(verified.Replace("observed_generation=1", "observed_generation=2")).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(verified.Replace("finished_generation=1", "finished_generation=2")).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(verified.Replace("terminal_compile_observed_ticks=130", "terminal_compile_observed_ticks=" + staleTerminalTicks)).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(replaced.Replace("terminal_compile_observed_ticks=130", "terminal_compile_observed_ticks=151")).Available, Is.False);
            Assert.That(ExplicitCompilationBarrier.ParseForTests(ValidPendingBlob().Replace("requested_ticks=100", "requested_ticks=" + (DateTime.MaxValue.Ticks + 1))).Available, Is.False);
        }

        static string ValidPendingBlob()
        {
            return ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).Serialize();
        }

        sealed class EqualToken
        {
            readonly int m_value;
            public EqualToken(int value) { m_value = value; }
            public override bool Equals(object obj) { return obj is EqualToken other && other.m_value == m_value; }
            public override int GetHashCode() { return m_value; }
        }
    }
}
