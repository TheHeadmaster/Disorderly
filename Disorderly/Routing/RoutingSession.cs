using System.Collections.Immutable;

namespace Disorderly.Routing;

public abstract record RoutingResult
{
    private protected RoutingResult() { }

    public sealed record NoMatch : RoutingResult;
    public sealed record ContextEntered(string ContextId, string BindingId) : RoutingResult;
    public sealed record ContextReturned(string ContextId) : RoutingResult;
    public sealed record DismissRequested : RoutingResult;
    public sealed record CommandRequested(string CommandId, string BindingId, string ContextId) : RoutingResult;
}

/// <summary>A single interaction's navigation state. Use on one interaction thread; no UI side effects.</summary>
public sealed class RoutingSession
{
    private readonly List<string> path;

    public RoutingSession(RoutingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        this.Snapshot = snapshot;
        this.path = [snapshot.RootContextId];
    }

    public RoutingSnapshot Snapshot { get; }
    public ContextDefinition ActiveContext => this.Snapshot.GetContext(this.path[^1]);
    public ImmutableArray<string> NavigationPath => this.path.ToImmutableArray();
    public IReadOnlyList<BindingDefinition> AvailableBindings => this.ActiveContext.Bindings;

    public RoutingResult Route(LogicalGesture gesture)
    {
        if (gesture == LogicalGesture.Escape)
        {
            return this.Back();
        }

        BindingDefinition? binding = this.AvailableBindings.FirstOrDefault(binding => binding.Gesture == gesture);
        if (binding is null)
        {
            return new RoutingResult.NoMatch();
        }

        if (binding.Destination is BindingDestination.Context context)
        {
            this.path.Add(context.ContextId);
            return new RoutingResult.ContextEntered(context.ContextId, binding.Id);
        }

        BindingDestination.Command command = (BindingDestination.Command)binding.Destination;
        return new RoutingResult.CommandRequested(command.CommandId, binding.Id, this.ActiveContext.Id);
    }

    public RoutingResult Back()
    {
        if (this.path.Count == 1)
        {
            return new RoutingResult.DismissRequested();
        }

        this.path.RemoveAt(this.path.Count - 1);
        return new RoutingResult.ContextReturned(this.path[^1]);
    }
}