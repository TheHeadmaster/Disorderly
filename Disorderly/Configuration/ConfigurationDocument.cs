using System.Collections.Immutable;
using Disorderly.Commands;
using Disorderly.Routing;

namespace Disorderly.Configuration;

/// <summary>Complete typed versioned proposal. JSON maps explicitly onto the existing domain contracts.</summary>
public sealed record ConfigurationDocument(int Version, RoutingConfiguration Routing, IReadOnlyList<CommandDefinition> Commands)
{
    public static ConfigurationDocument FromSnapshot(CommandConfigurationSnapshot snapshot) => new(1,
        new(snapshot.Routing.RootContextId, snapshot.Routing.Contexts), snapshot.Catalog.Definitions);
}

public sealed record ConfigurationDiagnostic(string Code, string Location, string Message, bool Warning = false,
    CommandDiagnostic? Command = null, RoutingDiagnostic? Routing = null);
public sealed record ConfigurationDecodeResult(ConfigurationDocument? Document, ImmutableArray<ConfigurationDiagnostic> Diagnostics, bool UnsupportedVersion = false);

public sealed class ConfigurationDocumentException(string code, string location, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Location { get; } = location;
}