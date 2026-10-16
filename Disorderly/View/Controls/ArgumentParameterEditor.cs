using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Disorderly.Commands;
using Disorderly.Commands.Editing;
using Disorderly.Services;

namespace Disorderly.View.Controls;

/// <summary>A stable control projection: parsing and validation stay in ArgumentEditorSession.</summary>
public sealed class ArgumentParameterEditor : UserControl
{
    private sealed record BooleanChoice(bool? Value, string Label)
    {
        public override string ToString() => this.Label;
    }

    private readonly TextBlock label = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock description = new() { TextWrapping = TextWrapping.Wrap };
    private bool updating;
    private Guid activation;
    private ArgumentEditingSnapshot? editing;
    private ParameterInputState? parameter;

    public ArgumentParameterEditor()
    {
        StackPanel panel = new() { Margin = new Thickness(0, 10, 0, 4) };
        panel.Children.Add(this.label);
        panel.Children.Add(this.description);
        panel.Children.Add(this.Supplied);
        panel.Children.Add(this.ValueInput);
        panel.Children.Add(this.BooleanInput);
        panel.Children.Add(this.Diagnostics);
        this.Foreground = Brushes.White;
        this.Content = panel;
        this.BooleanInput.ItemsSource = new BooleanChoice[] { new(null, "Not supplied"), new(false, "False"), new(true, "True") };
        this.ValueInput.SelectionChanged += (_, _) => this.Send(new ArgumentEdit.Caret(this.ValueInput.SelectionStart, this.ValueInput.SelectionLength));
        this.ValueInput.GotKeyboardFocus += (_, _) => this.Send(new ArgumentEdit.Caret(this.ValueInput.SelectionStart, this.ValueInput.SelectionLength));
        this.BooleanInput.GotKeyboardFocus += (_, _) => this.Send(new ArgumentEdit.Caret(0, 0));
        this.ValueInput.TextChanged += (_, _) => this.Send(new ArgumentEdit.RawText(this.ValueInput.Text));
        this.Supplied.Checked += (_, _) => this.Send(new ArgumentEdit.RawText(this.ValueInput.Text));
        this.Supplied.Unchecked += (_, _) => this.Send(new ArgumentEdit.Clear());
        this.BooleanInput.SelectionChanged += (_, _) =>
        {
            if (this.BooleanInput.SelectedItem is BooleanChoice choice)
            {
                this.Send(new ArgumentEdit.Boolean(choice.Value));
            }
        };
    }

    public event EventHandler<PaletteArgumentEditEventArgs>? Edited;
    public TextBox ValueInput { get; } = new() { Margin = new Thickness(0, 4, 0, 2), Padding = new Thickness(6), Background = new SolidColorBrush(Color.FromRgb(37, 44, 59)), Foreground = Brushes.White, CaretBrush = Brushes.White };
    public CheckBox Supplied { get; } = new() { Content = "Supplied", Margin = new Thickness(0, 4, 0, 0) };
    public ComboBox BooleanInput { get; } = new() { Margin = new Thickness(0, 4, 0, 2), Foreground = Brushes.Black, Background = Brushes.White };
    public TextBlock Diagnostics { get; } = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(255, 203, 153)) };

    public void Update(Guid activationId, ArgumentEditingSnapshot snapshot, ParameterInputState state)
    {
        this.updating = true;
        try
        {
            this.activation = activationId;
            this.editing = snapshot;
            this.parameter = state;
            this.label.Text = state.Label;
            this.description.Text = state.Parameter.Description ?? string.Empty;
            bool boolean = state.Parameter.ValueType == ParameterValueType.Boolean;
            this.ValueInput.Visibility = boolean ? Visibility.Collapsed : Visibility.Visible;
            this.Supplied.Visibility = boolean ? Visibility.Collapsed : Visibility.Visible;
            this.BooleanInput.Visibility = boolean ? Visibility.Visible : Visibility.Collapsed;
            if (this.ValueInput.Text != state.RawText)
            {
                this.ValueInput.Text = state.RawText;
            }

            this.ValueInput.IsReadOnly = state.IsPreset || snapshot.IsReadOnly;
            this.Supplied.IsChecked = state.IsSupplied;
            this.Supplied.IsEnabled = !state.IsPreset && !snapshot.IsReadOnly;
            this.BooleanInput.IsEnabled = !state.IsPreset && !snapshot.IsReadOnly;
            this.BooleanInput.SelectedIndex = !state.IsSupplied ? 0 : state.BooleanValue == true ? 2 : 1;
            this.Diagnostics.Text = string.Join("; ", snapshot.Diagnostics.Where(diagnostic => diagnostic.ParameterId == state.Parameter.Id).Select(diagnostic => diagnostic.Message));
            System.Windows.Automation.AutomationProperties.SetName(this.ValueInput, $"{state.Parameter.Name} {state.Parameter.ValueType} argument");
            System.Windows.Automation.AutomationProperties.SetName(this.Supplied, $"Supply {state.Parameter.Name}");
            System.Windows.Automation.AutomationProperties.SetName(this.BooleanInput, $"{state.Parameter.Name}: Not supplied, False or True");
            System.Windows.Automation.AutomationProperties.SetName(this.Diagnostics, $"{state.Parameter.Name} validation");
        }
        finally
        {
            this.updating = false;
        }
    }

    private void Send(ArgumentEdit change)
    {
        if (!this.updating && this.editing is not null && this.parameter is not null)
        {
            this.Edited?.Invoke(this, new(this.activation, this.editing.Invocation.Id, this.editing.SessionId,
                this.editing.Revision, this.parameter.Parameter.Id, change));
        }
    }
}