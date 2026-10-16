using Disorderly.Routing;
using NUnit.Framework;
using static Disorderly.Testing.Routing.RoutingTestData;

namespace Disorderly.Testing.Routing;

[TestFixture]
public class RoutingSessionTests
{
    [Test]
    public void RootMayBeEmptyAndItsIdentityDoesNotDependOnItsName()
    {
        RoutingSession session = Session(new RoutingConfiguration("entry", [new ContextDefinition("entry", "Not Root", [])]));
        Assert.That(session.ActiveContext.Id, Is.EqualTo("entry"));
        Assert.That(session.AvailableBindings, Is.Empty);
        Assert.That(session.NavigationPath, Is.EqualTo(new[] { "entry" }));
        Assert.That(session.Route(LogicalGesture.Escape), Is.TypeOf<RoutingResult.DismissRequested>());
        Assert.That(session.NavigationPath, Has.Length.EqualTo(1));
    }

    [Test]
    public void RoutingIsContextLocalAndCommandRequestHasIdentityWithoutChangingHistory()
    {
        RoutingSession session = Session();
        Assert.That(session.Route(new LogicalGesture("C")), Is.EqualTo(new RoutingResult.CommandRequested("root-command", "root-c", "root")));
        Assert.That(session.Route(new LogicalGesture("D")), Is.EqualTo(new RoutingResult.ContextEntered("development", "dev")));
        Assert.That(session.Route(new LogicalGesture("C")), Is.EqualTo(new RoutingResult.CommandRequested("dev-command", "dev-c", "development")));
        Assert.That(session.Route(new LogicalGesture("W")), Is.TypeOf<RoutingResult.NoMatch>()); // No parent inheritance.
        Assert.That(session.Route(new LogicalGesture("Q")), Is.TypeOf<RoutingResult.NoMatch>());
        Assert.That(session.Route(new LogicalGesture("Backspace")), Is.TypeOf<RoutingResult.NoMatch>());
        Assert.That(session.Route(default), Is.TypeOf<RoutingResult.NoMatch>());
        Assert.That(session.NavigationPath, Is.EqualTo(new[] { "root", "development" }));
        Assert.That(session.Route(new LogicalGesture("S")), Is.TypeOf<RoutingResult.ContextEntered>());
        Assert.That(session.NavigationPath, Is.EqualTo(new[] { "root", "development", "servers" }));
        Assert.That(session.Route(LogicalGesture.Escape), Is.EqualTo(new RoutingResult.ContextReturned("development")));
        Assert.That(session.Back(), Is.EqualTo(new RoutingResult.ContextReturned("root")));
        Assert.That(session.Back(), Is.TypeOf<RoutingResult.DismissRequested>());
    }

    [TestCase("D", "development")]
    [TestCase("W", "work")]
    public void SharedContextReturnsThroughActualEntryPath(string entry, string previous)
    {
        RoutingSession session = Session();
        session.Route(new LogicalGesture(entry));
        session.Route(new LogicalGesture("S"));
        Assert.That(session.ActiveContext.Id, Is.EqualTo("servers"));
        Assert.That(session.AvailableBindings.Select(static binding => binding.Id), Is.EqualTo(new[] { "server-a", "server-c" }));
        Assert.That(session.Back(), Is.EqualTo(new RoutingResult.ContextReturned(previous)));
        Assert.That(session.Back(), Is.EqualTo(new RoutingResult.ContextReturned("root")));
    }

    [Test]
    public void EnumerationIsDeterministicLocalAndIndependentOfInputOrder()
    {
        RoutingConfiguration original = Graph();
        RoutingConfiguration reversed = original with
        {
            Contexts = original.Contexts.Reverse().Select(static context => context with { Bindings = context.Bindings.Reverse().ToArray() }).ToArray(),
        };
        RoutingSession first = Session(original);
        RoutingSession second = Session(reversed);
        Assert.That(first.AvailableBindings.Select(static binding => binding.Id), Is.EqualTo(second.AvailableBindings.Select(static binding => binding.Id)));
        Assert.That(first.AvailableBindings.Select(static binding => binding.Id), Is.EqualTo(new[] { "root-c", "dev", "work" }));
        Assert.That(first.AvailableBindings[1].Destination, Is.EqualTo(new BindingDestination.Context("development")));
    }

    [Test]
    public void ModifiedEscapeTabAndBackspaceRemainOrdinaryBindings()
    {
        RoutingSession session = Session(new RoutingConfiguration("root", [Context("root",
            Command("root", "modified-escape", "Escape", "escape-command", GestureModifiers.Control),
            Command("root", "tab", "Tab", "tab-command"), Command("root", "backspace", "Backspace", "backspace-command"))]));
        Assert.That(session.Route(new LogicalGesture("Escape", GestureModifiers.Control)), Is.TypeOf<RoutingResult.CommandRequested>());
        Assert.That(session.Route(new LogicalGesture("Tab")), Is.TypeOf<RoutingResult.CommandRequested>());
        Assert.That(session.Route(new LogicalGesture("Backspace")), Is.TypeOf<RoutingResult.CommandRequested>());
        Assert.That(session.Route(LogicalGesture.Escape), Is.TypeOf<RoutingResult.DismissRequested>());
    }
}