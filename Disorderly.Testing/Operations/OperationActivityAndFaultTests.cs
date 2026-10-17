using System.Collections.Concurrent;
using Disorderly.Operations;
using NUnit.Framework;

namespace Disorderly.Testing.Operations;

[TestFixture]
public class OperationActivityAndFaultTests
{
    [TestCase(OperationStepOutcome.KnownFailure, OperationState.Failed)]
    [TestCase(OperationStepOutcome.Uncertain, OperationState.NeedsRecovery)]
    public async Task FailureAndUncertaintyRemainDistinctWithoutRetry(OperationStepOutcome outcome, OperationState expected)
    {
        OperationFixture.Runner runner = new() { Execute = _ => Task.FromResult(new OperationStepResult(outcome, true, Diagnostic: new("Simulated", "Safe diagnostic"))) };
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(mutation: true));
        await coordinator.RunNextAsync();
        OperationRecord record = coordinator.Find(id)!;
        Assert.That(record.State, Is.EqualTo(expected));
        Assert.That(record.Diagnostic!.Uncertain, Is.EqualTo(outcome == OperationStepOutcome.Uncertain));
        Assert.That(record.OwnsMutationSlot, Is.False, "A reported safe boundary allows releasing the slot even when effects need reconciliation.");
        Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.False);
        Assert.That(coordinator.Cancel(id, OperationFixture.Request()).Accepted, Is.False);
        Assert.That(coordinator.Reevaluate(id), Is.False);
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        Assert.That(runner.Calls.Count, Is.EqualTo(1));
        Assert.That(coordinator.Activity.Entries.Single().NeedsAttention, Is.True);
    }

    [Test]
    public async Task UnexpectedExceptionCannotProveSafeReleaseAndBlocksMutationUntilFutureReconciliation()
    {
        OperationFixture.Runner runner = new() { Execute = _ => throw new InvalidOperationException("private raw value must not reach diagnostics") };
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(mutation: true));
        Assert.That(await coordinator.RunNextAsync(), Is.True);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.NeedsRecovery));
        Assert.That(coordinator.Find(id)!.Diagnostic!.Message, Does.Not.Contain("private raw value"));
        Assert.That(coordinator.Find(id)!.OwnsMutationSlot, Is.True);
        Assert.That(coordinator.Find(id)!.SafeBoundary, Is.False);
        coordinator.AcceptSimulation(OperationFixture.Plan("Other mutation", mutation: true));
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        runner.Execute = _ => Task.FromResult(OperationStepResult.Completed());
        coordinator.AcceptSimulation(OperationFixture.Plan("Non-mutating"));
        await coordinator.RunNextAsync();
        Assert.That(runner.Calls.Count, Is.EqualTo(2));
        Assert.That(coordinator.RequestShutdownAsync().IsCompleted, Is.False, "No timeout may pretend unresolved unsafe finalization was graceful.");
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task UnverifiedCompletionOrUnsafeReturnIsNotProgress(bool safe, bool verified)
    {
        OperationCoordinator coordinator = new(new OperationFixture.Runner { Execute = _ => Task.FromResult(new OperationStepResult(OperationStepOutcome.Completed, safe, verified)) });
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(mutation: true));
        await coordinator.RunNextAsync();
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.NeedsRecovery));
        Assert.That(coordinator.Find(id)!.VerifiedSteps, Is.Zero);
        Assert.That(coordinator.Find(id)!.OwnsMutationSlot, Is.EqualTo(!safe));
    }

    [Test]
    public async Task InvalidRunnerInputContractBlocksRecoveryAndDoesNotContinue()
    {
        OperationInputDefinition input = OperationFixture.Input();
        OperationFixture.Runner runner = new() { Execute = _ => Task.FromResult(OperationFixture.Await(input, input)) };
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        await coordinator.RunNextAsync();
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.NeedsRecovery));
        Assert.That(coordinator.Find(id)!.InputRequests, Is.Empty);
        Assert.That(coordinator.Respond(id, input.Id, OperationResponse.Accept).Accepted, Is.False);
        Assert.That(await coordinator.RunNextAsync(), Is.False);
    }

    [Test]
    public async Task ActivityIsImmutableFactualAndKeepsUnknownTotalsAndBlockedHistory()
    {
        OperationFixture.Pending pending = new();
        OperationFixture.Runner runner = new() { Execute = pending.Run };
        OperationCoordinator coordinator = new(runner);
        List<OperationActivitySnapshot> observations = [];
        coordinator.ActivityChanged += observations.Add;
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(count: 2, knownTotal: false));
        OperationActivitySnapshot queued = coordinator.Activity;
        Task<bool> task = coordinator.RunNextAsync();
        await pending.Started.Task;
        Assert.That(coordinator.Activity.Entries.Single().VerifiedSteps, Is.Zero);
        Assert.That(coordinator.Activity.Entries.Single().TotalSteps, Is.Null);
        pending.Completion.SetResult(OperationStepResult.Completed()); await task;
        coordinator.Pause(id, OperationFixture.Request());
        OperationActivityEntry paused = coordinator.Activity.Entries.Single();
        Assert.That(paused.State, Is.EqualTo(OperationState.Paused));
        Assert.That(paused.VerifiedSteps, Is.EqualTo(1));
        Assert.That(paused.CurrentStep, Is.EqualTo("Step 1"));
        Assert.That(queued.Entries.Single().State, Is.EqualTo(OperationState.Queued));
        Assert.That(queued.Entries.Single().VerifiedSteps, Is.Zero);
        Assert.That(observations.Select(snapshot => snapshot.Revision), Is.Ordered);
        coordinator.Cancel(id, OperationFixture.Request());
        Assert.That(coordinator.Activity.Entries.Single().State, Is.EqualTo(OperationState.Canceled));
        Assert.That(coordinator.Activity.Entries.Single().VerifiedSteps, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentNotificationsAreOrderedOutsideStateLocksAndObserverErrorsAreIsolated()
    {
        OperationCoordinator coordinator = new(new OperationFixture.Runner());
        ConcurrentQueue<long> revisions = new();
        coordinator.ActivityChanged += _ => throw new InvalidOperationException("Observer failure");
        coordinator.ActivityChanged += snapshot =>
        {
            // A different thread can acquire the state lock during a callback; no global lock is held here.
            Task.Run(() => coordinator.Operations).GetAwaiter().GetResult();
            revisions.Enqueue(snapshot.Revision);
        };
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() => coordinator.AcceptSimulation(OperationFixture.Plan($"Work {index}")))));
        Assert.That(revisions.Count, Is.EqualTo(24));
        Assert.That(revisions, Is.Ordered);
        Assert.That(revisions, Is.Unique);
        Assert.That(coordinator.Operations.Length, Is.EqualTo(24));
    }

    [Test]
    public async Task ShutdownStopsAdmissionsAndDispatchButWaitsForFinalization()
    {
        OperationFixture.Pending pending = new();
        OperationFixture.Runner runner = new() { Execute = pending.Run };
        OperationCoordinator coordinator = new(runner);
        OperationId active = coordinator.AcceptSimulation(OperationFixture.Plan(count: 2, mutation: true));
        Task<bool> execution = coordinator.RunNextAsync();
        OperationStepContext context = await pending.Started.Task;
        OperationId queued = coordinator.AcceptSimulation(OperationFixture.Plan());
        Task shutdown = coordinator.RequestShutdownAsync();
        Assert.That(shutdown.IsCompleted, Is.False);
        Assert.That(context.CheckpointRequested(), Is.True);
        Assert.That(coordinator.Find(active)!.OwnsMutationSlot, Is.True);
        Assert.That(coordinator.Find(active)!.State, Is.EqualTo(OperationState.Pausing));
        Assert.That(coordinator.Find(queued)!.State, Is.EqualTo(OperationState.Paused));
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        Assert.Throws<InvalidOperationException>(() => coordinator.AcceptSimulation(OperationFixture.Plan()));
        Assert.That(coordinator.Resume(queued, OperationFixture.Request()).Accepted, Is.False);
        pending.Completion.SetResult(OperationStepResult.Completed());
        await execution; await shutdown;
        Assert.That(coordinator.Find(active)!.State, Is.EqualTo(OperationState.Paused));
        Assert.That(coordinator.Find(active)!.VerifiedSteps, Is.EqualTo(1));
        Assert.That(coordinator.Find(active)!.OwnsMutationSlot, Is.False);
        Assert.That(coordinator.RequestShutdownAsync(), Is.SameAs(shutdown));
        Assert.That(runner.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task EligibilityExceptionFailsClosedWithoutCallingRunner()
    {
        OperationFixture.Eligibility eligibility = new() { Evaluate = (_, _) => throw new InvalidOperationException("Private") };
        OperationFixture.Runner runner = new();
        OperationCoordinator coordinator = new(runner, eligibility);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Queued));
        Assert.That(coordinator.Find(id)!.Diagnostic!.Code, Is.EqualTo("EligibilityException"));
        Assert.That(runner.Calls, Is.Empty);
    }
}