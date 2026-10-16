using System.Collections.Frozen;
using System.Text;

namespace Disorderly.Routing;

[Flags]
public enum GestureModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Meta = 8,
}

/// <summary>A logical key supplied by a future layout-aware adapter, never a scan code.</summary>
public readonly struct LogicalGesture : IEquatable<LogicalGesture>
{
    private const GestureModifiers SupportedModifiers = GestureModifiers.Control | GestureModifiers.Shift | GestureModifiers.Alt | GestureModifiers.Meta;
    private static readonly FrozenSet<string> NamedKeys = new[]
    {
        "ESCAPE", "TAB", "BACKSPACE", "ENTER", "SPACE", "DELETE", "INSERT", "HOME", "END",
        "PAGEUP", "PAGEDOWN", "LEFT", "RIGHT", "UP", "DOWN",
    }.Concat(Enumerable.Range(1, 24).Select(static number => $"F{number}")).ToFrozenSet(StringComparer.Ordinal);

    public LogicalGesture(string key, GestureModifiers modifiers = GestureModifiers.None)
    {
        this.Key = key?.ToUpperInvariant();
        this.Modifiers = modifiers;
    }

    public string? Key { get; }
    public GestureModifiers Modifiers { get; }
    public static LogicalGesture Escape => new("Escape");

    public bool IsValid => (this.Modifiers & ~SupportedModifiers) == 0
        && this.Key is { Length: > 0 }
        && (NamedKeys.Contains(this.Key) || (Rune.TryGetRuneAt(this.Key, 0, out Rune rune)
            && rune.Utf16SequenceLength == this.Key.Length && !Rune.IsControl(rune) && !Rune.IsWhiteSpace(rune)));

    public bool Equals(LogicalGesture other) => StringComparer.Ordinal.Equals(this.Key, other.Key) && this.Modifiers == other.Modifiers;
    public override bool Equals(object? obj) => obj is LogicalGesture other && this.Equals(other);

    // Stable across processes as well as independent of modifier input ordering.
    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            foreach (char character in this.Key ?? string.Empty)
            {
                hash = (hash * 31) + character;
            }

            return (hash * 31) + (int)this.Modifiers;
        }
    }

    public override string ToString() => $"{(this.Modifiers.HasFlag(GestureModifiers.Control) ? "Control+" : "")}{(this.Modifiers.HasFlag(GestureModifiers.Shift) ? "Shift+" : "")}{(this.Modifiers.HasFlag(GestureModifiers.Alt) ? "Alt+" : "")}{(this.Modifiers.HasFlag(GestureModifiers.Meta) ? "Meta+" : "")}{this.Key ?? "<invalid>"}";
    public static bool operator ==(LogicalGesture left, LogicalGesture right)
    {
        return left.Equals(right);
    }
    public static bool operator !=(LogicalGesture left, LogicalGesture right)
    {
        return !left.Equals(right);
    }
}