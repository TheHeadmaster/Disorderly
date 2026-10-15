using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Disorderly.Utilities;

internal static class DesktopNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly PixelBounds Bounds => new(this.Left, this.Top, this.Right - this.Left, this.Bottom - this.Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    internal static MonitorInfo GetMonitor(nint monitor)
    {
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return info;
    }

    internal static nint SelectActivationMonitor(nint palette)
    {
        nint foreground = GetForegroundWindow();
        nint monitor = foreground != 0 && foreground != palette ? MonitorFromWindow(foreground, 0) : 0;
        if (monitor == 0 && GetCursorPos(out NativePoint cursor))
        {
            monitor = MonitorFromPoint(cursor, 2);
        }

        return monitor != 0 ? monitor : MonitorFromPoint(default, 1);
    }

    internal static void Place(nint window, PixelBounds bounds, bool resize = true)
    {
        if (!SetWindowPos(window, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0014u | (resize ? 0u : 0x0001u)))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);
}