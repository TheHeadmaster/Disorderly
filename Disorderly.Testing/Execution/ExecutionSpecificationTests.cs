using Disorderly.Commands;
using Disorderly.Routing;
using NUnit.Framework;

namespace Disorderly.Testing.Execution;

[TestFixture]
public class ExecutionSpecificationTests
{
    private sealed record Unsupported : CommandExecutionSpecification;

    [Test]
    public void UnconfiguredCommandsRemainValidAndArgumentReady()
    {
        CommandInvocation invocation = ExecutionFixture.Invocation(configured: false);
        Assert.That(invocation.IsExecutionReady, Is.True);
        Assert.That(invocation.PrepareAttempt().Succeeded, Is.True);
        Assert.That(invocation.Command.Execution, Is.Null);
    }

    [Test]
    public void RequiredPathExecutionContractPublishesWithoutAccessingItsTarget()
    {
        CommandConfigurationStore store = ExecutionFixture.Store(path: @"Z:\Does Not Exist\example.exe");
        Assert.That(store.Current!.Catalog.Find("launch")!.Execution, Is.EqualTo(new CommandExecutionSpecification.LaunchExecutable("executable")));
    }

    [TestCase("missing", ParameterValueType.Path, true)]
    [TestCase("executable", ParameterValueType.Text, true)]
    [TestCase("executable", ParameterValueType.Boolean, true)]
    [TestCase("executable", ParameterValueType.Integer, true)]
    [TestCase("executable", ParameterValueType.Path, false)]
    [TestCase("", ParameterValueType.Path, true)]
    [TestCase("invalid parameter", ParameterValueType.Path, true)]
    public void InvalidExecutionParameterRejectsAtomicReplacement(string id, ParameterValueType type, bool required)
    {
        CommandConfigurationStore store = ExecutionFixture.Store();
        CommandConfigurationSnapshot previous = store.Current!;
        CommandDefinition command = ExecutionFixture.Command() with
        {
            Parameters = [new("executable", "Executable", type, required)],
            Execution = new CommandExecutionSpecification.LaunchExecutable(id),
        };
        CommandConfigurationValidationResult result = store.Publish(new RoutingConfiguration("root", [new("root", "Root", [])]), [command]);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == CommandDiagnosticCode.InvalidExecutionSpecification && diagnostic.CommandId == "launch"), Is.True);
        Assert.That(store.Current, Is.SameAs(previous));
    }

    [Test]
    public void UnsupportedSpecificationIsRejectedInsteadOfBecomingUnconfigured()
    {
        CommandCatalogValidationResult result = new CommandCatalogValidator().Validate([ExecutionFixture.Command() with { Execution = new Unsupported() }]);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Diagnostics.Single().Code, Is.EqualTo(CommandDiagnosticCode.InvalidExecutionSpecification));
    }
}