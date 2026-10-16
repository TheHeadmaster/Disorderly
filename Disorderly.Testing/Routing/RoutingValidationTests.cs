using Disorderly.Routing;
using NUnit.Framework;
using static Disorderly.Testing.Routing.RoutingTestData;

namespace Disorderly.Testing.Routing;

[TestFixture]
public class RoutingValidationTests
{
    [Test]
    public void DuplicateNamesAndRenamingDoNotAffectReferences()
    {
        RoutingConfiguration graph = Graph();
        RoutingConfiguration renamed = graph with { Contexts = graph.Contexts.Select(static context => context with { DisplayName = "Same name" }).ToArray() };
        RoutingValidationResult result = new RoutingValidator().Validate(renamed);
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Diagnostics, Is.Empty);
        RoutingSession session = new(result.Snapshot!);
        session.Route(new LogicalGesture("D"));
        Assert.That(session.ActiveContext.Id, Is.EqualTo("development"));
    }

    [Test]
    public void MissingRootDuplicateContextAndDuplicateBindingAreFatal()
    {
        AssertError(new RoutingConfiguration("missing", [Context("root")]), RoutingDiagnosticCode.MissingRoot);
        AssertError(new RoutingConfiguration("root", [Context("root"), Context("root")]), RoutingDiagnosticCode.DuplicateContext);
        AssertError(new RoutingConfiguration("root", [Context("root", Command("root", "same", "A", "one")), Context("other", Command("other", "same", "B", "two"))]), RoutingDiagnosticCode.DuplicateBinding);
    }

    [Test]
    public void DuplicateNormalizedGesturesAreRejectedEvenForIdenticalDestinations()
    {
        RoutingConfiguration configuration = new("root", [Context("root", Command("root", "first", "c", "same", GestureModifiers.Shift | GestureModifiers.Control),
            Command("root", "second", "C", "same", GestureModifiers.Control | GestureModifiers.Shift))]);
        RoutingValidationResult result = AssertError(configuration, RoutingDiagnosticCode.DuplicateGesture);
        RoutingDiagnostic diagnostic = result.Diagnostics.Single(static item => item.Code == RoutingDiagnosticCode.DuplicateGesture);
        Assert.That(diagnostic.ContextId, Is.EqualTo("root"));
        Assert.That(diagnostic.BindingId, Is.EqualTo("second"));
        Assert.That(diagnostic.Gesture, Is.EqualTo(new LogicalGesture("C", GestureModifiers.Control | GestureModifiers.Shift)));
        Assert.That(new RoutingValidator().Validate(Graph()).IsValid, Is.True); // C in different contexts is valid.
    }

    [Test]
    public void TransitionAndCommandCannotShareAGesture()
    {
        AssertError(new RoutingConfiguration("root", [Context("root", Enter("root", "enter", "A", "other"), Command("root", "command", "A", "opaque")), Context("other")]), RoutingDiagnosticCode.DuplicateGesture);
    }

    [Test]
    public void EscapeIsReservedButOtherExactGesturesCanBeAddedPerMode()
    {
        RoutingConfiguration escape = new("root", [Context("root", Command("root", "escape", "Escape", "command"))]);
        AssertError(escape, RoutingDiagnosticCode.ReservedGesture);
        RoutingConfiguration tab = new("root", [Context("root", Command("root", "tab", "Tab", "command"))]);
        Assert.That(new RoutingValidator().Validate(tab).IsValid, Is.True);
        Assert.That(new RoutingValidator([new LogicalGesture("Tab")]).Validate(tab).Diagnostics.Any(static item => item.Code == RoutingDiagnosticCode.ReservedGesture), Is.True);
        Assert.Throws<ArgumentException>(() => new RoutingValidator([default]));
    }

    [Test]
    public void MissingContextHasStructuredDestinationDiagnostic()
    {
        RoutingValidationResult result = AssertError(new RoutingConfiguration("root", [Context("root", Enter("root", "missing", "M", "absent"))]), RoutingDiagnosticCode.MissingContext);
        RoutingDiagnostic diagnostic = result.Diagnostics.Single(static item => item.Code == RoutingDiagnosticCode.MissingContext);
        Assert.That(diagnostic.DestinationId, Is.EqualTo("absent"));
        Assert.That(diagnostic.BindingId, Is.EqualTo("missing"));
    }

    [Test]
    public void UnreachableContextsAreRetainedWithWarnings()
    {
        RoutingValidationResult result = new RoutingValidator().Validate(new RoutingConfiguration("root", [Context("root"), Context("unused")]));
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Diagnostics.Single(), Is.EqualTo(new RoutingDiagnostic(RoutingDiagnosticSeverity.Warning, RoutingDiagnosticCode.UnreachableContext, "This context is not reachable from Root.", "unused")));
        Assert.That(result.Snapshot!.GetContext("unused").Id, Is.EqualTo("unused"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CyclesAreRejectedWhetherReachableOrNot(bool unreachable)
    {
        List<ContextDefinition> contexts =
        [
            Context("a", Enter("a", "a-b", "B", "b")),
            Context("b", Enter("b", "b-c", "C", "c")),
            Context("c", Enter("c", "c-a", "A", "a")),
        ];
        if (unreachable)
        {
            contexts.Add(Context("root"));
        }

        AssertError(new RoutingConfiguration(unreachable ? "root" : "a", contexts), RoutingDiagnosticCode.ContextCycle);
        AssertError(new RoutingConfiguration("self", [Context("self", Enter("self", "self-link", "S", "self"))]), RoutingDiagnosticCode.ContextCycle);
    }

    [Test]
    public void LongAcyclicGraphDoesNotDependOnRecursiveStackDepth()
    {
        ContextDefinition[] contexts = Enumerable.Range(0, 4000).Select(index => index == 3999
            ? Context($"c{index}") : Context($"c{index}", Enter($"c{index}", $"b{index}", "A", $"c{index + 1}"))).ToArray();
        Assert.That(new RoutingValidator().Validate(new RoutingConfiguration("c0", contexts)).IsValid, Is.True);
    }

    [TestCase("")]
    [TestCase("bad id")]
    [TestCase("/path")]
    public void MalformedIdentifiersAreRejected(string id)
    {
        AssertError(new RoutingConfiguration(id, [Context(id)]), RoutingDiagnosticCode.InvalidIdentifier);
        AssertError(new RoutingConfiguration("root", [Context("root", Command("root", id, "A", "command"))]), RoutingDiagnosticCode.InvalidIdentifier);
        AssertError(new RoutingConfiguration("root", [Context("root", Command("root", "binding", "A", id))]), RoutingDiagnosticCode.InvalidDestination);
    }

    [Test]
    public void InvalidGesturesOwnersDestinationsAndCollectionsDoNotPublish()
    {
        AssertError(new RoutingConfiguration("root", [Context("root", Command("root", "bad-key", "UnknownKey", "command"))]), RoutingDiagnosticCode.InvalidGesture);
        AssertError(new RoutingConfiguration("root", [Context("root", Command("wrong", "bad-owner", "A", "command"))]), RoutingDiagnosticCode.InvalidBindingOwner);
        AssertError(new RoutingConfiguration("root", [Context("root", new BindingDefinition("none", "root", new LogicalGesture("A"), null!))]), RoutingDiagnosticCode.InvalidDestination);
        AssertError(new RoutingConfiguration("root", [Context("root", Enter("root", "bad-target", "A", "bad id"))]), RoutingDiagnosticCode.InvalidDestination);
        AssertError(new RoutingConfiguration("root", [Context("root", null!)]), RoutingDiagnosticCode.InvalidConfiguration);
        AssertError(new RoutingConfiguration("root", [new ContextDefinition("root", "Root", null!)]), RoutingDiagnosticCode.InvalidConfiguration);
        AssertError(new RoutingConfiguration("root", null!), RoutingDiagnosticCode.InvalidConfiguration);
        AssertError(null!, RoutingDiagnosticCode.InvalidConfiguration);
        AssertError(new RoutingConfiguration("root", [Context("root"), null!]), RoutingDiagnosticCode.InvalidIdentifier);
        AssertError(new RoutingConfiguration(null!, [Context("root")]), RoutingDiagnosticCode.MissingRoot);
        AssertError(new RoutingConfiguration("root", [new ContextDefinition("root", " ", [])]), RoutingDiagnosticCode.InvalidDisplayName);
        AssertError(new RoutingConfiguration("root", [Context("root", new BindingDefinition("default", "root", default, new BindingDestination.Command("command")))]), RoutingDiagnosticCode.InvalidGesture);
    }

    private static RoutingValidationResult AssertError(RoutingConfiguration configuration, RoutingDiagnosticCode code)
    {
        RoutingValidationResult result = new RoutingValidator().Validate(configuration);
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Snapshot, Is.Null);
        Assert.That(result.Diagnostics.Any(diagnostic => diagnostic.Code == code && diagnostic.Severity == RoutingDiagnosticSeverity.Error), Is.True, code.ToString());
        return result;
    }
}