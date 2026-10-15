namespace Disorderly.Services;

public enum PaletteState
{
    Hidden,
    Opening,
    VisibleAtRoot,
    Dismissing,
}

public interface ICommandPaletteView : IDisposable
{
    event EventHandler? DismissRequested;
    void ResetRoot();
    bool ShowAtForegroundMonitor();
    void Hide();
}

/// <summary>Visibility is separate from interaction state. A future draft service can participate in ResetRoot.</summary>
public sealed class CommandPaletteController : IDisposable
{
    private readonly ICommandPaletteView view;
    private bool disposed;

    public CommandPaletteController(ICommandPaletteView view)
    {
        this.view = view;
        this.view.DismissRequested += this.OnDismissRequested;
    }

    public PaletteState State { get; private set; } = PaletteState.Hidden;

    public void Toggle()
    {
        if (this.State == PaletteState.VisibleAtRoot)
        {
            this.Dismiss();
        }
        else if (this.State == PaletteState.Hidden)
        {
            this.Open();
        }
    }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.State != PaletteState.Hidden)
        {
            return;
        }

        this.State = PaletteState.Opening;
        try
        {
            this.view.ResetRoot();
            bool focused = this.view.ShowAtForegroundMonitor();
            this.State = PaletteState.VisibleAtRoot;
            if (!focused)
            {
                this.Dismiss();
            }
        }
        catch
        {
            this.State = PaletteState.VisibleAtRoot;
            this.Dismiss();
            throw;
        }
    }

    public void Dismiss()
    {
        if (this.State != PaletteState.VisibleAtRoot)
        {
            return;
        }

        this.State = PaletteState.Dismissing;
        this.view.Hide();
        this.State = PaletteState.Hidden;
    }

    private void OnDismissRequested(object? sender, EventArgs args) => this.Dismiss();

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.Dismiss();
        this.view.DismissRequested -= this.OnDismissRequested;
        this.view.Dispose();
        this.disposed = true;
    }
}