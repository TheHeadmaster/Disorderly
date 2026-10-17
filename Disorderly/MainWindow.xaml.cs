using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Disorderly.Commands;
using Disorderly.Configuration;
using Disorderly.Configuration.Editing;
using Disorderly.Routing;
using Serilog;

namespace Disorderly;
/// <summary>Reusable management surface. Controls edit a private session, never the live catalog.</summary>
public partial class MainWindow : Window
{
    private bool refreshing;
    private bool exitRequested;
    private ConfigurationEditToken commandToken;
    private ConfigurationEditToken contextToken;
    public MainWindow()
    {
        this.InitializeComponent();
        this.Closing += this.OnClosing;
        this.Sections.SelectionChanged += (_, args) =>
        {
            if (args.Source != this.Sections || this.Editor is null)
            {
                return;
            }

            if (this.Sections.SelectedIndex == 0 && this.CommandList.SelectedItem is Choice command)
            {
                this.ShowCommand(command.Id);
            }

            if (this.Sections.SelectedIndex == 1 && this.ContextList.SelectedItem is Choice context)
            {
                this.ShowContext(context.Id);
            }
        };
        this.Activated += (_, _) => this.RefreshStatus();
    }

    public MainWindow(ConfigurationService service) : this()
    {
        this.Editor = new ConfigurationEditorSession(service);
        this.BuildNewForms();
        this.RefreshLists();
        this.RefreshStatus();
    }

    public ConfigurationEditorSession? Editor { get; }

    public void OpenManagement()
    {
        this.RefreshStatus();
        this.Show();
        if (this.WindowState == WindowState.Minimized)
        {
            this.WindowState = WindowState.Normal;
        }

        this.Activate();
    }

    public void EndLifetime()
    {
        this.exitRequested = true;
        this.Close();
    }

