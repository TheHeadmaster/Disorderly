using Disorderly.Commands;

namespace Disorderly.Execution;

public enum ExecutionOutcome
{
    Succeeded,
    Rejected,
    FailedNotStarted,
    Uncertain,
    AlreadyAccepted,
}

public sealed record ExecutionResult(Guid InvocationId, Guid AttemptId, string CommandId,
    ExecutionOutcome Outcome, string Code, string Message)
{
    public bool CanRetry => this.Outcome == ExecutionOutcome.FailedNotStarted;

    public static ExecutionResult For(ExecutionReadyRequest request, ExecutionOutcome outcome, string code, string message) =>
        new(request.InvocationId, request.AttemptId, request.Invocation.Command.Id, outcome, code, message);
}

public sealed record ExecutionAvailability(bool Available, string Message);

public interface ICommandExecutionHandler
{
    bool Supports(CommandExecutionSpecification specification);
    Task<ExecutionResult> ExecuteAsync(ExecutionReadyRequest request);
}

/// <summary>A small, fixed resolver, not a plugin runtime. Ambiguous registrations are rejected.</summary>
public sealed class ExecutionHandlerRegistry(IEnumerable<ICommandExecutionHandler> handlers)
{
    private readonly ICommandExecutionHandler[] handlers = handlers.ToArray();

    public ICommandExecutionHandler? Resolve(CommandExecutionSpecification? specification)
    {
        if (specification is null)
        {
            return null;
        }

        ICommandExecutionHandler[] matches = this.handlers.Where(handler => handler.Supports(specification)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}