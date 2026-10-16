using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Disorderly.Commands;
using Disorderly.Input;
using Disorderly.Services;
using Disorderly.Testing.Commands.Editing;
using Disorderly.Testing.Execution;
using Disorderly.View.Controls;
using Disorderly.View.Windows;
using NUnit.Framework;

namespace Disorderly.Testing;

[TestFixture]
[Apartment(ApartmentState.STA)]
public class ArgumentEditorWindowTests
{
    // Renders real WPF controls without asking CI for foreground/desktop privileges.
    private sealed class Host : ICommandPaletteView
    {
        public CommandPaletteWindow Window { get; } = new();
        public PalettePresentation Presentation { get; private set; } = null!;
        public event EventHandler<PaletteDismissEventArgs>? DismissRequested { add => this.Window.DismissRequested += value; remove => this.Window.DismissRequested -= value; }
        public event EventHandler<PaletteGestureEventArgs>? GestureReceived;
        public event EventHandler<PaletteSubmissionEventArgs>? SubmissionRequested { add => this.Window.SubmissionRequested += value; remove => this.Window.SubmissionRequested -= value; }
        public event EventHandler<PaletteArgumentEditEventArgs>? ArgumentEdited { add => this.Window.ArgumentEdited += value; remove => this.Window.ArgumentEdited -= value; }
        public void Render(PalettePresentation presentation) { this.Presentation = presentation; this.Window.Render(presentation); }
        public bool ShowAtForegroundMonitor() => true;
        public void Hide() => this.Window.Hide();
        public void Dispose() => this.Window.Dispose();
        public void Key(string key) => this.GestureReceived?.Invoke(this, new(this.Presentation.ActivationId, new(key)));
        public ArgumentParameterEditor Field(int index) => (ArgumentParameterEditor)((StackPanel)this.Window.FindName("ArgumentEditors")).Children[index];
        public RunInputCapture Capture => (RunInputCapture)typeof(CommandPaletteWindow).GetField("runInput", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(this.Window)!;
    }

    private static void Select(CommandPaletteController controller, Host view) { controller.Open(); view.Key("D"); view.Key("N"); }

    [Test]
    public void FieldsPreserveDeclarationOrderPresetsAndExplicitEmptyTextPresence()
    {
        Host host = new();
        using CommandPaletteController controller = new(host, EditingFixture.Store(requiredText: true));
        Select(controller, host);
        Assert.That(host.Field(3).ValueInput.IsReadOnly, Is.True);
        Assert.That(host.Field(3).ValueInput.Text, Is.EqualTo(@"C:\Preset\example.exe"));
        Assert.That(host.Presentation.CanRun, Is.False);
        host.Field(0).Supplied.IsChecked = true;
        Assert.That(host.Presentation.Selection!.Arguments.Assignments.Single(value => value.ParameterId == "text").Value, Is.EqualTo(new ArgumentValue.Text("")));
        Assert.That(host.Presentation.CanRun, Is.True);
        host.Field(0).Supplied.IsChecked = false;
        Assert.That(host.Presentation.CanRun, Is.False);
        Assert.That(host.Field(0).Diagnostics.Text, Does.Contain("required"));
        Assert.That(host.Presentation.Editing!.Parameters.Select(parameter => parameter.Parameter.Id), Is.EqualTo(new[] { "text", "flag", "count", "executable" }));
    }

    [Test]
    public void ValidationUpdatesKeepControlIdentityAndSelectionAndRetainInvalidRawInput()
    {
        Host host = new();
        using CommandPaletteController controller = new(host, EditingFixture.Store());
        Select(controller, host);
        ArgumentParameterEditor text = host.Field(0);
        text.ValueInput.Text = "abcdef";
        text.ValueInput.Select(2, 2);
        host.Field(2).ValueInput.Text = "-";
        Assert.That(host.Field(0), Is.SameAs(text));
        Assert.That(text.ValueInput.SelectionStart, Is.EqualTo(2));
        Assert.That(text.ValueInput.SelectionLength, Is.EqualTo(2));
        Assert.That(host.Field(2).ValueInput.Text, Is.EqualTo("-"));
        Assert.That(host.Field(2).Diagnostics.Text, Does.Contain("whole number"));
        Assert.That(((Button)host.Window.FindName("RunButton")).IsEnabled, Is.False);
        host.Field(2).ValueInput.Text = "-12";
        Assert.That(((Button)host.Window.FindName("RunButton")).IsEnabled, Is.True);
        Assert.That(host.Presentation.AttemptId, Is.Null);
    }

