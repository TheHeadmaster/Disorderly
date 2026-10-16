using System.Collections.Immutable;
using Disorderly.Domain;
using Disorderly.Routing;

namespace Disorderly.Commands;

public abstract record InvocationOrigin
{
    private protected InvocationOrigin() { }

    public sealed record Direct : InvocationOrigin;
    public sealed record Binding(string BindingId, string ContextId) : InvocationOrigin;
}

/// <summary>Immutable state, tied to a validated catalog. Construction is confined to validated factories.</summary>
public sealed class CommandInvocation
{
    internal CommandInvocation(Guid id, CommandCatalogSnapshot catalog, CommandDefinition command, ArgumentSet arguments,
        InvocationOrigin origin, CompletionPolicy completionPolicy)
    {
        if (id == Guid.Empty || !ReferenceEquals(catalog.Find(command.Id), command) || !Enum.IsDefined(completionPolicy))
        {
            throw new ArgumentException("Internal invocation construction requires a valid identity, catalog definition and policy.");
        }

        bool validOrigin = origin is InvocationOrigin.Direct
            || (origin is InvocationOrigin.Binding binding && StableIdentifier.IsValid(binding.BindingId) && StableIdentifier.IsValid(binding.ContextId));
        if (!validOrigin)
        {
            throw new ArgumentException("Internal invocation construction requires a valid origin.", nameof(origin));
        }

        this.Id = id;
        this.Catalog = catalog;
        this.Command = command;
        this.Arguments = arguments;
        this.Origin = origin;
        this.CompletionPolicy = completionPolicy;
        this.Validation = ArgumentValidator.Validate(command, arguments);
    }

    public Guid Id { get; }
    public CommandCatalogSnapshot Catalog { get; }
    public CommandDefinition Command { get; }
    public ArgumentSet Arguments { get; }
    public InvocationOrigin Origin { get; }
    public CompletionPolicy CompletionPolicy { get; }
    public ArgumentValidationResult Validation { get; }
    public bool IsExecutionReady => this.Validation.Status == ArgumentValidationStatus.Valid;

    /// <summary>Replace state while retaining identity. A future editor must enforce its own preset editing policy.</summary>
    public CommandInvocation WithArguments(ArgumentSet arguments) => new(this.Id, this.Catalog, this.Command,
        arguments ?? new ArgumentSet(null!), this.Origin, this.CompletionPolicy);

    /// <summary>An explicit submission/retry preparation action, never an execution or automatic retry.</summary>
    public AttemptPreparationResult PrepareAttempt() => this.IsExecutionReady
        ? new AttemptPreparationResult(new ExecutionReadyRequest(Guid.NewGuid(), this), [])
        : new AttemptPreparationResult(null, this.Validation.Diagnostics);
}

public sealed class InvocationPreparationResult
{
    internal InvocationPreparationResult(CommandInvocation? invocation, IEnumerable<CommandDiagnostic> diagnostics)
    {
        this.Invocation = invocation;
        this.Diagnostics = CommandDiagnostic.Order(diagnostics);
    }

    public CommandInvocation? Invocation { get; }
    public ImmutableArray<CommandDiagnostic> Diagnostics { get; }
    public bool Succeeded => this.Invocation is not null;
}

public sealed class ExecutionReadyRequest
{
    internal ExecutionReadyRequest(Guid attemptId, CommandInvocation invocation)
    {
        if (attemptId == Guid.Empty || !invocation.IsExecutionReady)
        {
            throw new ArgumentException("Only a validated invocation can produce an identified execution-ready request.");
        }

        this.AttemptId = attemptId;
        this.Invocation = invocation;
    }

    public Guid AttemptId { get; }
    public Guid InvocationId => this.Invocation.Id;
    public CommandInvocation Invocation { get; }
}

public sealed class AttemptPreparationResult
{
    internal AttemptPreparationResult(ExecutionReadyRequest? request, IEnumerable<CommandDiagnostic> diagnostics)
    {
        this.Request = request;
        this.Diagnostics = CommandDiagnostic.Order(diagnostics);
    }

    public ExecutionReadyRequest? Request { get; }
    public ImmutableArray<CommandDiagnostic> Diagnostics { get; }
    public bool Succeeded => this.Request is not null;
}

public static class CommandInvocationFactory
{
    public static InvocationPreparationResult PrepareDirect(CommandCatalogSnapshot catalog, string commandId, ArgumentSet? arguments = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        CommandDefinition? command = catalog.Find(commandId);
        return command is null ? Missing(commandId) : Create(catalog, command, arguments ?? ArgumentSet.Empty,
            new InvocationOrigin.Direct(), command.DefaultCompletionPolicy ?? CompletionPolicy.Dismiss);
    }

    public static InvocationPreparationResult PrepareBinding(CommandConfigurationSnapshot configuration, RoutingResult.CommandRequested request)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (request is null)
        {
            return new InvocationPreparationResult(null, [new(CommandDiagnosticCode.InvalidOrigin, "A contextual routing result is required.")]);
        }

        CommandDefinition? command = configuration.Catalog.Find(request.CommandId);
        if (command is null)
        {
            return Missing(request.CommandId);
        }

        ContextDefinition? context = configuration.Routing.Contexts.FirstOrDefault(context => context.Id == request.ContextId);
        BindingDefinition? binding = context?.Bindings.FirstOrDefault(binding => binding.Id == request.BindingId);
        if (binding?.Destination is not BindingDestination.Command destination || destination.CommandId != request.CommandId
            || !StableIdentifier.IsValid(request.BindingId) || !StableIdentifier.IsValid(request.ContextId))
        {
            return new InvocationPreparationResult(null, [new(CommandDiagnosticCode.InvalidOrigin, "The request must match its binding and invoking context in the associated routing snapshot.", request.CommandId, request.BindingId)]);
        }

        return Create(configuration.Catalog, command, destination.Metadata?.Presets ?? ArgumentSet.Empty,
            new InvocationOrigin.Binding(binding.Id, context!.Id),
            destination.Metadata?.CompletionOverride ?? command.DefaultCompletionPolicy ?? CompletionPolicy.Dismiss);
    }

    private static InvocationPreparationResult Missing(string commandId) => new(null,
        [new(CommandDiagnosticCode.MissingCommand, "The command is absent from the associated catalog snapshot.", commandId)]);

    private static InvocationPreparationResult Create(CommandCatalogSnapshot catalog, CommandDefinition command, ArgumentSet arguments,
        InvocationOrigin origin, CompletionPolicy policy)
    {
        CommandInvocation invocation = new(Guid.NewGuid(), catalog, command, arguments, origin, policy);
        return new InvocationPreparationResult(invocation, invocation.Validation.Diagnostics);
    }
}