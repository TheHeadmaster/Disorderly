using System.Collections.Immutable;
using Disorderly.Domain;

namespace Disorderly.Commands;

public enum ArgumentValidationStatus
{
    Valid,
    Incomplete,
    Invalid,
}

public sealed class ArgumentValidationResult
{
    internal ArgumentValidationResult(IEnumerable<CommandDiagnostic> diagnostics)
    {
        this.Diagnostics = CommandDiagnostic.Order(diagnostics);
        this.Status = this.Diagnostics.IsEmpty ? ArgumentValidationStatus.Valid
            : this.Diagnostics.All(static diagnostic => diagnostic.Code == CommandDiagnosticCode.MissingRequired)
                ? ArgumentValidationStatus.Incomplete : ArgumentValidationStatus.Invalid;
    }

    public ImmutableArray<CommandDiagnostic> Diagnostics { get; }
    public ArgumentValidationStatus Status { get; }
}

public static class ArgumentValidator
{
    /// <summary>The definition must come from a validated catalog. Presets may omit required parameters.</summary>
    public static ArgumentValidationResult Validate(CommandDefinition command, ArgumentSet arguments, bool requireRequired = true)
    {
        List<CommandDiagnostic> diagnostics = [];
        if (arguments is null || !arguments.IsWellFormed)
        {
            diagnostics.Add(new(CommandDiagnosticCode.InvalidArguments, "An argument assignment collection is required.", command.Id));
        }

        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (ArgumentAssignment? assignment in arguments?.Assignments ?? [])
        {
            if (assignment is null || !StableIdentifier.IsValid(assignment.ParameterId))
            {
                diagnostics.Add(new(CommandDiagnosticCode.InvalidArguments, "Assignments need valid parameter identifiers.", command.Id, ParameterId: assignment?.ParameterId));
                continue;
            }

            if (!present.Add(assignment.ParameterId))
            {
                diagnostics.Add(new(CommandDiagnosticCode.DuplicateArgument, "A parameter can be assigned only once.", command.Id, ParameterId: assignment.ParameterId));
            }

            ParameterDefinition? parameter = command.Parameters.FirstOrDefault(parameter => parameter.Id == assignment.ParameterId);
            if (parameter is null)
            {
                diagnostics.Add(new(CommandDiagnosticCode.UnknownParameter, "The command does not declare this parameter.", command.Id, ParameterId: assignment.ParameterId));
                continue;
            }

            bool structurallyValid = assignment.Value switch
            {
                ArgumentValue.Text value => value.Value is not null,
                ArgumentValue.Boolean or ArgumentValue.Integer => true,
                ArgumentValue.Path value => IsPathText(value.Value),
                _ => false,
            };
            if (!structurallyValid)
            {
                diagnostics.Add(new(CommandDiagnosticCode.InvalidValue, "An argument must contain a structurally valid typed value.", command.Id, ParameterId: parameter.Id));
                continue;
            }

            bool correctType = (parameter.ValueType, assignment.Value) switch
            {
                (ParameterValueType.Text, ArgumentValue.Text) or (ParameterValueType.Boolean, ArgumentValue.Boolean)
                    or (ParameterValueType.Integer, ArgumentValue.Integer) or (ParameterValueType.Path, ArgumentValue.Path) => true,
                _ => false,
            };
            if (!correctType)
            {
                diagnostics.Add(new(CommandDiagnosticCode.WrongType, "The argument type does not match the parameter contract.", command.Id, ParameterId: parameter.Id));
            }
        }

        if (requireRequired)
        {
            foreach (ParameterDefinition parameter in command.Parameters.Where(static parameter => parameter.Required))
            {
                if (!present.Contains(parameter.Id))
                {
                    diagnostics.Add(new(CommandDiagnosticCode.MissingRequired, "A required parameter has not been supplied.", command.Id, ParameterId: parameter.Id));
                }
            }
        }

        return new ArgumentValidationResult(diagnostics);
    }

    internal static bool IsPathText(string? path) => !string.IsNullOrWhiteSpace(path) && !path.Contains('\0');
}