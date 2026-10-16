using Disorderly.Commands;

namespace Disorderly.Routing;

/// <summary>Caller-owned proposal data; do not mutate its collections during validation.</summary>
public sealed record RoutingConfiguration(string RootContextId, IReadOnlyList<ContextDefinition> Contexts);

public sealed record ContextDefinition(string Id, string DisplayName, IReadOnlyList<BindingDefinition> Bindings);

public sealed record BindingDefinition(string Id, string ContextId, LogicalGesture Gesture, BindingDestination Destination);

/// <summary>Exactly one typed destination. Future presets belong to binding metadata, not duplicated commands.</summary>
public abstract record BindingDestination
{
    private protected BindingDestination() { }

    public sealed record Context(string ContextId) : BindingDestination;
    public sealed record Command(string CommandId) : BindingDestination
    {
        public CommandBindingMetadata? Metadata { get; init; }
    }
}