    [Test]
    public void BooleanControlKeepsAbsenceAndFalseDistinct()
    {
        Host host = new();
        using CommandPaletteController controller = new(host, EditingFixture.Store());
        Select(controller, host);
        Assert.That(host.Field(1).BooleanInput.SelectedIndex, Is.Zero);
        host.Field(1).BooleanInput.SelectedIndex = 1;
        Assert.That(host.Presentation.Selection!.Arguments.Assignments.Single(value => value.ParameterId == "flag").Value, Is.EqualTo(new ArgumentValue.Boolean(false)));
        host.Field(1).BooleanInput.SelectedIndex = 0;
        Assert.That(host.Presentation.Selection!.Arguments.Assignments.Any(value => value.ParameterId == "flag"), Is.False);
    }

    [Test]
    public void DescendantControlsHaveEditorOwnershipAndNativeTextEditingIsNotReplaced()
    {
        Host host = new();
        using CommandPaletteController controller = new(host, EditingFixture.Store(presetPath: false));
        Select(controller, host);
        foreach (int index in new[] { 0, 2, 3 })
        {
            ArgumentParameterEditor field = host.Field(index);
            Assert.That(PaletteInputOwnership.FindEditor(field.ValueInput), Is.SameAs(field));
            Assert.That(PaletteInputOwnership.IsInteractive(field.ValueInput), Is.True);
            Assert.That(field.ValueInput.IsReadOnly, Is.False);
            Assert.That(field.ValueInput.AcceptsReturn, Is.False);
            Assert.That(field.ValueInput.AcceptsTab, Is.False);
            Assert.That(System.Windows.Input.KeyboardNavigation.GetIsTabStop(field.ValueInput), Is.True);
        }

        TextBlock descendant = new() { Text = "Supplied" };
        host.Field(0).Supplied.Content = descendant;
        Assert.That(PaletteInputOwnership.FindEditor(descendant), Is.SameAs(host.Field(0)));
        Assert.That(PaletteInputOwnership.IsInteractive(descendant), Is.True);
        Assert.That(PaletteInputOwnership.IsInteractive((DependencyObject)host.Window.FindName("FocusSurface")), Is.False);
    }

    [Test]
    public void WindowClearsInterruptedRunCaptureOnHideReopenAndSelectionChange()
    {
        Host host = new();
        using CommandPaletteController controller = new(host, EditingFixture.Store());
        Select(controller, host);
        host.Capture.Begin();
        Assert.That(host.Capture.HasCapturedInput, Is.True);
        controller.Dismiss();
        Assert.That(host.Capture.HasCapturedInput, Is.False);
        Select(controller, host); host.Capture.Begin();
        Assert.That(host.Capture.Take()!.InvocationId, Is.EqualTo(host.Presentation.Selection!.Id));
        host.Capture.Begin(); host.Key("N");
        Assert.That(host.Capture.HasCapturedInput, Is.False);
        Assert.That(host.Capture.Take(), Is.Null, "The old physical input cannot become a new selection's click.");
        host.Capture.Begin();
        Assert.That(host.Capture.Take()!.InvocationId, Is.EqualTo(host.Presentation.Selection!.Id));
        controller.Dispose();
        Assert.That(host.Capture.HasCapturedInput, Is.False);
    }

    [Test]
    public void EditingRevisionInvalidatesCapturedRunAndNewPhysicalInputRecovers()
    {
        Host host = new();
        using CommandPaletteController controller = new(host, EditingFixture.Store());
        Select(controller, host); host.Capture.Begin();
        host.Field(0).ValueInput.Text = "new revision";
        Assert.That(host.Capture.Take(), Is.Null);
        host.Capture.Begin();
        Assert.That(host.Capture.Take()!.EditingRevision, Is.EqualTo(host.Presentation.Editing!.Revision));
    }
    [Test]
    public void CanceledRunKeyboardFocusDoesNotBlockLaterAutomation()
    {
        Host host = new();
        using CommandPaletteController controller = new(host, EditingFixture.Store());
        Select(controller, host); host.Capture.Begin();
        Button run = (Button)host.Window.FindName("RunButton");
        run.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
            Environment.TickCount, run, null) { RoutedEvent = System.Windows.Input.Keyboard.LostKeyboardFocusEvent });
        Assert.That(host.Capture.HasCapturedInput, Is.False);
        host.Key("N");
        Assert.That(host.Capture.Take()!.InvocationId, Is.EqualTo(host.Presentation.Selection!.Id));
    }

}