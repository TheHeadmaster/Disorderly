using Disorderly.Commands;
using Disorderly.Execution;
using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing.Execution;

[TestFixture]
public class PaletteExecutionTests
{
    private static PaletteSubmissionEventArgs Run(PaletteInteractionTests.View view) =>
        new(view.Presentation.ActivationId, view.Presentation.Selection!.Id, view.Presentation.AttemptId);

    private static void Select(CommandPaletteController controller, PaletteInteractionTests.View view)
    {
        controller.Open(); view.Key("D"); view.Key("N");
    }

    [TestCase(null, null, PaletteState.Hidden)]
    [TestCase(CompletionPolicy.StayOpen, null, PaletteState.Visible)]
    [TestCase(CompletionPolicy.StayOpen, CompletionPolicy.Dismiss, PaletteState.Hidden)]
    [TestCase(CompletionPolicy.Dismiss, CompletionPolicy.StayOpen, PaletteState.Visible)]
    public async Task SuccessAppliesRetainedEffectiveCompletionPolicy(CompletionPolicy? commandPolicy, CompletionPolicy? bindingPolicy, PaletteState expected)
    {
        PaletteInteractionTests.View view = new();
        ExecutionFixture.Launcher launcher = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(commandPolicy, bindingPolicy), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        Assert.That(view.Presentation.AttemptId, Is.Null, "Selection does not prepare an attempt.");
        Assert.That(launcher.Calls, Is.Empty);
        Assert.That(view.Presentation.CanRun, Is.True);
        await controller.SubmitAsync(Run(view));
        Assert.That(controller.State, Is.EqualTo(expected));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
        if (expected == PaletteState.Visible)
        {
            Assert.That(view.Presentation.Breadcrumb, Is.EqualTo("Root / Development"));
            Assert.That(view.Presentation.Execution!.Outcome, Is.EqualTo(ExecutionOutcome.Succeeded));
            Assert.That(view.Presentation.CanRun, Is.False);
            await controller.SubmitAsync(Run(view));
            Assert.That(launcher.Calls.Count, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task PendingSubmissionDisablesRunAndRepeatedConfirmationCannotCreateAnotherAttempt()
    {
        TaskCompletionSource<ProcessLaunchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionFixture.Launcher launcher = new() { Start = _ => completion.Task };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(CompletionPolicy.StayOpen), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        PaletteSubmissionEventArgs original = Run(view);
        Task first = controller.SubmitAsync(original);
        Guid? attempt = view.Presentation.AttemptId;
        Assert.That(attempt, Is.Not.Null);
        Assert.That(view.Presentation.Submitting, Is.True);
        Assert.That(view.Presentation.CanRun, Is.False);
        Assert.That(view.Presentation.SelectionStatus, Is.EqualTo("Launching..."));
        await controller.SubmitAsync(original);
        await controller.SubmitAsync(Run(view));
        Assert.That(view.Presentation.AttemptId, Is.EqualTo(attempt));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
        completion.SetResult(ExecutionFixture.Success);
        await first;
        await controller.SubmitAsync(original);
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task KnownFailureRetainsInvocationArgumentsAndAllowsOnlyIdentifiedExplicitRetry()
    {
        ExecutionFixture.Launcher launcher = new() { Start = _ => Task.FromResult(ExecutionFixture.KnownFailure) };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        CommandInvocation invocation = view.Presentation.Selection!;
        PaletteSubmissionEventArgs original = Run(view);
        await controller.SubmitAsync(original);
        Guid? firstAttempt = view.Presentation.AttemptId;
        Assert.That(view.Presentation.Selection, Is.SameAs(invocation));
        Assert.That(view.Presentation.Selection!.Arguments, Is.SameAs(invocation.Arguments));
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Development"));
        Assert.That(view.Presentation.CanRun, Is.True);
        Assert.That(view.Presentation.RunLabel, Is.EqualTo("Retry"));
        Assert.That(view.Presentation.SelectionStatus, Does.Contain("not found"));
        await controller.SubmitAsync(original);
        Assert.That(launcher.Calls.Count, Is.EqualTo(1), "A stale Run event is not a Retry.");
        PaletteSubmissionEventArgs retry = Run(view);
        await controller.SubmitAsync(retry);
        Assert.That(view.Presentation.AttemptId, Is.Not.EqualTo(firstAttempt));
        Assert.That(view.Presentation.Selection!.Id, Is.EqualTo(invocation.Id));
        await controller.SubmitAsync(retry);
        Assert.That(launcher.Calls.Count, Is.EqualTo(2), "A stale Retry cannot create another attempt.");
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
    }

    [TestCase(ExecutionOutcome.Uncertain)]
    [TestCase(ExecutionOutcome.Rejected)]
    public async Task UncertainAndRejectedResultsDoNotOfferBlindRetry(ExecutionOutcome outcome)
    {
        ExecutionFixture.Launcher launcher = new() { Start = _ => Task.FromResult(new ProcessLaunchResult(outcome, "Outcome", "Do not retry blindly.")) };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        await controller.SubmitAsync(Run(view));
        Assert.That(view.Presentation.CanRun, Is.False);
        await controller.SubmitAsync(Run(view));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task IncompleteOrUnconfiguredSelectionCannotSubmit(bool configured, bool incomplete)
    {
        PaletteInteractionTests.View view = new();
        ExecutionFixture.Launcher launcher = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(configured: configured, extraRequired: incomplete), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        Assert.That(view.Presentation.CanRun, Is.False);
        await controller.SubmitAsync(Run(view));
        Assert.That(view.Presentation.AttemptId, Is.Null);
        Assert.That(launcher.Calls, Is.Empty);
    }

    [TestCase("dismiss")]
    [TestCase("focus")]
    [TestCase("escape")]
    [TestCase("selection")]
    [TestCase("reopen")]
    [TestCase("dispose")]
    public async Task LateCompletionDoesNotCancelLaunchOrRestoreObsoletePresentation(string transition)
    {
        TaskCompletionSource<ProcessLaunchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionFixture.Launcher launcher = new() { Start = _ => completion.Task };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(CompletionPolicy.StayOpen), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        PaletteSubmissionEventArgs old = Run(view);
        Task pending = controller.SubmitAsync(old);
        switch (transition)
        {
            case "dismiss": controller.Dismiss(); break;
            case "focus": view.LoseFocus(); break;
            case "escape": view.Key("Escape"); break;
            case "selection": view.Key("N"); break;
            case "reopen": controller.Dismiss(); Select(controller, view); break;
            case "dispose": controller.Dispose(); break;
        }

        PalettePresentation current = view.Presentation;
        PaletteState state = controller.State;
        completion.SetResult(ExecutionFixture.Success);
        await pending;
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
        Assert.That(view.Presentation, Is.SameAs(current));
        Assert.That(controller.State, Is.EqualTo(state));
        if (transition == "escape")
        {
            Assert.That(current.ContextName, Is.EqualTo("Root"));
            Assert.That(state, Is.EqualTo(PaletteState.Visible));
        }

        await controller.SubmitAsync(old);
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task HandlerExceptionIsObservedEvenAfterControllerDisposal()
    {
        TaskCompletionSource<ProcessLaunchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionFixture.Launcher launcher = new() { Start = _ => completion.Task };
        PaletteInteractionTests.View view = new();
        CommandPaletteController controller = new(view, ExecutionFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        Task pending = controller.SubmitAsync(Run(view));
        controller.Dispose(); controller.Dispose();
        completion.SetException(new InvalidOperationException("private failure"));
        await pending;
        Assert.That(pending.IsCompletedSuccessfully, Is.True);
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden));
    }

    [Test]
    public async Task ReplacementConfigurationDoesNotRemapSelectedExecutionOrCompletionPolicy()
    {
        PaletteInteractionTests.View view = new();
        ExecutionFixture.Launcher launcher = new();
        CommandConfigurationStore store = ExecutionFixture.Store(CompletionPolicy.StayOpen, path: @"C:\Original\app.exe");
        using CommandPaletteController controller = new(view, store, ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        Assert.That(store.Publish(new("root", [new("root", "New Root", [])]), [ExecutionFixture.Command() with { Execution = null }]).IsValid, Is.True);
        await controller.SubmitAsync(Run(view));
        Assert.That(launcher.Calls.Single().FileName, Is.EqualTo(@"C:\Original\app.exe"));
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Development"));
        controller.Dismiss(); controller.Open();
        Assert.That(view.Presentation.ContextName, Is.EqualTo("New Root"));
    }
    [Test]
    public void ViewRunEventIsObservedAndSelectionRemainsSideEffectFree()
    {
        PaletteInteractionTests.View view = new();
        ExecutionFixture.Launcher launcher = new();
        using CommandPaletteController controller = new(view, ExecutionFixture.Store(CompletionPolicy.StayOpen), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        Assert.That(launcher.Calls, Is.Empty);
        PaletteSubmissionEventArgs args = Run(view);
        view.RequestRun(args);
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
        Assert.That(view.Presentation.Execution!.Outcome, Is.EqualTo(ExecutionOutcome.Succeeded));
        view.RequestRun(args);
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }

}