using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Disorderly.View.Windows;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
[Apartment(ApartmentState.STA)]
public class CommandPaletteWindowTests
{
    [Test]
    public void RootPresentationResetsInputAndEscapeRequestsDismissal()
    {
        using CommandPaletteWindow window = new();
        TextBox input = (TextBox)window.FindName("CommandInput");
        input.Text = "temporary presentation state";
        window.ResetRoot();
        Assert.That(input.Text, Is.Empty);
        Assert.That(window.ShowInTaskbar, Is.False);
        Assert.That(window.ResizeMode, Is.EqualTo(ResizeMode.NoResize));
        int dismissals = 0;
        window.DismissRequested += (_, _) => dismissals++;
        nint handle = new WindowInteropHelper(window).EnsureHandle();
        KeyEventArgs escape = new(Keyboard.PrimaryDevice, HwndSource.FromHwnd(handle), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(escape);
        Assert.That(escape.Handled, Is.True);
        Assert.That(dismissals, Is.EqualTo(1));
    }
}