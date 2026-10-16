namespace Disorderly.Routing;

/// <summary>Publishes whole validated snapshots atomically. Existing sessions retain their original snapshot.</summary>
public sealed class RoutingConfigurationStore
{
    private readonly RoutingValidator validator;
    private RoutingSnapshot? current;

    public RoutingConfigurationStore(RoutingValidator? validator = null)
    {
        this.validator = validator ?? new RoutingValidator();
    }

    public RoutingSnapshot? Current => Volatile.Read(ref this.current);

    public RoutingValidationResult Publish(RoutingConfiguration configuration)
    {
        RoutingValidationResult result = this.validator.Validate(configuration);
        if (result.Snapshot is not null)
        {
            Interlocked.Exchange(ref this.current, result.Snapshot);
        }

        return result;
    }

    public RoutingSession CreateSession() => new(this.Current ?? throw new InvalidOperationException("Publish a valid routing configuration before creating a session."));
}