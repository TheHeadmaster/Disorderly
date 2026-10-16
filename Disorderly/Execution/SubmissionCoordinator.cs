using Disorderly.Commands;
using Serilog;
using Serilog.Events;

namespace Disorderly.Execution;

/// <summary>Process-lifetime acceptance ledger. Completed requests/results are not retained.</summary>
public sealed class SubmissionCoordinator(ExecutionHandlerRegistry handlers, int acceptanceCapacity = 16384)
{
    private readonly ExecutionHandlerRegistry handlers = handlers;
    private readonly int acceptanceCapacity = acceptanceCapacity > 0 ? acceptanceCapacity : throw new ArgumentOutOfRangeException(nameof(acceptanceCapacity));
    private readonly Lock gate = new();
    private readonly HashSet<Guid> accepted = [];

    public static SubmissionCoordinator CreateDefault() => new(new ExecutionHandlerRegistry([new LaunchExecutableHandler(new WindowsProcessLauncher())]));

    public bool HasImplementation(CommandInvocation invocation) => this.handlers.Resolve(invocation.Command.Execution) is not null;

    public ExecutionAvailability Availability(CommandInvocation invocation)
    {
        if (!invocation.IsExecutionReady)
        {
            return new(false, "Arguments are incomplete or invalid.");
        }

        if (!ReferenceEquals(invocation.Catalog.Find(invocation.Command.Id), invocation.Command))
        {
            return new(false, "The retained command catalog does not match the invocation.");
        }

        return !this.HasImplementation(invocation)
            ? new(false, "Execution is not yet configured or no supported handler is available.")
            : new(true, "Executable implementation available.");
    }

    public async Task<ExecutionResult> SubmitAsync(ExecutionReadyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ExecutionAvailability availability = this.Availability(request.Invocation);
        ICommandExecutionHandler? handler = this.handlers.Resolve(request.Invocation.Command.Execution);
        if (request.AttemptId == Guid.Empty || request.InvocationId == Guid.Empty || !availability.Available || handler is null)
        {
            return Report(ExecutionResult.For(request, ExecutionOutcome.Rejected, "InvalidSubmission", availability.Message));
        }

        lock (this.gate)
        {
            if (this.accepted.Contains(request.AttemptId))
            {
                return ExecutionResult.For(request, ExecutionOutcome.AlreadyAccepted, "AlreadyAccepted", "This attempt has already been accepted.");
            }

            // Fail closed at a bounded budget rather than evict IDs and permit replay of old attempts.
            if (this.accepted.Count >= this.acceptanceCapacity)
            {
                return Report(ExecutionResult.For(request, ExecutionOutcome.Rejected, "AcceptanceCapacity", "The process submission budget is exhausted. Restart Disorderly before submitting new work."));
            }

            this.accepted.Add(request.AttemptId);
        }

        ExecutionResult result;
        try
        {
            result = await handler.ExecuteAsync(request).ConfigureAwait(false);
            if (result.InvocationId != request.InvocationId || result.AttemptId != request.AttemptId || result.CommandId != request.Invocation.Command.Id
                || !Enum.IsDefined(result.Outcome) || result.Outcome == ExecutionOutcome.AlreadyAccepted)
            {
                result = ExecutionResult.For(request, ExecutionOutcome.Uncertain, "InvalidHandlerResult", "The execution result could not be established. Do not retry blindly.");
            }
        }
        catch (Exception error)
        {
            // Avoid recording exception messages or argument values; they may contain sensitive paths.
            Log.Warning("Execution handler raised {ExceptionType} for {CommandId}, invocation {InvocationId}, attempt {AttemptId}.",
                error.GetType().Name, request.Invocation.Command.Id, request.InvocationId, request.AttemptId);
            result = ExecutionResult.For(request, ExecutionOutcome.Uncertain, "UnexpectedExecutionFailure", "The execution result could not be established. Do not retry blindly.");
        }

        return Report(result);
    }

    private static ExecutionResult Report(ExecutionResult result)
    {
        Log.Write(result.Outcome == ExecutionOutcome.Succeeded ? LogEventLevel.Information : LogEventLevel.Warning,
            "Execution {Outcome} ({Code}) for {CommandId}, invocation {InvocationId}, attempt {AttemptId}.",
            result.Outcome, result.Code, result.CommandId, result.InvocationId, result.AttemptId);
        return result;
    }
}