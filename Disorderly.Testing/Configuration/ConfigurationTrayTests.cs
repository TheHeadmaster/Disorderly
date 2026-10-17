using System.Windows.Forms;
using Disorderly.Configuration;
using Disorderly.View;
using NUnit.Framework;

namespace Disorderly.Testing.Configuration;

[TestFixture]
[Apartment(ApartmentState.STA)]
public class ConfigurationTrayTests
{
    [Test]
    public void TrayReloadUsesTheSharedServiceAndReportsStatusWithoutANativeTrayIcon()
    {
        ConfigurationFixture.Files files = new() { Content = ConfigurationCodec.Encode(ConfigurationFixture.Full()) };
        ConfigurationService service = ConfigurationFixture.Service(files);
        Task<ConfigurationResult>? pending = null;
        using ContextMenuStrip menu = TrayIconHandler.CreateMenu(() => { }, () => pending = service.ReloadAsync());
        ToolStripItem reload = menu.Items.Cast<ToolStripItem>().Single(item => item.Text == "Reload Configuration");
        reload.PerformClick();
        ConfigurationResult result = pending!.GetAwaiter().GetResult();
        Assert.That(result.Status, Is.EqualTo(ConfigurationStatus.Loaded));
        Assert.That(service.Store.Current!.Catalog.Find("launch"), Is.Not.Null);
        object current = service.Store.Current;
        files.Content = ConfigurationFixture.Bytes("{");
        reload.PerformClick();
        result = pending!.GetAwaiter().GetResult();
        TrayIconHandler.SetConfigurationStatus(result.Summary);
        Assert.That(menu.Items.Cast<ToolStripItem>().Single(item => item.Text!.StartsWith("Configuration:")).Text, Does.Contain("InvalidDocument"));
        Assert.That(service.Store.Current, Is.SameAs(current));
        Assert.That(menu.Items.Cast<ToolStripItem>().Count(item => item.Text == "Open Command Palette"), Is.EqualTo(1));
        Assert.That(menu.Items.Cast<ToolStripItem>().Count(item => item.Text == "Exit"), Is.EqualTo(1));
    }
}