using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Disorderly.Routing;

public sealed class RoutingValidator
{
    private readonly FrozenSet<LogicalGesture> reservedGestures;

    public RoutingValidator(IEnumerable<LogicalGesture>? additionalReservedGestures = null)
    {
        LogicalGesture[] reserved = (additionalReservedGestures ?? []).Append(LogicalGesture.Escape).ToArray();
        if (reserved.Any(static gesture => !gesture.IsValid))
        {
            throw new ArgumentException("Reserved gestures must be valid logical gestures.", nameof(additionalReservedGestures));
        }

        this.reservedGestures = reserved.ToFrozenSet();
    }

    public RoutingValidationResult Validate(RoutingConfiguration configuration)
    {
        List<RoutingDiagnostic> diagnostics = [];
        if (configuration?.Contexts is null)
        {
            diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidConfiguration, "A configuration and context collection are required."));
            return new RoutingValidationResult(diagnostics, null);
        }

        Dictionary<string, ContextDefinition> contexts = new(StringComparer.Ordinal);
        HashSet<string> bindingIds = new(StringComparer.Ordinal);
        foreach (ContextDefinition? proposed in configuration.Contexts.ToArray())
        {
            if (proposed is null || !IsIdentifier(proposed.Id))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidIdentifier, "A context needs a valid stable identifier.", proposed?.Id));
                continue;
            }

            if (string.IsNullOrWhiteSpace(proposed.DisplayName))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidDisplayName, "A context needs a display name.", proposed.Id));
            }

            if (proposed.Bindings is null)
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidConfiguration, "A context needs a binding collection (which may be empty).", proposed.Id));
            }

            ContextDefinition context = proposed with { Bindings = proposed.Bindings?.ToImmutableArray() ?? [] };
            if (!contexts.TryAdd(context.Id, context))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.DuplicateContext, "Context identifiers must be unique.", context.Id));
            }

            this.ValidateBindings(context, bindingIds, diagnostics);
        }

        if (!IsIdentifier(configuration.RootContextId))
        {
            diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidIdentifier, "Root needs a valid context identifier.", configuration.RootContextId));
        }

        if (configuration.RootContextId is null || !contexts.ContainsKey(configuration.RootContextId))
        {
            diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.MissingRoot, "The identified Root context does not exist.", configuration.RootContextId));
        }

        Dictionary<string, string[]> edges = contexts.ToDictionary(static entry => entry.Key,
            entry => entry.Value.Bindings.OfType<BindingDefinition>()
                .Select(static binding => binding.Destination).OfType<BindingDestination.Context>()
                .Where(destination => destination.ContextId is not null && contexts.ContainsKey(destination.ContextId))
                .Select(static destination => destination.ContextId).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (ContextDefinition context in contexts.Values)
        {
            foreach (BindingDefinition binding in context.Bindings.OfType<BindingDefinition>())
            {
                if (binding.Destination is BindingDestination.Context destination
                    && IsIdentifier(destination.ContextId) && !contexts.ContainsKey(destination.ContextId))
                {
                    diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.MissingContext, "The transition destination does not exist.", context.Id, binding.Id, binding.Gesture, destination.ContextId));
                }
            }
        }

        ValidateGraph(edges, contexts, configuration.RootContextId, diagnostics);
        RoutingSnapshot? snapshot = diagnostics.Any(static diagnostic => diagnostic.Severity == RoutingDiagnosticSeverity.Error)
            ? null : new RoutingSnapshot(configuration.RootContextId!, contexts.Values);
        return new RoutingValidationResult(diagnostics, snapshot);
    }

    private void ValidateBindings(ContextDefinition context, HashSet<string> bindingIds, List<RoutingDiagnostic> diagnostics)
    {
        HashSet<LogicalGesture> gestures = [];
        foreach (BindingDefinition? binding in context.Bindings)
        {
            if (binding is null)
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidConfiguration, "A binding cannot be null.", context.Id));
                continue;
            }

            if (!IsIdentifier(binding.Id))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidIdentifier, "A binding needs a valid stable identifier.", context.Id, binding.Id));
            }
            else if (!bindingIds.Add(binding.Id))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.DuplicateBinding, "Binding identifiers must be globally unique.", context.Id, binding.Id));
            }

            if (!IsIdentifier(binding.ContextId) || !StringComparer.Ordinal.Equals(binding.ContextId, context.Id))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidBindingOwner, "Binding owner must identify its containing context.", context.Id, binding.Id));
            }

            if (!binding.Gesture.IsValid)
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidGesture, "Unsupported or malformed logical gesture.", context.Id, binding.Id, binding.Gesture));
            }
            else if (!gestures.Add(binding.Gesture))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.DuplicateGesture, "A gesture can have only one binding per context.", context.Id, binding.Id, binding.Gesture));
            }

            if (this.reservedGestures.Contains(binding.Gesture))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.ReservedGesture, "This exact gesture is reserved for navigation in this configuration's interaction mode.", context.Id, binding.Id, binding.Gesture));
            }

            string? destinationId = binding.Destination switch
            {
                BindingDestination.Context destination => destination.ContextId,
                BindingDestination.Command destination => destination.CommandId,
                _ => null,
            };
            if (!IsIdentifier(destinationId))
            {
                diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.InvalidDestination, "Exactly one typed destination with a valid stable identifier is required.", context.Id, binding.Id, binding.Gesture, destinationId));
            }
        }
    }

    private static bool IsIdentifier(string? identifier) => !string.IsNullOrEmpty(identifier)
        && char.IsAsciiLetterOrDigit(identifier[0])
        && identifier.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static void ValidateGraph(Dictionary<string, string[]> edges, Dictionary<string, ContextDefinition> contexts, string? rootId, List<RoutingDiagnostic> diagnostics)
    {
        Dictionary<string, int> colors = edges.Keys.ToDictionary(static id => id, static _ => 0, StringComparer.Ordinal);
        // Iterative DFS visits every component without risking recursion depth limits.
        foreach (string start in edges.Keys.Order(StringComparer.Ordinal))
        {
            Stack<(string Id, bool Exiting)> pending = new();
            pending.Push((start, false));
            while (pending.TryPop(out (string Id, bool Exiting) frame))
            {
                if (frame.Exiting)
                {
                    colors[frame.Id] = 2;
                    continue;
                }

                if (colors[frame.Id] != 0)
                {
                    continue;
                }

                colors[frame.Id] = 1;
                pending.Push((frame.Id, true));
                foreach (string destination in edges[frame.Id].Reverse())
                {
                    if (colors[destination] == 1)
                    {
                        BindingDefinition binding = contexts[frame.Id].Bindings.OfType<BindingDefinition>().First(binding => binding.Destination is BindingDestination.Context target && target.ContextId == destination);
                        diagnostics.Add(new(RoutingDiagnosticSeverity.Error, RoutingDiagnosticCode.ContextCycle, "Context transitions must be acyclic.", frame.Id, binding.Id, binding.Gesture, destination));
                    }
                    else if (colors[destination] == 0)
                    {
                        pending.Push((destination, false));
                    }
                }
            }
        }

        if (rootId is null || !edges.ContainsKey(rootId))
        {
            return;
        }

        HashSet<string> reachable = new(StringComparer.Ordinal);
        Stack<string> frontier = new();
        frontier.Push(rootId);
        while (frontier.TryPop(out string? id))
        {
            if (reachable.Add(id))
            {
                foreach (string destination in edges[id])
                {
                    frontier.Push(destination);
                }
            }
        }

        foreach (string id in edges.Keys.Where(id => !reachable.Contains(id)).Order(StringComparer.Ordinal))
        {
            diagnostics.Add(new(RoutingDiagnosticSeverity.Warning, RoutingDiagnosticCode.UnreachableContext, "This context is not reachable from Root.", id));
        }
    }
}