using Disorderly.Commands;
using Disorderly.Routing;
using NUnit.Framework;
using static Disorderly.Testing.Commands.CommandTestData;

namespace Disorderly.Testing.Commands;

[TestFixture]
public class CommandPublicationTests
{
    [Test]
    public void StandaloneRoutingPermitsOpaqueReferencesButCombinedPublicationRejectsThem()
    {
        RoutingConfiguration routing = RoutingProposal(Binding(commandId: "unknown"));
        RoutingConfigurationStore standalone = new();
        Assert.That(standalone.Publish(routing).IsValid, Is.True);
        CommandConfigurationStore combined = new();
        Assert.Throws<InvalidOperationException>(() => combined.CreateInteraction());
        CommandConfigurationValidationResult result = combined.Publish(routing, [Definition()]);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.MissingCommand));
        Assert.That(combined.Current, Is.Null);
    }

    [Test]
    public void InvalidRoutingCatalogOrPresetsCannotPartiallyReplacePair()
    {
        CommandConfigurationStore store = new();
        Assert.That(store.Publish(RoutingProposal(Binding(presets: PathArguments("original"))), [Definition()]).IsValid, Is.True);
        CommandConfigurationSnapshot original = store.Current!;
        Assert.That(store.Publish(RoutingProposal(Binding(commandId: "missing")), [Definition("new")]).IsValid, Is.False);
        Assert.That(store.Current, Is.SameAs(original));
        CommandConfigurationValidationResult invalidRouting = store.Publish(new RoutingConfiguration("missing", []), [Definition("new")]);
        Assert.That(invalidRouting.IsValid, Is.False);
        Assert.That(invalidRouting.RoutingDiagnostics.Any(static diagnostic => diagnostic.Severity == RoutingDiagnosticSeverity.Error), Is.True);
        Assert.That(store.Current, Is.SameAs(original));
        Assert.That(store.Publish(RoutingProposal(Binding()), [Definition() with { Parameters = null! }]).IsValid, Is.False);
        Assert.That(store.Current, Is.SameAs(original));
        Assert.That(store.Publish(RoutingProposal(Binding(presets: new ArgumentSet([new("path", new ArgumentValue.Integer(1))]))), [Definition()]).IsValid, Is.False);
        Assert.That(store.Current, Is.SameAs(original));
        Assert.That(Invoke(store.CreateInteraction()).Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("original")));
    }

    [Test]
    public void OldInteractionsAndInvocationsRetainCatalogPresetsAndPolicyAfterPublication()
    {
        CommandConfigurationStore store = new();
        store.Publish(RoutingProposal(Binding(presets: PathArguments("old"))), [Definition(policy: CompletionPolicy.StayOpen)]);
        CommandInteraction oldInteraction = store.CreateInteraction();
        RoutingResult.CommandRequested oldRequest = (RoutingResult.CommandRequested)oldInteraction.Routing.Route(new LogicalGesture("A"));
        CommandInvocation oldInvocation = oldInteraction.Prepare(oldRequest).Invocation!;
        store.Publish(RoutingProposal(Binding(presets: PathArguments("new"))), [Definition(policy: CompletionPolicy.Dismiss) with { DisplayName = "New definition" }]);
        CommandInvocation stillOld = oldInteraction.Prepare(oldRequest).Invocation!;
        CommandInvocation latest = Invoke(store.CreateInteraction());
        Assert.That(stillOld.Command.DisplayName, Is.EqualTo("Open Folder"));
        Assert.That(stillOld.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("old")));
        Assert.That(stillOld.CompletionPolicy, Is.EqualTo(CompletionPolicy.StayOpen));
        Assert.That(latest.Command.DisplayName, Is.EqualTo("New definition"));
        Assert.That(latest.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("new")));
        Assert.That(latest.CompletionPolicy, Is.EqualTo(CompletionPolicy.Dismiss));
        Assert.That(oldInvocation.PrepareAttempt().Request!.Invocation.Command, Is.SameAs(stillOld.Command));
    }

    [Test]
    public void MutatingCallerOwnedCollectionsCannotChangeNewOrExistingInteractions()
    {
        List<ArgumentAssignment> assignments = [new("path", new ArgumentValue.Path("original"))];
        ArgumentSet presets = new(assignments);
        List<BindingDefinition> bindings = [Binding(presets: presets)];
        List<ContextDefinition> contexts = [new("root", "Root", bindings)];
        List<ParameterDefinition> parameters = [new("path", "Path", ParameterValueType.Path, true)];
        List<CommandDefinition> commands = [new("open", "Open", parameters)];
        CommandConfigurationStore store = new();
        Assert.That(store.Publish(new RoutingConfiguration("root", contexts), commands).IsValid, Is.True);
        CommandInteraction original = store.CreateInteraction();
        assignments.Clear();
        bindings.Clear();
        contexts.Clear();
        parameters.Clear();
        commands.Clear();
        Assert.That(Invoke(original).Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("original")));
        Assert.That(Invoke(store.CreateInteraction()).Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("original")));
        Assert.Throws<NotSupportedException>(() => ((IList<ArgumentAssignment>)presets.Assignments).Clear());
    }

    [Test]
    public void WrongBindingCommandPairCannotBeUsedEvenWhenBothCommandsExist()
    {
        CommandConfigurationStore store = new();
        store.Publish(RoutingProposal(Binding()), [Definition(), Definition("other")]);
        InvocationPreparationResult result = store.CreateInteraction().Prepare(new RoutingResult.CommandRequested("other", "binding", "root"));
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.InvalidOrigin));
    }
}