using System.Collections.Immutable;
using System.Globalization;
using Disorderly.Commands;
using Disorderly.Routing;

namespace Disorderly.Configuration.Editing;
public readonly record struct ConfigurationEditToken(Guid SessionId, Guid SelectionId);
public sealed record ConfigurationFieldInput(string Raw, string? Error);
/// <summary>Single UI-thread owner of a copy-on-write proposal. Never publishes while editing.</summary>
public sealed class ConfigurationEditorSession
{
    private readonly ConfigurationService service;
    private ImmutableDictionary<string, ConfigurationFieldInput> inputs = ImmutableDictionary<string, ConfigurationFieldInput>.Empty;
    private CommandConfigurationSnapshot baseline;
    private bool ReconciliationRequired { get; set; }

    private Guid selectionId = Guid.NewGuid();
    public ConfigurationEditorSession(ConfigurationService service)
    {
        this.service = service;
        this.baseline = service.Store.Current ?? throw new InvalidOperationException("Publish the safe baseline first.");
        this.Document = ConfigurationDocument.FromSnapshot(this.baseline);
        this.BaselineRevision = service.Revision;
        this.Validate();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public long Revision { get; private set; }
    public long BaselineRevision { get; private set; }
    public ConfigurationDocument Document { get; private set; }
    public bool IsDirty { get; private set; }
    public bool IsSaving { get; private set; }
    public bool IsStale => this.BaselineRevision != this.service.Revision || !ReferenceEquals(this.baseline, this.service.Store.Current);
    public string? SelectedCommandId { get; private set; }
    public string? SelectedContextId { get; private set; }
    public string Status { get; private set; } = "Edits remain private until Save. Closing hides this window; Exit loses unsaved changes.";
    public ImmutableArray<ConfigurationDiagnostic> Diagnostics { get; private set; } = [];
    public bool CanSave => this.IsDirty && !this.IsSaving && !this.IsStale && !this.ReconciliationRequired && !this.Diagnostics.Any(issue => !issue.Warning);
    public ConfigurationEditToken Token => new(this.Id, this.selectionId);

    public ConfigurationFieldInput? Input(string key) => this.inputs.GetValueOrDefault(key);
    public ConfigurationEditToken Select(string? commandId, string? contextId)
    {
        if (this.IsSaving)
        {
            return this.Token;
        }

        this.SelectedCommandId = commandId;
        this.SelectedContextId = contextId;
        this.selectionId = Guid.NewGuid();
        return this.Token;
    }

    public bool SetInput(ConfigurationEditToken token, string key, string raw, string? error)
    {
        if (!this.Accepts(token))
        {
            return false;
        }

        this.inputs = this.inputs.SetItem(key, new(raw, error));
        this.Changed();
        return true;
    }

    public bool AddCommand(ConfigurationEditToken token, CommandDefinition command)
    {
        if (!this.Accepts(token) || this.Document.Commands.Any(item => item.Id == command.Id))
        {
            return false;
        }

        this.Document = this.Document with
        {
            Commands = this.Document.Commands.Append(Freeze(command)).ToImmutableArray()
        };
        this.Changed();
        return true;
    }

    public bool UpdateCommand(ConfigurationEditToken token, string id, CommandDefinition replacement)
    {
        if (!this.Accepts(token) || id != replacement.Id || !this.Document.Commands.Any(item => item.Id == id))
        {
            return false;
        }

        this.Document = this.Document with
        {
            Commands = this.Document.Commands.Select(item => item.Id == id ? Freeze(replacement) : item).ToImmutableArray()
        };
        this.Changed();
        return true;
    }

    public bool AddContext(ConfigurationEditToken token, ContextDefinition context)
    {
        if (!this.Accepts(token) || this.Document.Routing.Contexts.Any(item => item.Id == context.Id))
        {
            return false;
        }

        this.SetContexts(this.Document.Routing.Contexts.Append(Freeze(context)));
        return true;
    }

    public bool RenameContext(ConfigurationEditToken token, string id, string name)
    {
        if (!this.Accepts(token) || !this.Document.Routing.Contexts.Any(item => item.Id == id))
        {
            return false;
        }

        this.SetContexts(this.Document.Routing.Contexts.Select(item => item.Id == id ? item with { DisplayName = name } : item));
        return true;
    }

    public bool PutBinding(ConfigurationEditToken token, string contextId, BindingDefinition binding, bool add = false)
    {
        ContextDefinition? context = this.Document.Routing.Contexts.FirstOrDefault(item => item.Id == contextId);
        if (!this.Accepts(token) || context is null || binding.ContextId != contextId || (add == context.Bindings.Any(item => item.Id == binding.Id)))
        {
            return false;
        }

        ImmutableArray<BindingDefinition> bindings = add ? [..context.Bindings, binding] : [..context.Bindings.Select(item => item.Id == binding.Id ? binding : item)];
        this.SetContexts(this.Document.Routing.Contexts.Select(item => item.Id == contextId ? item with { Bindings = bindings } : item));
        return true;
    }

    public bool RemoveBinding(ConfigurationEditToken token, string contextId, string bindingId)
    {
        if (!this.Accepts(token))
        {
            return false;
        }

        this.ClearInputs($"binding/{bindingId}/");
        this.SetContexts(this.Document.Routing.Contexts.Select(item => item.Id == contextId ? item with { Bindings = item.Bindings.Where(binding => binding.Id != bindingId).ToImmutableArray() } : item));
        return true;
    }

    // Persisted definition deletion is deliberately deferred. Newly created, unreferenced items can be corrected.
    public bool RemoveNewCommand(ConfigurationEditToken token, string id)
    {
        if (!this.Accepts(token) || this.baseline.Catalog.Find(id)is not null || this.Document.Routing.Contexts.SelectMany(item => item.Bindings).Any(binding => binding.Destination is BindingDestination.Command command && command.CommandId == id))
        {
            return false;
        }

        this.ClearInputs($"command/{id}/");
        this.Document = this.Document with
        {
            Commands = this.Document.Commands.Where(item => item.Id != id).ToImmutableArray()
        };
        this.Changed();
        return true;
    }

    public bool RemoveNewContext(ConfigurationEditToken token, string id)
    {
        if (!this.Accepts(token) || id == this.Document.Routing.RootContextId || this.baseline.Routing.Contexts.Any(item => item.Id == id) || this.Document.Routing.Contexts.SelectMany(item => item.Bindings).Any(binding => binding.Destination is BindingDestination.Context target && target.ContextId == id))
        {
            return false;
        }

        foreach (BindingDefinition binding in this.Document.Routing.Contexts.First(item => item.Id == id).Bindings)
        {
            this.ClearInputs($"binding/{binding.Id}/");
        }

        this.SetContexts(this.Document.Routing.Contexts.Where(item => item.Id != id));
        return true;
    }

    public async Task<ConfigurationResult?> SaveAsync()
    {
        this.Validate();
        if (!this.CanSave)
        {
            return null;
        }

        this.IsSaving = true;
        this.Status = "Saving…";
        try
        {
            ConfigurationResult result = await this.service.SaveAsync(this.Document, this.BaselineRevision);
            if (result.Status == ConfigurationStatus.Saved)
            {
                this.baseline = this.service.Store.Current!;
                this.BaselineRevision = result.Revision;
                this.Document = ConfigurationDocument.FromSnapshot(this.baseline);
                this.IsDirty = false;
                this.inputs = ImmutableDictionary<string, ConfigurationFieldInput>.Empty;
                this.Status = "Saved. New palette activations use this configuration.";
            }
            else
            {
                this.ReconciliationRequired = result.Status is ConfigurationStatus.Conflict or ConfigurationStatus.ReplacementUncertain;
                this.Status = result.Status == ConfigurationStatus.ReplacementUncertain ? "Replacement uncertain. Keep these edits; inspect the file and Reload before another Save." : result.Replaced == true ? "File saved but not published. Keep these edits; Reload to reconcile." : $"{result.Status}: edits retained. Newer or invalid file content may exist. Inspect it, then Reload; Discard explicitly adopts the published configuration.";
            }

            return result;
        }
        catch
        {
            this.ReconciliationRequired = true;
            this.Status = "Save outcome could not be established. Edits retained; inspect the file and Reload before another Save.";
            throw;
        }
        finally
        {
            this.IsSaving = false;
            this.Validate();
        }
    }

    /// <summary>Caller must obtain explicit discard consent when dirty. Does not reload disk or alter publication.</summary>
    public bool AdoptPublished(bool discardConfirmed)
    {
        if (this.IsSaving || (this.IsDirty && !discardConfirmed))
        {
            return false;
        }

        this.baseline = this.service.Store.Current!;
        this.Document = ConfigurationDocument.FromSnapshot(this.baseline);
        this.BaselineRevision = this.service.Revision;
        this.inputs = ImmutableDictionary<string, ConfigurationFieldInput>.Empty;
        this.Id = Guid.NewGuid();
        this.selectionId = Guid.NewGuid();
        this.Revision++;
        this.IsDirty = false;
        this.ReconciliationRequired = false;
        this.Status = "Working copy refreshed from the published configuration. Disk is unchanged.";
        this.Validate();
        return true;
    }

    public void RefreshPublicationStatus()
    {
        if (this.IsStale)
        {
            this.Status = "Newer configuration is published. Unsaved work is retained. Inspect published configuration or explicitly Discard to adopt it.";
        }
    }

    /// <summary>Typed configuration preset input. Invalid buffers remain separate and block Save.</summary>
    public bool SetPresetInput(ConfigurationEditToken token, string contextId, string bindingId, string parameterId, bool present, string raw)
    {
        if (!this.Accepts(token))
        {
            return false;
        }

        BindingDefinition? binding = this.Document.Routing.Contexts.FirstOrDefault(item => item.Id == contextId)?.Bindings.FirstOrDefault(item => item.Id == bindingId);
        if (binding?.Destination is not BindingDestination.Command destination)
        {
            return false;
        }

        ParameterDefinition? parameter = this.Document.Commands.FirstOrDefault(item => item.Id == destination.CommandId)?.Parameters.FirstOrDefault(item => item.Id == parameterId);
        if (parameter is null)
        {
            return false;
        }

        string key = $"binding/{bindingId}/{parameterId}/{parameter.ValueType}";
        ArgumentValue? value = null;
        string? error = null;
        if (present)
        {
            value = parameter.ValueType switch
            {
                ParameterValueType.Text => new ArgumentValue.Text(raw),
                ParameterValueType.Path => new ArgumentValue.Path(raw),
                ParameterValueType.Boolean when raw == "True" => new ArgumentValue.Boolean(true),
                ParameterValueType.Boolean when raw == "False" => new ArgumentValue.Boolean(false),
                ParameterValueType.Integer when long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number) => new ArgumentValue.Integer(number),
                _ => null,
            };
            if (value is null)
            {
                error = parameter.ValueType == ParameterValueType.Integer ? "Enter a signed 64-bit integer; malformed values and overflow are invalid." : "Choose True or False.";
            }
        }
        else
        {
            this.ClearInputs($"binding/{bindingId}/{parameterId}/");
        }

        this.inputs = this.inputs.SetItem(key, new(raw, error));
        CommandBindingMetadata metadata = destination.Metadata ?? new();
        ArgumentSet arguments = new((metadata.Presets ?? ArgumentSet.Empty).Assignments.Where(item => item.ParameterId != parameterId).Concat(value is null ? [] : new[] { new ArgumentAssignment(parameterId, value) }));
        return this.PutBinding(token, contextId, binding with { Destination = destination with { Metadata = metadata with { Presets = arguments } } });
    }

