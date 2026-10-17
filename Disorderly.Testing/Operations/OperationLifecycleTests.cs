using Disorderly.Operations;
using NUnit.Framework;

namespace Disorderly.Testing.Operations;

[TestFixture]
public class OperationLifecycleTests
{
    [Test]
    public async Task AcceptanceFreezesPlanAndDistinctIdentityDomainsAndCompletionAreFactual()
    {
        OperationFixture.Runner runner = new();
        OperationCoordinator coordinator = new(runner);
        Guid invocation = Guid.NewGuid();
        Guid attempt = Guid.NewGuid();
        List<OperationStepDefinition> steps = [new(OperationStepId.New(), "First"), new(OperationStepId.New(), "Second")];
        OperationId first = coordinator.AcceptSimulation(new("Work", steps, invocation, attempt));
        OperationId second = coordinator.AcceptSimulation(OperationFixture.Plan());
        steps.Clear();
        OperationRecord snapshot = coordinator.Find(first)!;
        Assert.That(snapshot.State, Is.EqualTo(OperationState.Queued));
        Assert.That(snapshot.Id, Is.Not.EqualTo(second));
        Assert.That(snapshot.Id.Value, Is.Not.EqualTo(invocation).And.Not.EqualTo(attempt));
        Assert.That(snapshot.InvocationId, Is.EqualTo(invocation));
        Assert.That(snapshot.AttemptId, Is.EqualTo(attempt));
        Assert.That(snapshot.Steps.Length, Is.EqualTo(2));
        Assert.That(snapshot.Steps.All(step => step.OperationId == first), Is.True);
        IList<OperationStepSnapshot> immutableSteps = snapshot.Steps;
        Assert.Throws<NotSupportedException>(() => immutableSteps[0] = immutableSteps[0] with { State = OperationStepState.Verified });
        await coordinator.RunNextAsync();
        Assert.That(coordinator.Find(first)!.VerifiedSteps, Is.EqualTo(1));
        await coordinator.RunNextAsync(); // Other FIFO operation gets its turn.
        await coordinator.RunNextAsync();
        Assert.That(coordinator.Find(first)!.State, Is.EqualTo(OperationState.Succeeded));
        Assert.That(coordinator.Find(first)!.Steps.Select(step => step.Definition.Id), Is.EqualTo(snapshot.Steps.Select(step => step.Definition.Id)));
        Assert.That(snapshot.VerifiedSteps, Is.Zero, "Old observations cannot change.");
        Assert.That(coordinator.Find(first)!.VerifiedSteps, Is.EqualTo(2));
        Assert.That(coordinator.Operations.Length, Is.EqualTo(2), "Terminal history is retained.");
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        Assert.That(coordinator.Pause(first, OperationFixture.Request()).Accepted, Is.False);
        Assert.That(coordinator.Cancel(first, OperationFixture.Request()).Accepted, Is.False);
        Assert.That(coordinator.Resume(first, OperationFixture.Request()).Accepted, Is.False);
    }

    [TestCase(OperationState.Succeeded)]
    [TestCase(OperationState.Failed)]
    [TestCase(OperationState.Canceled)]
    [TestCase(OperationState.NeedsRecovery)]
    public void TerminalAndRecoveryStatesHaveNoOrdinaryOutgoingTransitions(OperationState state)
    {
        foreach (OperationState destination in Enum.GetValues<OperationState>())
        {
            Assert.That(OperationTransitions.CanTransition(state, destination), Is.False);
        }
    }

    [TestCase(OperationState.Queued, OperationState.Succeeded)]
    [TestCase(OperationState.Paused, OperationState.Running)]
    [TestCase(OperationState.AwaitingInput, OperationState.Running)]
    [TestCase(OperationState.Running, OperationState.Running)]
    public void IllegalShortcutsAreRejected(OperationState from, OperationState to) => Assert.That(OperationTransitions.CanTransition(from, to), Is.False);

    [Test]
    public void InvalidMetadataOrSharedStepIdentitiesCannotBeAccepted()
    {
        OperationCoordinator coordinator = new(new OperationFixture.Runner());
        Assert.Throws<ArgumentException>(() => coordinator.AcceptSimulation(OperationFixture.Plan() with { Name = " " }));
        Assert.Throws<ArgumentException>(() => coordinator.AcceptSimulation(OperationFixture.Plan() with { InvocationId = Guid.Empty }));
        OperationStepDefinition step = new(OperationStepId.New(), "Same");
        Assert.Throws<ArgumentException>(() => coordinator.AcceptSimulation(new("Bad", [step, step])));
        coordinator.AcceptSimulation(new("First", [step]));
        Assert.Throws<ArgumentException>(() => coordinator.AcceptSimulation(new("Second", [step])));
        Assert.Throws<ArgumentException>(() => coordinator.AcceptSimulation(new("Invalid", [step with { Id = default }])));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OperationCoordinator(new OperationFixture.Runner(), manualResumeQuota: 0));
    }

    [Test]
    public async Task EmptyPlanSucceedsOnlyAfterSchedulerGrantsOwnership()
    {
        OperationFixture.Runner runner = new();
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(new("No steps", []));
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Queued));
        Assert.That(await coordinator.RunNextAsync(), Is.True);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Succeeded));
        Assert.That(runner.Calls, Is.Empty);
    }
}