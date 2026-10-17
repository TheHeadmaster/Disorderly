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

    /// <summary>Restore only a real path through this immutable graph, without partial mutation.</summary>
    public bool TryRestorePath(IReadOnlyList<string> navigationPath)
    {
        if (navigationPath.Count == 0 || navigationPath[0] != this.Snapshot.RootContextId)
        {
            return false;
        }

        for (int index = 1; index < navigationPath.Count; index++)
        {
            ContextDefinition previous = this.Snapshot.GetContext(navigationPath[index - 1]);
            if (!previous.Bindings.Any(binding => binding.Destination is BindingDestination.Context target && target.ContextId == navigationPath[index]))
            {
                return false;
            }
        }

        this.path.Clear();
        this.path.AddRange(navigationPath);
        return true;
    }

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