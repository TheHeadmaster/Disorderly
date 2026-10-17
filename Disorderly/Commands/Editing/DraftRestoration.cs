using Disorderly.Routing;

namespace Disorderly.Commands.Editing;

/// <summary>Conservative contract comparison; never migrates arguments or republishes an old configuration.</summary>
public static class DraftRestoration
{
    public static string? Check(InvocationDraft draft, CommandConfigurationSnapshot current)
    {
        CommandInvocation invocation = draft.Editing.Invocation;
        CommandConfigurationSnapshot? saved = draft.Configuration;
        if (saved is null || draft.NavigationPath.IsDefaultOrEmpty || !ReferenceEquals(saved.Catalog, invocation.Catalog))
        {
            return "This draft has no coherent saved navigation configuration. Its input has been preserved.";
        }

        RoutingSession routing = new(saved.Routing);
        if (!routing.TryRestorePath(draft.NavigationPath)
            || invocation.Origin is not InvocationOrigin.Binding origin || origin.ContextId != routing.ActiveContext.Id
            || saved.CreateInteraction().Prepare(new RoutingResult.CommandRequested(invocation.Command.Id, origin.BindingId, origin.ContextId)).Invocation is not CommandInvocation original
            || original.CompletionPolicy != invocation.CompletionPolicy
            || !draft.Editing.Parameters.Select(parameter => parameter.Parameter).SequenceEqual(invocation.Command.Parameters)
            || !original.Arguments.Assignments.All(invocation.Arguments.Assignments.Contains))
        {
            return "The saved navigation or command origin is invalid. The draft has been preserved.";
        }

        CommandDefinition? command = current.Catalog.Find(invocation.Command.Id);
        if (command is null || !SameCommand(command, invocation.Command) || current.Routing.RootContextId != saved.Routing.RootContextId)
        {
            return "The command configuration has changed. Restore is blocked; the unfinished input remains stored.";
        }

        ContextDefinition? currentOrigin = current.Routing.Contexts.FirstOrDefault(context => context.Id == origin.ContextId);
        BindingDefinition originalBinding = routing.ActiveContext.Bindings.Single(binding => binding.Id == origin.BindingId);
        if (currentOrigin?.Bindings.Any(binding => SameBinding(binding, originalBinding)) != true)
        {
            return "The original command binding has changed. Restore is blocked; the draft remains stored.";
        }

        for (int index = 1; index < draft.NavigationPath.Length; index++)
        {
            string previous = draft.NavigationPath[index - 1];
            string next = draft.NavigationPath[index];
            ContextDefinition? now = current.Routing.Contexts.FirstOrDefault(context => context.Id == previous);
            IEnumerable<BindingDefinition> oldEdges = saved.Routing.GetContext(previous).Bindings
                .Where(binding => binding.Destination is BindingDestination.Context target && target.ContextId == next);
            if (now is null || !oldEdges.Any(edge => now.Bindings.Any(binding => SameBinding(binding, edge))))
            {
                return "The original navigation edges have changed. Restore is blocked; the draft remains stored.";
            }
        }

        return null;
    }

    private static bool SameCommand(CommandDefinition left, CommandDefinition right) => left.Id == right.Id
        && left.Parameters.Select(parameter => (parameter.Id, parameter.ValueType, parameter.Required))
            .SequenceEqual(right.Parameters.Select(parameter => (parameter.Id, parameter.ValueType, parameter.Required))) && left.DefaultCompletionPolicy == right.DefaultCompletionPolicy && left.Execution == right.Execution;

    private static bool SameBinding(BindingDefinition left, BindingDefinition right)
    {
        if (left.Id != right.Id || left.ContextId != right.ContextId || left.Gesture != right.Gesture)
        {
            return false;
        }

        if (left.Destination is BindingDestination.Command a && right.Destination is BindingDestination.Command b)
        {
            return a.CommandId == b.CommandId && a.Metadata?.CompletionOverride == b.Metadata?.CompletionOverride
                && (a.Metadata?.Presets ?? ArgumentSet.Empty).Assignments.SequenceEqual((b.Metadata?.Presets ?? ArgumentSet.Empty).Assignments);
        }

        return left.Destination == right.Destination;
    }
}