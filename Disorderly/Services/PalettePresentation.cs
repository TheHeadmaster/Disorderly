using System.Collections.Immutable;
using Disorderly.Commands;
using Disorderly.Routing;

namespace Disorderly.Services;

public sealed record PaletteBindingLabel(string Gesture, string Name, string Kind)
{
    public string AccessibleName => $"{this.Gesture}: {this.Name}, {this.Kind}";
}

public sealed record PalettePresentation(Guid ActivationId, string ContextName, string Breadcrumb,
    ImmutableArray<PaletteBindingLabel> Bindings, bool IsRoot, CommandInvocation? Selection)
{
    public string NavigationHint => this.IsRoot ? "Esc  Dismiss" : "Esc  Back";
    public string SelectionStatus => this.Selection is null ? string.Empty : this.Selection.IsExecutionReady
        ? "Arguments complete. Execution is not yet available."
        : $"{string.Join("; ", this.Selection.Validation.Diagnostics.Select(diagnostic => diagnostic.Message))} Execution is not yet available.";
}

public sealed class PaletteGestureEventArgs(Guid activationId, LogicalGesture gesture) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
    public LogicalGesture Gesture { get; } = gesture;
}

public sealed class PaletteDismissEventArgs(Guid activationId) : EventArgs
{
    public Guid ActivationId { get; } = activationId;
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