using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Disorderly.Input;
using Disorderly.Services;
using Disorderly.Utilities;
using Serilog;

namespace Disorderly.View.Windows;

public partial class CommandPaletteWindow : Window, ICommandPaletteView
{
    private HwndSource? source;
    private bool disposing;
    private bool placing;
    private bool placementQueued;
    private DispatcherOperation? placementOperation;
    private readonly WpfGestureAdapter keyboard = new();
    private Guid activationId;
    private int activationTimestamp;

    public CommandPaletteWindow()
    {
        this.InitializeComponent();
        this.Deactivated += this.OnDeactivated;
        this.PreviewKeyDown += this.OnPreviewKeyDown;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, this.OnCompositionStarted);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, this.OnCompositionStarted);
        TextCompositionManager.AddPreviewTextInputHandler(this, this.OnCompositionCompleted);
    }

    public event EventHandler<PaletteDismissEventArgs>? DismissRequested;
    public event EventHandler<PaletteGestureEventArgs>? GestureReceived;

    public void Render(PalettePresentation presentation)
    {
        this.Dispatcher.VerifyAccess();
        if (presentation.ActivationId != this.activationId)
        {
            this.activationId = presentation.ActivationId;
            this.activationTimestamp = Environment.TickCount;
            this.keyboard.Reset();
        }

        this.ContextTitle.Text = presentation.ContextName;
        this.Breadcrumb.Text = presentation.Breadcrumb;
        this.BindingList.ItemsSource = presentation.Bindings;
        this.EmptyContext.Visibility = presentation.Bindings.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        this.NavigationHint.Text = presentation.NavigationHint;
        this.SelectionPreview.Visibility = presentation.Selection is null ? Visibility.Collapsed : Visibility.Visible;
        this.SelectionTitle.Text = presentation.Selection?.Command.DisplayName ?? string.Empty;
        this.SelectionDescription.Text = presentation.Selection?.Command.Description ?? string.Empty;
        this.SelectionStatus.Text = presentation.SelectionStatus;
        System.Windows.Automation.AutomationProperties.SetName(this.ContextTitle, $"Active context: {presentation.ContextName}");
        if (this.IsVisible)
        {
            this.QueuePlacement();
        }
    }

    public new void Hide()
    {
        this.activationId = Guid.Empty;
        this.placementOperation?.Abort();
        this.placementOperation = null;
        this.placementQueued = false;
        this.keyboard.Reset();
        base.Hide();
        this.BindingList.ItemsSource = null;
        this.SelectionPreview.Visibility = Visibility.Collapsed;
        this.SelectionTitle.Text = string.Empty;
        this.SelectionDescription.Text = string.Empty;
        this.SelectionStatus.Text = string.Empty;
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

    private void OnCompositionStarted(object sender, TextCompositionEventArgs args) => this.keyboard.BeginComposition();
    private void OnCompositionCompleted(object sender, TextCompositionEventArgs args) => this.keyboard.CompleteComposition();

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        // Input queued before this activation cannot route into its replacement session.
        // No text events dispatch gestures. Future editors can take ownership through OriginalSource.
        bool contextual = args.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase;
        if (this.activationId == Guid.Empty || !this.IsVisible || !this.IsKeyboardFocusWithin
            || unchecked(args.Timestamp - this.activationTimestamp) < 0)
        {
            return;
        }

        if (this.keyboard.TryTranslate(args.Key, args.SystemKey, Keyboard.Modifiers, args.IsRepeat,
            Keyboard.IsKeyDown(Key.RightAlt), contextual, out Disorderly.Routing.LogicalGesture gesture))
        {
            args.Handled = true;
            this.GestureReceived?.Invoke(this, new PaletteGestureEventArgs(this.activationId, gesture));
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
        this.activationId = Guid.Empty;
        this.placementOperation?.Abort();
        this.source?.RemoveHook(this.HandleMessage);
        this.Deactivated -= this.OnDeactivated;
        this.PreviewKeyDown -= this.OnPreviewKeyDown;
        TextCompositionManager.RemovePreviewTextInputStartHandler(this, this.OnCompositionStarted);
        TextCompositionManager.RemovePreviewTextInputUpdateHandler(this, this.OnCompositionStarted);
        TextCompositionManager.RemovePreviewTextInputHandler(this, this.OnCompositionCompleted);
        this.Close();
    }
}