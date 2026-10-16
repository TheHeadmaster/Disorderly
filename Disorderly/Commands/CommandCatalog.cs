using System.Collections.Frozen;
using System.Collections.Immutable;
using Disorderly.Domain;

namespace Disorderly.Commands;

public sealed class CommandCatalogSnapshot
{
    private readonly FrozenDictionary<string, CommandDefinition> commands;

    internal CommandCatalogSnapshot(IEnumerable<CommandDefinition> definitions)
    {
        this.Definitions = definitions.OrderBy(static definition => definition.Id, StringComparer.Ordinal)
            .Select(static definition => definition with { Parameters = definition.Parameters.ToImmutableArray() }).ToImmutableArray();
        this.commands = this.Definitions.ToFrozenDictionary(static definition => definition.Id, StringComparer.Ordinal);
    }

    public ImmutableArray<CommandDefinition> Definitions { get; }
    public CommandDefinition? Find(string commandId) => commandId is not null ? this.commands.GetValueOrDefault(commandId) : null;
}

public sealed class CommandCatalogValidationResult
{
    internal CommandCatalogValidationResult(IEnumerable<CommandDiagnostic> diagnostics, CommandCatalogSnapshot? snapshot)
    {
        this.Diagnostics = CommandDiagnostic.Order(diagnostics);
        this.Snapshot = snapshot;
    }

    public ImmutableArray<CommandDiagnostic> Diagnostics { get; }
    public CommandCatalogSnapshot? Snapshot { get; }
    public bool IsValid => this.Snapshot is not null;
}

public sealed class CommandCatalogValidator
{
    public CommandCatalogValidationResult Validate(IReadOnlyList<CommandDefinition> definitions)
    {
        List<CommandDiagnostic> diagnostics = [];
        List<CommandDefinition> copied = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (definitions is null)
        {
            return new CommandCatalogValidationResult([new(CommandDiagnosticCode.InvalidDefinition, "A command collection is required (and may be empty).")], null);
        }

        foreach (CommandDefinition? proposed in definitions.ToArray())
        {
            if (proposed is null)
            {
                diagnostics.Add(new(CommandDiagnosticCode.InvalidDefinition, "A command definition cannot be null."));
                continue;
            }

            if (!StableIdentifier.IsValid(proposed.Id))
            {
                diagnostics.Add(new(CommandDiagnosticCode.InvalidIdentifier, "A command needs a valid stable identifier.", proposed.Id));
            }
            else if (!ids.Add(proposed.Id))
            {
                diagnostics.Add(new(CommandDiagnosticCode.DuplicateCommand, "Command identifiers must be unique.", proposed.Id));
            }

            if (string.IsNullOrWhiteSpace(proposed.DisplayName) || proposed.Parameters is null)
            {
                diagnostics.Add(new(CommandDiagnosticCode.InvalidDefinition, "A command needs a display name and parameter collection.", proposed.Id));
            }

            CommandDefinition command = proposed with { Parameters = proposed.Parameters?.ToImmutableArray() ?? [] };
            copied.Add(command);
            HashSet<string> parameterIds = new(StringComparer.Ordinal);
            foreach (ParameterDefinition? parameter in command.Parameters)
            {
                if (parameter is null || !StableIdentifier.IsValid(parameter.Id)
                    || string.IsNullOrWhiteSpace(parameter.Name) || !Enum.IsDefined(parameter.ValueType))
                {
                    diagnostics.Add(new(CommandDiagnosticCode.InvalidParameter, "A parameter needs a valid ID, name and supported value type.", command.Id, ParameterId: parameter?.Id));
                }
                else if (!parameterIds.Add(parameter.Id))
                {
                    diagnostics.Add(new(CommandDiagnosticCode.DuplicateParameter, "Parameter identifiers must be unique within their command.", command.Id, ParameterId: parameter.Id));
                }
            }

            if (command.DefaultCompletionPolicy is CompletionPolicy policy && !Enum.IsDefined(policy))
            {
                diagnostics.Add(new(CommandDiagnosticCode.InvalidCompletionPolicy, "Unsupported command completion policy.", command.Id));
            }

            bool validIcon = command.Icon switch
            {
                null or CommandIcon.Automatic => true,
                CommandIcon.CustomFile icon => ArgumentValidator.IsPathText(icon.Path),
                CommandIcon.BuiltIn icon => StableIdentifier.IsValid(icon.Id),
                _ => false,
            };
            if (!validIcon)
            {
                diagnostics.Add(new(CommandDiagnosticCode.InvalidIcon, "Icon metadata needs a supported mode and meaningful path or built-in identifier.", command.Id));
            }
        }

        return new CommandCatalogValidationResult(diagnostics, diagnostics.Count == 0 ? new CommandCatalogSnapshot(copied) : null);
    }
}