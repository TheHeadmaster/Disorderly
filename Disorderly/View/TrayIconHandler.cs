using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Disorderly.View;

public static class TrayIconHandler
{
    private static NotifyIcon? trayIcon;
    private static ToolStripMenuItem? configurationStatus;

    public static void Initialize(Action openPalette, Action? reloadConfiguration = null, Action? openManagement = null)
    {
        using Stream stream = Application.GetResourceStream(new("/Resources/Images/Disorderly.ico", UriKind.Relative)).Stream;

        trayIcon = new NotifyIcon
        {
            Icon = new Icon(stream),
            Visible = true,
            Text = "Disorderly",
        };

        trayIcon.DoubleClick += (_, _) => openManagement?.Invoke();

        trayIcon.ContextMenuStrip = CreateMenu(openPalette, reloadConfiguration, openManagement);
    }

    /// <summary>Builds the existing tray actions independently of native icon lifetime.</summary>
    public static ContextMenuStrip CreateMenu(Action openPalette, Action? reloadConfiguration = null, Action? openManagement = null)
    {
        ContextMenuStrip contextMenu = new();

        contextMenu.Items.Add("Open Command Palette", null, (_, _) => openPalette());
        contextMenu.Items.Add("Open Disorderly", null, (_, _) => openManagement?.Invoke());
        ToolStripItem reload = contextMenu.Items.Add("Reload Configuration", null, (_, _) => reloadConfiguration?.Invoke());
        reload.Enabled = reloadConfiguration is not null;
        configurationStatus = new ToolStripMenuItem("Configuration: not loaded") { Enabled = false };
        contextMenu.Items.Add(configurationStatus);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => Application.Current.Shutdown());

        return contextMenu;
    }

    public static void SetConfigurationStatus(string status) => configurationStatus?.Text = "Configuration: " + status;

    public static void Shutdown() => trayIcon?.Dispose();
}