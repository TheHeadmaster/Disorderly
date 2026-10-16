using Disorderly.Routing;

namespace Disorderly.Testing.Routing;

internal static class RoutingTestData
{
    internal static BindingDefinition Command(string owner, string id, string key, string command, GestureModifiers modifiers = GestureModifiers.None) =>
        new(id, owner, new LogicalGesture(key, modifiers), new BindingDestination.Command(command));

    internal static BindingDefinition Enter(string owner, string id, string key, string context) =>
        new(id, owner, new LogicalGesture(key), new BindingDestination.Context(context));

    internal static ContextDefinition Context(string id, params BindingDefinition[] bindings) => new(id, id, bindings);

    internal static RoutingConfiguration Graph() => new("root",
    [
        Context("root", Enter("root", "dev", "D", "development"), Enter("root", "work", "W", "work"), Command("root", "root-c", "C", "root-command")),
        Context("development", Enter("development", "dev-servers", "S", "servers"), Command("development", "dev-c", "C", "dev-command")),
        Context("work", Enter("work", "work-servers", "S", "servers")),
        Context("servers", Command("servers", "server-c", "C", "server-command"), Command("servers", "server-a", "A", "server-action")),
    ]);

    internal static RoutingSession Session(RoutingConfiguration? configuration = null) =>
        new(new RoutingValidator().Validate(configuration ?? Graph()).Snapshot!);
}