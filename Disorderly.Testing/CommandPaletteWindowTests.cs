using System.Windows;
using System.Windows.Controls;
using System.Collections.Immutable;
using Disorderly.Services;
using Disorderly.View.Windows;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
[Apartment(ApartmentState.STA)]
public class CommandPaletteWindowTests
{
    [Test]
    public void ContextLabelsEmptyStateAndCommandPreviewRenderWithoutNativeActivation()
    {
        using CommandPaletteWindow window = new();
        PaletteInteractionTests.View view = new();
        using CommandPaletteController controller = new(view, PaletteInteractionTests.Configuration());
        controller.Open();
        window.Render(view.Presentation);
        Assert.That(((TextBlock)window.FindName("ContextTitle")).Text, Is.EqualTo("Root"));
        Assert.That(((TextBlock)window.FindName("NavigationHint")).Text, Is.EqualTo("Esc  Dismiss"));
        Assert.That(window.ShowInTaskbar, Is.False);
        Assert.That(window.ResizeMode, Is.EqualTo(ResizeMode.NoResize));
        Assert.That(((Border)window.FindName("FocusSurface")).Focusable, Is.True);
        view.Key("D"); window.Render(view.Presentation);
        Assert.That(((TextBlock)window.FindName("ContextTitle")).Text, Is.EqualTo("Development"));
        Assert.That(((ItemsControl)window.FindName("BindingList")).Items.Count, Is.EqualTo(2));
        Assert.That(((TextBlock)window.FindName("NavigationHint")).Text, Is.EqualTo("Esc  Back"));
        view.Key("C"); window.Render(view.Presentation);
        Assert.That(((TextBlock)window.FindName("SelectionTitle")).Text, Is.EqualTo("Example"));
        Assert.That(((Border)window.FindName("SelectionPreview")).Visibility, Is.EqualTo(Visibility.Visible));
        view.Key("S"); window.Render(view.Presentation);
        Assert.That(((TextBlock)window.FindName("EmptyContext")).Visibility, Is.EqualTo(Visibility.Visible));
        Assert.That(((Border)window.FindName("SelectionPreview")).Visibility, Is.EqualTo(Visibility.Collapsed));
        window.Hide();
        Assert.That(((TextBlock)window.FindName("SelectionTitle")).Text, Is.Empty);
    }
    [Test]
    public void LongContextContentUsesBoundedScrollableLayout()
    {
        using CommandPaletteWindow window = new();
        ImmutableArray<PaletteBindingLabel> bindings = [.. Enumerable.Range(0, 80).Select(number => new PaletteBindingLabel($"F{number}", $"Command {number}", "Command"))];
        window.Render(new(Guid.NewGuid(), "Root", "Root", bindings, true, null));
        FrameworkElement content = (FrameworkElement)window.Content;
        content.Measure(new Size(360, 480));
        content.Arrange(new Rect(0, 0, 360, 480));
        Assert.That(content.DesiredSize.Height, Is.LessThanOrEqualTo(480));
        ScrollViewer scroll = (ScrollViewer)window.FindName("ContextScroll");
        Assert.That(scroll.ExtentHeight, Is.GreaterThan(scroll.ViewportHeight));
        Assert.That(scroll.ViewportHeight, Is.GreaterThan(0));
    }

}