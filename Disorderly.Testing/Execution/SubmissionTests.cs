using Disorderly.Commands;
using Disorderly.Execution;
using NUnit.Framework;

namespace Disorderly.Testing.Execution;

[TestFixture]
public class SubmissionTests
{
    [Test]
    public async Task AcceptedAttemptKeepsIdentityAndRejectsReplay()
    {
        ExecutionFixture.Launcher launcher = new();
        SubmissionCoordinator coordinator = ExecutionFixture.Coordinator(launcher);
        ExecutionReadyRequest request = ExecutionFixture.Invocation().PrepareAttempt().Request!;
        ExecutionResult result = await coordinator.SubmitAsync(request);
        Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Succeeded));
        Assert.That(result.InvocationId, Is.EqualTo(request.InvocationId));
        Assert.That(result.AttemptId, Is.EqualTo(request.AttemptId));
        Assert.That(result.CommandId, Is.EqualTo("launch"));
        Assert.That((await coordinator.SubmitAsync(request)).Outcome, Is.EqualTo(ExecutionOutcome.AlreadyAccepted));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentDuplicateCallsAcceptOnlyOnceWhilePendingAndAfterCompletion()
    {
        TaskCompletionSource<ProcessLaunchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionFixture.Launcher launcher = new() { Start = _ => completion.Task };
        SubmissionCoordinator coordinator = ExecutionFixture.Coordinator(launcher);
        ExecutionReadyRequest request = ExecutionFixture.Invocation().PrepareAttempt().Request!;
        Task<ExecutionResult> first = coordinator.SubmitAsync(request);
        Task<ExecutionResult>[] calls = Enumerable.Range(0, 32).Select(_ => Task.Run(() => coordinator.SubmitAsync(request))).ToArray();
        ExecutionResult[] results = await Task.WhenAll(calls);
        Assert.That(first.IsCompleted, Is.False);
        Assert.That(results.All(result => result.Outcome == ExecutionOutcome.AlreadyAccepted), Is.True);
        completion.SetResult(ExecutionFixture.Success);
        Assert.That((await first).Outcome, Is.EqualTo(ExecutionOutcome.Succeeded));
        Assert.That((await coordinator.SubmitAsync(request)).Outcome, Is.EqualTo(ExecutionOutcome.AlreadyAccepted));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task MissingHandlerRejectsEvenWhenArgumentsAreComplete()
    {
        ExecutionFixture.Launcher launcher = new();
        SubmissionCoordinator coordinator = ExecutionFixture.Coordinator(launcher);
        CommandInvocation invocation = ExecutionFixture.Invocation(configured: false);
        Assert.That(invocation.IsExecutionReady, Is.True);
        Assert.That(coordinator.Availability(invocation).Available, Is.False);
        Assert.That((await coordinator.SubmitAsync(invocation.PrepareAttempt().Request!)).Outcome, Is.EqualTo(ExecutionOutcome.Rejected));
        Assert.That(launcher.Calls, Is.Empty);
        SubmissionCoordinator noHandlers = new(new([]));
        Assert.That((await noHandlers.SubmitAsync(ExecutionFixture.Invocation().PrepareAttempt().Request!)).Outcome, Is.EqualTo(ExecutionOutcome.Rejected));
    }

    [Test]
    public void IncompleteArgumentsCannotProduceSubmissionRequest()
    {
        CommandInvocation invocation = ExecutionFixture.Store(extraRequired: true).CreateInteraction().PrepareDirect("launch").Invocation!;
        Assert.That(invocation.PrepareAttempt().Request, Is.Null);
        Assert.That(ExecutionFixture.Coordinator(new()).Availability(invocation).Available, Is.False);
    }

    [Test]
    public async Task UnexpectedExceptionIsUncertainAndNeverAutomaticallyRetried()
    {
        ExecutionFixture.Launcher launcher = new() { Start = _ => throw new InvalidOperationException("sensitive internal detail") };
        SubmissionCoordinator coordinator = ExecutionFixture.Coordinator(launcher);
        ExecutionReadyRequest request = ExecutionFixture.Invocation().PrepareAttempt().Request!;
        ExecutionResult result = await coordinator.SubmitAsync(request);
        Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Uncertain));
        Assert.That(result.CanRetry, Is.False);
        Assert.That(result.Message, Does.Not.Contain("sensitive"));
        Assert.That((await coordinator.SubmitAsync(request)).Outcome, Is.EqualTo(ExecutionOutcome.AlreadyAccepted));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task BoundedAcceptanceBudgetNeverEvictsIdsOrPermitsReplay()
    {
        ExecutionFixture.Launcher launcher = new();
        SubmissionCoordinator coordinator = ExecutionFixture.Coordinator(launcher, 1);
        CommandInvocation invocation = ExecutionFixture.Invocation();
        ExecutionReadyRequest first = invocation.PrepareAttempt().Request!;
        await coordinator.SubmitAsync(first);
        Assert.That((await coordinator.SubmitAsync(invocation.PrepareAttempt().Request!)).Code, Is.EqualTo("AcceptanceCapacity"));
        Assert.That((await coordinator.SubmitAsync(first)).Outcome, Is.EqualTo(ExecutionOutcome.AlreadyAccepted));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }
    private sealed class MismatchedHandler : ICommandExecutionHandler
    {
        public bool Supports(CommandExecutionSpecification specification) => true;
        public Task<ExecutionResult> ExecuteAsync(ExecutionReadyRequest request) => Task.FromResult(new ExecutionResult(
            Guid.NewGuid(), Guid.NewGuid(), "wrong-command", ExecutionOutcome.Succeeded, "BadResult", "Wrong result"));
    }

    [Test]
    public async Task AHandlerCannotReportSuccessForAnUnrelatedAttempt()
    {
        SubmissionCoordinator coordinator = new(new([new MismatchedHandler()]));
        ExecutionReadyRequest request = ExecutionFixture.Invocation().PrepareAttempt().Request!;
        ExecutionResult result = await coordinator.SubmitAsync(request);
        Assert.That(result.Outcome, Is.EqualTo(ExecutionOutcome.Uncertain));
        Assert.That(result.AttemptId, Is.EqualTo(request.AttemptId));
        Assert.That(result.CanRetry, Is.False);
    }

}