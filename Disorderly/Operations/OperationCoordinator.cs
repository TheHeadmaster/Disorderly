using System.Collections.Concurrent;
using System.Collections.Immutable;
using Disorderly.Domain;
using Serilog;

namespace Disorderly.Operations;

/// <summary>Volatile operation store and explicit scheduler. No worker loop, persistence or production filesystem executor.</summary>
public sealed class OperationCoordinator
{
    private sealed class Entry(OperationId id, OperationDefinition definition, long order)
    {
        public OperationId Id { get; } = id;
        public OperationDefinition Definition { get; } = definition;
        public long CreationOrder { get; } = order;
        public long QueueOrder { get; set; } = order;
        public long Revision { get; set; }
        public OperationState State { get; private set; } = OperationState.Queued;
        public int Position { get; set; }
        public bool Pause { get; set; }
        public bool Cancel { get; set; }
        public bool Manual { get; set; }
        public bool Safe { get; set; } = true;
        public OperationDiagnostic? Diagnostic { get; set; }
        public List<OperationInputRequest> Inputs { get; } = [];
        public Dictionary<Guid, string> Controls { get; } = [];
        public OperationStepState[] StepStates { get; } = definition.Steps.Select(_ => OperationStepState.Pending).ToArray();
        public int[] DispatchCounts { get; } = new int[definition.Steps.Count];

        public void Move(OperationState state)
        {
            if (!OperationTransitions.CanTransition(this.State, state))
            {
                throw new InvalidOperationException($"Invalid internal operation transition: {this.State} to {state}.");
            }

            this.State = state;
        }
    }

    private readonly Lock gate = new();
    private readonly SemaphoreSlim scheduling = new(1, 1);
    private readonly Dictionary<OperationId, Entry> operations = [];
    private readonly HashSet<OperationId> checking = [];
    private readonly IOperationStepRunner runner;
    private readonly IOperationEligibility eligibility;
    private readonly int manualResumeQuota;
    private readonly ConcurrentQueue<OperationActivitySnapshot> observations = new();
    private readonly TaskCompletionSource shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private OperationId? mutationOwner;
    private long order;
    private long activityRevision;
    private int priorityStreak;
    private int inFlight;
    private int notifying;
    private bool stopping;

    private sealed class SimulationEligibility : IOperationEligibility
    {
        public OperationEligibility Check(OperationRecord operation, OperationEligibilityReason reason) => new(true);
    }

