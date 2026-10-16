using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Execution;
using Disorderly.Services;
using Disorderly.Testing.Execution;
using NUnit.Framework;

namespace Disorderly.Testing.Commands.Editing;

[TestFixture]
public class PaletteArgumentTests
{
    private static void Select(CommandPaletteController controller, PaletteInteractionTests.View view)
    {
        controller.Open(); view.Key("D"); view.Key("N");
    }

    private static PaletteArgumentEditEventArgs Change(PalettePresentation view, string parameter, ArgumentEdit edit) =>
        new(view.ActivationId, view.Selection!.Id, view.Editing!.SessionId, view.Editing.Revision, parameter, edit);
    private static void Edit(PaletteInteractionTests.View view, string parameter, string text) => view.Edit(Change(view.Presentation, parameter, new ArgumentEdit.RawText(text)));
    private static PaletteSubmissionEventArgs Run(PalettePresentation view) => new(view.ActivationId, view.Selection!.Id, view.AttemptId, view.Editing!.Revision);

    [TestCase(CompletionPolicy.Dismiss)]
    [TestCase(CompletionPolicy.StayOpen)]
    public async Task RequiredInputMakesRunReadyAndSubmissionUsesLatestImmutableRevision(CompletionPolicy policy)
    {
        PaletteInteractionTests.View view = new();
        ExecutionFixture.Launcher launcher = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(presetPath: false, requiredText: true, policy: policy), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        CommandInvocation original = view.Presentation.Selection!;
        Assert.That(view.Presentation.Editing!.Parameters.Select(parameter => parameter.Parameter.Id), Is.EqualTo(new[] { "text", "flag", "count", "executable" }));
        Assert.That(view.Presentation.CanRun, Is.False);
        Edit(view, "text", "Unicode 日本語");
        Edit(view, "executable", @"C:\Old\app.exe");
        PaletteSubmissionEventArgs staleRun = Run(view.Presentation);
        Edit(view, "executable", @"C:\Newest\app.exe");
        Assert.That(view.Presentation.Selection!.Id, Is.EqualTo(original.Id));
        Assert.That(view.Presentation.CanRun, Is.True);
        Assert.That(view.Presentation.AttemptId, Is.Null);
        await controller.SubmitAsync(staleRun);
        await controller.SubmitAsync(new(view.Presentation.ActivationId, original.Id));
        Assert.That(launcher.Calls, Is.Empty, "Edited submissions require the current editing revision.");
        await controller.SubmitAsync(Run(view.Presentation));
        Assert.That(launcher.Calls.Single().FileName, Is.EqualTo(@"C:\Newest\app.exe"));
        Assert.That(view.Presentation.Selection!.Arguments.Assignments.Single(argument => argument.ParameterId == "text").Value, Is.EqualTo(new ArgumentValue.Text("Unicode 日本語")));
        Assert.That(original.Arguments.Assignments, Is.Empty);
        controller.Dismiss();
        Assert.That(controller.DraftStore.Drafts, Is.Empty, "Completed invocations are not unfinished drafts.");
    }

    [Test]
    public async Task InvalidOptionalBufferBlocksRunEvenWhenTypedInvocationIsReady()
    {
        PaletteInteractionTests.View view = new();
        ExecutionFixture.Launcher launcher = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        Edit(view, "count", "-");
        Assert.That(view.Presentation.Selection!.IsExecutionReady, Is.True);
        Assert.That(view.Presentation.CanRun, Is.False);
        Assert.That(view.Presentation.SelectionStatus, Does.Contain("whole number"));
        await controller.SubmitAsync(Run(view.Presentation));
        Assert.That(view.Presentation.AttemptId, Is.Null);
        Assert.That(launcher.Calls, Is.Empty);
        Edit(view, "count", "0");
        Assert.That(view.Presentation.CanRun, Is.True);
    }

