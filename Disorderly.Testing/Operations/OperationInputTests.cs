using Disorderly.Operations;
using NUnit.Framework;

namespace Disorderly.Testing.Operations;

[TestFixture]
public class OperationInputTests
{
    [Test]
    public async Task MultipleIdentifiedRequestsBlockEntireOperationAndResponsesAreAcceptedOnce()
    {
        List<OperationResponse> allowed = [OperationResponse.Accept];
        OperationInputDefinition first = OperationFixture.Input() with { AllowedResponses = allowed };
        OperationInputDefinition second = OperationFixture.Input();
        OperationFixture.Runner runner = new() { Execute = _ => Task.FromResult(OperationFixture.Await(first, second)) };
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(count: 2, mutation: true));
        OperationId other = coordinator.AcceptSimulation(OperationFixture.Plan("Other"));
        await coordinator.RunNextAsync();
        allowed.Clear();
        OperationRecord waiting = coordinator.Find(id)!;
        Assert.That(waiting.State, Is.EqualTo(OperationState.AwaitingInput));
        Assert.That(waiting.OwnsMutationSlot, Is.False);
        Assert.That(waiting.VerifiedSteps, Is.Zero);
        Assert.That(waiting.InputRequests.Select(input => input.Id), Is.Unique);
        Assert.That(waiting.InputRequests.All(input => input.OperationId == id), Is.True);
        Assert.That(coordinator.FindInput(id, first.Id)!.AllowedResponses, Is.EqualTo(new[] { OperationResponse.Accept }));
        Assert.That(coordinator.Respond(other, first.Id, OperationResponse.Accept).Accepted, Is.False);
        Assert.That(coordinator.Respond(id, first.Id, OperationResponse.Decline).Accepted, Is.False);
        Assert.That(coordinator.Respond(id, OperationInputId.New(), OperationResponse.Accept).Accepted, Is.False);
        Assert.That(coordinator.Respond(id, first.Id, OperationResponse.Accept).Accepted, Is.True);
        Assert.That(coordinator.Respond(id, first.Id, OperationResponse.Accept).Accepted, Is.False);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.AwaitingInput));
        Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.False);
        Assert.That(coordinator.Respond(id, second.Id, (OperationResponse)99).Accepted, Is.False);
        Assert.That(coordinator.Respond(id, second.Id, OperationResponse.Decline).Accepted, Is.True);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Queued));
        Assert.That(waiting.InputRequests.All(input => input.State == OperationInputState.Outstanding), Is.True, "Old snapshots remain frozen.");
        runner.Execute = context =>
        {
            if (context.Operation.Id == id)
            {
                Assert.That(context.Operation.InputRequests.All(input => input.State == OperationInputState.Resolved), Is.True);
            }

            return Task.FromResult(OperationStepResult.Completed());
        };
        await coordinator.RunNextAsync(); // Other normal work keeps FIFO precedence.
        Assert.That(runner.Calls.Last().Operation.Id, Is.EqualTo(other));
        await coordinator.RunNextAsync();
        Assert.That(coordinator.Find(id)!.VerifiedSteps, Is.EqualTo(1));
        Assert.That(coordinator.Find(id)!.Steps[1].DispatchCount, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PauseIntentOrCancelPreventsInputFromAutomaticallyResuming(bool cancel)
    {
        OperationInputDefinition input = OperationFixture.Input();
        OperationFixture.Runner runner = new() { Execute = _ => Task.FromResult(OperationFixture.Await(input)) };
        OperationCoordinator coordinator = new(runner);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        await coordinator.RunNextAsync();
        coordinator.Pause(id, OperationFixture.Request());
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.AwaitingInput));
        Assert.That(coordinator.Find(id)!.PauseRequested, Is.True);
        if (cancel)
        {
            coordinator.Cancel(id, OperationFixture.Request());
        }

        OperationControlResult response = coordinator.Respond(id, input.Id, OperationResponse.Accept);
        Assert.That(response.Accepted, Is.EqualTo(!cancel));
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(cancel ? OperationState.Canceled : OperationState.Paused));
        Assert.That(await coordinator.RunNextAsync(), Is.False);
        if (!cancel)
        {
            Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.True);
        }
        else
        {
            Assert.That(coordinator.FindInput(id, input.Id)!.State, Is.EqualTo(OperationInputState.Invalidated));
        }
    }

    [Test]
    public async Task PauseDuringUnsafeSegmentIsPreservedWhenInputBoundaryArrives()
    {
        OperationFixture.Pending pending = new();
        OperationCoordinator coordinator = new(new OperationFixture.Runner { Execute = pending.Run });
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(mutation: true));
        Task<bool> task = coordinator.RunNextAsync();
        await pending.Started.Task;
        coordinator.Pause(id, OperationFixture.Request());
        OperationInputDefinition input = OperationFixture.Input();
        pending.Completion.SetResult(OperationFixture.Await(input)); await task;
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.AwaitingInput));
        Assert.That(coordinator.Find(id)!.OwnsMutationSlot, Is.False);
        coordinator.Respond(id, input.Id, OperationResponse.Accept);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Paused));
    }

    [Test]
    public async Task InputResolutionRevalidatesAndUsesNormalRatherThanManualPriority()
    {
        bool permitted = true;
        OperationFixture.Eligibility eligibility = new() { Evaluate = (_, reason) => new(reason != OperationEligibilityReason.InputResolved || permitted) };
        OperationInputDefinition input = OperationFixture.Input();
        OperationFixture.Runner runner = new() { Execute = _ => Task.FromResult(OperationFixture.Await(input)) };
        OperationCoordinator coordinator = new(runner, eligibility);
        OperationId waiting = coordinator.AcceptSimulation(OperationFixture.Plan("Input"));
        await coordinator.RunNextAsync();
        OperationId normal = coordinator.AcceptSimulation(OperationFixture.Plan("Normal"));
        permitted = false;
        coordinator.Respond(waiting, input.Id, OperationResponse.Accept);
        Assert.That(coordinator.Find(waiting)!.State, Is.EqualTo(OperationState.AwaitingInput));
        Assert.That(coordinator.Find(waiting)!.InputRequests.Count(request => request.State == OperationInputState.Outstanding), Is.Zero);
        permitted = true;
        Assert.That(coordinator.Reevaluate(waiting), Is.True);
        runner.Execute = _ => Task.FromResult(OperationStepResult.Completed());
        await coordinator.RunNextAsync();
        Assert.That(runner.Calls.Last().Operation.Id, Is.EqualTo(normal));
        await coordinator.RunNextAsync();
        Assert.That(runner.Calls.Last().Operation.Id, Is.EqualTo(waiting));
        Assert.That(eligibility.Reasons, Does.Contain(OperationEligibilityReason.InputResolved));
    }

    [Test]
    public async Task ConcurrentResponsesAcceptOneDecisionAndCancelRaceCannotReviveWork()
    {
        OperationInputDefinition input = OperationFixture.Input();
        OperationCoordinator coordinator = new(new OperationFixture.Runner { Execute = _ => Task.FromResult(OperationFixture.Await(input)) });
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        await coordinator.RunNextAsync();
        OperationControlResult[] responses = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => coordinator.Respond(id, input.Id, OperationResponse.Accept))));
        Assert.That(responses.Count(response => response.Accepted), Is.EqualTo(1));
        coordinator.Cancel(id, OperationFixture.Request());
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Canceled));
        Assert.That(coordinator.Reevaluate(id), Is.False);
        Assert.That(await coordinator.RunNextAsync(), Is.False);
    }

    [Test]
    public async Task AwaitingInputReleasesMutationSlotForAnotherMutation()
    {
        OperationFixture.Runner runner = new() { Execute = context => Task.FromResult(context.Operation.Name == "Input" ? OperationFixture.Await(OperationFixture.Input()) : OperationStepResult.Completed()) };
        OperationCoordinator coordinator = new(runner);
        OperationId blocked = coordinator.AcceptSimulation(OperationFixture.Plan("Input", mutation: true));
        OperationId other = coordinator.AcceptSimulation(OperationFixture.Plan("Other", mutation: true));
        await coordinator.RunNextAsync(); await coordinator.RunNextAsync();
        Assert.That(coordinator.Find(blocked)!.State, Is.EqualTo(OperationState.AwaitingInput));
        Assert.That(coordinator.Find(other)!.State, Is.EqualTo(OperationState.Succeeded));
    }
}