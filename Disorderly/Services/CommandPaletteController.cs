using System.Collections.Immutable;
using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Execution;
using Disorderly.Routing;
using Serilog;

namespace Disorderly.Services;

public enum PaletteState
{
    Hidden,
    Opening,
    Visible,
    Dismissing,
}

public interface ICommandPaletteView : IDisposable
{
    event EventHandler<PaletteDismissEventArgs>? DismissRequested;
    event EventHandler<PaletteGestureEventArgs>? GestureReceived;
    event EventHandler<PaletteSubmissionEventArgs>? SubmissionRequested;
    event EventHandler<PaletteArgumentEditEventArgs>? ArgumentEdited;
    void Render(PalettePresentation presentation);
    bool ShowAtForegroundMonitor();
    void Hide();
}

/// <summary>Owns visibility and one transient, snapshot-isolated interaction per activation.</summary>
public sealed class CommandPaletteController : IDisposable
{
    private readonly ICommandPaletteView view;
    private readonly CommandConfigurationStore configurations;
    private readonly SubmissionCoordinator submissions;
    private ExecutionReadyRequest? attempt;
    private ExecutionResult? execution;
    private bool submitting;
    private CommandInteraction? interaction;
    private CommandInvocation? selection;
    private ArgumentEditorSession? editor;
    private CommandConfigurationSnapshot? selectionConfiguration;
    private ImmutableArray<string> selectionPath;
    private bool browsingDrafts;
    private string? draftMessage;
    private PaletteDraftLabel? resumeSuggestion;
    private bool restoreEditorFocus;
    private Guid activationId;
    private bool disposed;

    public CommandPaletteController(ICommandPaletteView view, CommandConfigurationStore? configurations = null, SubmissionCoordinator? submissions = null, IInvocationDraftStore? drafts = null)
    {
        this.view = view;
        this.configurations = configurations ?? PaletteConfiguration.CreateEmptyStore();
        this.submissions = submissions ?? SubmissionCoordinator.CreateDefault();
        this.DraftStore = drafts ?? new InvocationDraftStore();
        this.view.DismissRequested += this.OnDismissRequested;
        this.view.GestureReceived += this.OnGestureReceived;
        this.view.SubmissionRequested += this.OnSubmissionRequested;
        this.view.ArgumentEdited += this.OnArgumentEdited;
        if (view is IDraftPaletteView draftView)
        {
            draftView.DraftActionRequested += this.OnDraftActionRequested;
        }
    }

    public IInvocationDraftStore DraftStore { get; }

    public PaletteState State { get; private set; } = PaletteState.Hidden;

