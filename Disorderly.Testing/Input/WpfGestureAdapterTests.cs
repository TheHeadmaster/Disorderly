using System.Windows.Input;
using Disorderly.Input;
using Disorderly.Routing;
using NUnit.Framework;

namespace Disorderly.Testing.Input;

[TestFixture]
public class WpfGestureAdapterTests
{
    private sealed class Translator(string? text, bool dead = false) : IKeyboardTranslator
    {
        public KeyboardTranslation Translate(Key key) => new(text, dead);
    }

    [TestCase(Key.A, "a", ModifierKeys.None, "A")]
    [TestCase(Key.D1, "1", ModifierKeys.Control, "1")]
    [TestCase(Key.OemPeriod, ">", ModifierKeys.Shift, ">")]
    [TestCase(Key.OemPeriod, ":", ModifierKeys.Shift, ":")]
    [TestCase(Key.Y, "z", ModifierKeys.None, "Z")]
    public void EffectiveLayoutOutputDefinesLogicalKey(Key key, string text, ModifierKeys modifiers, string expected)
    {
        WpfGestureAdapter adapter = new(new Translator(text));
        Assert.That(adapter.TryTranslate(key, Key.None, modifiers, false, false, true, out LogicalGesture gesture), Is.True);
        Assert.That(gesture.Key, Is.EqualTo(expected));
        Assert.That(gesture.Modifiers.HasFlag(GestureModifiers.Shift), Is.EqualTo(modifiers.HasFlag(ModifierKeys.Shift)));
        Assert.That(gesture.Modifiers.HasFlag(GestureModifiers.Control), Is.EqualTo(modifiers.HasFlag(ModifierKeys.Control)));
    }

    [TestCase(Key.Escape, "ESCAPE")]
    [TestCase(Key.Back, "BACKSPACE")]
    [TestCase(Key.Tab, "TAB")]
    [TestCase(Key.F12, "F12")]
    [TestCase(Key.Left, "LEFT")]
    public void NamedKeysUseEstablishedCanonicalNames(Key key, string expected)
    {
        WpfGestureAdapter adapter = new(new Translator(null));
        Assert.That(adapter.TryTranslate(key, Key.None, ModifierKeys.None, false, false, true, out LogicalGesture gesture), Is.True);
        Assert.That(gesture.Key, Is.EqualTo(expected));
    }

    [Test]
    public void SystemKeyAndModifierFlagsAreRetained()
    {
        WpfGestureAdapter adapter = new(new Translator("s"));
        Assert.That(adapter.TryTranslate(Key.System, Key.S, ModifierKeys.Alt | ModifierKeys.Control, false, false, true, out LogicalGesture gesture), Is.True);
        Assert.That(gesture, Is.EqualTo(new LogicalGesture("S", GestureModifiers.Alt | GestureModifiers.Control)));
        Assert.That(adapter.TryTranslate(Key.Escape, Key.None, ModifierKeys.Control, false, false, true, out gesture), Is.True);
        Assert.That(gesture, Is.Not.EqualTo(LogicalGesture.Escape));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ab")]
    [TestCase("\u0001")]
    public void AmbiguousOrUnsupportedTextFailsSafely(string? text)
    {
        WpfGestureAdapter adapter = new(new Translator(text));
        Assert.That(adapter.TryTranslate(Key.A, Key.None, ModifierKeys.None, false, false, true, out _), Is.False);
    }

    [Test]
    public void RepeatCompositionAltGrAndTextOwnershipDoNotDispatch()
    {
        WpfGestureAdapter adapter = new(new Translator("a"));
        Assert.That(adapter.TryTranslate(Key.A, Key.None, ModifierKeys.None, true, false, true, out _), Is.False);
        Assert.That(adapter.TryTranslate(Key.A, Key.None, ModifierKeys.None, false, false, false, out _), Is.False);
        Assert.That(adapter.TryTranslate(Key.A, Key.None, ModifierKeys.Control | ModifierKeys.Alt, false, true, true, out _), Is.False);
        Assert.That(adapter.TryTranslate(Key.ImeProcessed, Key.None, ModifierKeys.None, false, false, true, out _), Is.False);
        Assert.That(adapter.TryTranslate(Key.DeadCharProcessed, Key.None, ModifierKeys.None, false, false, true, out _), Is.False);
        adapter.BeginComposition();
        Assert.That(adapter.TryTranslate(Key.A, Key.None, ModifierKeys.None, false, false, true, out _), Is.False);
        adapter.CompleteComposition();
        Assert.That(adapter.TryTranslate(Key.A, Key.None, ModifierKeys.None, false, false, true, out _), Is.True);
    }

    [Test]
    public void DeadKeySuppressesFollowupUntilTextCompositionCompletesOrActivationResets()
    {
        WpfGestureAdapter adapter = new(new Translator(null, true));
        Assert.That(adapter.TryTranslate(Key.OemQuotes, Key.None, ModifierKeys.None, false, false, true, out _), Is.False);
        Assert.That(adapter.TryTranslate(Key.Escape, Key.None, ModifierKeys.None, false, false, true, out _), Is.False);
        adapter.Reset();
        Assert.That(adapter.TryTranslate(Key.Escape, Key.None, ModifierKeys.None, false, false, true, out _), Is.True);
    }
}