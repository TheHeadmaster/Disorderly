using Disorderly.Commands;
using Disorderly.Routing;
using NUnit.Framework;
using static Disorderly.Testing.Commands.CommandTestData;

namespace Disorderly.Testing.Commands;

[TestFixture]
public class BindingContractTests
{
    [TestCase(null, null, CompletionPolicy.Dismiss)]
    [TestCase(CompletionPolicy.StayOpen, null, CompletionPolicy.StayOpen)]
    [TestCase(CompletionPolicy.Dismiss, null, CompletionPolicy.Dismiss)]
    [TestCase(null, CompletionPolicy.StayOpen, CompletionPolicy.StayOpen)]
    [TestCase(CompletionPolicy.StayOpen, CompletionPolicy.Dismiss, CompletionPolicy.Dismiss)]
    [TestCase(CompletionPolicy.Dismiss, CompletionPolicy.StayOpen, CompletionPolicy.StayOpen)]
    public void CompletionResolvesBindingThenCommandThenDismissWithoutNavigation(CompletionPolicy? command, CompletionPolicy? binding, CompletionPolicy expected)
    {
        CommandInteraction interaction = Interaction(Definition(required: false, policy: command), bindingPolicy: binding);
        CommandInvocation invocation = Invoke(interaction);
        Assert.That(invocation.CompletionPolicy, Is.EqualTo(expected));
        Assert.That(interaction.Routing.NavigationPath, Is.EqualTo(new[] { "root" }));
    }

    [Test]
    public void DifferentContextLocalPresetsReferenceOneSharedCommand()
    {
        CommandConfigurationStore store = new();
        RoutingConfiguration routing = new("root", [
            new ContextDefinition("root", "Root", [Binding(presets: PathArguments("downloads")), new BindingDefinition("enter-work", "root", new LogicalGesture("W"), new BindingDestination.Context("work"))]),
            new ContextDefinition("work", "Work", [Binding("work", "work-binding", presets: PathArguments("projects"))]),
        ]);
        Assert.That(store.Publish(routing, [Definition()]).IsValid, Is.True);
        CommandInteraction interaction = store.CreateInteraction();
        RoutingResult.CommandRequested rootRequest = (RoutingResult.CommandRequested)interaction.Routing.Route(new LogicalGesture("A"));
        interaction.Routing.Route(new LogicalGesture("W"));
        CommandInvocation original = interaction.Prepare(rootRequest).Invocation!; // Use recorded Root origin, not current Work.
        CommandInvocation work = Invoke(interaction);
        Assert.That(original.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("downloads")));
        Assert.That(work.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("projects")));
        Assert.That(original.Command, Is.SameAs(work.Command));
        Assert.That(original.Origin, Is.EqualTo(new InvocationOrigin.Binding("binding", "root")));
        Assert.That(work.Origin, Is.EqualTo(new InvocationOrigin.Binding("work-binding", "work")));
    }

    [Test]
    public void PresetsMayOmitRequiredArgumentsAndLegacyBindingsRemainValid()
    {
        CommandInteraction interaction = Interaction();
        CommandInvocation invocation = Invoke(interaction);
        Assert.That(invocation.Validation.Status, Is.EqualTo(ArgumentValidationStatus.Incomplete));
        Assert.That(invocation.PrepareAttempt().Succeeded, Is.False);
        CommandInvocation supplied = invocation.WithArguments(PathArguments("later"));
        Assert.That(supplied.Id, Is.EqualTo(invocation.Id));
        Assert.That(supplied.IsExecutionReady, Is.True);
    }

    [Test]
    public void InvalidPresetValuesAndOverridesRejectCombinedPublication()
    {
        ArgumentSet[] invalid = [
            new([new("unknown", new ArgumentValue.Text("bad"))]),
            new([new("path", new ArgumentValue.Boolean(false))]),
            new([new("path", new ArgumentValue.Path("one")), new("path", new ArgumentValue.Path("two"))]),
            new([new("path", null!)]), new(null!),
        ];
        foreach (ArgumentSet presets in invalid)
        {
            CommandConfigurationValidationResult result = new CommandConfigurationStore().Publish(RoutingProposal(Binding(presets: presets)), [Definition()]);
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Diagnostics.All(static diagnostic => diagnostic.BindingId == "binding" && diagnostic.CommandId == "open"), Is.True);
        }

        CommandConfigurationValidationResult badPolicy = new CommandConfigurationStore().Publish(RoutingProposal(Binding(policy: (CompletionPolicy)99)), [Definition()]);
        Assert.That(badPolicy.IsValid, Is.False);
        Assert.That(badPolicy.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.InvalidCompletionPolicy));
    }
}