using System.Collections.Immutable;

namespace Disorderly.Commands;

public enum CommandDiagnosticSeverity
{
    Error,
    Warning,
}

public enum CommandDiagnosticCode
{
    InvalidDefinition,
    InvalidExecutionSpecification,
    InvalidIdentifier,
    DuplicateCommand,
    InvalidParameter,
    DuplicateParameter,
    InvalidCompletionPolicy,
    InvalidIcon,
    InvalidArguments,
    InvalidIntegerText,
    IntegerOverflow,
    InvalidPathText,
    DuplicateArgument,
    InvalidValue,
    WrongType,
    UnknownParameter,
    MissingRequired,
    MissingCommand,
    InvalidOrigin,
}

public sealed record CommandDiagnostic(CommandDiagnosticCode Code, string Message, string? CommandId = null,
    string? BindingId = null, string? ParameterId = null, CommandDiagnosticSeverity Severity = CommandDiagnosticSeverity.Error)
{
    internal static ImmutableArray<CommandDiagnostic> Order(IEnumerable<CommandDiagnostic> diagnostics) => diagnostics
        .OrderBy(static diagnostic => diagnostic.CommandId, StringComparer.Ordinal)
        .ThenBy(static diagnostic => diagnostic.BindingId, StringComparer.Ordinal)
        .ThenBy(static diagnostic => diagnostic.ParameterId, StringComparer.Ordinal)
        .ThenBy(static diagnostic => diagnostic.Code)
        .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal).ToImmutableArray();
}