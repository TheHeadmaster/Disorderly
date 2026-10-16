using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Disorderly.Utilities;
using Button = System.Windows.Controls.Button;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseEventHandler = System.Windows.Input.MouseEventHandler;
using Point = System.Windows.Point;

namespace Disorderly.View.Windows;

public class BorderlessWindow : Window
{
    private HwndSource hwndSource = null!;

    private bool isManualDrag;

    private bool isMouseButtonDown;

    private Point mouseDownPosition;

    private Point positionBeforeDrag;

    static BorderlessWindow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(BorderlessWindow), new FrameworkPropertyMetadata(typeof(BorderlessWindow)));
    }

    public BorderlessWindow()
    {
        this.SizeChanged += this.OnSizeChanged;
        this.StateChanged += this.OnStateChanged;
        this.Closed += this.OnClosed;
        this.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(this.OnMouseButtonUp), true);
        this.AddHandler(MouseMoveEvent, new MouseEventHandler(this.OnMouseMove));
    }

    public Button CloseButton { get; private set; } = null!;

    public Grid HeaderBar { get; private set; } = null!;

    public double HeightBeforeMaximize { get; private set; } = 0;

    public Grid LayoutRoot { get; private set; } = null!;

    public Button MaximizeButton { get; private set; } = null!;

    public Button MinimizeButton { get; private set; } = null!;

    public WindowState PreviousState { get; private set; }

    public Button RestoreButton { get; private set; } = null!;

    public double WidthBeforeMaximize { get; private set; } = 0;

    public Grid WindowRoot { get; private set; } = null!;

    private void CloseButton_Click(object sender, RoutedEventArgs args) => this.Close();

    private void MaximizeButton_Click(object sender, RoutedEventArgs args) => this.ToggleWindowState();

    private void MinimizeButton_Click(object sender, RoutedEventArgs args) => this.WindowState = WindowState.Minimized;

    private void OnMouseButtonUp(object sender, MouseButtonEventArgs args)
    {
        this.isMouseButtonDown = false;
        this.isManualDrag = false;
        this.ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs args)
    {
        if (!this.isMouseButtonDown)
        {
            return;
        }

        Point position = args.GetPosition(this);
        Point screen = this.PointToScreen(position);
        double x = this.mouseDownPosition.X - position.X;
        double y = this.mouseDownPosition.Y - position.Y;
        if (Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2)) > 1)
        {
            double actualWidth = this.mouseDownPosition.X;

            if (this.mouseDownPosition.X <= 0)
            {
                actualWidth = 0;
            }
            else if (this.mouseDownPosition.X >= this.ActualWidth)
            {
                actualWidth = this.WidthBeforeMaximize;
            }

            if (this.WindowState == WindowState.Maximized)
            {
                this.ToggleWindowState();
                SystemHelper.MoveWindow(this, screen, new Point(Math.Min(actualWidth, this.ActualWidth), position.Y));
                this.CaptureMouse();
            }

            this.isManualDrag = true;

            SystemHelper.MoveWindow(this, screen, new Point(Math.Min(actualWidth, this.ActualWidth), this.mouseDownPosition.Y));
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (this.WindowState == WindowState.Normal)
        {
            this.HeightBeforeMaximize = this.ActualHeight;
            this.WidthBeforeMaximize = this.ActualWidth;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        this.hwndSource = (HwndSource)PresentationSource.FromVisual(this);
        this.hwndSource.AddHook(this.HandleWindowMessage);
    }

    private void OnClosed(object? sender, EventArgs args) => this.hwndSource?.RemoveHook(this.HandleWindowMessage);

    private nint HandleWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0024) // WM_GETMINMAXINFO uses physical pixels relative to the monitor origin.
        {
            DesktopNative.MonitorInfo monitor = DesktopNative.GetMonitor(DesktopNative.MonitorFromWindow(hwnd, 2));
            DesktopNative.MinMaxInfo info = Marshal.PtrToStructure<DesktopNative.MinMaxInfo>(lParam);
            PixelBounds bounds = MaximizedBounds.Calculate(monitor.Monitor.Bounds, monitor.Work.Bounds);
            info.MaxPosition.X = bounds.X;
            info.MaxPosition.Y = bounds.Y;
            info.MaxSize.X = bounds.Width;
            info.MaxSize.Y = bounds.Height;
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
        }

        // Leave DPI/display changes to Windows and WPF. Subsequent native sizing requests
        // obtain fresh bounds above without restoring/remaximizing or changing restore geometry.

        return 0;
    }

    private void OnStateChanged(object? sender, EventArgs args)
    {
        Thickness thickness = new(0);
        if (this.WindowState != WindowState.Maximized)
        {
            this.MaxHeight = double.PositiveInfinity;
            this.MaxWidth = double.PositiveInfinity;

            if (this.WindowState != WindowState.Maximized)
            {
                this.SetMaximizeButtonsVisibility(Visibility.Visible, Visibility.Collapsed);
            }
        }
        else
        {
            thickness = this.GetDefaultMarginForDpi();
            if (this.PreviousState == WindowState.Minimized || (this.Left == this.positionBeforeDrag.X && this.Top == this.positionBeforeDrag.Y))
            {
                thickness = this.GetFromMinimizedMarginForDpi();
            }

            this.SetMaximizeButtonsVisibility(Visibility.Collapsed, Visibility.Visible);
        }

        this.LayoutRoot?.Margin = thickness;
        this.PreviousState = this.WindowState;
    }

    private void OpenSystemContextMenu(MouseButtonEventArgs args)
    {
        Point position = args.GetPosition(this);
        Point screen = this.PointToScreen(position);
        int num = 36;
        if (position.Y < num)
        {
            nint handle = new WindowInteropHelper(this).Handle;
            nint systemMenu = NativeUtilities.GetSystemMenu(handle, false);
            if (this.WindowState != WindowState.Maximized)
            {
                NativeUtilities.EnableMenuItem(systemMenu, 61488, 0);
            }
            else
            {
                NativeUtilities.EnableMenuItem(systemMenu, 61488, 1);
            }

            int num1 = NativeUtilities.TrackPopupMenuEx(systemMenu, NativeUtilities.TPM_LEFTALIGN | NativeUtilities.TPM_RETURNCMD, Convert.ToInt32(screen.X + 2), Convert.ToInt32(screen.Y + 2), handle, nint.Zero);
            if (num1 == 0)
            {
                return;
            }

            NativeUtilities.PostMessageW(handle, 274, new nint(num1), nint.Zero);
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs args) => this.ToggleWindowState();

    private void SetMaximizeButtonsVisibility(Visibility maximizeButtonVisibility, Visibility reverseMaximizeButtonVisiility)
    {
        this.MaximizeButton?.Visibility = maximizeButtonVisibility;

        this.RestoreButton?.Visibility = reverseMaximizeButtonVisiility;
    }

    // WM_GETMINMAXINFO already constrains the native window to the working area.
    // No system-DPI-specific padding table is needed or applied a second time.
    protected virtual Thickness GetDefaultMarginForDpi() => new(0);

    protected virtual Thickness GetFromMinimizedMarginForDpi() => new(0);

    protected virtual void OnHeaderBarMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (this.isManualDrag)
        {
            return;
        }

        Point position = args.GetPosition(this);
        int headerBarHeight = 36;
        int leftmostClickableOffset = 50;

        if (position.X - this.LayoutRoot.Margin.Left <= leftmostClickableOffset && position.Y <= headerBarHeight)
        {
            if (args.ClickCount != 2)
            {
                this.OpenSystemContextMenu(args);
            }
            else
            {
                this.Close();
            }

            args.Handled = true;
            return;
        }

        if (args.ClickCount == 2 && this.ResizeMode == ResizeMode.CanResize)
        {
            this.ToggleWindowState();
            return;
        }

        if (this.WindowState == WindowState.Maximized)
        {
            this.isMouseButtonDown = true;
            this.mouseDownPosition = position;
        }
        else
        {
            try
            {
                this.positionBeforeDrag = new Point(this.Left, this.Top);
                this.DragMove();
            }
            catch { }
        }
    }

    protected override void OnInitialized(EventArgs args)
    {
        this.SourceInitialized += this.OnSourceInitialized;
        base.OnInitialized(args);
    }

    protected void ToggleWindowState() => this.WindowState = this.WindowState != WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;

    public T GetRequiredTemplateChild<T>(string childName) where T : DependencyObject => (T)this.GetTemplateChild(childName);

    public override void OnApplyTemplate()
    {
        this.WindowRoot = this.GetRequiredTemplateChild<Grid>("WindowRoot");
        this.LayoutRoot = this.GetRequiredTemplateChild<Grid>("LayoutRoot");
        this.MinimizeButton = this.GetRequiredTemplateChild<Button>("MinimizeButton");
        this.MaximizeButton = this.GetRequiredTemplateChild<Button>("MaximizeButton");
        this.RestoreButton = this.GetRequiredTemplateChild<Button>("RestoreButton");
        this.CloseButton = this.GetRequiredTemplateChild<Button>("CloseButton");
        this.HeaderBar = this.GetRequiredTemplateChild<Grid>("PART_HeaderBar");

        if (this.LayoutRoot is not null && this.WindowState == WindowState.Maximized)
        {
            this.LayoutRoot.Margin = this.GetDefaultMarginForDpi();
        }

        this.CloseButton?.Click += this.CloseButton_Click;

        this.MinimizeButton?.Click += this.MinimizeButton_Click;

        this.RestoreButton?.Click += this.RestoreButton_Click;

        this.MaximizeButton?.Click += this.MaximizeButton_Click;

        this.HeaderBar?.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(this.OnHeaderBarMouseLeftButtonDown));

        base.OnApplyTemplate();
    }
}