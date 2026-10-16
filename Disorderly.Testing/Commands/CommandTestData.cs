using Disorderly.Commands;
using Disorderly.Routing;

namespace Disorderly.Testing.Commands;

internal static class CommandTestData
{
    internal static CommandDefinition Definition(string id = "open", bool required = true, CompletionPolicy? policy = null, CommandIcon? icon = null) => new(id, "Open Folder",
    [
        new("path", "Target directory", ParameterValueType.Path, required),
        new("recursive", "Recursive", ParameterValueType.Boolean),
        new("count", "Count", ParameterValueType.Integer),
        new("label", "Label", ParameterValueType.Text),
    ], DefaultCompletionPolicy: policy, Icon: icon);

    internal static ArgumentSet PathArguments(string path) => new([new("path", new ArgumentValue.Path(path))]);
    internal static CommandCatalogSnapshot Catalog(params CommandDefinition[] commands) => new CommandCatalogValidator().Validate(commands).Snapshot!;

    internal static BindingDefinition Binding(string owner = "root", string id = "binding", string key = "A", string commandId = "open", ArgumentSet? presets = null, CompletionPolicy? policy = null) =>
        new(id, owner, new LogicalGesture(key), new BindingDestination.Command(commandId)
        {
            Metadata = presets is null && policy is null ? null : new CommandBindingMetadata(presets, policy),
        });

    internal static RoutingConfiguration RoutingProposal(params BindingDefinition[] bindings) => new("root", [new ContextDefinition("root", "Root", bindings)]);

    internal static CommandInteraction Interaction(CommandDefinition? command = null, ArgumentSet? presets = null, CompletionPolicy? bindingPolicy = null)
    {
        CommandConfigurationStore store = new();
        store.Publish(RoutingProposal(Binding(presets: presets, policy: bindingPolicy)), [command ?? Definition()]);
        return store.CreateInteraction();
    }

    internal static CommandInvocation Invoke(CommandInteraction interaction, string key = "A") =>
        interaction.Prepare((RoutingResult.CommandRequested)interaction.Routing.Route(new LogicalGesture(key))).Invocation!;
}