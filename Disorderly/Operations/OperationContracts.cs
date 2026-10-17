using System.Collections.Immutable;

namespace Disorderly.Operations;

public readonly record struct OperationId(Guid Value)
{
    public static OperationId New() => new(Guid.NewGuid());
}

public readonly record struct OperationStepId(Guid Value)
{
    public static OperationStepId New() => new(Guid.NewGuid());
}

public readonly record struct OperationInputId(Guid Value)
{
    public static OperationInputId New() => new(Guid.NewGuid());
}

public enum OperationState { Queued, Running, Pausing, Paused, AwaitingInput, CancellationRequested, Succeeded, Failed, Canceled, NeedsRecovery }
public enum OperationStepState { Pending, Running, Continuing, AwaitingInput, Verified, Failed, Uncertain }
public enum OperationStepOutcome { Completed, MoreWork, AwaitInput, KnownFailure, Uncertain }
public enum OperationResponse { Accept, Decline }
public enum OperationInputState { Outstanding, Resolved, Invalidated }
public enum OperationEligibilityReason { Dispatch, ManualResume, InputResolved }

public sealed record OperationStepDefinition(OperationStepId Id, string Description, bool RequiresMutationSlot = false);

/// <summary>Caller-owned simulation proposal. Acceptance freezes the ordered plan. No production mutation acceptance exists yet.</summary>
public sealed record OperationDefinition(string Name, IReadOnlyList<OperationStepDefinition> Steps,
    Guid? InvocationId = null, Guid? AttemptId = null, bool ReportKnownTotal = true);

public sealed record OperationDiagnostic(string Code, string Message, bool Uncertain = false);
public sealed record OperationEligibility(bool Eligible, OperationDiagnostic? Diagnostic = null);
public sealed record OperationControlResult(bool Accepted, bool Changed, string Code);
public sealed record OperationInputDefinition(OperationInputId Id, string Kind, string Prompt, IReadOnlyList<OperationResponse> AllowedResponses);
public sealed record OperationInputRequest(OperationInputId Id, OperationId OperationId, string Kind, string Prompt,
    ImmutableArray<OperationResponse> AllowedResponses, OperationInputState State, OperationResponse? Response = null);
public sealed record OperationStepSnapshot(OperationId OperationId, OperationStepDefinition Definition, int Order, OperationStepState State, int DispatchCount);

public sealed record OperationRecord(OperationId Id, string Name, long CreationOrder, long Revision,
    Guid? InvocationId, Guid? AttemptId, OperationState State, ImmutableArray<OperationStepSnapshot> Steps,
    int NextStep, int VerifiedSteps, int? TotalSteps, ImmutableArray<OperationInputRequest> InputRequests,
    bool PauseRequested, bool CancelRequested, bool OwnsMutationSlot, bool SafeBoundary, OperationDiagnostic? Diagnostic);

public sealed record OperationActivityEntry(OperationId Id, string Name, OperationState State, long CreationOrder,
    string? CurrentStep, int VerifiedSteps, int? TotalSteps, int OutstandingRequests, bool PauseRequested,
    bool CancelRequested, bool NeedsAttention, OperationDiagnostic? Diagnostic);
public sealed record OperationActivitySnapshot(long Revision, ImmutableArray<OperationActivityEntry> Entries);

/// <summary>Return only after required verification/finalization. SafeCheckpoint explicitly authorizes yielding ownership.</summary>
public sealed record OperationStepResult(OperationStepOutcome Outcome, bool SafeCheckpoint, bool Verified = false,
    IReadOnlyList<OperationInputDefinition>? Requests = null, OperationDiagnostic? Diagnostic = null)
{
    public static OperationStepResult Completed() => new(OperationStepOutcome.Completed, true, true);
    public static OperationStepResult Continue() => new(OperationStepOutcome.MoreWork, true);
}

public sealed record OperationStepContext(OperationRecord Operation, OperationStepSnapshot Step, Func<bool> CheckpointRequested);

/// <summary>Simulation/side-effect-free test boundary only. No filesystem runner is installed in production.</summary>
public interface IOperationStepRunner
{
    Task<OperationStepResult> RunAsync(OperationStepContext context);
}

/// <summary>Quick side-effect-free checks outside the state lock. Future resource checks must be implemented before real mutation.</summary>
public interface IOperationEligibility
{
    OperationEligibility Check(OperationRecord operation, OperationEligibilityReason reason);
}

public static class OperationTransitions
{
    public static bool IsTerminal(OperationState state) => state is OperationState.Succeeded or OperationState.Failed or OperationState.Canceled;

    public static bool CanTransition(OperationState from, OperationState to) => (from, to) switch
    {
        (OperationState.Queued, OperationState.Running or OperationState.Paused or OperationState.Canceled) => true,
        (OperationState.Running, OperationState.Queued or OperationState.Pausing or OperationState.CancellationRequested
            or OperationState.AwaitingInput or OperationState.Succeeded or OperationState.Failed or OperationState.NeedsRecovery) => true,
        (OperationState.Pausing, OperationState.Paused or OperationState.CancellationRequested or OperationState.AwaitingInput
            or OperationState.Failed or OperationState.NeedsRecovery) => true,
        (OperationState.CancellationRequested, OperationState.Canceled or OperationState.Failed or OperationState.NeedsRecovery) => true,
        (OperationState.Paused, OperationState.Queued or OperationState.Canceled) => true,
        (OperationState.AwaitingInput, OperationState.Queued or OperationState.Paused or OperationState.Canceled) => true,
        _ => false,
    };
}