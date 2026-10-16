using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Routing;
using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing.Commands.Editing;

internal static class EditingFixture
{
    internal static CommandConfigurationStore Store(bool presetPath = true, bool requiredText = false, bool configured = true, CompletionPolicy policy = CompletionPolicy.StayOpen)
    {
        CommandDefinition command = new("edit", "Editable command", [
            new("text", "Working Label", ParameterValueType.Text, requiredText),
            new("flag", "Recursive", ParameterValueType.Boolean),
            new("count", "Retry Count", ParameterValueType.Integer),
            new("executable", "Executable", ParameterValueType.Path, true),
        ], "Parameter editor fixture", policy, Execution: configured ? new CommandExecutionSpecification.LaunchExecutable("executable") : null);
        BindingDestination.Command destination = new("edit")
        {
            Metadata = new(presetPath ? new ArgumentSet([new("executable", new ArgumentValue.Path(@"C:\Preset\example.exe"))]) : ArgumentSet.Empty),
        };
        CommandConfigurationStore store = PaletteConfiguration.CreateStore();
        Assert.That(store.Publish(new("root", [
            new("root", "Root", [new("child", "root", new("D"), new BindingDestination.Context("child"))]),
            new("child", "Child", [new("select", "child", new("N"), destination)]),
        ]), [command]).IsValid, Is.True);
        return store;
    }

    internal static ArgumentEditorSession Session(bool presetPath = true, bool requiredText = false)
    {
        CommandInteraction interaction = Store(presetPath, requiredText).CreateInteraction();
        interaction.Routing.Route(new("D"));
        CommandInvocation invocation = interaction.Prepare((RoutingResult.CommandRequested)interaction.Routing.Route(new("N"))).Invocation!;
        return new ArgumentEditorSession(invocation);
    }

    internal static bool Apply(ArgumentEditorSession editor, string id, ArgumentEdit edit) => editor.TryApply(editor.Id, editor.Snapshot.Revision, id, edit);
}