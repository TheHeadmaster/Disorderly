using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Disorderly.View;

public static class TrayIconHandler
{
    private static NotifyIcon? trayIcon;

    public static void Initialize()
    {
        using Stream stream = Application.GetResourceStream(new("/Resources/Images/Disorderly.ico", UriKind.Relative)).Stream;

        trayIcon = new NotifyIcon
        {
            Icon = new Icon(stream),
            Visible = true,
            Text = "Disorderly",
        };

        trayIcon.DoubleClick += (_, _) => OpenMainDisorderlyWindow();

        ContextMenuStrip contextMenu = new();

        contextMenu.Items.Add("Open Disorderly", null, (_, _) => OpenMainDisorderlyWindow());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        trayIcon.ContextMenuStrip = contextMenu;
    }

    public static void Shutdown() => trayIcon?.Dispose();

    private static void OpenMainDisorderlyWindow()
    {
        // TODO: Open the main window here when we set that up.
    }
}
