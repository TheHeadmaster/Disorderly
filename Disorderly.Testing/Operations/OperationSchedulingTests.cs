using Disorderly.Operations;
using NUnit.Framework;

namespace Disorderly.Testing.Operations;

[TestFixture]
public class OperationSchedulingTests
{
    [Test]
    public async Task FifoYieldsAtEveryExplicitSafeSegment()
    {
        OperationFixture.Runner runner = new() { Execute = context => Task.FromResult(context.Step.DispatchCount == 1 ? OperationStepResult.Continue() : OperationStepResult.Completed()) };
        OperationCoordinator coordinator = new(runner);
        OperationId first = coordinator.AcceptSimulation(OperationFixture.Plan("First"));
        OperationId second = coordinator.AcceptSimulation(OperationFixture.Plan("Second"));
        for (int index = 0; index < 4; index++) { Assert.That(await coordinator.RunNextAsync(), Is.True); }
        Assert.That(runner.Calls.Select(context => context.Operation.Id), Is.EqualTo(new[] { first, second, first, second }));
        Assert.That(coordinator.Find(first)!.VerifiedSteps, Is.EqualTo(1));
        Assert.That(coordinator.Find(second)!.VerifiedSteps, Is.EqualTo(1));
    }

    [Test]
    public async Task ManualQuotaBoundsNormalWaitingAndIsConsumedAfterOneSelection()
    {
        OperationFixture.Runner runner = new();
        OperationCoordinator coordinator = new(runner, manualResumeQuota: 2);
        OperationId normalA = coordinator.AcceptSimulation(OperationFixture.Plan("Normal A"));
        OperationId normalB = coordinator.AcceptSimulation(OperationFixture.Plan("Normal B"));
        List<OperationId> manual = [];
        for (int index = 0; index < 3; index++)
        {
            OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan($"Manual {index}"));
            coordinator.Pause(id, OperationFixture.Request());
            coordinator.Resume(id, OperationFixture.Request());
            Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.False);
            manual.Add(id);
        }

        for (int index = 0; index < 5; index++) { await coordinator.RunNextAsync(); }
        Assert.That(runner.Calls.Select(context => context.Operation.Id), Is.EqualTo(new[] { manual[0], manual[1], normalA, manual[2], normalB }));
    }

    [Test]
    public async Task RepeatedPauseResumeCannotAccumulatePriorityOrStarveNormalWork()
    {
        OperationFixture.Runner runner = new() { Execute = _ => Task.FromResult(OperationStepResult.Continue()) };
        OperationCoordinator coordinator = new(runner, manualResumeQuota: 1);
        OperationId normal = coordinator.AcceptSimulation(OperationFixture.Plan("Normal"));
        OperationId manual = coordinator.AcceptSimulation(OperationFixture.Plan("Manual"));
        for (int index = 0; index < 10; index++)
        {
            coordinator.Pause(manual, OperationFixture.Request());
            coordinator.Resume(manual, OperationFixture.Request());
        }

        await coordinator.RunNextAsync();
        coordinator.Pause(manual, OperationFixture.Request());
        coordinator.Resume(manual, OperationFixture.Request());
        await coordinator.RunNextAsync();
        Assert.That(runner.Calls.Select(context => context.Operation.Id), Is.EqualTo(new[] { manual, normal }));
    }

    [Test]
    public async Task MutatingOwnershipIncludesFinalizationAndManualResumeCannotPreempt()
    {
        TaskCompletionSource mutation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finalization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource finalizing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationFixture.Runner runner = new()
        {
            Execute = async _ =>
            {
                await mutation.Task;
                finalizing.SetResult();
                await finalization.Task;
                return OperationStepResult.Completed();
            },
        };
        OperationCoordinator coordinator = new(runner);
        OperationId first = coordinator.AcceptSimulation(OperationFixture.Plan("First", mutation: true));
        Task<bool> running = coordinator.RunNextAsync();
        OperationId other = coordinator.AcceptSimulation(OperationFixture.Plan("Other", mutation: true));
        coordinator.Pause(other, OperationFixture.Request()); coordinator.Resume(other, OperationFixture.Request());
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        mutation.SetResult(); await finalizing.Task;
        Assert.That(coordinator.Find(first)!.OwnsMutationSlot, Is.True);
        Assert.That(coordinator.Find(first)!.VerifiedSteps, Is.Zero);
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        finalization.SetResult(); await running;
        Assert.That(coordinator.Find(first)!.OwnsMutationSlot, Is.False);
        runner.Execute = _ => Task.FromResult(OperationStepResult.Completed());
        await coordinator.RunNextAsync();
        Assert.That(runner.Calls.Select(context => context.Operation.Id), Is.EqualTo(new[] { first, other }));
    }

    [Test]
    public async Task NonMutatingSegmentsOverlapWhileOneMutationOwnsItsSlot()
    {
        TaskCompletionSource<OperationStepResult> complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationFixture.Runner runner = new() { Execute = _ => complete.Task };
        OperationCoordinator coordinator = new(runner);
        OperationId mutating = coordinator.AcceptSimulation(OperationFixture.Plan("Mutating", mutation: true));
        coordinator.AcceptSimulation(OperationFixture.Plan("Blocked mutation", mutation: true));
        OperationId readA = coordinator.AcceptSimulation(OperationFixture.Plan("Read A"));
        OperationId readB = coordinator.AcceptSimulation(OperationFixture.Plan("Read B"));
        Task<bool>[] active = [coordinator.RunNextAsync(), coordinator.RunNextAsync(), coordinator.RunNextAsync()];
        Assert.That(runner.Calls.Select(context => context.Operation.Id), Is.EqualTo(new[] { mutating, readA, readB }));
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        complete.SetResult(OperationStepResult.Completed());
        await Task.WhenAll(active);
    }

    [Test]
    public async Task ConcurrentDispatchCannotStartOneStepTwice()
    {
        OperationFixture.Pending pending = new();
        OperationFixture.Runner runner = new() { Execute = pending.Run };
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(mutation: true));
        Task<bool>[] calls = Enumerable.Range(0, 24).Select(_ => Task.Run(coordinator.RunNextAsync)).ToArray();
        await pending.Started.Task;
        pending.Completion.SetResult(OperationStepResult.Completed());
        bool[] results = await Task.WhenAll(calls);
        Assert.That(results.Count(result => result), Is.EqualTo(1));
        Assert.That(runner.Calls.Count, Is.EqualTo(1));
        Assert.That(coordinator.Find(id)!.Steps.Single().DispatchCount, Is.EqualTo(1));
    }

    [Test]
    public async Task BlockedEligibilityDoesNotBlockUnrelatedEligibleWork()
    {
        OperationFixture.Runner runner = new();
        OperationFixture.Eligibility eligibility = new() { Evaluate = (operation, _) => new(operation.Name != "Blocked") };
        OperationCoordinator coordinator = new(runner, eligibility);
        OperationId blocked = coordinator.AcceptSimulation(OperationFixture.Plan("Blocked"));
        OperationId ready = coordinator.AcceptSimulation(OperationFixture.Plan("Ready"));
        await coordinator.RunNextAsync();
        Assert.That(runner.Calls.Single().Operation.Id, Is.EqualTo(ready));
        Assert.That(coordinator.Find(blocked)!.State, Is.EqualTo(OperationState.Queued));
        Assert.That(coordinator.Find(blocked)!.Diagnostic!.Code, Is.EqualTo("EligibilityBlocked"));
    }
}