namespace Disorderly.Commands;

public enum ParameterValueType
{
    Text,
    Boolean,
    Integer,
    Path,
}

public enum CompletionPolicy
{
    Dismiss,
    StayOpen,
}

public sealed record ParameterDefinition(string Id, string Name, ParameterValueType ValueType, bool Required = false, string? Description = null);

/// <summary>A reusable contract, never mutable invocation or execution state.</summary>
public sealed record CommandDefinition(string Id, string DisplayName, IReadOnlyList<ParameterDefinition> Parameters,
    string? Description = null, CompletionPolicy? DefaultCompletionPolicy = null, CommandIcon? Icon = null)
{
    public CommandIcon EffectiveIcon => this.Icon ?? new CommandIcon.Automatic();
}

public abstract record CommandIcon
{
    private protected CommandIcon() { }

    public sealed record Automatic : CommandIcon;
    public sealed record CustomFile(string Path) : CommandIcon;
    public sealed record BuiltIn(string Id) : CommandIcon;
}

/// <summary>Only command destinations can carry these values. Editing authorization is deferred.</summary>
public sealed record CommandBindingMetadata(ArgumentSet? Presets = null, CompletionPolicy? CompletionOverride = null);