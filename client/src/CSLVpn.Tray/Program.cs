using System.Drawing;
using System.Windows.Forms;

namespace CSLVpn.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext();
        Application.Run(context);
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _status;

    public TrayApplicationContext()
    {
        _status = new ToolStripMenuItem("Статус: не подключён") { Enabled = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Настройки", null, (_, _) => ShowAbout());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "CSL VPN",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowAbout();
    }

    private static void ShowAbout()
    {
        MessageBox.Show(
            "CSL VPN Client\nКлиентская оболочка разработки. Подключение будет доступно после интеграции движков и службы управления.",
            "CSL VPN",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
