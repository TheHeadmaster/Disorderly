using System.Collections.Concurrent;
using Disorderly.Operations;

namespace Disorderly.Testing.Operations;

internal static class OperationFixture
{
    internal sealed class Runner : IOperationStepRunner
    {
        internal ConcurrentQueue<OperationStepContext> Calls { get; } = new();
        internal Func<OperationStepContext, Task<OperationStepResult>> Execute { get; set; } = _ => Task.FromResult(OperationStepResult.Completed());
        public Task<OperationStepResult> RunAsync(OperationStepContext context)
        {
            this.Calls.Enqueue(context);
            return this.Execute(context);
        }
    }

    internal sealed class Pending
    {
        internal TaskCompletionSource<OperationStepContext> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<OperationStepResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<OperationStepResult> Run(OperationStepContext context)
        {
            this.Started.TrySetResult(context);
            return this.Completion.Task;
        }
    }

    internal sealed class Eligibility : IOperationEligibility
    {
        internal Func<OperationRecord, OperationEligibilityReason, OperationEligibility> Evaluate { get; set; } = (_, _) => new(true);
        internal ConcurrentQueue<OperationEligibilityReason> Reasons { get; } = new();
        public OperationEligibility Check(OperationRecord operation, OperationEligibilityReason reason)
        {
            this.Reasons.Enqueue(reason);
            return this.Evaluate(operation, reason);
        }
    }

    internal static OperationDefinition Plan(string name = "Simulation", int count = 1, bool mutation = false, bool knownTotal = true) => new(name,
        Enumerable.Range(0, count).Select(index => new OperationStepDefinition(OperationStepId.New(), $"Step {index}", mutation)).ToArray(), ReportKnownTotal: knownTotal);
    internal static OperationInputDefinition Input() => new(OperationInputId.New(), "decision", "A simulated decision is required.", [OperationResponse.Accept, OperationResponse.Decline]);
    internal static OperationStepResult Await(params OperationInputDefinition[] requests) => new(OperationStepOutcome.AwaitInput, true, Requests: requests);
    internal static Guid Request() => Guid.NewGuid();
}