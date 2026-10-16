using System.Collections.Generic;
using Disorderly.Routing;
using NUnit.Framework;
using static Disorderly.Testing.Routing.RoutingTestData;

namespace Disorderly.Testing.Routing;

[TestFixture]
public class RoutingPublicationTests
{
    [Test]
    public void InvalidReplacementPreservesPublishedSnapshotAndExistingSession()
    {
        RoutingConfigurationStore store = new();
        Assert.Throws<InvalidOperationException>(() => store.CreateSession());
        Assert.That(store.Publish(Graph()).IsValid, Is.True);
        RoutingSnapshot original = store.Current!;
        RoutingSession session = store.CreateSession();
        Assert.That(store.Publish(new RoutingConfiguration("missing", [])).IsValid, Is.False);
        Assert.That(store.Current, Is.SameAs(original));
        Assert.That(store.CreateSession().Snapshot, Is.SameAs(original));
        Assert.That(session.Route(new LogicalGesture("C")), Is.EqualTo(new RoutingResult.CommandRequested("root-command", "root-c", "root")));
    }

    [Test]
    public void SuccessfulPublicationChangesOnlyNewSessionsIncludingAfterBackToRoot()
    {
        RoutingConfigurationStore store = new();
        store.Publish(Graph());
        RoutingSession existing = store.CreateSession();
        existing.Route(new LogicalGesture("D"));
        RoutingConfiguration replacement = new("root", [new ContextDefinition("root", "Renamed", [Command("root", "new", "C", "new-command")])]);
        Assert.That(store.Publish(replacement).IsValid, Is.True);
        existing.Back();
        Assert.That(existing.Route(new LogicalGesture("C")), Is.EqualTo(new RoutingResult.CommandRequested("root-command", "root-c", "root")));
        RoutingSession latest = store.CreateSession();
        Assert.That(latest.ActiveContext.DisplayName, Is.EqualTo("Renamed"));
        Assert.That(latest.Route(new LogicalGesture("C")), Is.EqualTo(new RoutingResult.CommandRequested("new-command", "new", "root")));
        Assert.That(latest.Snapshot, Is.Not.SameAs(existing.Snapshot));
    }

    [Test]
    public void RetainedSourceCollectionsAndExposedCollectionsCannotChangeSnapshot()
    {
        List<BindingDefinition> bindings = [Command("root", "original", "A", "original-command")];
        List<ContextDefinition> contexts = [new("root", "Root", bindings)];
        RoutingConfigurationStore store = new();
        store.Publish(new RoutingConfiguration("root", contexts));
        RoutingSession session = store.CreateSession();
        bindings[0] = Command("root", "replacement", "A", "replacement-command");
        bindings.Add(Command("root", "added", "B", "added-command"));
        contexts.Clear();
        Assert.That(session.Route(new LogicalGesture("A")), Is.EqualTo(new RoutingResult.CommandRequested("original-command", "original", "root")));
        Assert.That(session.Route(new LogicalGesture("B")), Is.TypeOf<RoutingResult.NoMatch>());
        IList<BindingDefinition> exposed = (IList<BindingDefinition>)session.AvailableBindings;
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
        Assert.That(session.NavigationPath, Is.EqualTo(new[] { "root" }));
        Assert.That(store.CreateSession().AvailableBindings, Has.Count.EqualTo(1));
    }
}