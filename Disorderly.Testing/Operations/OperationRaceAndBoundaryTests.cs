using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Execution;
using Disorderly.Operations;
using Disorderly.Services;
using Disorderly.Testing.Commands.Editing;
using Disorderly.Testing.Execution;
using NUnit.Framework;

namespace Disorderly.Testing.Operations;

[TestFixture]
public class OperationRaceAndBoundaryTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task StateChangesDuringDispatchRevalidationCannotStartAnObsoleteStep(bool shutdown)
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource checking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationFixture.Eligibility eligibility = new()
        {
            Evaluate = (_, _) =>
            {
                checking.TrySetResult();
                release.Wait();
                return new(true);
            },
        };
        OperationFixture.Runner runner = new();
        OperationCoordinator coordinator = new(runner, eligibility);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(mutation: true));
        Task<bool> dispatch = Task.Run(coordinator.RunNextAsync);
        await checking.Task;
        Task? stopping = null;
        if (shutdown)
        {
            stopping = coordinator.RequestShutdownAsync();
            Assert.That(stopping.IsCompleted, Is.False);
        }
        else
        {
            coordinator.Cancel(id, OperationFixture.Request());
        }

        release.Set();
        Assert.That(await dispatch, Is.False);
        if (stopping is not null) { await stopping; }
        Assert.That(runner.Calls, Is.Empty);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(shutdown ? OperationState.Paused : OperationState.Canceled));
    }

    [TestCase(OperationEligibilityReason.ManualResume)]
    [TestCase(OperationEligibilityReason.InputResolved)]
    public async Task CancellationDuringEligibilityCannotRequeueCanceledWork(OperationEligibilityReason reason)
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource checking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationFixture.Eligibility eligibility = new()
        {
            Evaluate = (_, current) =>
            {
                if (reason == current)
                {
                    checking.TrySetResult();
                    release.Wait();
                }

                return new(true);
            },
        };
        OperationInputDefinition input = OperationFixture.Input();
        OperationCoordinator coordinator = new(new OperationFixture.Runner { Execute = _ => Task.FromResult(OperationFixture.Await(input)) }, eligibility);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        Task<OperationControlResult> request;
        if (reason == OperationEligibilityReason.ManualResume)
        {
            coordinator.Pause(id, OperationFixture.Request());
            request = Task.Run(() => coordinator.Resume(id, OperationFixture.Request()));
        }
        else
        {
            await coordinator.RunNextAsync();
            request = Task.Run(() => coordinator.Respond(id, input.Id, OperationResponse.Accept));
        }

        await checking.Task;
        coordinator.Cancel(id, OperationFixture.Request()); release.Set();
        await request;
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Canceled));
        Assert.That(await coordinator.RunNextAsync(), Is.False);
    }

    [Test]
    public async Task OneOutstandingRequestHasExactlyOneWinnerWhenResolutionRacesCancellation()
    {
        OperationInputDefinition input = OperationFixture.Input();
        OperationCoordinator coordinator = new(new OperationFixture.Runner { Execute = _ => Task.FromResult(OperationFixture.Await(input)) });
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        await coordinator.RunNextAsync();
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task resolve = Task.Run(async () => { await start.Task; coordinator.Respond(id, input.Id, OperationResponse.Accept); });
        Task cancel = Task.Run(async () => { await start.Task; coordinator.Cancel(id, OperationFixture.Request()); });
        start.SetResult(); await Task.WhenAll(resolve, cancel);
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Canceled));
        Assert.That(coordinator.FindInput(id, input.Id)!.State, Is.Not.EqualTo(OperationInputState.Outstanding));
        Assert.That(coordinator.Respond(id, input.Id, OperationResponse.Accept).Accepted, Is.False);
        Assert.That(await coordinator.RunNextAsync(), Is.False);
    }

    [Test]
    public async Task ExplicitResumePriorityExpiresAfterItsFirstSafeSegment()
    {
        OperationFixture.Runner runner = new() { Execute = _ => Task.FromResult(OperationStepResult.Continue()) };
        OperationCoordinator coordinator = new(runner);
        OperationId normal = coordinator.AcceptSimulation(OperationFixture.Plan("Normal"));
        OperationId manual = coordinator.AcceptSimulation(OperationFixture.Plan("Manual"));
        coordinator.Pause(manual, OperationFixture.Request()); coordinator.Resume(manual, OperationFixture.Request());
        await coordinator.RunNextAsync(); await coordinator.RunNextAsync();
        Assert.That(runner.Calls.Select(context => context.Operation.Id), Is.EqualTo(new[] { manual, normal }));
    }

    [Test]
    public async Task ContradictoryRunnerClaimsAndNullResultsNeverBecomeSuccess()
    {
        OperationStepResult?[] invalid = [
            new(OperationStepOutcome.KnownFailure, true, Diagnostic: new("Ambiguous", "Uncertain", true)),
            new(OperationStepOutcome.MoreWork, true, true),
            new(OperationStepOutcome.Completed, true, true, [OperationFixture.Input()]),
            new((OperationStepOutcome)100, true),
            null,
        ];
        foreach (OperationStepResult? result in invalid)
        {
            OperationCoordinator coordinator = new(new OperationFixture.Runner { Execute = _ => Task.FromResult(result!) });
            OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
            await coordinator.RunNextAsync();
            Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.NeedsRecovery));
            Assert.That(coordinator.Find(id)!.VerifiedSteps, Is.Zero);
        }
    }

    [Test]
    public async Task DirectLaunchAndDraftRestorationRemainSeparateFromOperationResume()
    {
        OperationFixture.Runner runner = new();
        OperationCoordinator operations = new(runner);
        ExecutionFixture.Launcher launcher = new();
        CommandInvocation invocation = ExecutionFixture.Invocation();
        ExecutionResult launched = await ExecutionFixture.Coordinator(launcher).SubmitAsync(invocation.PrepareAttempt().Request!);
        Assert.That(launched.Outcome, Is.EqualTo(ExecutionOutcome.Succeeded));
        Assert.That(operations.Operations, Is.Empty);
        Assert.That(runner.Calls, Is.Empty);

        PaletteInteractionTests.View view = new();
        using CommandPaletteController palette = new(view, EditingFixture.Store());
        palette.Open(); view.Key("D"); view.Key("N");
        PalettePresentation selected = view.Presentation;
        view.Edit(new(selected.ActivationId, selected.Selection!.Id, selected.Editing!.SessionId, selected.Editing.Revision, "count", new ArgumentEdit.RawText("-")));
        palette.Dismiss();
        InvocationDraft draft = palette.DraftStore.Drafts.Single();
        OperationId operation = operations.AcceptSimulation(OperationFixture.Plan());
        operations.Pause(operation, OperationFixture.Request()); operations.Resume(operation, OperationFixture.Request());
        Assert.That(palette.State, Is.EqualTo(PaletteState.Hidden), "Operation Resume cannot reopen the palette or a draft.");
        Assert.That(palette.DraftStore.Drafts.Single().Id, Is.EqualTo(draft.Id));
        palette.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Selection!.Id, Is.EqualTo(draft.Editing.Invocation.Id));
        Assert.That(operations.Operations.Single().State, Is.EqualTo(OperationState.Queued));
        Assert.That(runner.Calls, Is.Empty, "Draft Resume cannot dispatch operation work.");
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }
    [Test]
    public async Task PauseDuringFinalInputRevalidationCannotLeaveAnUnresumableEmptyInputState()
    {
        using ManualResetEventSlim release = new();
        TaskCompletionSource checking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationFixture.Eligibility eligibility = new()
        {
            Evaluate = (_, reason) =>
            {
                if (reason == OperationEligibilityReason.InputResolved)
                {
                    checking.TrySetResult(); release.Wait();
                }

                return new(true);
            },
        };
        OperationInputDefinition input = OperationFixture.Input();
        OperationCoordinator coordinator = new(new OperationFixture.Runner { Execute = _ => Task.FromResult(OperationFixture.Await(input)) }, eligibility);
        OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan());
        await coordinator.RunNextAsync();
        Task<OperationControlResult> response = Task.Run(() => coordinator.Respond(id, input.Id, OperationResponse.Accept));
        await checking.Task;
        coordinator.Pause(id, OperationFixture.Request());
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Paused));
        release.Set(); await response;
        Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Paused));
        Assert.That(coordinator.Resume(id, OperationFixture.Request()).Accepted, Is.True);
    }

    [Test]
    public async Task ConcurrentAdmissionsStillBoundManualPriority()
    {
        TaskCompletionSource<OperationStepResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        OperationFixture.Runner runner = new() { Execute = _ => completion.Task };
        OperationCoordinator coordinator = new(runner, manualResumeQuota: 2);
        List<OperationId> admissions = [];
        HashSet<OperationId> seen = [];
        coordinator.ActivityChanged += snapshot =>
        {
            foreach (OperationActivityEntry entry in snapshot.Entries.Where(entry => entry.State == OperationState.Running))
            {
                if (seen.Add(entry.Id)) { admissions.Add(entry.Id); }
            }
        };
        OperationId normal = coordinator.AcceptSimulation(OperationFixture.Plan("Normal"));
        for (int index = 0; index < 8; index++)
        {
            OperationId manual = coordinator.AcceptSimulation(OperationFixture.Plan($"Manual {index}"));
            coordinator.Pause(manual, OperationFixture.Request()); coordinator.Resume(manual, OperationFixture.Request());
        }

        Task<bool>[] dispatches = Enumerable.Range(0, 9).Select(_ => Task.Run(coordinator.RunNextAsync)).ToArray();
        completion.SetResult(OperationStepResult.Completed());
        await Task.WhenAll(dispatches);
        Assert.That(admissions.IndexOf(normal), Is.InRange(0, 2));
        Assert.That(runner.Calls.Count, Is.EqualTo(9));
    }

    [Test]
    public async Task CancelRacingSafeCompletionNeverDispatchesRemainingStepsOrErasesProgress()
    {
        for (int index = 0; index < 16; index++)
        {
            OperationFixture.Pending pending = new();
            OperationFixture.Runner runner = new() { Execute = pending.Run };
            OperationCoordinator coordinator = new(runner);
            OperationId id = coordinator.AcceptSimulation(OperationFixture.Plan(count: 2));
            Task<bool> execution = coordinator.RunNextAsync(); await pending.Started.Task;
            Task cancel = Task.Run(() => coordinator.Cancel(id, OperationFixture.Request()));
            pending.Completion.SetResult(OperationStepResult.Completed());
            await Task.WhenAll(execution, cancel);
            Assert.That(coordinator.Find(id)!.State, Is.EqualTo(OperationState.Canceled));
            Assert.That(coordinator.Find(id)!.VerifiedSteps, Is.EqualTo(1));
            Assert.That(coordinator.Find(id)!.Steps[1].DispatchCount, Is.Zero);
            Assert.That(await coordinator.RunNextAsync(), Is.False);
        }
    }
}