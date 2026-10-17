using System.Collections.Immutable;
using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Execution;
using Disorderly.Routing;

namespace Disorderly.Services;

public sealed record PaletteBindingLabel(string Gesture, string Name, string Kind)
{
    public string AccessibleName => $"{this.Gesture}: {this.Name}, {this.Kind}";
}

public sealed record PalettePresentation(Guid ActivationId, string ContextName, string Breadcrumb,
    ImmutableArray<PaletteBindingLabel> Bindings, bool IsRoot, CommandInvocation? Selection, bool ExecutionAvailable = false,
    bool Submitting = false, ExecutionResult? Execution = null, Guid? AttemptId = null, ArgumentEditingSnapshot? Editing = null, bool BrowsingDrafts = false,
    ImmutableArray<PaletteDraftLabel> Drafts = default, PaletteDraftLabel? ResumeSuggestion = null, string? DraftMessage = null, bool RestoreEditorFocus = false)
{
    public string NavigationHint => this.BrowsingDrafts ? "Esc  Root" : this.IsRoot ? "Esc  Dismiss" : "Esc  Back";
    public bool CanRun => this.Selection?.IsExecutionReady == true && this.ExecutionAvailable && !this.Submitting
        && (this.Editing is null || this.Editing.CanSubmit)
        && (this.AttemptId is null || this.Execution?.CanRetry == true);
    public string RunLabel => this.Execution?.CanRetry == true ? "Retry" : "Run";
    public string SelectionStatus
    {
        get
        {
            if (this.Submitting)
            {
                return "Launching...";
            }

            if (this.Selection is null)
            {
                return string.Empty;
            }

            string errors = string.Join("; ", (this.Editing?.Diagnostics ?? this.Selection.Validation.Diagnostics).Select(diagnostic => diagnostic.Message));
            if (this.Execution is not null)
            {
                return $"{this.Execution.Message} {errors}".TrimEnd();
            }

            return $"{(errors.Length == 0 ? "Arguments complete." : errors)} "
                + (this.ExecutionAvailable ? (errors.Length == 0 ? "Executable implementation available. Select Run to launch." : "Executable implementation available. Resolve argument diagnostics before Run.")
                    : "Execution is not yet available. No executable implementation is configured or arguments are incomplete.");
        }
    }
}

public sealed class PaletteGestureEventArgs(Guid activationId, LogicalGesture gesture) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
    public LogicalGesture Gesture { get; } = gesture;
    public bool Handled { get; set; } = true;
}

public sealed class PaletteDismissEventArgs(Guid activationId) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
}

/// <summary>Retry must name the previous failed attempt; stale Run/Retry events cannot create fresh attempts.</summary>
public sealed class PaletteSubmissionEventArgs(Guid activationId, Guid invocationId, Guid? previousAttemptId = null, long? editingRevision = null) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
    public Guid InvocationId { get; } = invocationId;
    public Guid? PreviousAttemptId { get; } = previousAttemptId;
    public long? EditingRevision { get; } = editingRevision;
}

public sealed class PaletteArgumentEditEventArgs(Guid activationId, Guid invocationId, Guid editorId, long revision,
    string parameterId, ArgumentEdit change) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
    public Guid InvocationId { get; } = invocationId;
    public Guid EditorId { get; } = editorId;
    public long Revision { get; } = revision;
    public string ParameterId { get; } = parameterId;
    public ArgumentEdit Change { get; } = change;
}

/// <summary>The palette configuration source reserves exactly the external activation gesture and Escape.</summary>
public static class PaletteConfiguration
{
    public static LogicalGesture ActivationGesture => new("D", GestureModifiers.Control | GestureModifiers.Shift);

    public static CommandConfigurationStore CreateStore() => new(new RoutingValidator([ActivationGesture]));

    public static CommandConfigurationStore CreateEmptyStore()
    {
        CommandConfigurationStore store = CreateStore();
        store.Publish(new RoutingConfiguration("root", [new ContextDefinition("root", "Root", [])]), []);
        return store;
    }
}

public sealed record PaletteDraftLabel(Guid Id, long Generation, string Name, string Status, string Path)
{
    public string AccessibleName => $"{this.Name}, unfinished, {this.Status}, draft {this.Id}";
}

public enum PaletteDraftAction { Browse, Back, Resume, Discard }

public sealed class PaletteDraftEventArgs(Guid activationId, PaletteDraftAction action, Guid draftId = default, long generation = 0) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
    public PaletteDraftAction Action { get; } = action;
    public Guid DraftId { get; } = draftId;
    public long Generation { get; } = generation;
}

public interface IDraftPaletteView
{
    event EventHandler<PaletteDraftEventArgs>? DraftActionRequested;
}
