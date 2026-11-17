using System.Drawing;
using System.Windows.Forms;

namespace CSL.Vpn.WindowsTray;

/// <summary>
/// Tray shell for the future CSL VPN client. It does not establish a tunnel.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;

    public TrayContext()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status…", null, OnStatusClicked);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, OnExitClicked);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CSL VPN (scaffold)",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private static void OnStatusClicked(object? sender, EventArgs e)
    {
        MessageBox.Show(
            "The CSL VPN client is a scaffold. Enrollment and the VPN data plane are not implemented; no tunnel is established.",
            "CSL VPN",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void OnExitClicked(object? sender, EventArgs e) => ExitThread();

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
