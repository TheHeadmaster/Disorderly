using System.Collections.Immutable;
using Disorderly.Execution;

namespace Disorderly.Commands.Editing;

public sealed record InvocationDraft(Guid Id, ArgumentEditingSnapshot Editing,
    ExecutionReadyRequest? PreviousAttempt, ExecutionResult? LastExecution,
    long Generation = 0, CommandConfigurationSnapshot? Configuration = null,
    ImmutableArray<string> NavigationPath = default);

public interface IInvocationDraftStore
{
    ImmutableArray<InvocationDraft> Drafts { get; }
    InvocationDraft? Find(Guid id);
    InvocationDraft? Take(Guid id, long generation);
    bool Discard(Guid id, long generation);
    void Complete(Guid invocationId);
    InvocationDraft? Retain(ArgumentEditingSnapshot editing, ExecutionReadyRequest? previousAttempt = null, ExecutionResult? lastExecution = null,
        CommandConfigurationSnapshot? configuration = null, ImmutableArray<string> navigationPath = default);
}

/// <summary>Process-local transfer of suspended editors. Active leases preserve identity without appearing in the available list.</summary>
public sealed class InvocationDraftStore : IInvocationDraftStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, InvocationDraft> drafts = [];
    private readonly HashSet<Guid> active = [];
    private long sequence;

    public ImmutableArray<InvocationDraft> Drafts
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.drafts.Values.Where(draft => !this.active.Contains(draft.Id)).OrderByDescending(draft => draft.Generation).ThenBy(draft => draft.Id)];
            }
        }
    }

    public InvocationDraft? Find(Guid id)
    {
        lock (this.gate)
        {
            return this.drafts.Values.FirstOrDefault(draft => draft.Id == id && !this.active.Contains(id));
        }
    }

    public InvocationDraft? Take(Guid id, long generation)
    {
        lock (this.gate)
        {
            InvocationDraft? draft = this.Find(id);
            if (draft is null || draft.Generation != generation)
            {
                return null;
            }

            this.active.Add(id);
            return draft;
        }
    }

    public bool Discard(Guid id, long generation)
    {
        lock (this.gate)
        {
            InvocationDraft? draft = this.Find(id);
            return draft is not null && draft.Generation == generation && this.drafts.Remove(draft.Editing.Invocation.Id);
        }
    }

    public void Complete(Guid invocationId)
    {
        lock (this.gate)
        {
            if (this.drafts.Remove(invocationId, out InvocationDraft? draft))
            {
                this.active.Remove(draft.Id);
            }
        }
    }

    public InvocationDraft? Retain(ArgumentEditingSnapshot editing, ExecutionReadyRequest? previousAttempt = null, ExecutionResult? lastExecution = null,
        CommandConfigurationSnapshot? configuration = null, ImmutableArray<string> navigationPath = default)
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
            InvocationDraft? existing = this.drafts.GetValueOrDefault(editing.Invocation.Id);
            Guid id = existing?.Id ?? Guid.NewGuid();
            InvocationDraft draft = new(id, editing, previousAttempt, lastExecution, checked(++this.sequence),
                configuration ?? existing?.Configuration, navigationPath.IsDefault ? existing?.NavigationPath ?? default : navigationPath);
            this.drafts[editing.Invocation.Id] = draft;
            this.active.Remove(id);
            return draft;
        }
    }
}