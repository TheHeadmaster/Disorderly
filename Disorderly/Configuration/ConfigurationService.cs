using System.Collections.Immutable;
using System.IO;
using Disorderly.Commands;
using Disorderly.Routing;
using Disorderly.Services;

namespace Disorderly.Configuration;

public enum ConfigurationStatus { Loaded, Saved, Missing, InvalidDocument, UnsupportedVersion, DomainInvalid, IoFailure, Conflict, ReplacementUncertain }
public sealed record ConfigurationResult(ConfigurationStatus Status, long Revision, ImmutableArray<ConfigurationDiagnostic> Diagnostics, bool? Replaced = false)
{
    public bool Success => this.Status is ConfigurationStatus.Loaded or ConfigurationStatus.Saved;
    public string Summary => this.Success ? $"{this.Status} (revision {this.Revision})" : this.Status == ConfigurationStatus.Missing
        ? "No configuration file; current configuration retained" : $"{this.Status}; see configuration diagnostics in the log";
}

/// <summary>One serialized owner of file loading/saving/publication. No command, draft or operation execution.</summary>
public sealed class ConfigurationService
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly IConfigurationFiles files;
    private readonly RoutingValidator paletteRouting = new([PaletteConfiguration.ActivationGesture]);
    private long revision;
    private string? fingerprint;
    private CommandConfigurationSnapshot? publication;

    public ConfigurationService(CommandConfigurationStore store, string path, IConfigurationFiles? files = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.Store = store;
        this.publication = store.Current;
        this.Path = System.IO.Path.GetFullPath(path);
        this.files = files ?? new ConfigurationFiles();
    }

    public CommandConfigurationStore Store { get; }
    public string Path { get; }
    public ConfigurationResult? LastResult { get; private set; }
    public long Revision => Interlocked.Read(ref this.revision);

    public async Task<ConfigurationResult> ReloadAsync()
    {
        await this.serial.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[]? bytes = await this.files.ReadAsync(this.Path).ConfigureAwait(false);
            if (bytes is null)
            {
                this.fingerprint = null; // An explicit successful observation of absence, without clearing the active snapshot.
                return this.Result(ConfigurationStatus.Missing);
            }

            ConfigurationDecodeResult decoded = ConfigurationCodec.Decode(bytes);
            if (decoded.Document is null)
            {
                return this.Result(decoded.UnsupportedVersion ? ConfigurationStatus.UnsupportedVersion : ConfigurationStatus.InvalidDocument, decoded.Diagnostics);
            }

            CommandConfigurationValidationResult validated = this.Validate(decoded.Document);
            if (!validated.IsValid)
            {
                return this.Result(ConfigurationStatus.DomainInvalid, Diagnostics(validated));
            }

            this.Publish(validated.Snapshot!);
            this.fingerprint = ConfigurationFiles.Fingerprint(bytes);
            return this.Result(ConfigurationStatus.Loaded, Diagnostics(validated));
        }
        catch (ConfigurationDocumentException error)
        {
            return this.Result(ConfigurationStatus.InvalidDocument, [new(error.Code, error.Location, error.Message)]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return this.Result(ConfigurationStatus.IoFailure, [new("ReadFailed", "$", "Cannot read the configuration file. Check access permissions and retry Reload.")]);
        }
        finally
        {
            this.serial.Release();
        }
    }

    /// <summary>Required optimistic revision; save from an unobserved existing file conflicts rather than overwriting it.</summary>
    public async Task<ConfigurationResult> SaveAsync(ConfigurationDocument document, long expectedRevision)
    {
        await this.serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (expectedRevision != this.Revision || !ReferenceEquals(this.publication, this.Store.Current))
            {
                return this.Result(ConfigurationStatus.Conflict, [new("StaleRevision", "$", "Configuration changed since this proposal was based on it.")]);
            }

            if (document.Version != 1)
            {
                return this.Result(ConfigurationStatus.UnsupportedVersion, [new("UnsupportedVersion", "$.version", "Only version 1 is supported.")]);
            }

            CommandConfigurationValidationResult validated = this.Validate(document);
            if (!validated.IsValid)
            {
                return this.Result(ConfigurationStatus.DomainInvalid, Diagnostics(validated));
            }

            // Encode the frozen complete proposal, not caller-owned mutable collections or view state.
            byte[] bytes = ConfigurationCodec.Encode(ConfigurationDocument.FromSnapshot(validated.Snapshot!));
            ConfigurationDecodeResult checkedDocument = ConfigurationCodec.Decode(bytes);
            if (checkedDocument.Document is null)
            {
                return this.Result(ConfigurationStatus.InvalidDocument, checkedDocument.Diagnostics);
            }

            ConfigurationWriteResult written = await this.files.WriteAsync(this.Path, bytes, this.fingerprint).ConfigureAwait(false);
            if (written.Status != ConfigurationWriteStatus.Saved || written.Replaced != true)
            {
                ConfigurationStatus status = written.Status switch
                {
                    ConfigurationWriteStatus.Conflict => ConfigurationStatus.Conflict,
                    ConfigurationWriteStatus.Uncertain => ConfigurationStatus.ReplacementUncertain,
                    _ => ConfigurationStatus.IoFailure,
                };
                return this.Result(status, [new(written.Code, "$", "Configuration was not published. Keep the proposal; inspect the file and Reload before retrying.")], written.Replaced);
            }

            if (!ReferenceEquals(this.publication, this.Store.Current))
            {
                return this.Result(ConfigurationStatus.Conflict, [new("PublicationChangedDuringSave", "$", "The file was saved, but another publisher changed the active snapshot. Reload to reconcile; the newer publication was preserved.")], true);
            }

            this.Publish(validated.Snapshot!);
            this.fingerprint = ConfigurationFiles.Fingerprint(bytes);
            return this.Result(ConfigurationStatus.Saved, Diagnostics(validated), true);
        }
        catch (ConfigurationDocumentException error)
        {
            return this.Result(ConfigurationStatus.InvalidDocument, [new(error.Code, error.Location, error.Message)]);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return this.Result(ConfigurationStatus.ReplacementUncertain, [new("SaveIoFailure", "$", "Save could not establish replacement; nothing was published. Inspect the file and Reload.")], null);
        }
        finally
        {
            this.serial.Release();
        }
    }

    private CommandConfigurationValidationResult Validate(ConfigurationDocument document)
    {
        CommandConfigurationValidationResult combined = this.Store.Validate(document.Routing, document.Commands);
        RoutingValidationResult palette = this.paletteRouting.Validate(document.Routing);
        return palette.IsValid ? combined : new CommandConfigurationValidationResult(combined.Diagnostics, palette.Diagnostics, null);
    }

    private void Publish(CommandConfigurationSnapshot snapshot)
    {
        CommandConfigurationValidationResult publication = this.Store.Publish(new(snapshot.Routing.RootContextId, snapshot.Routing.Contexts), snapshot.Catalog.Definitions);
        if (!publication.IsValid)
        {
            throw new InvalidOperationException("A previously validated frozen configuration could not be published.");
        }

        this.publication = this.Store.Current;
        Interlocked.Increment(ref this.revision);
    }

    private ConfigurationResult Result(ConfigurationStatus status, ImmutableArray<ConfigurationDiagnostic> diagnostics = default, bool? replaced = false)
    {
        ConfigurationResult result = new(status, this.Revision, diagnostics.IsDefault ? [] : diagnostics, replaced);
        this.LastResult = result;
        return result;
    }

    private static ImmutableArray<ConfigurationDiagnostic> Diagnostics(CommandConfigurationValidationResult validation) =>
        [.. validation.RoutingDiagnostics.Select(diagnostic => new ConfigurationDiagnostic(diagnostic.Code.ToString(), "$.contexts", diagnostic.Message,
            diagnostic.Severity == RoutingDiagnosticSeverity.Warning, Routing: diagnostic)),
        .. validation.Diagnostics.Select(diagnostic => new ConfigurationDiagnostic(diagnostic.Code.ToString(), "$.commands", diagnostic.Message,
            diagnostic.Severity == CommandDiagnosticSeverity.Warning, Command: diagnostic))];
}