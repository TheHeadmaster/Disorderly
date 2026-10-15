using Disorderly.Services;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
public class GlobalHotkeyServiceTests
{
    [Test]
    public void StartRegistersOnlyOnceAndDisposeUnregistersOnlyOnce()
    {
        FakeRegistration registration = new();
        GlobalHotkeyService service = new(registration);
        int activations = 0;
        service.Activated += (_, _) => activations++;
        Assert.That(service.Start(), Is.True);
        Assert.That(service.Start(), Is.True);
        registration.Raise();
        Assert.That(service.IsRegistered, Is.True);
        Assert.That(registration.RegisterCount, Is.EqualTo(1));
        Assert.That(activations, Is.EqualTo(1));
        service.Dispose();
        service.Dispose();
        registration.Raise();
        Assert.That(service.IsRegistered, Is.False);
        Assert.That(registration.UnregisterCount, Is.EqualTo(1));
        Assert.That(registration.DisposeCount, Is.EqualTo(1));
        Assert.That(activations, Is.EqualTo(1));
    }

    [Test]
    public void ConflictDoesNotRetryOrPreventSafeShutdown()
    {
        FakeRegistration registration = new() { Succeeds = false };
        using GlobalHotkeyService service = new(registration);
        Assert.That(service.Start(), Is.False);
        Assert.That(service.Start(), Is.False);
        service.Dispose();
        Assert.That(registration.RegisterCount, Is.EqualTo(1));
        Assert.That(registration.UnregisterCount, Is.Zero);
        Assert.That(registration.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void ShutdownBeforeStartIsSafe()
    {
        FakeRegistration registration = new();
        new GlobalHotkeyService(registration).Dispose();
        Assert.That(registration.RegisterCount, Is.Zero);
        Assert.That(registration.UnregisterCount, Is.Zero);
    }

    private sealed class FakeRegistration : IHotkeyRegistration
    {
        public event EventHandler? Activated;
        public bool Succeeds { get; init; } = true;
        public int RegisterCount { get; private set; }
        public int UnregisterCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool Register()
        {
            this.RegisterCount++;
            return this.Succeeds;
        }

        public void Unregister() => this.UnregisterCount++;
        public void Dispose() => this.DisposeCount++;
        public void Raise() => this.Activated?.Invoke(this, EventArgs.Empty);
    }
}