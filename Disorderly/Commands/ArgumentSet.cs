using System.Collections.Immutable;

namespace Disorderly.Commands;

public abstract record ArgumentValue
{
    private protected ArgumentValue() { }

    public sealed record Text(string Value) : ArgumentValue;
    public sealed record Boolean(bool Value) : ArgumentValue;
    public sealed record Integer(long Value) : ArgumentValue;
    // Opaque path text: constructing this value never resolves a filesystem object.
    public sealed record Path(string Value) : ArgumentValue;
}

public sealed record ArgumentAssignment(string ParameterId, ArgumentValue Value);

/// <summary>Preserves duplicates and malformed entries for validation; never silently overwrites assignments.</summary>
public sealed class ArgumentSet
{
    public ArgumentSet(IEnumerable<ArgumentAssignment> assignments)
    {
        this.IsWellFormed = assignments is not null;
        this.Assignments = (assignments ?? []).OrderBy(static assignment => assignment?.ParameterId, StringComparer.Ordinal).ToImmutableArray();
    }

    public static ArgumentSet Empty { get; } = new([]);
    public ImmutableArray<ArgumentAssignment> Assignments { get; }
    internal bool IsWellFormed { get; }
}