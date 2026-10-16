using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Execution;
using NUnit.Framework;

namespace Disorderly.Testing.Commands.Editing;

[TestFixture]
public class InvocationDraftTests
{
    [Test]
    public void MeaningfulRawWorkIsRetainedIndependentlyAndCanReconstructAnEditor()
    {
        InvocationDraftStore store = new();
        ArgumentEditorSession first = EditingFixture.Session();
        EditingFixture.Apply(first, "text", new ArgumentEdit.Caret(0, 0));
        Assert.That(store.Retain(first.Snapshot), Is.Null, "Focus alone is not meaningful argument work.");
        EditingFixture.Apply(first, "count", new ArgumentEdit.RawText("-"));
        EditingFixture.Apply(first, "count", new ArgumentEdit.Caret(1, 0));
        InvocationDraft draft = store.Retain(first.Snapshot)!;
        EditingFixture.Apply(first, "count", new ArgumentEdit.RawText("17"));
        Assert.That(draft.Editing.Parameters[2].RawText, Is.EqualTo("-"));
        Assert.That(store.Retain(first.Snapshot)!.Id, Is.EqualTo(draft.Id));
        ArgumentEditorSession second = EditingFixture.Session();
        EditingFixture.Apply(second, "text", new ArgumentEdit.RawText("independent"));
        store.Retain(second.Snapshot);
        Assert.That(store.Drafts.Length, Is.EqualTo(2));
        Assert.That(store.Drafts.Select(value => value.Editing.Invocation.Id).Distinct().Count(), Is.EqualTo(2));
        ArgumentEditorSession restored = ArgumentEditorSession.Restore(draft.Editing);
        Assert.That(restored.Id, Is.Not.EqualTo(draft.Editing.SessionId));
        Assert.That(restored.Snapshot.Invocation.Id, Is.EqualTo(first.Snapshot.Invocation.Id));
        Assert.That(restored.Snapshot.Invocation.Catalog, Is.SameAs(first.Snapshot.Invocation.Catalog));
        Assert.That(restored.Snapshot.Parameters[2].RawText, Is.EqualTo("-"));
        Assert.That(restored.Snapshot.Parameters[^1].IsPreset, Is.True);
        Assert.That(restored.Snapshot.CanSubmit, Is.False);
        Assert.That(restored.Snapshot.Position, Is.EqualTo(new ArgumentEditPosition("count", 1, 0)));
    }

    [TestCase(ExecutionOutcome.Succeeded)]
    [TestCase(ExecutionOutcome.Uncertain)]
    [TestCase(ExecutionOutcome.AlreadyAccepted)]
    public void AcceptedOrPossiblyExecutedWorkDoesNotBecomeAnUnfinishedDraft(ExecutionOutcome outcome)
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText("work"));
        ExecutionReadyRequest request = editor.Snapshot.Invocation.PrepareAttempt().Request!;
        InvocationDraftStore store = new();
        Assert.That(store.Retain(editor.Snapshot, request), Is.Null, "Pending work is already an execution attempt.");
        Assert.That(store.Retain(editor.Snapshot, request, ExecutionResult.For(request, outcome, "Outcome", "Result")), Is.Null);
        Assert.That(store.Drafts, Is.Empty);
    }

    [Test]
    public void NonStartFailureDraftRetainsAttemptProvenanceForFutureExplicitRestoration()
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        EditingFixture.Apply(editor, "text", new ArgumentEdit.RawText("work"));
        ExecutionReadyRequest request = editor.Snapshot.Invocation.PrepareAttempt().Request!;
        InvocationDraftStore store = new();
        InvocationDraft draft = store.Retain(editor.Snapshot, request,
            ExecutionResult.For(request, ExecutionOutcome.FailedNotStarted, "NotStarted", "Known failure"))!;
        Assert.That(draft.PreviousAttempt, Is.SameAs(request));
        Assert.That(draft.LastExecution!.CanRetry, Is.True);
    }
}