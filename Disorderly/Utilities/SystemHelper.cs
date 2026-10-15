using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Disorderly.Utilities;

internal static class SystemHelper
{
    /// <summary>Use a specific visual's WPF DPI, including before its HWND exists.</summary>
    public static double GetCurrentDPIScaleFactor(Visual visual) => VisualTreeHelper.GetDpi(visual).DpiScaleX;

    public static int GetCurrentDPI(Visual visual) => (int)Math.Round(VisualTreeHelper.GetDpi(visual).PixelsPerInchX);

    /// <summary>Places a window using physical coordinates without scaling the desktop origin.</summary>
    public static void MoveWindow(Window window, Point screenPosition, Point anchorDip)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        DesktopNative.Place(handle, new PixelBounds(
            (int)Math.Round(screenPosition.X - (anchorDip.X * dpi.DpiScaleX)),
            (int)Math.Round(screenPosition.Y - (anchorDip.Y * dpi.DpiScaleY)), 0, 0), false);
    }
}