    [Test]
    public void UnconfiguredExecutionRemainsUnavailableAfterValidEditing()
    {
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(configured: false));
        Select(controller, view); Edit(view, "text", "edited");
        Assert.That(view.Presentation.Selection!.IsExecutionReady, Is.True);
        Assert.That(view.Presentation.CanRun, Is.False);
    }

    [Test]
    public void NavigationDismissalAndReselectionRetainIndependentMeaningfulDraftsOnly()
    {
        PaletteInteractionTests.View view = new();
        InvocationDraftStore drafts = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(), drafts: drafts);
        Select(controller, view); view.Key("Escape");
        Assert.That(drafts.Drafts, Is.Empty);
        view.Key("D"); view.Key("N");
        Guid first = view.Presentation.Selection!.Id;
        Edit(view, "count", "-"); view.Key("Escape");
        Assert.That(drafts.Drafts.Single().Editing.Invocation.Id, Is.EqualTo(first));
        Assert.That(drafts.Drafts.Single().Editing.Parameters[2].RawText, Is.EqualTo("-"));
        view.Key("D"); view.Key("N");
        Guid second = view.Presentation.Selection!.Id;
        Edit(view, "text", "second invocation");
        view.Key("N");
        Assert.That(view.Presentation.Selection!.Id, Is.Not.EqualTo(second));
        Assert.That(drafts.Drafts.Length, Is.EqualTo(2));
        Edit(view, "text", "third invocation"); view.LoseFocus();
        Assert.That(drafts.Drafts.Length, Is.EqualTo(3));
        controller.Open();
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Root"));
        Assert.That(view.Presentation.Selection, Is.Null);
        Assert.That(view.Presentation.Editing, Is.Null);
    }

    [Test]
    public void StaleRevisionSelectionAndActivationEditsCannotMutateReplacementState()
    {
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store());
        Select(controller, view);
        PaletteArgumentEditEventArgs stale = Change(view.Presentation, "text", new ArgumentEdit.RawText("stale"));
        Edit(view, "text", "current"); view.Edit(stale);
        Assert.That(view.Presentation.Editing!.Parameters[0].RawText, Is.EqualTo("current"));
        view.Key("N"); view.Edit(stale);
        Assert.That(view.Presentation.Editing!.Parameters[0].IsSupplied, Is.False);
        PaletteArgumentEditEventArgs oldActivation = Change(view.Presentation, "text", new ArgumentEdit.RawText("old"));
        controller.Dismiss(); Select(controller, view); view.Edit(oldActivation);
        Assert.That(view.Presentation.Editing!.Parameters[0].IsSupplied, Is.False);
    }

    [Test]
    public async Task SubmittedVersionFreezesAndKnownFailureAllowsCorrectionWithIdentifiedRetry()
    {
        TaskCompletionSource<ProcessLaunchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionFixture.Launcher launcher = new() { Start = _ => completion.Task };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(presetPath: false), ExecutionFixture.Coordinator(launcher));
        Select(controller, view);
        Edit(view, "executable", @"C:\First\app.exe");
        Guid invocationId = view.Presentation.Selection!.Id;
        Task pending = controller.SubmitAsync(Run(view.Presentation));
        Assert.That(view.Presentation.Editing!.IsReadOnly, Is.True);
        Edit(view, "executable", @"C:\Blocked\app.exe");
        Assert.That(view.Presentation.Editing.Parameters[^1].RawText, Is.EqualTo(@"C:\First\app.exe"));
        completion.SetResult(ExecutionFixture.KnownFailure);
        await pending;
        Guid? firstAttempt = view.Presentation.AttemptId;
        Assert.That(view.Presentation.Editing!.IsReadOnly, Is.False);
        Edit(view, "executable", @"C:\Corrected\app.exe");
        await controller.SubmitAsync(new(view.Presentation.ActivationId, invocationId));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1), "Correction does not turn a stale Run into Retry.");
        launcher.Start = _ => Task.FromResult(ExecutionFixture.Success);
        await controller.SubmitAsync(Run(view.Presentation));
        Assert.That(launcher.Calls.Last().FileName, Is.EqualTo(@"C:\Corrected\app.exe"));
        Assert.That(view.Presentation.Selection!.Id, Is.EqualTo(invocationId));
        Assert.That(view.Presentation.AttemptId, Is.Not.EqualTo(firstAttempt));
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
        controller.Dismiss();
        Assert.That(controller.DraftStore.Drafts, Is.Empty);
    }

    [Test]
    public async Task UncertainResultFreezesEditingAndDoesNotCreateResumableWork()
    {
        ExecutionFixture.Launcher launcher = new() { Start = _ => Task.FromResult(ExecutionFixture.Uncertain) };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view); Edit(view, "text", "work");
        await controller.SubmitAsync(Run(view.Presentation));
        Edit(view, "text", "blocked");
        Assert.That(view.Presentation.Editing!.Parameters[0].RawText, Is.EqualTo("work"));
        Assert.That(view.Presentation.CanRun, Is.False);
        controller.Dismiss();
        Assert.That(controller.DraftStore.Drafts, Is.Empty);
    }

    [Test]
    public void PublishingConfigurationCannotChangeEditorInterpretation()
    {
        PaletteInteractionTests.View view = new();
        CommandConfigurationStore store = EditingFixture.Store();
        using CommandPaletteController controller = new(view, store);
        Select(controller, view);
        CommandCatalogSnapshot catalog = view.Presentation.Selection!.Catalog;
        Assert.That(store.Publish(new("root", [new("root", "Replacement Root", [])]), []).IsValid, Is.True);
        Edit(view, "text", "old contract still applies");
        Assert.That(view.Presentation.Selection!.Catalog, Is.SameAs(catalog));
        Assert.That(view.Presentation.Editing!.Parameters.Length, Is.EqualTo(4));
        controller.Dismiss(); controller.Open();
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Replacement Root"));
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task ADelayedKnownFailureRetainsMeaningfulWorkWithoutRestoringItsOldUi(bool reopen)
    {
        TaskCompletionSource<ProcessLaunchResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ExecutionFixture.Launcher launcher = new() { Start = _ => completion.Task };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view); Edit(view, "text", "unfinished non-start work");
        Guid invocation = view.Presentation.Selection!.Id;
        Task pending = controller.SubmitAsync(Run(view.Presentation));
        controller.Dismiss();
        Assert.That(controller.DraftStore.Drafts, Is.Empty, "Accepted pending work is not yet a draft.");
        if (reopen) { controller.Open(); }
        PalettePresentation current = view.Presentation;
        completion.SetResult(ExecutionFixture.KnownFailure);
        await pending;
        Assert.That(view.Presentation, Is.SameAs(current));
        InvocationDraft draft = controller.DraftStore.Drafts.Single();
        Assert.That(draft.Editing.Invocation.Id, Is.EqualTo(invocation));
        Assert.That(draft.Editing.Parameters[0].RawText, Is.EqualTo("unfinished non-start work"));
        Assert.That(draft.LastExecution!.CanRetry, Is.True);
        Assert.That(controller.State, Is.EqualTo(reopen ? PaletteState.Visible : PaletteState.Hidden));
    }

}