using System.Collections.Concurrent;
using System.Diagnostics;
using Disorderly.Commands;
using Disorderly.Execution;
using Disorderly.Routing;
using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing.Execution;

internal static class ExecutionFixture
{
    internal static CommandDefinition Command(CompletionPolicy? policy = null, bool configured = true, bool extraRequired = false) =>
        new("launch", "Open Test Application", extraRequired
            ? [new("executable", "Executable", ParameterValueType.Path, true), new("extra", "Extra", ParameterValueType.Text, true)]
            : [new("executable", "Executable", ParameterValueType.Path, true)], "Launch description", policy,
            Execution: configured ? new CommandExecutionSpecification.LaunchExecutable("executable") : null);

    internal static CommandConfigurationStore Store(CompletionPolicy? commandPolicy = null, CompletionPolicy? bindingPolicy = null,
        bool configured = true, bool extraRequired = false, string path = @"C:\Program Files\Example\example.exe")
    {
        CommandConfigurationStore store = PaletteConfiguration.CreateStore();
        BindingDestination.Command destination = new("launch")
        {
            Metadata = new(new ArgumentSet([new("executable", new ArgumentValue.Path(path))]), bindingPolicy),
        };
        Assert.That(store.Publish(new("root", [
            new("root", "Root", [new("enter", "root", new("D"), new BindingDestination.Context("development"))]),
            new("development", "Development", [new("launch-binding", "development", new("N"), destination)]),
        ]), [Command(commandPolicy, configured, extraRequired)]).IsValid, Is.True);
        return store;
    }

    internal static CommandInvocation Invocation(string path = @"C:\Program Files\Example\example.exe", bool configured = true)
    {
        CommandInteraction interaction = Store(configured: configured, path: path).CreateInteraction();
        interaction.Routing.Route(new("D"));
        return interaction.Prepare((RoutingResult.CommandRequested)interaction.Routing.Route(new("N"))).Invocation!;
    }

    internal sealed class Launcher : IProcessLauncher
    {
        internal ConcurrentQueue<ProcessStartInfo> Calls { get; } = new();
        internal Func<ProcessStartInfo, Task<ProcessLaunchResult>> Start { get; set; } = _ => Task.FromResult(Success);
        public Task<ProcessLaunchResult> LaunchAsync(ProcessStartInfo startInfo)
        {
            this.Calls.Enqueue(startInfo);
            return this.Start(startInfo);
        }
    }

    internal static ProcessLaunchResult Success => new(ExecutionOutcome.Succeeded, "Started", "Launch accepted.");
    internal static ProcessLaunchResult KnownFailure => new(ExecutionOutcome.FailedNotStarted, "NotFound", "The executable was not found.");
    internal static ProcessLaunchResult Uncertain => new(ExecutionOutcome.Uncertain, "Unknown", "The launch outcome is uncertain. Do not retry blindly.");
    internal static SubmissionCoordinator Coordinator(Launcher launcher, int capacity = 16384) => new(new([new LaunchExecutableHandler(launcher)]), capacity);
}