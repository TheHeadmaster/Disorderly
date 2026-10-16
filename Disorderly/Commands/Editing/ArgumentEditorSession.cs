using System.Collections.Immutable;
using System.Globalization;

namespace Disorderly.Commands.Editing;

public abstract record ArgumentEdit
{
    public sealed record RawText(string Text) : ArgumentEdit;
    public sealed record Boolean(bool? Value) : ArgumentEdit;
    public sealed record Clear : ArgumentEdit;
    public sealed record Caret(int Start, int Length) : ArgumentEdit;
}

public sealed record ParameterInputState(ParameterDefinition Parameter, bool IsPreset, bool IsSupplied,
    string RawText, bool? BooleanValue, ImmutableArray<CommandDiagnostic> InputErrors)
{
    public string Label => $"{this.Parameter.Name} · {this.Parameter.ValueType} · {(this.Parameter.Required ? "Required" : "Optional")}{(this.IsPreset ? " · Preset (read-only in this version)" : "")}";
}

public sealed record ArgumentEditPosition(string ParameterId, int Start, int Length);

public sealed record ArgumentEditingSnapshot(Guid SessionId, long Revision, CommandInvocation Invocation,
    ImmutableArray<ParameterInputState> Parameters, bool HasMeaningfulChanges, bool IsReadOnly, ArgumentEditPosition? Position = null)
{
    public bool HasInvalidInput => this.Parameters.Any(static parameter => !parameter.InputErrors.IsEmpty);
    public bool CanSubmit => !this.IsReadOnly && !this.HasInvalidInput && this.Invocation.IsExecutionReady;
    public ImmutableArray<CommandDiagnostic> Diagnostics => [.. this.Invocation.Validation.Diagnostics
        .Where(diagnostic => !this.Parameters.Any(parameter => parameter.Parameter.Id == diagnostic.ParameterId && !parameter.InputErrors.IsEmpty))
        .Concat(this.Parameters.SelectMany(static parameter => parameter.InputErrors))];
}

/// <summary>One interaction-thread editor. Only missing/user-entered assignments are editable in this slice.</summary>
public sealed class ArgumentEditorSession
{
    private readonly List<ParameterInputState> parameters;
    private CommandInvocation invocation;
    private long revision;
    private bool meaningful;
    private bool readOnly;
    private ArgumentEditPosition? position;

    public ArgumentEditorSession(CommandInvocation invocation)
    {
        this.invocation = invocation;
        this.Id = Guid.NewGuid();
        this.parameters = invocation.Command.Parameters.Select(parameter =>
        {
            ArgumentValue? value = invocation.Arguments.Assignments.FirstOrDefault(assignment => assignment.ParameterId == parameter.Id)?.Value;
            string raw = value switch
            {
                ArgumentValue.Text text => text.Value,
                ArgumentValue.Path path => path.Value,
                ArgumentValue.Integer integer => integer.Value.ToString(CultureInfo.InvariantCulture),
                _ => string.Empty,
            };
            return new ParameterInputState(parameter, invocation.Origin is InvocationOrigin.Binding && value is not null,
                value is not null, raw, (value as ArgumentValue.Boolean)?.Value, []);
        }).ToList();
    }

    private ArgumentEditorSession(ArgumentEditingSnapshot snapshot)
    {
        this.Id = Guid.NewGuid(); // A future restoration gets a fresh editing-event epoch, not a new invocation.
        this.invocation = snapshot.Invocation;
        this.parameters = snapshot.Parameters.ToList();
        this.meaningful = snapshot.HasMeaningfulChanges;
        this.readOnly = snapshot.IsReadOnly;
        this.position = snapshot.Position;
    }

    public Guid Id { get; }
    public ArgumentEditingSnapshot Snapshot => new(this.Id, this.revision, this.invocation, [.. this.parameters], this.meaningful, this.readOnly, this.position);
    public static ArgumentEditorSession Restore(ArgumentEditingSnapshot snapshot) => new(snapshot);

    public void SetReadOnly(bool value)
    {
        if (this.readOnly != value)
        {
            this.readOnly = value;
            this.revision++;
        }
    }