    public void Toggle()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.State == PaletteState.Visible)
        {
            this.Dismiss();
        }
        else if (this.State == PaletteState.Hidden)
        {
            this.Open();
        }
    }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.State != PaletteState.Hidden)
        {
            return;
        }

        this.State = PaletteState.Opening;
        try
        {
            this.interaction = this.configurations.CreateInteraction();
            // Also guard programmer-injected stores that omitted the palette reservation validator.
            if (this.interaction.Snapshot.Routing.Contexts.SelectMany(context => context.Bindings)
                .Any(binding => binding.Gesture == PaletteConfiguration.ActivationGesture))
            {
                throw new InvalidOperationException("Palette configurations must reserve Ctrl+Shift+D. Use PaletteConfiguration.CreateStore().");
            }

            this.activationId = Guid.NewGuid();
            this.browsingDrafts = false;
            this.draftMessage = null;
            this.ClearSelection();
            this.Render();
            if (this.State != PaletteState.Opening)
            {
                return;
            }

            bool focused = this.view.ShowAtForegroundMonitor();
            if (this.State == PaletteState.Opening)
            {
                this.State = PaletteState.Visible;
                if (!focused)
                {
                    this.Dismiss();
                }
            }
        }
        catch
        {
            this.Dismiss();
            throw;
        }
    }

    public void Dismiss()
    {
        if (this.State is not (PaletteState.Visible or PaletteState.Opening))
        {
            return;
        }

        this.State = PaletteState.Dismissing;
        this.ClearSelection();
        this.interaction = null;
        this.activationId = Guid.Empty;
        try
        {
            this.view.Hide();
        }
        finally
        {
            this.State = PaletteState.Hidden;
        }
    }

    private void OnGestureReceived(object? sender, PaletteGestureEventArgs args)
    {
        if (this.State != PaletteState.Visible || args.ActivationId != this.activationId || this.interaction is null)
        {
            return;
        }

        if (this.browsingDrafts)
        {
            if (args.Gesture == LogicalGesture.Escape)
            {
                this.HandleDraftAction(new(this.activationId, PaletteDraftAction.Back));
            }

            args.Handled = args.Gesture == LogicalGesture.Escape;
            return;
        }

        if (args.Gesture == new LogicalGesture("Tab") && this.interaction.Routing.NavigationPath.Length == 1
            && this.selection is null && !this.interaction.Routing.AvailableBindings.Any(binding => binding.Gesture == args.Gesture)
            && this.resumeSuggestion is PaletteDraftLabel suggested)
        {
            this.HandleDraftAction(new(this.activationId, PaletteDraftAction.Resume, suggested.Id, suggested.Generation));
            return;
        }

        this.draftMessage = null;
        RoutingResult result = this.interaction.Routing.Route(args.Gesture);
        // Unassigned Tab may use ordinary WPF focus navigation; an assigned Tab still routes locally.
        args.Handled = result is not RoutingResult.NoMatch || args.Gesture.Key != "TAB";
        switch (result)
        {
            case RoutingResult.DismissRequested:
                this.Dismiss();
                return;
            case RoutingResult.ContextEntered:
            case RoutingResult.ContextReturned:
                this.ClearSelection();
                this.Render();
                break;
            case RoutingResult.CommandRequested request:
                this.ClearSelection();
                this.selection = this.interaction.Prepare(request).Invocation;
                this.editor = this.selection is null ? null : new ArgumentEditorSession(this.selection);
                this.selectionConfiguration = this.interaction.Snapshot;
                this.selectionPath = this.interaction.Routing.NavigationPath;
                this.Render();
                break;
        }
    }

    private void Render()
    {
        CommandInteraction current = this.interaction!;
        RoutingSession routing = current.Routing;
        ImmutableArray<PaletteDraftLabel> drafts = [.. this.DraftStore.Drafts.Select(Label)];
        this.resumeSuggestion = routing.NavigationPath.Length == 1 && this.selection is null && !this.browsingDrafts ? drafts.FirstOrDefault() : null;
        this.view.Render(new PalettePresentation(this.activationId, routing.ActiveContext.DisplayName,
            string.Join(" / ", routing.NavigationPath.Select(id => current.Snapshot.Routing.GetContext(id).DisplayName)),
            [.. routing.AvailableBindings.Select(binding => binding.Destination switch
            {
                BindingDestination.Context context => new PaletteBindingLabel(binding.Gesture.ToString(), current.Snapshot.Routing.GetContext(context.ContextId).DisplayName, "Context"),
                BindingDestination.Command command => new PaletteBindingLabel(binding.Gesture.ToString(), current.Snapshot.Catalog.Find(command.CommandId)!.DisplayName, "Command"),
                _ => throw new InvalidOperationException("Validated destination required."),
            })], routing.NavigationPath.Length == 1, this.selection,
            this.selection is not null && this.submissions.HasImplementation(this.selection),
            this.submitting, this.execution, this.attempt?.AttemptId, this.editor?.Snapshot,
            this.browsingDrafts, drafts, this.resumeSuggestion,
            this.draftMessage, this.restoreEditorFocus));
        this.restoreEditorFocus = false;
    }

    private void ClearSelection()
    {
        if (this.editor is not null)
        {
            this.DraftStore.Retain(this.editor.Snapshot, this.attempt, this.execution, this.selectionConfiguration, this.selectionPath);
        }

        this.selectionConfiguration = null;
        this.selectionPath = default;
        this.editor = null;
        this.selection = null;
        this.attempt = null;
        this.execution = null;
        this.submitting = false;
    }

    private static PaletteDraftLabel Label(InvocationDraft draft)
    {
        ArgumentEditingSnapshot editing = draft.Editing;
        int supplied = editing.Parameters.Count(parameter => parameter.IsSupplied);
        int missing = editing.Invocation.Command.Parameters.Count(parameter => parameter.Required
            && !editing.Invocation.Arguments.Assignments.Any(assignment => assignment.ParameterId == parameter.Id));
        string status = draft.LastExecution?.CanRetry == true ? "Previous launch did not start; explicit Retry available."
            : editing.HasInvalidInput ? "Unfinished input needs correction."
            : $"{supplied} parameters supplied; {missing} required values missing.";
        string path = draft.Configuration is null || draft.NavigationPath.IsDefaultOrEmpty ? "Original context unavailable"
            : string.Join(" / ", draft.NavigationPath.Select(id => draft.Configuration.Routing.Contexts.FirstOrDefault(context => context.Id == id)?.DisplayName ?? "Unknown context"));
        return new(draft.Id, draft.Generation, editing.Invocation.Command.DisplayName, status, path);
    }

    private void OnDraftActionRequested(object? sender, PaletteDraftEventArgs args) => this.HandleDraftAction(args);

    /// <summary>Identified actions from the current view; no execution or attempt preparation occurs here.</summary>
    public void HandleDraftAction(PaletteDraftEventArgs args)
    {
        if (this.disposed || this.State != PaletteState.Visible || args.ActivationId != this.activationId || this.interaction is null)
        {
            return;
        }

        if (args.Action == PaletteDraftAction.Back && this.browsingDrafts)
        {
            this.browsingDrafts = false;
            this.draftMessage = null;
            this.Render();
            return;
        }

        if (this.interaction.Routing.NavigationPath.Length != 1 || this.selection is not null)
        {
            return;
        }

        if (args.Action == PaletteDraftAction.Browse)
        {
            this.browsingDrafts = true;
            this.draftMessage = null;
            this.Render();
            return;
        }

        InvocationDraft? draft = this.DraftStore.Find(args.DraftId);
        if (draft is null || draft.Generation != args.Generation)
        {
            return;
        }

        if (args.Action == PaletteDraftAction.Discard && this.browsingDrafts)
        {
            this.DraftStore.Discard(draft.Id, draft.Generation);
            this.Render();
            return;
        }

        if (args.Action != PaletteDraftAction.Resume)
        {
            return;
        }

        this.draftMessage = DraftRestoration.Check(draft, this.configurations.Current!);
        if (this.draftMessage is not null)
        {
            this.Render();
            return;
        }

        CommandInteraction restored = draft.Configuration!.CreateInteraction();
        if (!restored.Routing.TryRestorePath(draft.NavigationPath) || this.DraftStore.Take(draft.Id, draft.Generation) is null)
        {
            return;
        }

        this.interaction = restored;
        this.selectionConfiguration = draft.Configuration;
        this.selectionPath = draft.NavigationPath;
        this.editor = ArgumentEditorSession.Restore(draft.Editing);
        this.editor.SetReadOnly(draft.PreviousAttempt is not null && draft.LastExecution?.CanRetry != true);
        this.selection = this.editor.Snapshot.Invocation;
        this.attempt = draft.PreviousAttempt;
        this.execution = draft.LastExecution;
        this.browsingDrafts = false;
        this.restoreEditorFocus = true;
        this.Render();
    }

    private void OnArgumentEdited(object? sender, PaletteArgumentEditEventArgs args)
    {
        if (this.disposed || this.State != PaletteState.Visible || args.ActivationId != this.activationId
            || args.InvocationId != this.selection?.Id || this.editor is null || this.submitting
            || (this.attempt is not null && this.execution?.CanRetry != true))
        {
            return;
        }

        if (this.editor.TryApply(args.EditorId, args.Revision, args.ParameterId, args.Change))
        {
            this.selection = this.editor.Snapshot.Invocation;
            if (args.Change is not ArgumentEdit.Caret)
            {
                this.Render();
            }
        }
    }

    /// <summary>Called on the interaction thread. Accepted work outlives navigation/dismissal.</summary>
    public async Task SubmitAsync(PaletteSubmissionEventArgs args)
    {
        if (this.disposed || this.State != PaletteState.Visible || this.activationId != args.ActivationId
            || this.selection is null || this.selection.Id != args.InvocationId || this.submitting)
        {
            return;
        }

        if (this.attempt is null ? args.PreviousAttemptId is not null
            : this.execution?.CanRetry != true || args.PreviousAttemptId != this.attempt.AttemptId)
        {
            return;
        }

        CommandInvocation invocation = this.selection;
        if (this.editor is not null && (!this.editor.Snapshot.CanSubmit
            || (args.EditingRevision is long revision ? revision != this.editor.Snapshot.Revision : this.editor.Snapshot.HasMeaningfulChanges)))
        {
            return;
        }

        if (!this.submissions.Availability(invocation).Available)
        {
            return;
        }

        ExecutionReadyRequest? request = invocation.PrepareAttempt().Request;
        if (request is null)
        {
            return;
        }

        ArgumentEditingSnapshot? submittedEditing = this.editor?.Snapshot;
        CommandConfigurationSnapshot? submittedConfiguration = this.selectionConfiguration;
        ImmutableArray<string> submittedPath = this.selectionPath;
        this.attempt = request;
        this.submitting = true;
        this.editor?.SetReadOnly(true);
        this.execution = null;
        this.Render();
        ExecutionResult result;
        try
        {
            result = await this.submissions.SubmitAsync(request);
        }
        catch (Exception error)
        {
            Log.Warning("Submission raised {ExceptionType} for {CommandId}, invocation {InvocationId}, attempt {AttemptId}.",
                error.GetType().Name, invocation.Command.Id, invocation.Id, request.AttemptId);
            result = ExecutionResult.For(request, ExecutionOutcome.Uncertain, "SubmissionFailure", "The launch outcome could not be established. Do not retry blindly.");
        }

        if (result.Outcome is ExecutionOutcome.Succeeded or ExecutionOutcome.Uncertain)
        {
            this.DraftStore.Complete(invocation.Id);
        }

        if (this.disposed || this.State != PaletteState.Visible || this.activationId != args.ActivationId
            || this.selection?.Id != invocation.Id || !ReferenceEquals(this.attempt, request))
        {
            // Preserve meaningful non-start failures independently, without restoring obsolete UI.
            if (submittedEditing is not null)
            {
                this.DraftStore.Retain(submittedEditing, request, result, submittedConfiguration, submittedPath);
            }

            return;
        }

        this.submitting = false;
        this.execution = result;
        this.editor?.SetReadOnly(!result.CanRetry);
        if (result.Outcome == ExecutionOutcome.Succeeded && invocation.CompletionPolicy == CompletionPolicy.Dismiss)
        {
            this.Dismiss();
        }
        else
        {
            // Stay Open preserves the current path. A completed preview cannot submit again.
            this.Render();
        }
    }

    private async void OnSubmissionRequested(object? sender, PaletteSubmissionEventArgs args)
    {
        try
        {
            await this.SubmitAsync(args);
        }
        catch (Exception error)
        {
            // Observe event-task/presentation failures without taking down the dispatcher.
            Log.Warning("Palette submission presentation raised {ExceptionType} for invocation {InvocationId}.", error.GetType().Name, args.InvocationId);
        }
    }

    private void OnDismissRequested(object? sender, PaletteDismissEventArgs args)
    {
        if (args.ActivationId == this.activationId)
        {
            this.Dismiss();
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Dismiss();
        this.view.DismissRequested -= this.OnDismissRequested;
        this.view.GestureReceived -= this.OnGestureReceived;
        this.view.SubmissionRequested -= this.OnSubmissionRequested;
        this.view.ArgumentEdited -= this.OnArgumentEdited;
        if (this.view is IDraftPaletteView draftView)
        {
            draftView.DraftActionRequested -= this.OnDraftActionRequested;
        }

        this.view.Dispose();
        this.disposed = true;
    }
}