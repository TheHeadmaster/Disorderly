using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Serilog;

namespace Disorderly.Services;

/// <summary>A dispatcher-owned message-only HWND, independent of all visible windows.</summary>
internal sealed class WindowsHotkeyRegistration : IHotkeyRegistration
{
    private const int HotkeyId = 1;
    private readonly HwndSource source;
    private readonly Dispatcher dispatcher;
    private bool registered;
    private bool disposed;

    public WindowsHotkeyRegistration()
    {
        this.dispatcher = Dispatcher.CurrentDispatcher;
        this.source = new HwndSource(new HwndSourceParameters("Disorderly.Activation")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        this.source.AddHook(this.HandleMessage);
    }

    public event EventHandler? Activated;

    public bool Register()
    {
        this.dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(this.disposed, this);
        if (this.registered)
        {
            return true;
        }

        this.registered = RegisterHotKey(this.source.Handle, HotkeyId, 0x4006, 0x44); // NOREPEAT | CONTROL | SHIFT, D
        if (!this.registered)
        {
            int error = Marshal.GetLastWin32Error();
            Log.Error("Cannot register Ctrl+Shift+D: {Error} ({Message}). Use Open Command Palette in the tray menu.", error, new Win32Exception(error).Message);
        }

        return this.registered;
    }

    public void Unregister()
    {
        this.dispatcher.VerifyAccess();
        if (this.registered)
        {
            if (!UnregisterHotKey(this.source.Handle, HotkeyId))
            {
                Log.Warning("UnregisterHotKey failed with {Error}.", Marshal.GetLastWin32Error());
            }

            this.registered = false;
        }
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == HotkeyId && this.registered)
        {
            handled = true;
            this.Activated?.Invoke(this, EventArgs.Empty);
        }

        return 0;
    }

    public void Dispose()
    {
        this.dispatcher.VerifyAccess();
        if (this.disposed)
        {
            return;
        }

        this.Unregister();
        this.source.RemoveHook(this.HandleMessage);
        this.source.Dispose();
        this.disposed = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}