    public bool TryApply(Guid sessionId, long expectedRevision, string parameterId, ArgumentEdit edit)
    {
        int index = this.parameters.FindIndex(parameter => parameter.Parameter.Id == parameterId);
        if (sessionId != this.Id || expectedRevision != this.revision || index < 0)
        {
            return false;
        }

        ParameterInputState previous = this.parameters[index];
        if (edit is ArgumentEdit.Caret caret)
        {
            if (caret.Start < 0 || caret.Length < 0)
            {
                return false;
            }

            int start = Math.Min(caret.Start, previous.RawText.Length);
            this.position = new(parameterId, start, Math.Min(caret.Length, previous.RawText.Length - start));
            return true; // Focus/selection metadata never qualifies work or revises arguments.
        }

        if (this.readOnly || previous.IsPreset)
        {
            return false;
        }

        if (edit is ArgumentEdit.Boolean && previous.Parameter.ValueType != ParameterValueType.Boolean)
        {
            return false;
        }

        ArgumentValue? value = null;
        CommandDiagnostic? parseError = null;
        ParameterInputState next;
        if (edit is ArgumentEdit.Clear or ArgumentEdit.Boolean { Value: null })
        {
            next = previous with { IsSupplied = false, RawText = string.Empty, BooleanValue = null, InputErrors = [] };
        }
        else if (edit is ArgumentEdit.Boolean boolean && previous.Parameter.ValueType == ParameterValueType.Boolean)
        {
            value = new ArgumentValue.Boolean(boolean.Value!.Value);
            next = previous with { IsSupplied = true, BooleanValue = boolean.Value, InputErrors = [] };
        }
        else if (edit is ArgumentEdit.RawText raw && raw.Text is not null && previous.Parameter.ValueType != ParameterValueType.Boolean)
        {
            next = previous with { IsSupplied = true, RawText = raw.Text, InputErrors = [] };
            switch (previous.Parameter.ValueType)
            {
                case ParameterValueType.Text: value = new ArgumentValue.Text(raw.Text); break;
                case ParameterValueType.Path: value = new ArgumentValue.Path(raw.Text); break;
                case ParameterValueType.Integer:
                    bool syntax = IsIntegerText(raw.Text);
                    if (syntax && long.TryParse(raw.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
                    {
                        value = new ArgumentValue.Integer(integer);
                    }
                    else
                    {
                        parseError = new(syntax ? CommandDiagnosticCode.IntegerOverflow : CommandDiagnosticCode.InvalidIntegerText,
                            syntax ? "Integer is outside the signed 64-bit range." : "Enter a signed whole number using digits 0–9 (no spaces or fractions).",
                            this.invocation.Command.Id, ParameterId: parameterId);
                    }

                    break;
            }
        }
        else
        {
            return false;
        }

        if (value is not null)
        {
            // Domain validation owns value structure/type rules, including opaque Path validation.
            ArgumentValidationResult validation = ArgumentValidator.Validate(this.invocation.Command, new ArgumentSet([new(parameterId, value)]), false);
            if (!validation.Diagnostics.IsEmpty)
            {
                parseError = previous.Parameter.ValueType == ParameterValueType.Path
                    ? new(CommandDiagnosticCode.InvalidPathText, "Path must be nonblank and contain no NUL characters.", this.invocation.Command.Id, ParameterId: parameterId)
                    : validation.Diagnostics[0];
                value = null;
            }
        }

        next = next with { InputErrors = parseError is null ? [] : [parseError] };
        if (next.IsSupplied == previous.IsSupplied && next.RawText == previous.RawText && next.BooleanValue == previous.BooleanValue
            && next.InputErrors.SequenceEqual(previous.InputErrors))
        {
            return false;
        }

        ArgumentSet arguments = new(this.invocation.Arguments.Assignments.Where(assignment => assignment.ParameterId != parameterId)
            .Concat(value is null ? [] : new[] { new ArgumentAssignment(parameterId, value) }));
        this.invocation = this.invocation.WithArguments(arguments);
        this.parameters[index] = next;
        this.meaningful = true;
        this.revision++;
        return true;
    }

    private static bool IsIntegerText(string text)
    {
        int start = text.StartsWith('+') || text.StartsWith('-') ? 1 : 0;
        return text.Length > start && text[start..].All(char.IsAsciiDigit);
    }
}