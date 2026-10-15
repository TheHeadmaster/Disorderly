using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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

    public CommandPaletteWindow()
    {
        this.InitializeComponent();
        this.Deactivated += this.OnDeactivated;
        this.PreviewKeyDown += this.OnPreviewKeyDown;
    }

    public event EventHandler? DismissRequested;

    // Future interaction/draft service integration: expose a genuine resume suggestion here.
    public void ResetRoot() => this.CommandInput.Clear();

    public bool ShowAtForegroundMonitor()
    {
        this.Dispatcher.VerifyAccess();
        nint monitor = DesktopNative.SelectActivationMonitor(new WindowInteropHelper(this).Handle);
        new WindowInteropHelper(this).EnsureHandle();
        try
        {
            this.PlaceOnMonitor(monitor);
            this.Show();
            this.PlaceOnMonitor(monitor);
        }
        catch (Win32Exception error)
        {
            Log.Warning(error, "Palette placement failed; a display may have been disconnected. Try activating again.");
            return false;
        }

        DesktopNative.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        this.Activate();
        this.CommandInput.Focus();
        Keyboard.Focus(this.CommandInput);
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
            // Move the hidden HWND first so WPF receives the destination monitor's DPI.
            DesktopNative.Place(handle, new PixelBounds(work.X, work.Y, 1, 1));
            double scale = DesktopNative.GetDpiForWindow(handle) / 96.0;
            if (scale <= 0)
            {
                scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            }

            PixelBounds available = PalettePlacement.Calculate(work, 360, work.Height / scale, scale, scale);
            this.MaxWidth = available.Width / scale;
            this.MaxHeight = available.Height / scale;
            this.Width = Math.Min(360, this.MaxWidth);
            this.UpdateLayout();
            FrameworkElement content = (FrameworkElement)this.Content;
            content.Measure(new Size(this.Width, this.MaxHeight));
            double height = Math.Min(content.DesiredSize.Height, this.MaxHeight);
            PixelBounds bounds = PalettePlacement.Calculate(work, this.Width, Math.Max(1, height), scale, scale);
            DesktopNative.Place(handle, bounds);
        }
        finally
        {
            this.placing = false;
        }
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
        if ((message is 0x02E0 or 0x007E or 0x001A) && !this.placing && !this.placementQueued && this.IsVisible)
        {
            this.placementQueued = true;
            this.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                this.placementQueued = false;
                if (this.IsVisible && !this.disposing)
                {
                    try
                    {
                        this.PlaceOnMonitor(DesktopNative.MonitorFromWindow(hwnd, 2));
                    }
                    catch (Win32Exception error)
                    {
                        Log.Warning(error, "Palette display configuration changed during placement.");
                        this.DismissRequested?.Invoke(this, EventArgs.Empty);
                    }
                }
            }));
        }

        return 0;
    }

    private void OnDeactivated(object? sender, EventArgs args)
    {
        // Defer until focus settles; owned dialogs and future owned popups may take focus.
        this.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!this.disposing && this.IsVisible && !this.IsActive && !this.HasActiveOwnedWindow())
            {
                this.DismissRequested?.Invoke(this, EventArgs.Empty);
            }
        }));
    }

    private bool HasActiveOwnedWindow()
    {
        foreach (Window window in Application.Current.Windows)
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

    private void OnPreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            args.Handled = true;
            this.DismissRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClosing(CancelEventArgs args)
    {
        if (!this.disposing)
        {
            args.Cancel = true;
            this.DismissRequested?.Invoke(this, EventArgs.Empty);
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
        this.source?.RemoveHook(this.HandleMessage);
        this.Deactivated -= this.OnDeactivated;
        this.PreviewKeyDown -= this.OnPreviewKeyDown;
        this.Close();
    }
}