    public bool ClearFieldInputs(ConfigurationEditToken token, string prefix)
    {
        if (!this.Accepts(token))
        {
            return false;
        }

        this.ClearInputs(prefix);
        this.Changed();
        return true;
    }

    public string FileStatus => this.service.LastResult is ConfigurationResult result ? $"File: {result.Status}. " + (result.Success || result.Status == ConfigurationStatus.Missing ? "" : "User file is not overwritten on load failure. Inspect and correct the file, then use tray Reload. Save conflicts retain this working copy.") : "File has not yet been loaded.";

    public bool IsNewParameter(string commandId, string parameterId) => this.baseline.Catalog.Find(commandId)?.Parameters.Any(item => item.Id == parameterId) != true;
    public string PublishedSummary => string.Join(Environment.NewLine, this.service.Store.Current!.Catalog.Definitions.Select(item => $"Command: {item.DisplayName} ({item.Id})").Concat(this.service.Store.Current.Routing.Contexts.Select(item => $"Context: {item.DisplayName} ({item.Id})")));

    private bool Accepts(ConfigurationEditToken token) => !this.IsSaving && token == this.Token;
    private static CommandDefinition Freeze(CommandDefinition command) => command with
    {
        Parameters = command.Parameters.ToImmutableArray()
    };
    private static ContextDefinition Freeze(ContextDefinition context) => context with
    {
        Bindings = context.Bindings.ToImmutableArray()
    };
    private void SetContexts(IEnumerable<ContextDefinition> contexts)
    {
        this.Document = this.Document with
        {
            Routing = this.Document.Routing with
            {
                Contexts = contexts.Select(Freeze).ToImmutableArray()
            }
        };
        this.Changed();
    }

    private void ClearInputs(string prefix) => this.inputs = this.inputs.RemoveRange(this.inputs.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)));
    private void Changed()
    {
        this.Revision++;
        this.IsDirty = true;
        this.Validate();
    }

    private void Validate()
    {
        CommandConfigurationValidationResult result = this.service.Store.Validate(this.Document.Routing, this.Document.Commands);
        this.Diagnostics = [..result.Diagnostics.Select(issue => new ConfigurationDiagnostic(issue.Code.ToString(), "Commands", issue.Message, issue.Severity == CommandDiagnosticSeverity.Warning, Command: issue)), ..result.RoutingDiagnostics.Select(issue => new ConfigurationDiagnostic(issue.Code.ToString(), "Contexts", issue.Message, issue.Severity == RoutingDiagnosticSeverity.Warning, Routing: issue)), ..this.inputs.Where(pair => pair.Value.Error is not null).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ConfigurationDiagnostic("InvalidInput", pair.Key, pair.Value.Error!))];
    }
}