using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Disorderly.Input;
using Disorderly.Commands.Editing;
using Disorderly.View.Controls;
using Disorderly.Services;
using Disorderly.Utilities;
using Serilog;

namespace Disorderly.View.Windows;

public partial class CommandPaletteWindow : Window, ICommandPaletteView, IDraftPaletteView
{
    private HwndSource? source;
    private bool disposing;
    private bool placing;
    private bool placementQueued;
    private DispatcherOperation? placementOperation;
    private readonly WpfGestureAdapter keyboard = new();
    private Guid activationId;
    private int activationTimestamp;
    private PaletteSubmissionEventArgs? submission;
    private readonly RunInputCapture runInput = new();
    private Guid editorId;
    private bool browsingDrafts;
    private PaletteDraftLabel[] renderedDrafts = [];
    private Guid draftActivation;
    private DispatcherOperation? editorFocusOperation;
    private readonly Dictionary<string, ArgumentParameterEditor> parameterEditors = [];
    private PaletteBindingLabel[] renderedBindings = [];

    public CommandPaletteWindow()
    {
        this.InitializeComponent();
        this.Deactivated += this.OnDeactivated;
        this.PreviewKeyDown += this.OnPreviewKeyDown;
        this.PreviewKeyUp += this.OnPreviewKeyUp;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, this.OnCompositionStarted);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, this.OnCompositionStarted);
        TextCompositionManager.AddPreviewTextInputHandler(this, this.OnCompositionCompleted);
    }

    public event EventHandler<PaletteDraftEventArgs>? DraftActionRequested;
    public event EventHandler<PaletteDismissEventArgs>? DismissRequested;
    public event EventHandler<PaletteGestureEventArgs>? GestureReceived;
    public event EventHandler<PaletteSubmissionEventArgs>? SubmissionRequested;
    public event EventHandler<PaletteArgumentEditEventArgs>? ArgumentEdited;

    public void Render(PalettePresentation presentation)
    {
        this.Dispatcher.VerifyAccess();
        if (presentation.ActivationId != this.activationId)
        {
            this.runInput.Clear();
            this.activationId = presentation.ActivationId;
            this.activationTimestamp = Environment.TickCount;
            this.keyboard.Reset();
        }

        this.RenderDrafts(presentation);
        this.ContextTitle.Text = presentation.BrowsingDrafts ? "Suspended drafts" : presentation.ContextName;
        this.Breadcrumb.Text = presentation.Breadcrumb;
        if (!this.renderedBindings.SequenceEqual(presentation.Bindings))
        {
            this.renderedBindings = [.. presentation.Bindings];
            this.BindingList.ItemsSource = this.renderedBindings;
        }

        this.EmptyContext.Visibility = !presentation.BrowsingDrafts && presentation.Bindings.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        this.NavigationHint.Text = presentation.NavigationHint;
        this.SelectionPreview.Visibility = presentation.Selection is null ? Visibility.Collapsed : Visibility.Visible;
        this.SelectionTitle.Text = presentation.Selection?.Command.DisplayName ?? string.Empty;
        this.SelectionDescription.Text = presentation.Selection?.Command.Description ?? string.Empty;
        this.SelectionStatus.Text = presentation.SelectionStatus;
        this.EditingHint.Visibility = presentation.Editing?.Parameters.IsEmpty == false ? Visibility.Visible : Visibility.Collapsed;
        this.submission = presentation.Selection is null ? null
            : new PaletteSubmissionEventArgs(presentation.ActivationId, presentation.Selection.Id, presentation.AttemptId, presentation.Editing?.Revision);
        this.runInput.Update(this.submission, presentation.CanRun);
        this.UpdateEditors(presentation);

        if (presentation.RestoreEditorFocus)
        {
            this.RestoreEditorPosition(presentation);
        }

        this.RunButton.Visibility = presentation.ExecutionAvailable ? Visibility.Visible : Visibility.Collapsed;
        this.RunButton.IsEnabled = presentation.CanRun;
        this.RunButton.Content = presentation.RunLabel;
        System.Windows.Automation.AutomationProperties.SetName(this.RunButton, $"{presentation.RunLabel} selected command");
        System.Windows.Automation.AutomationProperties.SetName(this.ContextTitle, $"Active context: {presentation.ContextName}");
        if (this.IsVisible)
        {
            this.QueuePlacement();
        }
    }

    public new void Hide()
    {
        this.runInput.Clear();
        this.RunButton.ReleaseMouseCapture();
        if (this.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }

        this.runInput.Cancel(); // Native button capture/press has now been canceled, not retargeted.
        this.activationId = Guid.Empty;
        this.editorFocusOperation?.Abort();
        this.DraftEntries.Children.Clear();
        this.renderedDrafts = [];
        this.draftActivation = Guid.Empty;
        this.ResumePanel.Visibility = Visibility.Collapsed;
        this.DraftBrowser.Visibility = Visibility.Collapsed;
        this.ClearEditors();
        this.placementOperation?.Abort();
        this.placementOperation = null;
        this.placementQueued = false;
        this.keyboard.Reset();
        base.Hide();
        this.BindingList.ItemsSource = null;
        this.renderedBindings = [];
        this.SelectionPreview.Visibility = Visibility.Collapsed;
        this.SelectionTitle.Text = string.Empty;
        this.SelectionDescription.Text = string.Empty;
        this.SelectionStatus.Text = string.Empty;
        this.submission = null;
        this.RunButton.IsEnabled = false;
        this.RunButton.Visibility = Visibility.Collapsed;
    }

    public bool ShowAtForegroundMonitor()
    {
        this.Dispatcher.VerifyAccess();
        nint monitor = DesktopNative.SelectActivationMonitor(new WindowInteropHelper(this).Handle);
        new WindowInteropHelper(this).EnsureHandle();
        try
        {
            this.PlaceOnMonitor(monitor);
            this.Show();
        }
        catch (Win32Exception error)
        {
            Log.Warning(error, "Palette placement failed; a display may have been disconnected. Try activating again.");
            return false;
        }

        DesktopNative.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        this.Activate();
        this.FocusSurface.Focus();
        Keyboard.Focus(this.FocusSurface);
        bool focused = this.IsActive && this.IsKeyboardFocusWithin;
        if (!focused)
        {
            Log.Warning("Windows did not grant palette foreground focus; dismissing until the next explicit activation.");
        }

        return focused;
    }

    private void PlaceOnMonitor(nint monitor)
    {
        if (this.placing)
        {
            return;
        }

        this.placing = true;
        try
        {
            PixelBounds work = DesktopNative.GetMonitor(monitor).Work.Bounds;
            nint handle = new WindowInteropHelper(this).Handle;
            PalettePlacement.Position(
                this.IsVisible,
                work,
                () =>
                {
                    // Visible refreshes run after WPF handles WM_DPICHANGED on this monitor.
                    double scale = DesktopNative.GetDpiForWindow(handle) / 96.0;
                    return scale > 0 ? scale : VisualTreeHelper.GetDpi(this).DpiScaleX;
                },
                this.MeasurePaletteHeight,
                bounds => DesktopNative.Place(handle, bounds));
        }
        finally
        {
            this.placing = false;
        }
    }

    private double MeasurePaletteHeight(double width, double maxHeight)
    {
        // Measure content without changing native geometry or WPF window-size properties.
        FrameworkElement content = (FrameworkElement)this.Content;
        content.Measure(new Size(width, maxHeight));
        return content.DesiredSize.Height;
    }

    protected override void OnSourceInitialized(EventArgs args)
    {
        base.OnSourceInitialized(args);
        this.source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        this.source?.AddHook(this.HandleMessage);
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x001C && wParam == 0) // WM_ACTIVATEAPP also covers focus leaving an owned dialog/popup.
        {
            this.OnDeactivated(this, EventArgs.Empty);
        }

        // WPF processes WM_DPICHANGED itself. Re-anchor after its layout and after display/work-area changes.
        bool geometryChanged = (message is 0x02E0 or 0x007E) || (message == 0x001A && wParam == 0x002F); // SPI_SETWORKAREA
        if (geometryChanged)
        {
            this.QueuePlacement();
        }

        return 0;
    }

    private void QueuePlacement()
    {
        if (this.placing || this.placementQueued || this.disposing || !this.IsVisible)
        {
            return;
        }

        this.placementQueued = true;
        Guid activation = this.activationId;
        this.placementOperation = this.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            this.placementQueued = false;
            this.placementOperation = null;
            if (!this.disposing && this.IsVisible && this.activationId == activation)
            {
                try
                {
                    this.PlaceOnMonitor(DesktopNative.MonitorFromWindow(new WindowInteropHelper(this).Handle, 2));
                }
                catch (Win32Exception error)
                {
                    Log.Warning(error, "Palette display configuration changed during placement.");
                    this.DismissRequested?.Invoke(this, new PaletteDismissEventArgs(activation));
                }
            }
        }));
    }

    private void OnDeactivated(object? sender, EventArgs args)
    {
        // Capture provenance before deferring so a later activation cannot inherit this dismissal.
        Guid activation = this.activationId;
        this.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (activation == this.activationId && !this.disposing && this.IsVisible && !this.IsActive && !this.HasActiveOwnedWindow())
            {
                this.DismissRequested?.Invoke(this, new PaletteDismissEventArgs(this.activationId));
            }
        }));
    }

    private bool HasActiveOwnedWindow()
    {
        foreach (Window window in Application.Current?.Windows.Cast<Window>() ?? [])
        {
            if (!window.IsActive)
            {
                continue;
            }

            for (Window? owner = window.Owner; owner is not null; owner = owner.Owner)
            {
                if (owner == this)
                {
                    return true;
                }
            }
        }

        nint foreground = DesktopNative.GetForegroundWindow();
        // WPF popups have their own HWND; follow native ownership back to this palette.
        while (foreground != 0)
        {
            foreground = DesktopNative.GetWindow(foreground, 4); // GW_OWNER
            if (foreground == new WindowInteropHelper(this).Handle)
            {
                return true;
            }
        }

        return false;
    }

    private void OnDraftActionRequested(object? sender, PaletteDraftEventArgs args)
    {
        if (this.IsVisible)
        {
            this.DraftActionRequested?.Invoke(this, args);
        }
    }

    private void RenderDrafts(PalettePresentation presentation)
    {
        bool enteringBrowser = !this.browsingDrafts && presentation.BrowsingDrafts;
        bool leavingBrowser = this.browsingDrafts && !presentation.BrowsingDrafts;
        this.browsingDrafts = presentation.BrowsingDrafts;
        this.DraftBrowser.Visibility = this.browsingDrafts ? Visibility.Visible : Visibility.Collapsed;
        this.ShortcutHint.Visibility = this.browsingDrafts ? Visibility.Collapsed : Visibility.Visible;
        this.BindingList.Visibility = this.browsingDrafts ? Visibility.Collapsed : Visibility.Visible;
        this.ResumePanel.Visibility = presentation.ResumeSuggestion is null ? Visibility.Collapsed : Visibility.Visible;
        this.ResumeName.Text = presentation.ResumeSuggestion?.Name ?? string.Empty;
        this.ResumeStatus.Text = presentation.ResumeSuggestion?.Status ?? string.Empty;
        this.ResumeButton.Action = presentation.ResumeSuggestion is PaletteDraftLabel suggestion
            ? new PaletteDraftEventArgs(presentation.ActivationId, PaletteDraftAction.Resume, suggestion.Id, suggestion.Generation) : null;
        System.Windows.Automation.AutomationProperties.SetName(this.ResumeButton, $"Resume unfinished {presentation.ResumeSuggestion?.Name}");
        this.BrowseDraftsButton.Visibility = presentation.IsRoot && !this.browsingDrafts && presentation.Selection is null ? Visibility.Visible : Visibility.Collapsed;
        this.BrowseDraftsButton.Action = new PaletteDraftEventArgs(presentation.ActivationId, PaletteDraftAction.Browse);
        this.DraftBackButton.Action = new PaletteDraftEventArgs(presentation.ActivationId, PaletteDraftAction.Back);
        this.DraftMessage.Text = presentation.DraftMessage ?? string.Empty;
        PaletteDraftLabel[] drafts = presentation.Drafts.IsDefault ? [] : [.. presentation.Drafts];
        this.DraftEmpty.Visibility = drafts.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (this.draftActivation != presentation.ActivationId || !this.renderedDrafts.SequenceEqual(drafts))
        {
            this.draftActivation = presentation.ActivationId;
            this.renderedDrafts = drafts;
            this.DraftEntries.Children.Clear();
            foreach (PaletteDraftLabel draft in drafts)
            {
                StackPanel entry = new() { Margin = new Thickness(0, 12, 0, 4) };
                System.Windows.Automation.AutomationProperties.SetName(entry, draft.AccessibleName);
                entry.Children.Add(new TextBlock { Text = draft.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                entry.Children.Add(new TextBlock { Text = draft.Status, TextWrapping = TextWrapping.Wrap });
                entry.Children.Add(new TextBlock { Text = draft.Path, TextWrapping = TextWrapping.Wrap });
                // A short visible identity also distinguishes identically named invocations without exposing arguments.
                entry.Children.Add(new TextBlock { Text = $"Draft {draft.Id.ToString()[..8]}", FontSize = 11 });
                StackPanel actions = new() { Orientation = Orientation.Horizontal };
                foreach (PaletteDraftAction kind in new[] { PaletteDraftAction.Resume, PaletteDraftAction.Discard })
                {
                    DraftActionButton button = new() { Content = kind.ToString(), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 8, 0),
                        Action = new PaletteDraftEventArgs(presentation.ActivationId, kind, draft.Id, draft.Generation) };
                    System.Windows.Automation.AutomationProperties.SetName(button, $"{kind} {draft.AccessibleName}");
                    button.ActionRequested += this.OnDraftActionRequested;
                    actions.Children.Add(button);
                }

                entry.Children.Add(actions);
                this.DraftEntries.Children.Add(entry);
            }
        }

        if (enteringBrowser)
        {
            this.DraftBackButton.Focus();
        }

        if (leavingBrowser && presentation.Selection is null)
        {
            this.FocusSurface.Focus();
        }
    }

    private void RestoreEditorPosition(PalettePresentation presentation)
    {
        this.editorFocusOperation?.Abort();
        ArgumentEditingSnapshot? expected = presentation.Editing;
        if (expected is null)
        {
            return;
        }

        this.editorFocusOperation = this.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            this.editorFocusOperation = null;
            if (this.disposing || !this.IsVisible || this.activationId != presentation.ActivationId || this.editorId != expected.SessionId)
            {
                return;
            }

            ArgumentEditPosition? position = expected.Position;
            ArgumentParameterEditor? control = position is null ? null : this.parameterEditors.GetValueOrDefault(position.ParameterId);
            if (control is null)
            {
                this.FocusSurface.Focus();
                return;
            }

            if (control.BooleanInput.IsVisible && control.BooleanInput.IsEnabled)
            {
                control.BooleanInput.Focus();
            }
            else if (control.ValueInput.IsVisible && !control.ValueInput.IsReadOnly)
            {
                control.ValueInput.Focus();
                int start = Math.Clamp(position!.Start, 0, control.ValueInput.Text.Length);
                control.ValueInput.Select(start, Math.Clamp(position.Length, 0, control.ValueInput.Text.Length - start));
                control.BringIntoView();
            }
            else
            {
                this.FocusSurface.Focus();
            }
        }));
    }

    private void UpdateEditors(PalettePresentation presentation)
    {
        ArgumentEditingSnapshot? editing = presentation.Editing;
        if (editing?.SessionId != this.editorId)
        {
            this.ClearEditors();
            this.editorId = editing?.SessionId ?? Guid.Empty;
            if (editing is not null)
            {
                foreach (ParameterInputState state in editing.Parameters)
                {
                    ArgumentParameterEditor control = new();
                    control.Edited += this.OnArgumentEdited;
                    this.parameterEditors.Add(state.Parameter.Id, control);
                    this.ArgumentEditors.Children.Add(control);
                }
            }
        }

        if (editing is not null)
        {
            foreach (ParameterInputState state in editing.Parameters)
            {
                this.parameterEditors[state.Parameter.Id].Update(presentation.ActivationId, editing, state);
            }
        }
    }

    private void ClearEditors()
    {
        foreach (ArgumentParameterEditor control in this.parameterEditors.Values)
        {
            control.Edited -= this.OnArgumentEdited;
        }

        this.parameterEditors.Clear();
        this.ArgumentEditors.Children.Clear();
        this.editorId = Guid.Empty;
    }

    private void OnArgumentEdited(object? sender, PaletteArgumentEditEventArgs args)
    {
        // New input wins over deferred saved-caret restoration, even within the same editor.
        this.editorFocusOperation?.Abort();
        this.editorFocusOperation = null;
        this.ArgumentEdited?.Invoke(this, args);
    }

    private void OnRunClick(object sender, RoutedEventArgs args)
    {
        PaletteSubmissionEventArgs? request = this.runInput.Take();
        if (this.IsVisible && this.RunButton.IsEnabled && request is not null)
        {
            this.SubmissionRequested?.Invoke(this, request);
        }
    }

    private void OnRunMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount > 1 || unchecked(args.Timestamp - this.activationTimestamp) < 0)
        {
            this.runInput.Cancel();
            args.Handled = true;
            return;
        }

        this.runInput.Begin();
    }

    private void OnRunLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args)
    {
        if (!this.RunButton.IsKeyboardFocusWithin)
        {
            this.runInput.Cancel();
            this.RunButton.ReleaseMouseCapture();
        }
    }

    private void OnRunLostMouseCapture(object sender, MouseEventArgs args)
    {
        // Normal mouse-up releases capture immediately before Click; retain that valid capture.
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            this.runInput.Cancel();
        }
    }

    private void OnRunMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (!this.RunButton.IsMouseOver || this.runInput.IsStale || unchecked(args.Timestamp - this.activationTimestamp) < 0)
        {
            args.Handled = true;
            this.runInput.Cancel();
            this.RunButton.ReleaseMouseCapture();
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs args)
    {
        if (this.RunButton.IsKeyboardFocusWithin && args.Key is Key.Enter or Key.Space
            && (this.runInput.IsStale || unchecked(args.Timestamp - this.activationTimestamp) < 0))
        {
            args.Handled = true;
            this.runInput.Cancel();
            this.FocusSurface.Focus();
        }
    }

    /// <summary>Focused buttons own activation/focus keys; repeats must not click again.</summary>
    public static bool RunControlOwnsKey(Key key) => key is Key.Enter or Key.Space or Key.Tab;

    private void OnCompositionStarted(object sender, TextCompositionEventArgs args) => this.keyboard.BeginComposition();
    private void OnCompositionCompleted(object sender, TextCompositionEventArgs args) => this.keyboard.CompleteComposition();

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        // Input queued before this activation cannot route into its replacement session.
        // No text events dispatch gestures. Future editors can take ownership through OriginalSource.
        DependencyObject? focus = Keyboard.FocusedElement as DependencyObject ?? args.OriginalSource as DependencyObject;
        ArgumentParameterEditor? editor = PaletteInputOwnership.FindEditor(focus)
            ?? PaletteInputOwnership.FindEditor(args.OriginalSource as DependencyObject)
            ?? this.parameterEditors.Values.FirstOrDefault(control => control.BooleanInput.IsDropDownOpen);
        bool contextual = !PaletteInputOwnership.IsInteractive(focus);
        if (this.activationId == Guid.Empty || !this.IsVisible || !this.IsKeyboardFocusWithin
            || unchecked(args.Timestamp - this.activationTimestamp) < 0)
        {
            args.Handled = this.RunButton.IsKeyboardFocusWithin && RunControlOwnsKey(args.Key);
            return;
        }

        if (this.browsingDrafts)
        {
            if (args.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
            {
                args.Handled = true;
                if (!args.IsRepeat)
                {
                    this.DraftActionRequested?.Invoke(this, new(this.activationId, PaletteDraftAction.Back));
                }
            }

            return; // Browser controls own their keys, including ordinary Tab focus traversal.
        }

        if (editor is not null)
        {
            if (args.Key == Key.Escape)
            {
                if (this.keyboard.IsComposing)
                {
                    this.keyboard.CompleteComposition(); // Let WPF cancel composition first; do not navigate.
                }
                else if (!editor.BooleanInput.IsDropDownOpen)
                {
                    args.Handled = true;
                    if (!args.IsRepeat)
                    {
                        this.FocusSurface.Focus();
                    }
                }
            }

            return; // Text, IME, navigation and standard shortcuts belong exclusively to the control.
        }

        if (this.RunButton.IsKeyboardFocusWithin && RunControlOwnsKey(args.Key))
        {
            args.Handled = args.IsRepeat && args.Key is Key.Enter or Key.Space;
            if (!args.Handled && args.Key is Key.Enter or Key.Space)
            {
                this.runInput.Begin();
            }

            return;
        }

        contextual = this.RunButton.IsKeyboardFocusWithin || contextual;
        if (this.keyboard.TryTranslate(args.Key, args.SystemKey, Keyboard.Modifiers, args.IsRepeat,
            Keyboard.IsKeyDown(Key.RightAlt), contextual, out Disorderly.Routing.LogicalGesture gesture))
        {
            PaletteGestureEventArgs input = new(this.activationId, gesture);
            this.GestureReceived?.Invoke(this, input);
            args.Handled = input.Handled;
        }
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (!this.disposing)
        {
            args.Cancel = true;
            this.DismissRequested?.Invoke(this, new PaletteDismissEventArgs(this.activationId));
        }

        base.OnClosing(args);
    }

    public void Dispose()
    {
        if (this.disposing)
        {
            return;
        }

        this.disposing = true;
        this.submission = null;
        this.runInput.Clear();
        this.RunButton.ReleaseMouseCapture();
        if (this.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }

        this.runInput.Cancel(); // Native button capture/press has now been canceled, not retargeted.
        this.activationId = Guid.Empty;
        this.editorFocusOperation?.Abort();
        this.DraftEntries.Children.Clear();
        this.renderedDrafts = [];
        this.draftActivation = Guid.Empty;
        this.ResumePanel.Visibility = Visibility.Collapsed;
        this.DraftBrowser.Visibility = Visibility.Collapsed;
        this.ClearEditors();
        this.placementOperation?.Abort();
        this.source?.RemoveHook(this.HandleMessage);
        this.Deactivated -= this.OnDeactivated;
        this.PreviewKeyDown -= this.OnPreviewKeyDown;
        this.PreviewKeyUp -= this.OnPreviewKeyUp;
        TextCompositionManager.RemovePreviewTextInputStartHandler(this, this.OnCompositionStarted);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(this, this.OnCompositionStarted);
        TextCompositionManager.RemovePreviewTextInputHandler(this, this.OnCompositionCompleted);
        this.Close();
    }
}