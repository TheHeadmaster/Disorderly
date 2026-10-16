using Disorderly.Routing;
using NUnit.Framework;

namespace Disorderly.Testing.Routing;

[TestFixture]
public class LogicalGestureTests
{
    [Test]
    public void ModifiersAndKeyCasingNormalizeForEqualityHashingAndPresentation()
    {
        LogicalGesture first = new("c", GestureModifiers.Control | GestureModifiers.Shift);
        LogicalGesture second = new("C", GestureModifiers.Shift | GestureModifiers.Control);
        Assert.That(first, Is.EqualTo(second));
        Assert.That(first.GetHashCode(), Is.EqualTo(second.GetHashCode()));
        Assert.That(first.GetHashCode(), Is.EqualTo(18417));
        Assert.That(first.ToString(), Is.EqualTo("Control+Shift+C"));
        Assert.That(new LogicalGesture("C", GestureModifiers.Control), Is.Not.EqualTo(first));
        Assert.That(new LogicalGesture(">"), Is.Not.EqualTo(new LogicalGesture(".", GestureModifiers.Shift)));
        Assert.That(new LogicalGesture("é").Key, Is.EqualTo("É"));
    }

    [TestCase("")]
    [TestCase(" ")]
    [TestCase(" C")]
    [TestCase("Control+C")]
    [TestCase("UnknownKey")]
    [TestCase("F25")]
    [TestCase("\n")]
    public void MalformedOrUnsupportedKeysAreInvalid(string key) => Assert.That(new LogicalGesture(key).IsValid, Is.False);

    [Test]
    public void DefaultNullSurrogateAndUnknownModifiersAreInvalid()
    {
        Assert.That(default(LogicalGesture).IsValid, Is.False);
        Assert.That(new LogicalGesture(null!).IsValid, Is.False);
        Assert.That(new LogicalGesture("\uD800").IsValid, Is.False);
        Assert.That(new LogicalGesture("A", (GestureModifiers)64).IsValid, Is.False);
    }

    [TestCase("Escape")]
    [TestCase("Tab")]
    [TestCase("Backspace")]
    [TestCase("F24")]
    [TestCase("A")]
    [TestCase(">")]
    [TestCase("😀")]
    public void SupportedLogicalKeysAreValid(string key) => Assert.That(new LogicalGesture(key).IsValid, Is.True);
}