using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Execution;
using Disorderly.Routing;
using Disorderly.Services;
using Disorderly.Testing.Execution;
using NUnit.Framework;

namespace Disorderly.Testing.Commands.Editing;

[TestFixture]
public class PaletteDraftTests
{
    private static void Select(CommandPaletteController controller, PaletteInteractionTests.View view)
    {
        controller.Open(); view.Key("D"); view.Key("N");
    }

    private static void Edit(PaletteInteractionTests.View view, string parameter, ArgumentEdit change)
    {
        PalettePresentation p = view.Presentation;
        view.Edit(new(p.ActivationId, p.Selection!.Id, p.Editing!.SessionId, p.Editing.Revision, parameter, change));
    }

    private static PaletteDraftEventArgs Action(PalettePresentation p, PaletteDraftAction action, PaletteDraftLabel draft) => new(p.ActivationId, action, draft.Id, draft.Generation);
    private static PaletteSubmissionEventArgs Run(PalettePresentation p) => new(p.ActivationId, p.Selection!.Id, p.AttemptId, p.Editing!.Revision);

    [Test]
    public void RootSuggestionIsPrivateAndExplicitResumeRestoresInvalidInputPositionAndIdentity()
    {
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store());
        Select(controller, view);
        Edit(view, "text", new ArgumentEdit.RawText("private secret"));
        Edit(view, "count", new ArgumentEdit.RawText("-"));
        Edit(view, "count", new ArgumentEdit.Caret(1, 0));
        PalettePresentation previous = view.Presentation;
        Guid editor = previous.Editing!.SessionId;
        controller.Dismiss(); controller.Open();
        Assert.That(view.Presentation.IsRoot, Is.True);
        Assert.That(view.Presentation.Selection, Is.Null);
        PaletteDraftLabel suggestion = view.Presentation.ResumeSuggestion!;
        Assert.That(suggestion.Name, Is.EqualTo("Editable command"));
        Assert.That(suggestion.ToString(), Does.Not.Contain("private secret"));
        PaletteDraftEventArgs action = Action(view.Presentation, PaletteDraftAction.Resume, suggestion);
        view.Key("Tab");
        Assert.That(view.Presentation.Selection!.Id, Is.EqualTo(previous.Selection!.Id));
        Assert.That(view.Presentation.Selection.Catalog, Is.SameAs(previous.Selection.Catalog));
        Assert.That(view.Presentation.Editing!.SessionId, Is.Not.EqualTo(editor));
        Assert.That(view.Presentation.Editing.Position, Is.EqualTo(new ArgumentEditPosition("count", 1, 0)));
        Assert.That(view.Presentation.Editing.Parameters[2].RawText, Is.EqualTo("-"));
        Assert.That(view.Presentation.CanRun, Is.False);
        Assert.That(view.Presentation.AttemptId, Is.Null);
        Assert.That(view.Presentation.Breadcrumb, Is.EqualTo("Root / Child"));
        Assert.That(controller.DraftStore.Drafts, Is.Empty);
        controller.HandleDraftAction(action);
        Assert.That(view.Presentation.Selection.Id, Is.EqualTo(previous.Selection.Id));
        controller.Dismiss(); controller.Open();
        Assert.That(view.Presentation.ResumeSuggestion!.Id, Is.EqualTo(suggestion.Id));
        Assert.That(view.Presentation.ResumeSuggestion.Generation, Is.GreaterThan(suggestion.Generation));
    }

    [Test]
    public void BrowserIsNotAContextAndCanRestoreOlderOrDiscardIdentifiedDraft()
    {
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store());
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("first"));
        controller.Dismiss(); controller.Open();
        PaletteDraftLabel older = view.Presentation.ResumeSuggestion!;
        view.Key("D"); view.Key("N"); Edit(view, "text", new ArgumentEdit.RawText("second"));
        controller.Dismiss(); controller.Open();
        PaletteDraftLabel newer = view.Presentation.ResumeSuggestion!;
        Assert.That(newer.Id, Is.Not.EqualTo(older.Id));
        controller.HandleDraftAction(new(view.Presentation.ActivationId, PaletteDraftAction.Browse));
        Assert.That(view.Presentation.BrowsingDrafts, Is.True);
        Assert.That(view.Presentation.Drafts.Select(draft => draft.Id), Is.EqualTo(new[] { newer.Id, older.Id }));
        Assert.That(view.Presentation.Drafts.Select(draft => draft.Name).Distinct().Count(), Is.EqualTo(1));
        view.Key("Tab");
        Assert.That(view.Presentation.BrowsingDrafts, Is.True);
        view.Key("Escape");
        Assert.That(view.Presentation.BrowsingDrafts, Is.False);
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
        Assert.That(view.Presentation.ResumeSuggestion!.Generation, Is.EqualTo(newer.Generation));
        controller.HandleDraftAction(new(view.Presentation.ActivationId, PaletteDraftAction.Browse));
        controller.HandleDraftAction(Action(view.Presentation, PaletteDraftAction.Discard, newer));
        Assert.That(view.Presentation.Drafts.Single().Id, Is.EqualTo(older.Id));
        controller.HandleDraftAction(Action(view.Presentation, PaletteDraftAction.Resume, older));
        Assert.That(view.Presentation.Editing!.Parameters[0].RawText, Is.EqualTo("first"));
        view.Key("Escape");
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Root"));
        Assert.That(controller.DraftStore.Drafts.Single().Id, Is.EqualTo(older.Id));
        view.Key("Escape");
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden));
    }

    [Test]
    public void StaleActivationGenerationAndEditorEventsCannotAffectRestoration()
    {
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store());
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("keep"));
        PalettePresentation old = view.Presentation;
        controller.Dismiss(); controller.Open();
        PaletteDraftLabel draft = view.Presentation.ResumeSuggestion!;
        controller.HandleDraftAction(new(old.ActivationId, PaletteDraftAction.Resume, draft.Id, draft.Generation));
        Assert.That(view.Presentation.Selection, Is.Null);
        controller.HandleDraftAction(new(view.Presentation.ActivationId, PaletteDraftAction.Resume, draft.Id, draft.Generation - 1));
        Assert.That(view.Presentation.Selection, Is.Null);
        view.Key("Tab");
        view.Edit(new(view.Presentation.ActivationId, old.Selection!.Id, old.Editing!.SessionId, old.Editing.Revision, "text", new ArgumentEdit.RawText("stale")));
        Assert.That(view.Presentation.Editing!.Parameters[0].RawText, Is.EqualTo("keep"));
        controller.Dismiss(); controller.Open();
        controller.HandleDraftAction(new(view.Presentation.ActivationId, PaletteDraftAction.Browse));
        controller.HandleDraftAction(Action(view.Presentation, PaletteDraftAction.Discard, draft));
        Assert.That(controller.DraftStore.Drafts.Length, Is.EqualTo(1));
    }

    [Test]
    public void IncompatibleConfigurationPreservesInputAndDoesNotRollBackPublication()
    {
        CommandConfigurationStore store = EditingFixture.Store();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, store);
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("keep")); controller.Dismiss();
        store.Publish(new("root", [new("root", "New Root", [])]), []);
        CommandConfigurationSnapshot latest = store.Current!;
        controller.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Selection, Is.Null);
        Assert.That(view.Presentation.DraftMessage, Does.Contain("changed"));
        Assert.That(controller.DraftStore.Drafts.Single().Editing.Parameters[0].RawText, Is.EqualTo("keep"));
        Assert.That(store.Current, Is.SameAs(latest));
    }

    [Test]
    public void EquivalentRepublishedConfigurationCanRestoreWithoutChangingRetainedCatalog()
    {
        CommandConfigurationStore store = EditingFixture.Store();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, store);
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("keep"));
        CommandCatalogSnapshot original = view.Presentation.Selection!.Catalog;
        controller.Dismiss();
        CommandConfigurationSnapshot copy = EditingFixture.Store().Current!;
        Assert.That(store.Publish(new(copy.Routing.RootContextId, copy.Routing.Contexts), copy.Catalog.Definitions).IsValid, Is.True);
        controller.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Selection!.Catalog, Is.SameAs(original));
        Assert.That(store.Current!.Catalog, Is.Not.SameAs(original));
    }

    [Test]
    public void ExplicitRootTabBindingWinsAndModifiersDoNotResume()
    {
        CommandConfigurationStore store = EditingFixture.Store();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, store);
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("keep")); controller.Dismiss();
        CommandConfigurationSnapshot snapshot = store.Current!;
        ContextDefinition root = snapshot.Routing.GetContext("root");
        Assert.That(store.Publish(new("root", [root with { Bindings = [.. root.Bindings, new("tab", "root", new("Tab"), new BindingDestination.Context("child"))] }, snapshot.Routing.GetContext("child")]), snapshot.Catalog.Definitions).IsValid, Is.True);
        controller.Open();
        view.Send(view.Presentation.ActivationId, new("Tab", GestureModifiers.Shift));
        Assert.That(view.Presentation.Selection, Is.Null);
        view.Key("Tab");
        Assert.That(view.Presentation.ContextName, Is.EqualTo("Child"));
        Assert.That(view.Presentation.Selection, Is.Null);
        Assert.That(controller.DraftStore.Drafts.Length, Is.EqualTo(1));
    }

    [TestCase("dev")]
    [TestCase("work")]
    public void SharedContextRestoresActualEntryPath(string branch)
    {
        CommandConfigurationStore store = PaletteConfiguration.CreateStore();
        store.Publish(new("root", [
            new("root", "Root", [new("d", "root", new("D"), new BindingDestination.Context("dev")), new("w", "root", new("W"), new BindingDestination.Context("work"))]),
            new("dev", "Development", [new("ds", "dev", new("S"), new BindingDestination.Context("shared"))]),
            new("work", "Work", [new("ws", "work", new("S"), new BindingDestination.Context("shared"))]),
            new("shared", "Shared", [new("c", "shared", new("C"), new BindingDestination.Command("command"))]),
        ]), [new("command", "Command", [new("text", "Text", ParameterValueType.Text)])]);
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, store);
        controller.Open(); view.Key(branch == "dev" ? "D" : "W"); view.Key("S"); view.Key("C");
        Edit(view, "text", new ArgumentEdit.RawText("value")); controller.Dismiss(); controller.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Breadcrumb, Is.EqualTo(branch == "dev" ? "Root / Development / Shared" : "Root / Work / Shared"));
        view.Key("Escape");
        Assert.That(view.Presentation.ContextName, Is.EqualTo(branch == "dev" ? "Development" : "Work"));
    }

    [Test]
    public void InvalidSavedPathIsRejectedWithoutTakingDraftOrMutatingRouting()
    {
        CommandConfigurationStore store = EditingFixture.Store();
        CommandInteraction interaction = store.CreateInteraction();
        interaction.Routing.Route(new("D"));
        ArgumentEditorSession editor = new(interaction.Prepare((RoutingResult.CommandRequested)interaction.Routing.Route(new("N"))).Invocation!);
        EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText("keep"));
        InvocationDraftStore drafts = new();
        drafts.Retain(editor.Snapshot, configuration: interaction.Snapshot, navigationPath: ["root", "missing"]);
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, store, drafts: drafts);
        controller.Open(); view.Key("Tab");
        Assert.That(view.Presentation.DraftMessage, Does.Contain("invalid"));
        Assert.That(drafts.Drafts.Length, Is.EqualTo(1));
        RoutingSession routing = store.CreateInteraction().Routing;
        Assert.That(routing.TryRestorePath(["root", "missing"]), Is.False);
        Assert.That(routing.NavigationPath, Is.EqualTo(new[] { "root" }));
    }

    [Test]
    public async Task RestoredKnownFailureRetriesExplicitlyAndSuccessRemovesOnlyThatInvocation()
    {
        ExecutionFixture.Launcher launcher = new() { Start = _ => Task.FromResult(ExecutionFixture.KnownFailure) };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("older")); controller.Dismiss();
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("failure"));
        await controller.SubmitAsync(Run(view.Presentation));
        Guid failedAttempt = view.Presentation.AttemptId!.Value;
        Guid invocation = view.Presentation.Selection!.Id;
        controller.Dismiss(); controller.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Selection!.Id, Is.EqualTo(invocation));
        Assert.That(view.Presentation.AttemptId, Is.EqualTo(failedAttempt));
        Assert.That(view.Presentation.RunLabel, Is.EqualTo("Retry"));
        Assert.That(launcher.Calls.Count, Is.EqualTo(1), "Restoration itself never launches.");
        Edit(view, "text", new ArgumentEdit.RawText("corrected"));
        launcher.Start = _ => Task.FromResult(ExecutionFixture.Success);
        await controller.SubmitAsync(Run(view.Presentation));
        controller.Dismiss(); controller.Open();
        Assert.That(controller.DraftStore.Drafts.Single().Editing.Parameters[0].RawText, Is.EqualTo("older"));
        Assert.That(controller.DraftStore.Drafts.Any(draft => draft.Editing.Invocation.Id == invocation), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingRestoredAttemptIsNotResumableAndLateCompletionDoesNotAlterNewActivation(bool uncertain)
    {
        TaskCompletionSource<ProcessLaunchResult> completion = new();
        ExecutionFixture.Launcher launcher = new() { Start = _ => completion.Task };
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, EditingFixture.Store(), ExecutionFixture.Coordinator(launcher));
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("work")); controller.Dismiss(); controller.Open(); view.Key("Tab");
        Task pending = controller.SubmitAsync(Run(view.Presentation));
        controller.Dismiss(); controller.Open();
        PalettePresentation current = view.Presentation;
        Assert.That(controller.DraftStore.Drafts, Is.Empty);
        completion.SetResult(uncertain ? ExecutionFixture.Uncertain : ExecutionFixture.Success);
        await pending;
        Assert.That(view.Presentation, Is.SameAs(current));
        Assert.That(controller.DraftStore.Drafts, Is.Empty);
        Assert.That(launcher.Calls.Count, Is.EqualTo(1));
    }
    [TestCase(false)]
    [TestCase(true)]
    public void RelevantPresetOrParameterChangesBlockRestoreWithoutErasingInput(bool changeParameter)
    {
        CommandConfigurationStore store = EditingFixture.Store();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, store);
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("saved")); controller.Dismiss();
        CommandConfigurationSnapshot snapshot = store.Current!;
        CommandDefinition command = snapshot.Catalog.Definitions[0];
        ContextDefinition child = snapshot.Routing.GetContext("child");
        if (changeParameter)
        {
            command = command with { Parameters = [command.Parameters[0] with { ValueType = ParameterValueType.Integer }, .. command.Parameters.Skip(1)] };
        }
        else
        {
            BindingDefinition binding = child.Bindings[0];
            child = child with { Bindings = [binding with { Destination = new BindingDestination.Command("edit") { Metadata = new(new ArgumentSet([new("executable", new ArgumentValue.Path(@"C:\Changed\app.exe"))])) } }] };
        }

        Assert.That(store.Publish(new("root", [snapshot.Routing.GetContext("root"), child]), [command]).IsValid, Is.True);
        controller.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Selection, Is.Null);
        Assert.That(view.Presentation.DraftMessage, Does.Contain("changed"));
        Assert.That(controller.DraftStore.Drafts.Single().Editing.Parameters[0].RawText, Is.EqualTo("saved"));
    }

    [Test]
    public void UnrelatedBindingsAndRenamesDoNotBlockCompatibleDraft()
    {
        CommandConfigurationStore store = EditingFixture.Store();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, store);
        Select(controller, view); Edit(view, "text", new ArgumentEdit.RawText("saved")); controller.Dismiss();
        CommandConfigurationSnapshot snapshot = store.Current!;
        ContextDefinition root = snapshot.Routing.GetContext("root");
        root = root with { DisplayName = "Renamed Root", Bindings = [.. root.Bindings, new("another", "root", new("X"), new BindingDestination.Context("child"))] };
        Assert.That(store.Publish(new("root", [root, snapshot.Routing.GetContext("child")]),
            [snapshot.Catalog.Definitions[0] with { DisplayName = "Renamed command" }]).IsValid, Is.True);
        controller.Open(); view.Key("Tab");
        Assert.That(view.Presentation.Selection!.Command.DisplayName, Is.EqualTo("Editable command"));
        Assert.That(view.Presentation.Editing!.Parameters[0].RawText, Is.EqualTo("saved"));
    }

    [Test]
    public void NoDraftKeepsTabUnassignedAndBrowserCanReturnFromEmptyList()
    {
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view);
        controller.Open();
        Assert.That(view.Presentation.ResumeSuggestion, Is.Null);
        view.Key("Tab");
        Assert.That(view.Presentation.Selection, Is.Null);
        controller.HandleDraftAction(new(view.Presentation.ActivationId, PaletteDraftAction.Browse));
        Assert.That(view.Presentation.BrowsingDrafts, Is.True);
        Assert.That(view.Presentation.Drafts, Is.Empty);
        view.Key("Escape");
        Assert.That(view.Presentation.BrowsingDrafts, Is.False);
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
    }
}