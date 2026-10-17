using System.Collections.Immutable;
using Disorderly.Routing;

namespace Disorderly.Commands;

/// <summary>One compatible routing/catalog pair, retained for the entire interaction.</summary>
public sealed class CommandConfigurationSnapshot
{
    internal CommandConfigurationSnapshot(RoutingSnapshot routing, CommandCatalogSnapshot catalog)
    {
        this.Routing = routing;
        this.Catalog = catalog;
    }

    public RoutingSnapshot Routing { get; }
    public CommandCatalogSnapshot Catalog { get; }
    public CommandInteraction CreateInteraction() => new(this);
}

public sealed class CommandInteraction
{
    internal CommandInteraction(CommandConfigurationSnapshot snapshot)
    {
        this.Snapshot = snapshot;
        this.Routing = new RoutingSession(snapshot.Routing);
    }

    public CommandConfigurationSnapshot Snapshot { get; }
    public RoutingSession Routing { get; }

    /// <summary>Use the result from this interaction, not a newer store or a newly resolved gesture.</summary>
    public InvocationPreparationResult Prepare(RoutingResult.CommandRequested request) => CommandInvocationFactory.PrepareBinding(this.Snapshot, request);
    public InvocationPreparationResult PrepareDirect(string commandId, ArgumentSet? arguments = null) => CommandInvocationFactory.PrepareDirect(this.Snapshot.Catalog, commandId, arguments);
}

public sealed class CommandConfigurationValidationResult
{
    internal CommandConfigurationValidationResult(IEnumerable<CommandDiagnostic> diagnostics, ImmutableArray<RoutingDiagnostic> routingDiagnostics, CommandConfigurationSnapshot? snapshot)
    {
        this.Diagnostics = CommandDiagnostic.Order(diagnostics);
        this.RoutingDiagnostics = routingDiagnostics;
        this.Snapshot = snapshot;
    }

    public ImmutableArray<CommandDiagnostic> Diagnostics { get; }
    public ImmutableArray<RoutingDiagnostic> RoutingDiagnostics { get; }
    public CommandConfigurationSnapshot? Snapshot { get; }
    public bool IsValid => this.Snapshot is not null;
}

public sealed class CommandConfigurationStore
{
    private readonly RoutingValidator routingValidator;
    private CommandConfigurationSnapshot? current;

    public CommandConfigurationStore(RoutingValidator? routingValidator = null)
    {
        this.routingValidator = routingValidator ?? new RoutingValidator();
    }

    public CommandConfigurationSnapshot? Current => Volatile.Read(ref this.current);

    public CommandConfigurationValidationResult Validate(RoutingConfiguration routing, IReadOnlyList<CommandDefinition> commands)
    {
        RoutingValidationResult routingResult = this.routingValidator.Validate(routing);
        CommandCatalogValidationResult catalogResult = new CommandCatalogValidator().Validate(commands);
        List<CommandDiagnostic> diagnostics = [.. catalogResult.Diagnostics];
        if (routingResult.Snapshot is RoutingSnapshot routingSnapshot && catalogResult.Snapshot is CommandCatalogSnapshot catalog)
        {
            foreach (ContextDefinition context in routingSnapshot.Contexts)
            {
                foreach (BindingDefinition binding in context.Bindings)
                {
                    if (binding.Destination is not BindingDestination.Command destination)
                    {
                        continue;
                    }

                    CommandDefinition? command = catalog.Find(destination.CommandId);
                    if (command is null)
                    {
                        diagnostics.Add(new(CommandDiagnosticCode.MissingCommand, "The binding references a command absent from the associated catalog.", destination.CommandId, binding.Id));
                        continue;
                    }

                    if (destination.Metadata?.CompletionOverride is CompletionPolicy policy && !Enum.IsDefined(policy))
                    {
                        diagnostics.Add(new(CommandDiagnosticCode.InvalidCompletionPolicy, "Unsupported binding completion override.", command.Id, binding.Id));
                    }

                    ArgumentValidationResult presets = ArgumentValidator.Validate(command, destination.Metadata?.Presets ?? ArgumentSet.Empty, false);
                    diagnostics.AddRange(presets.Diagnostics.Select(diagnostic => diagnostic with { BindingId = binding.Id }));
                }
            }
        }

        CommandConfigurationSnapshot? snapshot = routingResult.IsValid && catalogResult.IsValid && diagnostics.Count == 0
            ? new CommandConfigurationSnapshot(routingResult.Snapshot!, catalogResult.Snapshot!) : null;
        return new CommandConfigurationValidationResult(diagnostics, routingResult.Diagnostics, snapshot);
    }

    public CommandConfigurationValidationResult Publish(RoutingConfiguration routing, IReadOnlyList<CommandDefinition> commands)
    {
        CommandConfigurationValidationResult result = this.Validate(routing, commands);
        if (result.Snapshot is not null)
        {
            Interlocked.Exchange(ref this.current, result.Snapshot);
        }

        return result;
    }

    public CommandInteraction CreateInteraction() => (this.Current ?? throw new InvalidOperationException("Publish a valid combined configuration before creating an interaction.")).CreateInteraction();
}