    public void RefreshStatus()
    {
        if (this.Editor is null)
        {
            return;
        }

        this.Editor.RefreshPublicationStatus();
        this.StateLabel.Text = $"{(this.Editor.IsDirty ? "● Unsaved changes" : "Saved working copy")} · baseline {this.Editor.BaselineRevision} · edit {this.Editor.Revision}" + (this.Editor.IsStale ? " · NEWER PUBLICATION" : "");
        this.StatusLabel.Text = this.Editor.Status + "\n" + this.Editor.FileStatus;
        this.IssueCount.Text = $"{this.Editor.Diagnostics.Count(issue => !issue.Warning)} errors, {this.Editor.Diagnostics.Count(issue => issue.Warning)} warnings";
        this.Issues.ItemsSource = this.Editor.Diagnostics.Select(issue => $"{(issue.Warning ? "Warning" : "Error")} · {issue.Code} · {issue.Command?.CommandId ?? issue.Routing?.ContextId ?? issue.Location} {issue.Command?.BindingId ?? issue.Routing?.BindingId} {issue.Command?.ParameterId}: {issue.Message}").ToArray();
        this.SaveButton.IsEnabled = this.Editor.CanSave;
        this.Sections.IsEnabled = !this.Editor.IsSaving;
        this.DiscardButton.IsEnabled = !this.Editor.IsSaving;
    }

    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!this.exitRequested && this.Editor is not null)
        {
            args.Cancel = true;
            this.Hide();
        }
    }

    private void BuildNewForms()
    {
        this.NewCommandPanel.Children.Clear();
        this.NewContextPanel.Children.Clear();
        this.NewDefinition(this.NewCommandPanel, true);
        this.NewDefinition(this.NewContextPanel, false);
    }

    private void NewDefinition(StackPanel panel, bool command)
    {
        Label(panel, command ? "New command" : "New context");
        TextBox id = this.Text(panel, "New stable ID", "");
        TextBox name = this.Text(panel, "New display name", "");
        this.ActionButton(panel, "Add", () =>
        {
            ConfigurationEditToken token = this.Editor!.Token;
            bool added = command ? this.Editor.AddCommand(token, new(id.Text, name.Text, [])) : this.Editor.AddContext(token, new(id.Text, name.Text, []));
            if (!added)
            {
                this.StatusLabel.Text = "That ID already exists. Choose a distinct stable ID.";
                return;
            }

            id.Text = "";
            name.Text = "";
            this.RefreshLists(command ? this.Editor.Document.Commands.Last().Id : null, command ? null : this.Editor.Document.Routing.Contexts.Last().Id);
            this.RefreshStatus();
        });
    }

    private sealed record Choice(string Id, string Label)
    {
        public override string ToString() => this.Label;
    }

    private void RefreshLists(string? commandId = null, string? contextId = null)
    {
        this.refreshing = true;
        string? selectedCommand = commandId ?? (this.CommandList.SelectedItem as Choice)?.Id;
        string? selectedContext = contextId ?? (this.ContextList.SelectedItem as Choice)?.Id;
        this.CommandList.ItemsSource = this.Editor!.Document.Commands.Select(item => new Choice(item.Id, $"{item.DisplayName}\n{item.Id} · {(item.Execution is null ? "No execution" : "Launch Executable")}")).ToArray();
        this.CommandList.SelectedItem = this.CommandList.Items.Cast<Choice>().FirstOrDefault(item => item.Id == selectedCommand);
        this.ContextList.ItemsSource = this.Editor.Document.Routing.Contexts.Select(item => new Choice(item.Id, $"{item.DisplayName}{(item.Id == this.Editor.Document.Routing.RootContextId ? " · Root" : "")}\n{item.Id} · {item.Bindings.Count} bindings")).ToArray();
        this.ContextList.SelectedItem = this.ContextList.Items.Cast<Choice>().FirstOrDefault(item => item.Id == selectedContext);
        this.refreshing = false;
        if (commandId is not null)
        {
            this.ShowCommand(commandId);
        }

        if (contextId is not null)
        {
            this.ShowContext(contextId);
        }
    }

    private void OnCommandSelected(object sender, SelectionChangedEventArgs args)
    {
        if (!this.refreshing && this.CommandList.SelectedItem is Choice item)
        {
            this.ShowCommand(item.Id);
        }
    }

    private void OnContextSelected(object sender, SelectionChangedEventArgs args)
    {
        if (!this.refreshing && this.ContextList.SelectedItem is Choice item)
        {
            this.ShowContext(item.Id);
        }
    }

    private void ShowCommand(string id)
    {
        this.commandToken = this.Editor!.Select(id, null);
        ConfigurationEditToken token = this.commandToken;
        StackPanel panel = this.CommandDetails;
        panel.Children.Clear();
        panel.Tag = token;
        CommandDefinition command = this.Editor.Document.Commands.Single(item => item.Id == id);
        Label(panel, $"Command ID: {id}");
        void Change(Func<CommandDefinition, CommandDefinition> update)
        {
            if (this.Editor.Token != token || this.Editor.IsSaving)
            {
                return;
            }

            CommandDefinition current = this.Editor.Document.Commands.Single(item => item.Id == id);
            if (this.Editor.UpdateCommand(token, id, update(current)))
            {
                this.RefreshStatus();
                this.RefreshLists();
            }
        }

        this.Text(panel, "Display name", command.DisplayName, value => Change(current => current with { DisplayName = value }));
        this.Text(panel, "Description", command.Description ?? "", value => Change(current => current with { Description = value }));
        this.Policy(panel, "Default completion", command.DefaultCompletionPolicy, value => Change(current => current with { DefaultCompletionPolicy = value }));
        string execution = command.Execution is null && this.Editor.Input($"command/{id}/execution")?.Error is null ? "None" : "Launch Executable";
        this.Pick(panel, "Execution", ["None", "Launch Executable"], execution, value =>
        {
            if (value == "None")
            {
                Change(current => current with { Execution = null });
                this.Editor.SetInput(token, $"command/{id}/execution", "", null);
            }
            else
            {
                ParameterDefinition? eligible = this.Editor.Document.Commands.Single(item => item.Id == id).Parameters.FirstOrDefault(parameter => parameter.Required && parameter.ValueType == ParameterValueType.Path);
                if (eligible is null)
                {
                    this.Editor.SetInput(token, $"command/{id}/execution", value, "Add a required Path parameter, then select its executable source.");
                }
                else
                {
                    Change(current => current with { Execution = new CommandExecutionSpecification.LaunchExecutable(eligible.Id) });
                }
            }

            this.ShowCommand(id);
            this.RefreshStatus();
        });
        CommandDefinition updated = this.Editor.Document.Commands.Single(item => item.Id == id);
        Choice[] sources = updated.Parameters.Where(parameter => parameter.Required && parameter.ValueType == ParameterValueType.Path).Select(parameter => new Choice(parameter.Id, $"{parameter.Name} ({parameter.Id})")).ToArray();
        if (updated.Execution is CommandExecutionSpecification.LaunchExecutable || this.Editor.Input($"command/{id}/execution")?.Error is not null)
        {
            string? sourceId = (updated.Execution as CommandExecutionSpecification.LaunchExecutable)?.ExecutableParameterId;
            this.PickChoice(panel, "Executable parameter (required Path)", sources, sourceId, value =>
            {
                Change(current => current with { Execution = new CommandExecutionSpecification.LaunchExecutable(value) });
                this.Editor.SetInput(token, $"command/{id}/execution", value, null);
                this.RefreshStatus();
            });
        }

        string iconKind = command.Icon switch
        {
            CommandIcon.CustomFile => "Custom File",
            CommandIcon.BuiltIn => "Built In",
            CommandIcon.Automatic => "Automatic",
            _ => "Unspecified"
        };
        this.Pick(panel, "Icon metadata", ["Unspecified", "Automatic", "Custom File", "Built In"], iconKind, value =>
        {
            Change(current => current with { Icon = value switch
            {
                "Automatic" => new CommandIcon.Automatic(),
                "Custom File" => new CommandIcon.CustomFile(""),
                "Built In" => new CommandIcon.BuiltIn(""),
                _ => null
            } });
            this.ShowCommand(id);
        });
        if (command.Icon is CommandIcon.CustomFile custom)
        {
            this.Text(panel, "Custom icon path (not checked on disk)", custom.Path, value => Change(current => current with { Icon = new CommandIcon.CustomFile(value) }));
        }

        if (command.Icon is CommandIcon.BuiltIn builtIn)
        {
            this.Text(panel, "Built-in icon ID", builtIn.Id, value => Change(current => current with { Icon = new CommandIcon.BuiltIn(value) }));
        }

        Label(panel, "Parameters · declaration order");
        foreach (ParameterDefinition parameter in command.Parameters)
        {
            StackPanel row = Group(panel, parameter.Id);
            void Parameter(Func<ParameterDefinition, ParameterDefinition> update) => Change(current => current with { Parameters = current.Parameters.Select(item => item.Id == parameter.Id ? update(item) : item).ToArray() });
            this.Text(row, "Parameter name", parameter.Name, value => Parameter(current => current with { Name = value }));
            this.Text(row, "Parameter description", parameter.Description ?? "", value => Parameter(current => current with { Description = value }));
            this.Pick(row, "Value type", Enum.GetNames<ParameterValueType>(), parameter.ValueType.ToString(), value =>
            {
                Parameter(current => current with { ValueType = Enum.Parse<ParameterValueType>(value) });
                this.ShowCommand(id);
            });
            this.Check(row, "Required", parameter.Required, value =>
            {
                Parameter(current => current with { Required = value });
                this.ShowCommand(id);
            });
            void Move(int offset)
            {
                Change(current =>
                {
                    List<ParameterDefinition> parameters = current.Parameters.ToList();
                    int index = parameters.FindIndex(item => item.Id == parameter.Id);
                    int target = index + offset;
                    if (target >= 0 && target < parameters.Count)
                    {
                        (parameters[index], parameters[target]) = (parameters[target], parameters[index]);
                    }

                    return current with
                    {
                        Parameters = parameters.ToArray()
                    };
                });
                this.ShowCommand(id);
            }

            this.ActionButton(row, "Move Up", () => Move(-1));
            this.ActionButton(row, "Move Down", () => Move(1));
            this.ActionButton(row, "Remove new parameter", () =>
            {
                if (!this.Editor.IsNewParameter(id, parameter.Id))
                {
                    this.StatusLabel.Text = "Persisted parameter removal is deferred; references must be repaired explicitly.";
                    return;
                }

                Change(current => current with { Parameters = current.Parameters.Where(item => item.Id != parameter.Id).ToArray() });
                this.ShowCommand(id);
            });
        }

        TextBox parameterId = this.Text(panel, "New parameter ID", "");
        TextBox parameterName = this.Text(panel, "New parameter name", "");
        this.ActionButton(panel, "Add parameter", () =>
        {
            Change(current => current with { Parameters = [..current.Parameters, new(parameterId.Text, parameterName.Text, ParameterValueType.Text)] });
            this.ShowCommand(id);
        });
        this.ActionButton(panel, "Remove new unreferenced command", () =>
        {
            if (!this.Editor.RemoveNewCommand(token, id))
            {
                this.StatusLabel.Text = "Only a new, unreferenced command can be removed here. Remove references first.";
                return;
            }

            panel.Children.Clear();
            this.RefreshLists();
            this.RefreshStatus();
        });
    }

    private void ShowContext(string id)
    {
        this.contextToken = this.Editor!.Select(null, id);
        ConfigurationEditToken token = this.contextToken;
        StackPanel panel = this.ContextDetails;
        panel.Children.Clear();
        panel.Tag = token;
        ContextDefinition context = this.Editor.Document.Routing.Contexts.Single(item => item.Id == id);
        Label(panel, $"Context ID: {id}" + (id == this.Editor.Document.Routing.RootContextId ? " · Root" : ""));
        this.Text(panel, "Context display name", context.DisplayName, value =>
        {
            this.Editor.RenameContext(token, id, value);
            this.RefreshLists();
            this.RefreshStatus();
        });
        string[] incoming = this.Editor.Document.Routing.Contexts.Where(item => item.Bindings.Any(binding => binding.Destination is BindingDestination.Context target && target.ContextId == id)).Select(item => $"{item.DisplayName} ({item.Id})").ToArray();
        Label(panel, "Referenced from: " + (incoming.Length == 0 ? "none" : string.Join(", ", incoming)) + ". This edits one shared context.");
        foreach (BindingDefinition binding in context.Bindings)
        {
            this.BindingForm(panel, context, binding, token);
        }

        TextBox bindingId = this.Text(panel, "New binding ID", "");
        this.ActionButton(panel, "Add binding", () =>
        {
            CommandDefinition? first = this.Editor.Document.Commands.FirstOrDefault();
            BindingDestination destination = first is null ? new BindingDestination.Context(id) : new BindingDestination.Command(first.Id);
            if (!this.Editor.PutBinding(token, id, new(bindingId.Text, id, new LogicalGesture(""), destination), true))
            {
                this.StatusLabel.Text = "Choose a distinct binding ID.";
                return;
            }

            this.ShowContext(id);
            this.RefreshLists();
            this.RefreshStatus();
        });
        this.ActionButton(panel, "Remove new unreferenced context", () =>
        {
            if (!this.Editor.RemoveNewContext(token, id))
            {
                this.StatusLabel.Text = "Root, persisted or referenced contexts cannot be removed here.";
                return;
            }

            panel.Children.Clear();
            this.RefreshLists();
            this.RefreshStatus();
        });
    }

    private void BindingForm(StackPanel parent, ContextDefinition context, BindingDefinition original, ConfigurationEditToken token)
    {
        StackPanel panel = Group(parent, $"Binding: {original.Id} · owner {context.Id}");
        BindingDefinition Current() => this.Editor!.Document.Routing.Contexts.Single(item => item.Id == context.Id).Bindings.Single(item => item.Id == original.Id);
        void Change(Func<BindingDefinition, BindingDefinition> update)
        {
            if (this.Editor!.Token != token || this.Editor.IsSaving)
            {
                return;
            }

            if (this.Editor.PutBinding(token, context.Id, update(Current())))
            {
                this.RefreshStatus();
            }
        }

        TextBlock preview = Label(panel, original.Gesture.ToString());
        this.Text(panel, "Logical key", original.Gesture.Key ?? "", value =>
        {
            Change(current => current with { Gesture = new(value, current.Gesture.Modifiers) });
            if (this.Editor!.Token == token)
            {
                preview.Text = Current().Gesture.ToString();
            }
        });
        foreach (GestureModifiers modifier in new[]
        {
            GestureModifiers.Control,
            GestureModifiers.Shift,
            GestureModifiers.Alt,
            GestureModifiers.Meta
        }

        )
        {
            this.Check(panel, modifier.ToString(), original.Gesture.Modifiers.HasFlag(modifier), value =>
            {
                Change(current => current with { Gesture = new(current.Gesture.Key ?? "", value ? current.Gesture.Modifiers | modifier : current.Gesture.Modifiers & ~modifier) });
                preview.Text = Current().Gesture.ToString();
            });
        }

        this.Pick(panel, "Destination kind", ["Command", "Context"], original.Destination is BindingDestination.Command ? "Command" : "Context", value =>
        {
            Change(current => current with { Destination = value == "Command" ? new BindingDestination.Command(this.Editor!.Document.Commands.FirstOrDefault()?.Id ?? "") : new BindingDestination.Context(this.Editor!.Document.Routing.Contexts.First().Id) });
            this.ShowContext(context.Id);
        });
        if (original.Destination is BindingDestination.Context target)
        {
            this.ActionButton(panel, "Clear pending command preset input", () =>
            {
                this.Editor!.ClearFieldInputs(token, $"binding/{original.Id}/");
                this.RefreshStatus();
            });
            this.PickChoice(panel, "Target context (reused, not copied)", this.Editor!.Document.Routing.Contexts.Select(item => new Choice(item.Id, $"{item.DisplayName} ({item.Id})")).ToArray(), target.ContextId, value => Change(current => current with { Destination = new BindingDestination.Context(value) }));
        }
        else if (original.Destination is BindingDestination.Command targetCommand)
        {
            this.PickChoice(panel, "Target command", this.Editor!.Document.Commands.Select(item => new Choice(item.Id, $"{item.DisplayName} ({item.Id})")).ToArray(), targetCommand.CommandId, value =>
            {
                Change(current => current with { Destination = ((BindingDestination.Command)current.Destination)with { CommandId = value } });
                this.ShowContext(context.Id);
            });
            void Metadata(Func<CommandBindingMetadata, CommandBindingMetadata> update) => Change(current => current with { Destination = ((BindingDestination.Command)current.Destination)with { Metadata = update(((BindingDestination.Command)current.Destination).Metadata ?? new()) } });
            this.Policy(panel, "Binding completion override", targetCommand.Metadata?.CompletionOverride, value => Metadata(current => current with { CompletionOverride = value }));
            this.ActionButton(panel, "Clear all presets and pending preset input", () =>
            {
                if (!this.Editor.ClearFieldInputs(token, $"binding/{original.Id}/"))
                {
                    return;
                }

                Metadata(current => current with { Presets = ArgumentSet.Empty });
                this.ShowContext(context.Id);
            });
            CommandDefinition? command = this.Editor.Document.Commands.FirstOrDefault(item => item.Id == targetCommand.CommandId);
            if (command is not null)
            {
                foreach (ParameterDefinition parameter in command.Parameters)
                {
                    this.PresetForm(panel, original.Id, parameter, token, () => ((BindingDestination.Command)Current().Destination).Metadata?.Presets ?? ArgumentSet.Empty, arguments => Metadata(current => current with { Presets = arguments }));
                }

                foreach (ArgumentAssignment unknown in (targetCommand.Metadata?.Presets ?? ArgumentSet.Empty).Assignments.Where(item => !command.Parameters.Any(parameter => parameter.Id == item.ParameterId)))
                {
                    this.ActionButton(panel, $"Remove unknown preset {unknown.ParameterId}", () =>
                    {
                        Metadata(current => current with { Presets = new((current.Presets ?? ArgumentSet.Empty).Assignments.Where(item => item.ParameterId != unknown.ParameterId)) });
                        this.ShowContext(context.Id);
                    });
                }
            }
        }

        this.ActionButton(panel, "Remove binding", () =>
        {
            this.Editor!.RemoveBinding(token, context.Id, original.Id);
            this.ShowContext(context.Id);
            this.RefreshLists();
            this.RefreshStatus();
        });
    }

    private void PresetForm(StackPanel parent, string bindingId, ParameterDefinition parameter, ConfigurationEditToken token, Func<ArgumentSet> get, Action<ArgumentSet> set)
    {
        StackPanel panel = Group(parent, $"Preset: {parameter.Name} ({parameter.Id}) · {parameter.ValueType}");
        string key = $"binding/{bindingId}/{parameter.Id}/{parameter.ValueType}";
        ArgumentValue? value = get().Assignments.FirstOrDefault(item => item.ParameterId == parameter.Id)?.Value;
        bool present = value is not null || this.Editor!.Input(key)?.Error is not null;
        void Assign(ArgumentValue? next) => set(new(get().Assignments.Where(item => item.ParameterId != parameter.Id).Concat(next is null ? [] : new[] { new ArgumentAssignment(parameter.Id, next) })));
        StackPanel field = new();
        TextBlock error = new()
        {
            TextWrapping = TextWrapping.Wrap
        };
        this.Check(panel, "Supply preset", present, supplied =>
        {
            if (!this.Editor!.ClearFieldInputs(token, $"binding/{bindingId}/{parameter.Id}/"))
            {
                return;
            }

            Assign(supplied ? parameter.ValueType switch
            {
                ParameterValueType.Text => new ArgumentValue.Text(""),
                ParameterValueType.Boolean => new ArgumentValue.Boolean(false),
                ParameterValueType.Integer => new ArgumentValue.Integer(0),
                _ => new ArgumentValue.Path("")} : null);
            this.ShowContext(this.Editor.SelectedContextId!);
        });
        panel.Children.Add(field);
        panel.Children.Add(error);
        if (!present)
        {
            return;
        }

        bool compatible = value is null || (parameter.ValueType, value)is (ParameterValueType.Text, ArgumentValue.Text) or (ParameterValueType.Boolean, ArgumentValue.Boolean) or (ParameterValueType.Integer, ArgumentValue.Integer) or (ParameterValueType.Path, ArgumentValue.Path);
        if (!compatible)
        {
            Label(field, "Existing preset type is incompatible. Clear Supply preset explicitly, then supply a correctly typed value.");
            return;
        }

        if (parameter.ValueType == ParameterValueType.Boolean)
        {
            this.Pick(field, "Boolean preset", ["False", "True"], value is ArgumentValue.Boolean { Value: true } ? "True" : "False", raw =>
            {
                this.Editor!.SetPresetInput(token, this.Editor.SelectedContextId!, bindingId, parameter.Id, true, raw);
                this.RefreshStatus();
            });
            return;
        }

        string rawValue = this.Editor!.Input(key)?.Raw ?? value switch
        {
            ArgumentValue.Text text => text.Value,
            ArgumentValue.Path path => path.Value,
            ArgumentValue.Integer integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            _ => ""
        };
        error.Text = this.Editor.Input(key)?.Error;
        this.Text(field, "Preset value", rawValue, raw =>
        {
            if (this.Editor.Token != token || this.Editor.IsSaving)
            {
                return;
            }

            this.Editor.SetPresetInput(token, this.Editor.SelectedContextId!, bindingId, parameter.Id, true, raw);
            error.Text = this.Editor.Input(key)?.Error;
            this.RefreshStatus();
        });
    }

    private async void OnSave(object sender, RoutedEventArgs args)
    {
        if (this.Editor is null)
        {
            return;
        }

        try
        {
            Task<ConfigurationResult?> saving = this.Editor.SaveAsync();
            this.RefreshStatus();
            await saving;
            this.RefreshLists();
        }
        catch (Exception error)
        {
            Log.Warning("Management Save raised {ExceptionType}; working copy retained.", error.GetType().Name);
            this.StatusLabel.Text = "Save failed; keep these edits and inspect the file before retrying.";
        }
        finally
        {
            this.RefreshStatus();
        }
    }

    private void OnDiscard(object sender, RoutedEventArgs args)
    {
        if (this.Editor is null)
        {
            return;
        }

        bool consent = !this.Editor.IsDirty || MessageBox.Show(this, "Discard all unsaved configuration edits and adopt the current published configuration?", "Discard Changes", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        if (this.Editor.AdoptPublished(consent))
        {
            this.CommandDetails.Children.Clear();
            this.ContextDetails.Children.Clear();
            this.RefreshLists();
            if (this.Sections.SelectedIndex == 0 && this.CommandList.SelectedItem is Choice command)
            {
                this.ShowCommand(command.Id);
            }

            if (this.Sections.SelectedIndex == 1 && this.ContextList.SelectedItem is Choice context)
            {
                this.ShowContext(context.Id);
            }

            this.RefreshStatus();
        }
    }

    private void OnInspect(object sender, RoutedEventArgs args)
    {
        if (this.Editor is not null)
        {
            MessageBox.Show(this, this.Editor.PublishedSummary + "\n\nUnsaved working copy is unchanged. Tray Reload reads disk; Discard explicitly adopts the publication.", "Current published configuration");
        }
    }

    private bool EventAllowed(DependencyObject source)
    {
        if (this.Editor is null || this.Editor.IsSaving)
        {
            return false;
        }

        for (DependencyObject? current = source; current is not null; current = LogicalTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: ConfigurationEditToken token })
            {
                return token == this.Editor.Token;
            }
        }

        return true;
    }

    private static TextBlock Label(Panel panel, string text)
    {
        TextBlock label = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 3)
        };
        panel.Children.Add(label);
        return label;
    }

    private static StackPanel Group(Panel panel, string title)
    {
        StackPanel content = new()
        {
            Margin = new Thickness(8)
        };
        Label(content, title);
        panel.Children.Add(new Border { BorderThickness = new Thickness(1), BorderBrush = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, 8, 0, 4), Child = content });
        return content;
    }

    private TextBox Text(Panel panel, string name, string value, Action<string>? change = null)
    {
        TextBox field = new()
        {
            Text = value,
            Margin = new Thickness(0, 0, 0, 4),
            MinWidth = 100
        };
        Label label = new()
        {
            Content = name,
            Target = field,
            Padding = new Thickness(0, 4, 0, 2)
        };
        AutomationProperties.SetName(field, name);
        panel.Children.Add(label);
        panel.Children.Add(field);
        if (change is not null)
        {
            field.TextChanged += (_, _) =>
            {
                if (this.EventAllowed(panel))
                {
                    change(field.Text);
                }
            };
        }

        return field;
    }

    private void Check(Panel panel, string name, bool value, Action<bool> change)
    {
        CheckBox field = new()
        {
            Content = name,
            IsChecked = value,
            Margin = new Thickness(0, 5, 0, 5)
        };
        AutomationProperties.SetName(field, name);
        field.Click += (_, _) =>
        {
            if (this.EventAllowed(panel))
            {
                change(field.IsChecked == true);
            }
        };
        panel.Children.Add(field);
    }

    private void Pick(Panel panel, string name, string[] choices, string value, Action<string> change)
    {
        ComboBox field = new()
        {
            ItemsSource = choices,
            SelectedItem = value,
            MinWidth = 100
        };
        panel.Children.Add(new Label { Content = name, Target = field });
        AutomationProperties.SetName(field, name);
        field.SelectionChanged += (_, _) =>
        {
            if (this.EventAllowed(panel) && field.SelectedItem is string selected)
            {
                change(selected);
            }
        };
        panel.Children.Add(field);
    }

    private void PickChoice(Panel panel, string name, Choice[] choices, string? id, Action<string> change)
    {
        ComboBox field = new()
        {
            ItemsSource = choices,
            SelectedItem = choices.FirstOrDefault(item => item.Id == id),
            MinWidth = 100
        };
        panel.Children.Add(new Label { Content = name, Target = field });
        AutomationProperties.SetName(field, name);
        field.SelectionChanged += (_, _) =>
        {
            if (this.EventAllowed(panel) && field.SelectedItem is Choice selected)
            {
                change(selected.Id);
            }
        };
        panel.Children.Add(field);
    }

    private void Policy(Panel panel, string name, CompletionPolicy? value, Action<CompletionPolicy?> change) => this.Pick(panel, name, ["Unspecified", "Dismiss", "Stay Open"], value switch
    {
        CompletionPolicy.Dismiss => "Dismiss",
        CompletionPolicy.StayOpen => "Stay Open",
        _ => "Unspecified"
    }, selected => change(selected switch
    {
        "Dismiss" => CompletionPolicy.Dismiss,
        "Stay Open" => CompletionPolicy.StayOpen,
        _ => null
    }));
    private void ActionButton(Panel panel, string name, Action action)
    {
        Button button = new()
        {
            Content = name,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 4, 0, 4)
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            if (this.EventAllowed(panel))
            {
                action();
            }
        };
        panel.Children.Add(button);
    }
}