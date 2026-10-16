using Disorderly.Services;
using Disorderly.Routing;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
public class CommandPaletteControllerTests
{
    [Test]
    public void RepeatedActivationTogglesSameViewAndAlwaysResetsRoot()
    {
        FakeView view = new();
        using CommandPaletteController controller = new(view);
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden));
        controller.Toggle();
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
        controller.Open();
        Assert.That(view.ShowCount, Is.EqualTo(1));
        controller.Toggle();
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden));
        controller.Toggle();
        Assert.That(view.ResetCount, Is.EqualTo(2));
        Assert.That(view.ShowCount, Is.EqualTo(2));
        Assert.That(view.DisposeCount, Is.Zero);
    }

    [TestCase("focus loss")]
    [TestCase("Escape at root")]
    public void DismissalReturnsToHiddenWithoutDestroyingApplicationOrView(string reason)
    {
        FakeView view = new();
        using CommandPaletteController controller = new(view);
        controller.Open();
        if (reason == "Escape at root") { view.Send(LogicalGesture.Escape); }
        else { view.RequestDismiss(); }
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden), reason);
        Assert.That(view.HideCount, Is.EqualTo(1));
        Assert.That(view.DisposeCount, Is.Zero);
        controller.Open();
        Assert.That(view.ResetCount, Is.EqualTo(2));
    }

    [Test]
    public void FailedForegroundActivationReturnsToHiddenAndCanRetry()
    {
        FakeView view = new() { FocusSucceeds = false };
        using CommandPaletteController controller = new(view);
        controller.Open();
        Assert.That(controller.State, Is.EqualTo(PaletteState.Hidden));
        view.FocusSucceeds = true;
        controller.Open();
        Assert.That(controller.State, Is.EqualTo(PaletteState.Visible));
    }

    [Test]
    public void DisposeIsIdempotent()
    {
        FakeView view = new();
        CommandPaletteController controller = new(view);
        controller.Open();
        controller.Dispose();
        controller.Dispose();
        Assert.That(view.HideCount, Is.EqualTo(1));
        Assert.That(view.DisposeCount, Is.EqualTo(1));
    }

    private sealed class FakeView : ICommandPaletteView
    {
        public event EventHandler<PaletteDismissEventArgs>? DismissRequested;
        public event EventHandler<PaletteGestureEventArgs>? GestureReceived;
        public PalettePresentation? Presentation { get; private set; }
        public bool FocusSucceeds { get; set; } = true;
        public int ResetCount { get; private set; }
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }
        public int DisposeCount { get; private set; }
        public void Render(PalettePresentation presentation)
        {
            if (this.Presentation?.ActivationId != presentation.ActivationId) { this.ResetCount++; }
            this.Presentation = presentation;
        }
        public bool ShowAtForegroundMonitor()
        {
            this.ShowCount++;
            return this.FocusSucceeds;
        }

        public void Hide() => this.HideCount++;
        public void Dispose() => this.DisposeCount++;
        public void Send(LogicalGesture gesture) => this.GestureReceived?.Invoke(this, new PaletteGestureEventArgs(this.Presentation!.ActivationId, gesture));
        public void RequestDismiss() => this.DismissRequested?.Invoke(this, new PaletteDismissEventArgs(this.Presentation!.ActivationId));
    }
}