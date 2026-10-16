using System.Collections.Immutable;
using Disorderly.Commands;
using Disorderly.Execution;
using Disorderly.Routing;

namespace Disorderly.Services;

public sealed record PaletteBindingLabel(string Gesture, string Name, string Kind)
{
    public string AccessibleName => $"{this.Gesture}: {this.Name}, {this.Kind}";
}

public sealed record PalettePresentation(Guid ActivationId, string ContextName, string Breadcrumb,
    ImmutableArray<PaletteBindingLabel> Bindings, bool IsRoot, CommandInvocation? Selection, bool ExecutionAvailable = false,
    bool Submitting = false, ExecutionResult? Execution = null, Guid? AttemptId = null)
{
    public string NavigationHint => this.IsRoot ? "Esc  Dismiss" : "Esc  Back";
    public bool CanRun => this.Selection?.IsExecutionReady == true && this.ExecutionAvailable && !this.Submitting
        && (this.AttemptId is null || this.Execution?.CanRetry == true);
    public string RunLabel => this.Execution?.CanRetry == true ? "Retry" : "Run";
    public string SelectionStatus => this.Submitting ? "Launching..." : this.Execution?.Message ?? (this.Selection is null ? string.Empty
        : $"{(this.Selection.IsExecutionReady ? "Arguments complete." : string.Join("; ", this.Selection.Validation.Diagnostics.Select(diagnostic => diagnostic.Message)))} "
            + (this.ExecutionAvailable ? (this.Selection.IsExecutionReady ? "Executable implementation available. Select Run to launch." : "Executable implementation available. Complete the required arguments before Run.")
                : "Execution is not yet available. No executable implementation is configured or arguments are incomplete."));
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
public sealed class PaletteSubmissionEventArgs(Guid activationId, Guid invocationId, Guid? previousAttemptId = null) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
    public Guid InvocationId { get; } = invocationId;
    public Guid? PreviousAttemptId { get; } = previousAttemptId;
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