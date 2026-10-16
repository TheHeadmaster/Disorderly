using Disorderly.Commands;
using Disorderly.Routing;
using NUnit.Framework;
using static Disorderly.Testing.Commands.CommandTestData;

namespace Disorderly.Testing.Commands;

[TestFixture]
public class InvocationContractTests
{
    [TestCase(null, CompletionPolicy.Dismiss)]
    [TestCase(CompletionPolicy.StayOpen, CompletionPolicy.StayOpen)]
    [TestCase(CompletionPolicy.Dismiss, CompletionPolicy.Dismiss)]
    public void DirectSelectionUsesSharedCatalogWithoutInventingBinding(CompletionPolicy? policy, CompletionPolicy expected)
    {
        CommandCatalogSnapshot catalog = Catalog(Definition(required: false, policy: policy));
        CommandInvocation invocation = CommandInvocationFactory.PrepareDirect(catalog, "open").Invocation!;
        Assert.That(invocation.Origin, Is.TypeOf<InvocationOrigin.Direct>());
        Assert.That(invocation.CompletionPolicy, Is.EqualTo(expected));
        Assert.That(invocation.Command, Is.SameAs(catalog.Find("open")));
        Assert.That(invocation.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void IndependentInvocationsShareDefinitionButNotIdentityOrArgumentState()
    {
        CommandInteraction interaction = Interaction(Definition(), PathArguments("first"));
        CommandInvocation first = Invoke(interaction);
        CommandInvocation second = Invoke(interaction);
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
        Assert.That(second.Command, Is.SameAs(first.Command));
        CommandInvocation revised = first.WithArguments(PathArguments("changed"));
        Assert.That(revised.Id, Is.EqualTo(first.Id));
        Assert.That(first.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("first")));
        Assert.That(second.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("first")));
        Assert.That(revised.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("changed")));
    }

    [Test]
    public void InvalidAndIncompleteInvocationsRetainValuesButCannotPrepareAttempts()
    {
        CommandCatalogSnapshot catalog = Catalog(Definition());
        CommandInvocation incomplete = CommandInvocationFactory.PrepareDirect(catalog, "open").Invocation!;
        Assert.That(incomplete.IsExecutionReady, Is.False);
        Assert.That(incomplete.PrepareAttempt().Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.MissingRequired));
        CommandInvocation invalid = incomplete.WithArguments(new ArgumentSet([new("path", new ArgumentValue.Text("wrong type"))]));
        Assert.That(invalid.Validation.Status, Is.EqualTo(ArgumentValidationStatus.Invalid));
        Assert.That(invalid.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Text("wrong type")));
        Assert.That(invalid.PrepareAttempt().Request, Is.Null);
        Assert.That(invalid.WithArguments(null!).IsExecutionReady, Is.False);
    }

    [Test]
    public void ExplicitAttemptsHaveSeparateIdsAndFreezeTheirInvocationVersion()
    {
        CommandInteraction interaction = Interaction(Definition(), PathArguments("original"));
        CommandInvocation invocation = Invoke(interaction);
        ExecutionReadyRequest first = invocation.PrepareAttempt().Request!;
        ExecutionReadyRequest retry = invocation.PrepareAttempt().Request!;
        Assert.That(first.AttemptId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(first.AttemptId, Is.Not.EqualTo(invocation.Id));
        Assert.That(retry.AttemptId, Is.Not.EqualTo(first.AttemptId));
        Assert.That(first.InvocationId, Is.EqualTo(retry.InvocationId));
        Assert.That(first.InvocationId, Is.EqualTo(invocation.Id));
        invocation.WithArguments(PathArguments("new version"));
        Assert.That(first.Invocation.Arguments.Assignments[0].Value, Is.EqualTo(new ArgumentValue.Path("original")));
        Assert.That(interaction.Routing.ActiveContext.Id, Is.EqualTo("root"));
    }

    [Test]
    public void MissingCommandsAndMismatchedContextualOriginsAreStructuredFailures()
    {
        CommandInteraction interaction = Interaction(Definition(required: false));
        Assert.That(interaction.PrepareDirect("absent").Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.MissingCommand));
        Assert.That(interaction.Prepare(new RoutingResult.CommandRequested("absent", "binding", "root")).Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.MissingCommand));
        foreach (RoutingResult.CommandRequested request in new[] {
            new RoutingResult.CommandRequested("open", "wrong-binding", "root"),
            new RoutingResult.CommandRequested("open", "binding", "wrong-context"),
        })
        {
            InvocationPreparationResult result = interaction.Prepare(request);
            Assert.That(result.Invocation, Is.Null);
            Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.InvalidOrigin));
        }

        Assert.That(interaction.Prepare(null!).Succeeded, Is.False);
    }
}