using System.Collections.Concurrent;
using Disorderly.Commands.Editing;
using NUnit.Framework;

namespace Disorderly.Testing.Commands.Editing;

[TestFixture]
public class DraftStoreLifecycleTests
{
    private static ArgumentEditingSnapshot Work()
    {
        ArgumentEditorSession editor = EditingFixture.Session();
        EditingFixture.Apply(editor, "count", new ArgumentEdit.RawText("-"));
        return editor.Snapshot;
    }

    [Test]
    public void RecencyTransferResuspensionAndDiscardHaveStableIdentities()
    {
        InvocationDraftStore store = new();
        ArgumentEditingSnapshot first = Work();
        InvocationDraft a = store.Retain(first)!;
        InvocationDraft b = store.Retain(Work())!;
        Assert.That(store.Drafts.Select(draft => draft.Id), Is.EqualTo(new[] { b.Id, a.Id }));
        Assert.That(store.Take(a.Id, a.Generation + 1), Is.Null);
        Assert.That(store.Take(a.Id, a.Generation), Is.EqualTo(a));
        Assert.That(store.Take(a.Id, a.Generation), Is.Null);
        Assert.That(store.Find(a.Id), Is.Null);
        Assert.That(store.Discard(a.Id, a.Generation), Is.False);
        InvocationDraft again = store.Retain(first)!;
        Assert.That(again.Id, Is.EqualTo(a.Id));
        Assert.That(again.Generation, Is.GreaterThan(b.Generation));
        Assert.That(store.Drafts[0], Is.EqualTo(again));
        Assert.That(store.Discard(a.Id, a.Generation), Is.False);
        Assert.That(store.Discard(a.Id, again.Generation), Is.True);
        Assert.That(store.Drafts.Single(), Is.EqualTo(b));
        store.Complete(b.Editing.Invocation.Id);
        Assert.That(store.Drafts, Is.Empty);
    }

    [Test]
    public void ConcurrentSuspensionsHaveUniqueMonotonicOrderAndSingleTake()
    {
        InvocationDraftStore store = new();
        ArgumentEditingSnapshot[] work = Enumerable.Range(0, 32).Select(_ => Work()).ToArray();
        Parallel.ForEach(work, editing => store.Retain(editing));
        Assert.That(store.Drafts.Length, Is.EqualTo(32));
        Assert.That(store.Drafts.Select(draft => draft.Generation), Is.Ordered.Descending);
        Assert.That(store.Drafts.Select(draft => draft.Generation), Is.Unique);
        InvocationDraft newest = store.Drafts[0];
        ConcurrentBag<InvocationDraft> taken = [];
        Parallel.For(0, 16, _ =>
        {
            if (store.Take(newest.Id, newest.Generation) is InvocationDraft draft)
            {
                taken.Add(draft);
            }
        });
        Assert.That(taken.Count, Is.EqualTo(1));
        store.Retain(newest.Editing);
        Assert.That(store.Drafts.Length, Is.EqualTo(32));
        Assert.That(store.Drafts[0].Id, Is.EqualTo(newest.Id));
    }

    [Test]
    public void RepeatedSuspensionDoesNotDuplicateAndCompletedActiveLeaseIsRemoved()
    {
        InvocationDraftStore store = new();
        ArgumentEditingSnapshot editing = Work();
        InvocationDraft original = store.Retain(editing)!;
        for (int index = 0; index < 100; index++)
        {
            Assert.That(store.Retain(editing)!.Id, Is.EqualTo(original.Id));
        }

        InvocationDraft latest = store.Drafts.Single();
        store.Take(latest.Id, latest.Generation);
        store.Complete(editing.Invocation.Id);
        Assert.That(store.Take(latest.Id, latest.Generation), Is.Null);
        Assert.That(store.Drafts, Is.Empty);
        Assert.That(store.Find(Guid.NewGuid()), Is.Null);
        Assert.That(store.Discard(Guid.NewGuid(), 0), Is.False);
    }
}