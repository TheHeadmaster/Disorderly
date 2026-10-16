using System.Collections.Immutable;

namespace Disorderly.Routing;

public enum RoutingDiagnosticSeverity
{
    Error,
    Warning,
}

public enum RoutingDiagnosticCode
{
    InvalidConfiguration,
    InvalidIdentifier,
    InvalidDisplayName,
    MissingRoot,
    DuplicateContext,
    DuplicateBinding,
    InvalidBindingOwner,
    InvalidGesture,
    DuplicateGesture,
    ReservedGesture,
    InvalidDestination,
    MissingContext,
    ContextCycle,
    UnreachableContext,
}

public sealed record RoutingDiagnostic(
    RoutingDiagnosticSeverity Severity,
    RoutingDiagnosticCode Code,
    string Message,
    string? ContextId = null,
    string? BindingId = null,
    LogicalGesture? Gesture = null,
    string? DestinationId = null);

public sealed class RoutingValidationResult
{
    internal RoutingValidationResult(IEnumerable<RoutingDiagnostic> diagnostics, RoutingSnapshot? snapshot)
    {
        this.Diagnostics = diagnostics.ToImmutableArray();
        this.Snapshot = snapshot;
    }

    public ImmutableArray<RoutingDiagnostic> Diagnostics { get; }
    public RoutingSnapshot? Snapshot { get; }
    public bool IsValid => this.Snapshot is not null;
}