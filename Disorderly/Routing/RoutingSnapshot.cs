using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Disorderly.Routing;

/// <summary>Immutable validated graph; both context and binding enumeration use ordinal order.</summary>
public sealed class RoutingSnapshot
{
    private readonly FrozenDictionary<string, ContextDefinition> contexts;

    internal RoutingSnapshot(string rootContextId, IEnumerable<ContextDefinition> contexts)
    {
        this.RootContextId = rootContextId;
        this.Contexts = contexts.OrderBy(static context => context.Id, StringComparer.Ordinal)
            .Select(static context => context with
            {
                Bindings = context.Bindings.OrderBy(static binding => binding.Gesture.ToString(), StringComparer.Ordinal)
                    .ThenBy(static binding => binding.Id, StringComparer.Ordinal).ToImmutableArray(),
            }).ToImmutableArray();
        this.contexts = this.Contexts.ToFrozenDictionary(static context => context.Id, StringComparer.Ordinal);
    }

    public string RootContextId { get; }
    public ImmutableArray<ContextDefinition> Contexts { get; }
    public ContextDefinition GetContext(string contextId) => this.contexts[contextId];
}