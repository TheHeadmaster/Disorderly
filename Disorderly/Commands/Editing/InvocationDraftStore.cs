using System.Collections.Immutable;
using Disorderly.Execution;

namespace Disorderly.Commands.Editing;

public sealed record InvocationDraft(Guid Id, ArgumentEditingSnapshot Editing,
    ExecutionReadyRequest? PreviousAttempt, ExecutionResult? LastExecution);

public interface IInvocationDraftStore
{
    ImmutableArray<InvocationDraft> Drafts { get; }
    InvocationDraft? Retain(ArgumentEditingSnapshot editing, ExecutionReadyRequest? previousAttempt = null, ExecutionResult? lastExecution = null);
}

/// <summary>In-memory only, without expiry, persistence or a user-facing restoration workflow.</summary>
public sealed class InvocationDraftStore : IInvocationDraftStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, InvocationDraft> drafts = [];

    public ImmutableArray<InvocationDraft> Drafts
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.drafts.Values];
            }
        }
    }

    public InvocationDraft? Retain(ArgumentEditingSnapshot editing, ExecutionReadyRequest? previousAttempt = null, ExecutionResult? lastExecution = null)
    {
        if (!editing.HasMeaningfulChanges || (previousAttempt is not null
            && (lastExecution is null || lastExecution.Outcome is not (ExecutionOutcome.FailedNotStarted or ExecutionOutcome.Rejected)
                || previousAttempt.InvocationId != editing.Invocation.Id || lastExecution.InvocationId != editing.Invocation.Id
                || lastExecution.AttemptId != previousAttempt.AttemptId || lastExecution.CommandId != editing.Invocation.Command.Id
                || !ReferenceEquals(previousAttempt.Invocation.Catalog, editing.Invocation.Catalog))))
        {
            return null;
        }

        lock (this.gate)
        {
            Guid id = this.drafts.GetValueOrDefault(editing.Invocation.Id)?.Id ?? Guid.NewGuid();
            InvocationDraft draft = new(id, editing, previousAttempt, lastExecution);
            this.drafts[editing.Invocation.Id] = draft;
            return draft;
        }
    }
}