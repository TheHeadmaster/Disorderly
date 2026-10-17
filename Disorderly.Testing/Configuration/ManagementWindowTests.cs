using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Disorderly.Commands;
using Disorderly.Configuration;
using Disorderly.Routing;
using Disorderly.View;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;
[TestFixture]
[Apartment(ApartmentState.STA)]
public class ManagementWindowTests
{
    private static IEnumerable<T> Controls<T>(DependencyObject parent)
        where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is T found)
            {
                yield return found;
            }

            if (child is DependencyObject nested)
            {
                foreach (T descendant in Controls<T>(nested))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static TextBox Field(DependencyObject parent, string name) => Controls<TextBox>(parent).Single(item => AutomationProperties.GetName(item) == name);
    private static Button Button(DependencyObject parent, string name) => Controls<Button>(parent).Single(item => Equals(item.Content, name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    private static MainWindow Window()
    {
        ConfigurationService service = ConfigurationFixture.Service(new() { Content = ConfigurationCodec.Encode(ConfigurationFixture.Full()) });
        Assert.That(service.ReloadAsync().GetAwaiter().GetResult().Success, Is.True);
        return new MainWindow(service);
    }

    [Test]
    public void CommandsContextsAccessibilityAndScrollableFormsArePresented()
    {
        MainWindow window = Window();
        try
        {
            ListBox commands = (ListBox)window.FindName("CommandList");
            ListBox contexts = (ListBox)window.FindName("ContextList");
            Assert.That(commands.Items.Count, Is.EqualTo(3));
            Assert.That(contexts.Items.Count, Is.EqualTo(2));
            Assert.That(AutomationProperties.GetName(commands), Is.EqualTo("Command definitions"));
            commands.SelectedIndex = 0;
            StackPanel details = (StackPanel)window.FindName("CommandDetails");
            Assert.That(Field(details, "Display name"), Is.Not.Null);
            Assert.That(Controls<ScrollViewer>(window).Count(), Is.GreaterThanOrEqualTo(2));
            Assert.That(((Button)window.FindName("SaveButton")).IsEnabled, Is.False);
        }
        finally
        {
            window.EndLifetime();
        }
    }

    [Test]
    public void CreatingCommandAndParameterKeepsSelectionAndShowsDirtyValidation()
    {
        MainWindow window = Window();
        try
        {
            StackPanel panel = (StackPanel)window.FindName("NewCommandPanel");
            Field(panel, "New stable ID").Text = "created";
            Field(panel, "New display name").Text = "Created";
            Click(Button(panel, "Add"));
            StackPanel details = (StackPanel)window.FindName("CommandDetails");
            Field(details, "Display name").Text = "Renamed";
            Field(details, "New parameter ID").Text = "path";
            Field(details, "New parameter name").Text = "Path";
            Click(Button(details, "Add parameter"));
            Assert.That(window.Editor!.Document.Commands.Single(item => item.Id == "created").Parameters.Single().Id, Is.EqualTo("path"));
            Assert.That(Field(details, "Display name").Text, Is.EqualTo("Renamed"));
            Assert.That(((TextBlock)window.FindName("StateLabel")).Text, Does.Contain("Unsaved"));
            Field(details, "Display name").Text = "";
            Assert.That(((Button)window.FindName("SaveButton")).IsEnabled, Is.False);
            Field(details, "Display name").Text = "Valid";
            Assert.That(((Button)window.FindName("SaveButton")).IsEnabled, Is.True);
        }
        finally
        {
            window.EndLifetime();
        }
    }

    [Test]
    public void ContextBindingGestureAndTypedPresetsCanBeConfigured()
    {
        MainWindow window = Window();
        try
        {
            ((TabControl)window.FindName("Sections")).SelectedIndex = 1;
            StackPanel create = (StackPanel)window.FindName("NewContextPanel");
            Field(create, "New stable ID").Text = "new-context";
            Field(create, "New display name").Text = "New Context";
            Click(Button(create, "Add"));
            StackPanel details = (StackPanel)window.FindName("ContextDetails");
            Field(details, "New binding ID").Text = "new-binding";
            Click(Button(details, "Add binding"));
            Field(details, "Logical key").Text = "+";
            Assert.That(window.Editor!.Document.Routing.Contexts.Single(item => item.Id == "new-context").Bindings.Single().Gesture.Key, Is.EqualTo("+"));
            ComboBox destination = Controls<ComboBox>(details).Single(item => AutomationProperties.GetName(item) == "Destination kind");
            destination.SelectedItem = "Context";
            Assert.That(window.Editor.Document.Routing.Contexts.Single(item => item.Id == "new-context").Bindings.Single().Destination, Is.TypeOf<BindingDestination.Context>());
        }
        finally
        {
            window.EndLifetime();
        }
    }

    [Test]
    public void InvalidIntegerPresetRemainsVisibleAcrossSelectionAndBlocksSave()
    {
        MainWindow window = Window();
        try
        {
            ((TabControl)window.FindName("Sections")).SelectedIndex = 1;
            ListBox contexts = (ListBox)window.FindName("ContextList");
            contexts.SelectedIndex = 0; // Snapshot context enumeration is ordinal: child, root.
            StackPanel details = (StackPanel)window.FindName("ContextDetails");
            TextBox integer = Controls<TextBox>(details).Single(item => AutomationProperties.GetName(item) == "Preset value" && item.Text == long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            integer.Text = "-";
            Assert.That(((Button)window.FindName("SaveButton")).IsEnabled, Is.False);
            Assert.That(integer.Text, Is.EqualTo("-"));
            Assert.That(details.IsKeyboardFocusWithin, Is.False); // No desktop focus is manufactured by validation.
            contexts.SelectedIndex = 1;
            contexts.SelectedIndex = 0;
            Assert.That(Controls<TextBox>(details).Any(item => item.Text == "-"), Is.True);
        }
        finally
        {
            window.EndLifetime();
        }
    }

    [Test]
    public void OldControlEventsCannotModifyReplacementSelection()
    {
        MainWindow window = Window();
        try
        {
            ListBox commands = (ListBox)window.FindName("CommandList");
            commands.SelectedIndex = 0;
            TextBox obsolete = Field((StackPanel)window.FindName("CommandDetails"), "Display name");
            commands.SelectedIndex = 1;
            long revision = window.Editor!.Revision;
            obsolete.Text = "stale event";
            Assert.That(window.Editor.Revision, Is.EqualTo(revision));
        }
        finally
        {
            window.EndLifetime();
        }
    }

    [Test]
    public void ClosingHidesAndRetainsSameDirtySession()
    {
        MainWindow window = Window();
        try
        {
            window.Editor!.AddCommand(window.Editor.Token, new("private", "Private", []));
            Guid identity = window.Editor.Id;
            window.Close();
            Assert.That(window.Editor.Id, Is.EqualTo(identity));
            Assert.That(window.Editor.IsDirty, Is.True);
            Assert.That(window.IsVisible, Is.False);
        }
        finally
        {
            window.EndLifetime();
        }
    }

    [Test]
    public void TrayOpenInvokesInjectedManagementActionWithoutChangingReload()
    {
        int opens = 0;
        int reloads = 0;
        using System.Windows.Forms.ContextMenuStrip menu = TrayIconHandler.CreateMenu(() =>
        {
        }, () => reloads++, () => opens++);
        ((System.Windows.Forms.ToolStripMenuItem)menu.Items[1]).PerformClick();
        ((System.Windows.Forms.ToolStripMenuItem)menu.Items[1]).PerformClick();
        ((System.Windows.Forms.ToolStripMenuItem)menu.Items[2]).PerformClick();
        Assert.That(opens, Is.EqualTo(2));
        Assert.That(reloads, Is.EqualTo(1));
    }
}