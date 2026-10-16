namespace Disorderly.Commands;

/// <summary>Immutable execution intent; validation and side effects have separate boundaries.</summary>
public abstract record CommandExecutionSpecification
{
    public sealed record LaunchExecutable(string ExecutableParameterId) : CommandExecutionSpecification;
}