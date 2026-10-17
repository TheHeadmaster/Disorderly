using Disorderly.Operations;
using NUnit.Framework;

namespace Disorderly.Testing.Operations;

[TestFixture]
public class OperationControlTests
{
    [Test]
    public async Task QueuedPauseResumeAndCancelAreIdentifiedAndIdempotent()
    {
        OperationFixture.Runner runner = new();
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        Guid pause = OperationFixture.Request();
        Assert.That(coordinator.Pause(id, pause).Changed, Is.True);
        Assert.That(coordinator.Pause(id, pause).Changed, Is.False);
        Assert.That(coordinator.Pause(id, OperationFixture.Request()).Changed, Is.False);
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        Guid resume = OperationFixture.Request();
        Assert.That(coordinator.Resume(id, resume).Accepted, Is.True);
        Assert.That(coordinator.Resume(id, resume).Changed, Is.False);
        Assert.That(coordinator.Pause(id, pause).Changed, Is.False, "Old request cannot pause a new queued phase.");
        Assert.That(coordinator.Cancel(id, resume).Accepted, Is.False, "An ID cannot be reinterpreted as a different control.");
        Guid cancel = OperationFixture.Request();
        Assert.That(coordinator.Cancel(id, cancel).Changed, Is.True);
        Assert.That(coordinator.Cancel(id, cancel).Changed, Is.False);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Canceled));
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        Assert.That(runner.Calls, Is.Empty);
        Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RunningControlWaitsForFinalizationAndCancellationWins(bool cancel)
    {
        OperationFixture.Pending pending = new();
        OperationFixture.Runner runner = new() { Execute = pending.Run };
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(count: 2, mutation: true));
        Task<bool> run = coordinator.RunNextAsync();
        OperationStepContext context = await pending.Started.Task;
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Running));
        Assert.That(coordinator.Pause(id, OperationFixture.Request()).Accepted, Is.True);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Pausing));
        if (cancel)
        {
            coordinator.Cancel(id, OperationFixture.Request());
            Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.CancellationRequested));
            Assert.That(coordinator.Pause(id, OperationFixture.Request()).Accepted, Is.False);
        }

        Assert.That(context.CheckpointRequested(), Is.True);
        Assert.That(coordinator.Find(id)!.OwnsMutationSlot, Is.True);
        Assert.That(coordinator.Find(id)!.VerifiedSteps, Is.Zero);
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        pending.Completion.SetResult(OperationStepResult.Completed());
        await run;
        OperationRecord record = coordinator.Find(id)!;
        Assert.That(record.State, Is.EqualTo(cancel ? OperationState.Canceled : OperationState.Paused));
        Assert.That(record.OwnsMutationSlot, Is.False);
        Assert.That(record.VerifiedSteps, Is.EqualTo(1), "Cancel does not erase or undo verified work.");
        Assert.That(record.Steps[1].DispatchCount, Is.Zero);
        Assert.That(runner.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ResumeChecksEligibilityAndDoesNotBypassChangedResources()
    {
        bool allowed = false;
        OperationFixture.Eligibility eligibility = new() { Evaluate = (_, _) => new(allowed, allowed ? null : new("Blocked", "Simulated resource is unavailable.")) };
        OperationFixture.Runner runner = new();
        OperationCoordinator coordinator = new(runner, eligibility);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        coordinator.Pause(id, OperationFixture.Request());
        Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.False);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Paused));
        allowed = true;
        Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.True);
        allowed = false;
        Assert.That(await coordinator.RunNextAsync(), Is.False, "Eligibility is checked again at dispatch.");
        Assert.That(runner.Calls, Is.Empty);
        allowed = true;
        await coordinator.RunNextAsync();
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Succeeded));
    }

    [Test]
    public async Task ConcurrentControlsCannotReopenCanceledWork()
    {
        OperationCoordinator coordinator = new(new OperationFixture.Runner());
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        await Task.WhenAll(Enumerable.Range(0, 48).Select(index => Task.Run(() =>
        {
            if (index % 3 == 0) { coordinator.Pause(id, OperationFixture.Request()); }
            else if (index % 3 == 1) { coordinator.Resume(id, OperationFixture.Request()); }
            else { coordinator.Cancel(id, OperationFixture.Request()); }
        })));
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Canceled));
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        Assert.That(coordinator.Pause(default, Guid.NewGuid()).Accepted, Is.False);
        Assert.That(coordinator.Cancel(id, Guid.Empty).Accepted, Is.False);
    }
}