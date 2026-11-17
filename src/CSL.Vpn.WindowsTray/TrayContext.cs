using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace CSL.Vpn.WindowsTray;

/// <summary>
/// Tray shell for the CSL VPN client. Reads the agent's state file and shows
/// status/address. It does not establish a tunnel.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly string StateFile =
        Environment.GetEnvironmentVariable("CSLVPN_STATE_FILE") ?? @"C:\ProgramData\CSL VPN\state.json";

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Windows.Forms.Timer _timer;

    public TrayContext()
    {
        _statusItem = new ToolStripMenuItem("CSL VPN: unknown") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CSL VPN",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var text = ReadStatusText();
        _statusItem.Text = text;
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private static string ReadStatusText()
    {
        try
        {
            if (!File.Exists(StateFile))
            {
                return "CSL VPN: agent state not found";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(StateFile));
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString() ?? "unknown"
                : "unknown";
            var address = root.TryGetProperty("address", out var addressElement)
                && addressElement.ValueKind == JsonValueKind.String
                    ? addressElement.GetString()
                    : null;

            return address is null ? $"CSL VPN: {status}" : $"CSL VPN: {status} ({address})";
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return "CSL VPN: status unavailable";
        }
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _timer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        base.ExitThreadCore();
    }
}
