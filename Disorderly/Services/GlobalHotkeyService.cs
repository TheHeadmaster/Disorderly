namespace Disorderly.Services;

public interface IHotkeyRegistration : IDisposable
{
    event EventHandler? Activated;
    bool Register();
    void Unregister();
}

/// <summary>Owns the single activation registration. Failed registration is not retried automatically.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly IHotkeyRegistration registration;
    private bool attempted;
    private bool disposed;

    public GlobalHotkeyService(IHotkeyRegistration registration)
    {
        this.registration = registration;
        this.registration.Activated += this.OnActivated;
    }

    public event EventHandler? Activated;
    public bool IsRegistered { get; private set; }

    public bool Start()
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (!this.attempted)
        {
            this.attempted = true;
            this.IsRegistered = this.registration.Register();
        }

        return this.IsRegistered;
    }

    private void OnActivated(object? sender, EventArgs args)
    {
        if (this.IsRegistered && !this.disposed)
        {
            this.Activated?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.registration.Activated -= this.OnActivated;
        if (this.IsRegistered)
        {
            this.registration.Unregister();
            this.IsRegistered = false;
        }

        this.registration.Dispose();
    }
}