    public OperationCoordinator(IOperationStepRunner runner, IOperationEligibility? eligibility = null, int manualResumeQuota = 2)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(manualResumeQuota);
        this.runner = runner;
        this.eligibility = eligibility ?? new SimulationEligibility();
        this.manualResumeQuota = manualResumeQuota;
    }

    /// <summary>Ordered immutable observations, delivered outside the state lock. Observer failures are isolated.</summary>
    public event Action<OperationActivitySnapshot>? ActivityChanged;

    public OperationActivitySnapshot Activity
    {
        get
        {
            lock (this.gate)
            {
                return this.ActivityLocked();
            }
        }
    }

    public ImmutableArray<OperationRecord> Operations
    {
        get
        {
            lock (this.gate)
            {
                return [.. this.operations.Values.OrderBy(entry => entry.CreationOrder).Select(this.Snapshot)];
            }
        }
    }

    public OperationRecord? Find(OperationId id)
    {
        lock (this.gate)
        {
            return this.operations.TryGetValue(id, out Entry? entry) ? this.Snapshot(entry) : null;
        }
    }

    public OperationInputRequest? FindInput(OperationId id, OperationInputId requestId) => this.Find(id)?.InputRequests.FirstOrDefault(request => request.Id == requestId);

    /// <summary>Explicitly accepts simulated/side-effect-free work only. A future durable boundary must precede real mutation.</summary>
    public OperationId AcceptSimulation(OperationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        OperationStepDefinition[] steps = definition.Steps?.ToArray() ?? throw new ArgumentException("An ordered step plan is required.", nameof(definition));
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.InvocationId == Guid.Empty || definition.AttemptId == Guid.Empty
            || steps.Any(step => step is null || step.Id.Value == Guid.Empty || string.IsNullOrWhiteSpace(step.Description))
            || steps.Select(step => step.Id).Distinct().Count() != steps.Length)
        {
            throw new ArgumentException("Simulation metadata and step identities must be valid and unique.", nameof(definition));
        }

        OperationId id = OperationId.New();
        lock (this.gate)
        {
            if (this.stopping)
            {
                throw new InvalidOperationException("Operation acceptance has stopped for shutdown.");
            }

            if (this.operations.Values.SelectMany(entry => entry.Definition.Steps).Any(step => steps.Any(candidate => candidate.Id == step.Id)))
            {
                throw new ArgumentException("Step identities cannot belong to two operations.", nameof(definition));
            }

            this.operations.Add(id, new Entry(id, definition with { Steps = steps.ToImmutableArray() }, checked(++this.order)));
            this.PublishLocked();
        }

        this.Notify();
        return id;
    }

    public OperationControlResult Pause(OperationId id, Guid requestId) => this.Control(id, requestId, false);
    public OperationControlResult Cancel(OperationId id, Guid requestId) => this.Control(id, requestId, true);

    private OperationControlResult Control(OperationId id, Guid requestId, bool cancel)
    {
        OperationControlResult result;
        lock (this.gate)
        {
            if (!this.operations.TryGetValue(id, out Entry? entry) || requestId == Guid.Empty)
            {
                return new(false, false, "UnknownRequest");
            }

            string kind = cancel ? "Cancel" : "Pause";
            if (entry.Controls.TryGetValue(requestId, out string? previous))
            {
                return new(previous == kind, false, "AlreadyProcessed");
            }

            if (OperationTransitions.IsTerminal(entry.State) || entry.State == OperationState.NeedsRecovery || (!cancel && entry.Cancel))
            {
                return new(false, false, "LifecycleBlocked");
            }

            entry.Controls.Add(requestId, kind);
            bool changed = cancel ? !entry.Cancel : !entry.Pause;
            if (changed)
            {
                this.SetIntent(entry, cancel);
                entry.Revision++;
                this.PublishLocked();
            }

            result = new(true, changed, changed ? "Requested" : "AlreadyRequested");
        }

        this.Notify();
        return result;
    }

    private void SetIntent(Entry entry, bool cancel)
    {
        entry.Manual = false;
        if (cancel)
        {
            entry.Cancel = true;
            entry.Pause = false;
            if (entry.State is OperationState.Running or OperationState.Pausing)
            {
                entry.Move(OperationState.CancellationRequested);
            }
            else
            {
                entry.Move(OperationState.Canceled);
                this.InvalidateInputs(entry);
            }
        }
        else
        {
            entry.Pause = true;
            if (entry.State == OperationState.Queued || (entry.State == OperationState.AwaitingInput
                && !entry.Inputs.Any(input => input.State == OperationInputState.Outstanding)))
            {
                entry.Move(OperationState.Paused);
            }
            else if (entry.State == OperationState.Running)
            {
                entry.Move(OperationState.Pausing);
            }
        }
    }

    public OperationControlResult Resume(OperationId id, Guid requestId)
    {
        OperationRecord snapshot;
        lock (this.gate)
        {
            if (!this.operations.TryGetValue(id, out Entry? entry) || requestId == Guid.Empty || this.stopping)
            {
                return new(false, false, "UnknownOrStopping");
            }

            if (entry.Controls.TryGetValue(requestId, out string? previous))
            {
                return new(previous == "Resume", false, "AlreadyProcessed");
            }

            if (entry.State != OperationState.Paused || entry.Inputs.Any(input => input.State == OperationInputState.Outstanding))
            {
                return new(false, false, "NotEligiblePaused");
            }

            snapshot = this.Snapshot(entry);
        }

        OperationEligibility eligible = this.Check(snapshot, OperationEligibilityReason.ManualResume);
        OperationControlResult result;
        lock (this.gate)
        {
            Entry entry = this.operations[id];
            if (this.stopping || entry.Revision != snapshot.Revision || entry.State != OperationState.Paused)
            {
                return new(false, false, "StateChanged");
            }

            entry.Diagnostic = eligible.Diagnostic ?? (!eligible.Eligible ? new OperationDiagnostic("EligibilityBlocked", "Continuation eligibility was not established.") : null);
            if (eligible.Eligible)
            {
                entry.Controls.Add(requestId, "Resume");
                entry.Pause = false;
                this.Enqueue(entry, true);
            }

            entry.Revision++;
            this.PublishLocked();
            result = new(eligible.Eligible, eligible.Eligible, eligible.Eligible ? "Resumed" : "RevalidationBlocked");
        }

        this.Notify();
        return result;
    }

    public OperationControlResult Respond(OperationId id, OperationInputId requestId, OperationResponse response)
    {
        long revision;
        lock (this.gate)
        {
            if (!this.operations.TryGetValue(id, out Entry? entry) || entry.State != OperationState.AwaitingInput || !Enum.IsDefined(response))
            {
                return new(false, false, "NotAwaitingInput");
            }

            int index = entry.Inputs.FindIndex(input => input.Id == requestId && input.State == OperationInputState.Outstanding);
            if (index < 0 || !entry.Inputs[index].AllowedResponses.Contains(response))
            {
                return new(false, false, "InvalidResponse");
            }

            entry.Inputs[index] = entry.Inputs[index] with { State = OperationInputState.Resolved, Response = response };
            if (entry.Pause && !entry.Inputs.Any(input => input.State == OperationInputState.Outstanding))
            {
                entry.Move(OperationState.Paused);
            }

            revision = ++entry.Revision;
            this.PublishLocked();
        }

        this.Notify();
        this.Reevaluate(id, revision);
        return new(true, true, "ResponseAccepted");
    }

    /// <summary>Recheck input-resolved work after an external eligibility condition changes. This grants no manual priority.</summary>
    public bool Reevaluate(OperationId id) => this.Reevaluate(id, null);

    private bool Reevaluate(OperationId id, long? expectedRevision)
    {
        OperationRecord snapshot;
        lock (this.gate)
        {
            if (!this.operations.TryGetValue(id, out Entry? entry) || this.stopping || entry.State != OperationState.AwaitingInput
                || entry.Pause || entry.Cancel || entry.Inputs.Any(input => input.State == OperationInputState.Outstanding)
                || (expectedRevision is long revision && revision != entry.Revision))
            {
                return false;
            }

            snapshot = this.Snapshot(entry);
        }

        OperationEligibility eligible = this.Check(snapshot, OperationEligibilityReason.InputResolved);
        lock (this.gate)
        {
            Entry entry = this.operations[id];
            if (this.stopping || entry.Revision != snapshot.Revision)
            {
                return false;
            }

            entry.Diagnostic = eligible.Diagnostic ?? (!eligible.Eligible ? new OperationDiagnostic("EligibilityBlocked", "Continuation eligibility was not established.") : null);
            if (eligible.Eligible)
            {
                this.Enqueue(entry, false);
            }

            entry.Revision++;
            this.PublishLocked();
        }

        this.Notify();
        return eligible.Eligible;
    }

    /// <summary>Dispatch at most one safe segment. Concurrent calls may run independent non-mutating segments.</summary>
    public async Task<bool> RunNextAsync()
    {
        HashSet<OperationId> considered = [];
        Entry? selected = null;
        OperationStepContext? context = null;
        await this.scheduling.WaitAsync().ConfigureAwait(false);
        try
        {
            while (selected is null)
            {
                OperationRecord candidate;
                lock (this.gate)
                {
                    if (this.stopping)
                    {
                        return false;
                    }

                    Entry[] available = this.operations.Values.Where(entry => entry.State == OperationState.Queued && !this.checking.Contains(entry.Id)
                        && !considered.Contains(entry.Id) && (entry.Position == entry.Definition.Steps.Count
                            || !entry.Definition.Steps[entry.Position].RequiresMutationSlot || this.mutationOwner is null)).OrderBy(entry => entry.QueueOrder).ToArray();
                    Entry? normal = available.FirstOrDefault(entry => !entry.Manual);
                    Entry? manual = available.FirstOrDefault(entry => entry.Manual);
                    Entry? next = manual is not null && (this.priorityStreak < this.manualResumeQuota || normal is null) ? manual : normal;
                    if (next is null)
                    {
                        return false;
                    }

                    considered.Add(next.Id);
                    this.checking.Add(next.Id);
                    candidate = this.Snapshot(next);
                }

                OperationEligibility eligible = this.Check(candidate, OperationEligibilityReason.Dispatch);
                lock (this.gate)
                {
                    this.checking.Remove(candidate.Id);
                    Entry entry = this.operations[candidate.Id];
                    if (!this.stopping && entry.State == OperationState.Queued && entry.Revision == candidate.Revision)
                    {
                        if (eligible.Eligible && (entry.Position == entry.Definition.Steps.Count
                            || !entry.Definition.Steps[entry.Position].RequiresMutationSlot || this.mutationOwner is null))
                        {
                            this.priorityStreak = entry.Manual ? Math.Min(this.manualResumeQuota, this.priorityStreak + 1) : 0;
                            entry.Manual = false;
                            entry.Diagnostic = null;
                            entry.Move(OperationState.Running);
                            selected = entry;
                            if (entry.Position == entry.Definition.Steps.Count)
                            {
                                entry.Move(OperationState.Succeeded);
                            }
                            else
                            {
                                OperationStepDefinition step = entry.Definition.Steps[entry.Position];
                                if (step.RequiresMutationSlot)
                                {
                                    this.mutationOwner = entry.Id;
                                }

                                entry.Safe = false;
                                entry.StepStates[entry.Position] = OperationStepState.Running;
                                entry.DispatchCounts[entry.Position]++;
                                this.inFlight++;
                                context = new(this.Snapshot(entry), new(entry.Id, step, entry.Position, OperationStepState.Running, entry.DispatchCounts[entry.Position]),
                                    () => this.CheckpointRequested(entry.Id));
                            }
                        }
                        else if (!eligible.Eligible)
                        {
                            entry.Diagnostic = eligible.Diagnostic ?? new("EligibilityBlocked", "Resource eligibility must be revalidated.");
                        }

                        entry.Revision++;
                        if (context is not null)
                        {
                            context = context with { Operation = this.Snapshot(entry) };
                        }

                        this.PublishLocked();
                    }

                    this.CheckShutdownLocked();
                }
            }
        }
        finally
        {
            this.scheduling.Release();
            this.Notify();
        }

        if (context is null)
        {
            return true;
        }

        OperationStepResult result;
        try
        {
            result = await this.runner.RunAsync(context).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Log.Warning("Simulated operation {OperationId}, step {StepId}, raised {ExceptionType}.", selected.Id, context.Step.Definition.Id, error.GetType().Name);
            result = new(OperationStepOutcome.Uncertain, false, Diagnostic: new("RunnerException", "Step outcome and safe finalization could not be established.", true));
        }

        lock (this.gate)
        {
            this.Finish(selected, result);
            this.inFlight--;
            selected.Revision++;
            this.PublishLocked();
            this.CheckShutdownLocked();
        }

        this.Notify();
        return true;
    }

    private void Finish(Entry entry, OperationStepResult? result)
    {
        bool safe = result?.SafeCheckpoint == true;
        entry.Safe = safe;
        if (safe && this.mutationOwner == entry.Id)
        {
            this.mutationOwner = null;
        }

        if (result is null || !safe || !Enum.IsDefined(result.Outcome)
            || (result.Outcome == OperationStepOutcome.Completed && !result.Verified)
            || (result.Outcome != OperationStepOutcome.Completed && result.Verified)
            || (result.Outcome != OperationStepOutcome.AwaitInput && result.Requests?.Count > 0)
            || (result.Outcome != OperationStepOutcome.Uncertain && result.Diagnostic?.Uncertain == true))
        {
            this.Fail(entry, true, new("UnverifiedBoundary", "The step outcome or safe boundary was not established; reconciliation is required.", true));
            return;
        }

        if (result.Outcome is OperationStepOutcome.Uncertain or OperationStepOutcome.KnownFailure)
        {
            this.Fail(entry, result.Outcome == OperationStepOutcome.Uncertain, result.Diagnostic);
            return;
        }

        ImmutableArray<OperationInputRequest> requests = [];
        if (result.Outcome == OperationStepOutcome.AwaitInput)
        {
            if (!this.TryInputs(entry, result.Requests, out requests))
            {
                this.Fail(entry, true, new("InvalidInputContract", "The runner returned invalid input requests.", true));
                return;
            }
        }

        if (result.Outcome == OperationStepOutcome.Completed)
        {
            entry.StepStates[entry.Position++] = OperationStepState.Verified;
        }
        else
        {
            entry.StepStates[entry.Position] = result.Outcome == OperationStepOutcome.AwaitInput ? OperationStepState.AwaitingInput : OperationStepState.Continuing;
        }

        if (entry.Cancel)
        {
            entry.Move(OperationState.Canceled);
            this.InvalidateInputs(entry);
        }
        else if (result.Outcome == OperationStepOutcome.AwaitInput)
        {
            entry.Inputs.AddRange(requests);
            entry.Move(OperationState.AwaitingInput);
        }
        else if (entry.Pause || this.stopping)
        {
            entry.Move(OperationState.Paused);
        }
        else if (entry.Position == entry.Definition.Steps.Count)
        {
            entry.Move(OperationState.Succeeded);
        }
        else
        {
            this.Enqueue(entry, false);
        }
    }

    private bool TryInputs(Entry entry, IReadOnlyList<OperationInputDefinition>? definitions, out ImmutableArray<OperationInputRequest> requests)
    {
        requests = [];
        if (definitions is null || definitions.Count == 0)
        {
            return false;
        }

        List<OperationInputRequest> copied = [];
        foreach (OperationInputDefinition definition in definitions)
        {
            if (definition is null || definition.Id.Value == Guid.Empty || !StableIdentifier.IsValid(definition.Kind) || string.IsNullOrWhiteSpace(definition.Prompt)
                || definition.AllowedResponses is null || definition.AllowedResponses.Count == 0 || definition.AllowedResponses.Any(response => !Enum.IsDefined(response))
                || definition.AllowedResponses.Distinct().Count() != definition.AllowedResponses.Count
                || copied.Any(request => request.Id == definition.Id) || this.operations.Values.SelectMany(operation => operation.Inputs).Any(input => input.Id == definition.Id))
            {
                return false;
            }

            copied.Add(new(definition.Id, entry.Id, definition.Kind, definition.Prompt, [.. definition.AllowedResponses], OperationInputState.Outstanding));
        }

        requests = [.. copied];
        return true;
    }

    private void Fail(Entry entry, bool uncertain, OperationDiagnostic? diagnostic)
    {
        entry.StepStates[entry.Position] = uncertain ? OperationStepState.Uncertain : OperationStepState.Failed;
        entry.Diagnostic = diagnostic is null ? new(uncertain ? "Uncertain" : "KnownFailure", uncertain ? "Reconciliation is required; do not retry." : "The simulated step failed; no automatic retry.", uncertain)
            : diagnostic with { Uncertain = uncertain };
        entry.Move(uncertain ? OperationState.NeedsRecovery : OperationState.Failed);
        this.InvalidateInputs(entry);
    }

    private void InvalidateInputs(Entry entry)
    {
        for (int index = 0; index < entry.Inputs.Count; index++)
        {
            if (entry.Inputs[index].State == OperationInputState.Outstanding)
            {
                entry.Inputs[index] = entry.Inputs[index] with { State = OperationInputState.Invalidated };
            }
        }
    }

    private void Enqueue(Entry entry, bool manual)
    {
        entry.Move(OperationState.Queued);
        entry.Manual = manual;
        entry.QueueOrder = checked(++this.order);
    }

    private bool CheckpointRequested(OperationId id)
    {
        lock (this.gate)
        {
            Entry entry = this.operations[id];
            return this.stopping || entry.Pause || entry.Cancel;
        }
    }

    private OperationEligibility Check(OperationRecord snapshot, OperationEligibilityReason reason)
    {
        try
        {
            return this.eligibility.Check(snapshot, reason) ?? new(false, new("InvalidEligibility", "Eligibility was not established."));
        }
        catch (Exception error)
        {
            Log.Warning("Operation eligibility raised {ExceptionType} for {OperationId}.", error.GetType().Name, snapshot.Id);
            return new(false, new("EligibilityException", "Eligibility could not be established; execution remains blocked."));
        }
    }

    /// <summary>Stops acceptance/dispatch immediately and requests cooperative checkpoints. No timeout abandons unsafe ownership.</summary>
    public Task RequestShutdownAsync()
    {
        lock (this.gate)
        {
            if (!this.stopping)
            {
                this.stopping = true;
                foreach (Entry entry in this.operations.Values.Where(entry => entry.State is OperationState.Queued or OperationState.Running or OperationState.AwaitingInput))
                {
                    this.SetIntent(entry, false);
                    entry.Revision++;
                }

                this.PublishLocked();
            }

            this.CheckShutdownLocked();
        }

        this.Notify();
        return this.shutdown.Task;
    }

    private void CheckShutdownLocked()
    {
        if (this.stopping && this.inFlight == 0 && this.checking.Count == 0 && this.operations.Values.All(entry => entry.Safe))
        {
            this.shutdown.TrySetResult();
        }
    }

    private OperationRecord Snapshot(Entry entry) => new(entry.Id, entry.Definition.Name, entry.CreationOrder, entry.Revision,
        entry.Definition.InvocationId, entry.Definition.AttemptId, entry.State,
        [.. entry.Definition.Steps.Select((step, index) => new OperationStepSnapshot(entry.Id, step, index, entry.StepStates[index], entry.DispatchCounts[index]))],
        entry.Position, entry.StepStates.Count(state => state == OperationStepState.Verified), entry.Definition.ReportKnownTotal ? entry.Definition.Steps.Count : null,
        [.. entry.Inputs], entry.Pause, entry.Cancel, this.mutationOwner == entry.Id, entry.Safe, entry.Diagnostic);

    private OperationActivitySnapshot ActivityLocked() => new(this.activityRevision,
        [.. this.operations.Values.OrderBy(entry => entry.CreationOrder).Select(entry => new OperationActivityEntry(entry.Id, entry.Definition.Name, entry.State,
            entry.CreationOrder, entry.Position < entry.Definition.Steps.Count ? entry.Definition.Steps[entry.Position].Description : null,
            entry.StepStates.Count(state => state == OperationStepState.Verified), entry.Definition.ReportKnownTotal ? entry.Definition.Steps.Count : null,
            entry.Inputs.Count(input => input.State == OperationInputState.Outstanding), entry.Pause, entry.Cancel,
            entry.State is OperationState.AwaitingInput or OperationState.NeedsRecovery or OperationState.Failed || entry.Diagnostic is not null, entry.Diagnostic))]);

    private void PublishLocked()
    {
        this.activityRevision++;
        this.observations.Enqueue(this.ActivityLocked());
    }

    private void Notify()
    {
        if (Interlocked.CompareExchange(ref this.notifying, 1, 0) != 0)
        {
            return;
        }

        do
        {
            while (this.observations.TryDequeue(out OperationActivitySnapshot? snapshot))
            {
                foreach (Action<OperationActivitySnapshot> observer in this.ActivityChanged?.GetInvocationList().Cast<Action<OperationActivitySnapshot>>() ?? [])
                {
                    try
                    {
                        observer(snapshot);
                    }
                    catch (Exception error)
                    {
                        Log.Warning("Operation Activity observer raised {ExceptionType}.", error.GetType().Name);
                    }
                }
            }

            Interlocked.Exchange(ref this.notifying, 0);
        }
        while (!this.observations.IsEmpty && Interlocked.CompareExchange(ref this.notifying, 1, 0) == 0);
    }
}