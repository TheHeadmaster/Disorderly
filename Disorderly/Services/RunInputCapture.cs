namespace Disorderly.Services;

/// <summary>Capture one physical activation. Invalidated physical input never falls back to automation/current state.</summary>
public sealed class RunInputCapture
{
    private PaletteSubmissionEventArgs? current;
    private PaletteSubmissionEventArgs? captured;
    private bool physical;

    public bool HasCapturedInput => this.captured is not null;
    public bool IsStale => this.physical && !Same(this.captured, this.current);

    public void Update(PaletteSubmissionEventArgs? request, bool available)
    {
        PaletteSubmissionEventArgs? next = available ? request : null;
        if (!Same(this.current, next))
        {
            this.captured = null;
        }

        this.current = next;
    }

    public void Begin()
    {
        this.physical = true;
        this.captured = this.current;
    }

    public PaletteSubmissionEventArgs? Take()
    {
        PaletteSubmissionEventArgs? result = this.physical
            ? (Same(this.captured, this.current) ? this.captured : null) : this.current;
        this.Cancel();
        return result;
    }

    public void Clear()
    {
        this.current = null;
        this.captured = null;
    }

    public void Cancel()
    {
        this.physical = false;
        this.captured = null;
    }

    private static bool Same(PaletteSubmissionEventArgs? left, PaletteSubmissionEventArgs? right) => left is not null && right is not null
        && left.ActivationId == right.ActivationId && left.InvocationId == right.InvocationId
        && left.PreviousAttemptId == right.PreviousAttemptId && left.EditingRevision == right.EditingRevision;
}