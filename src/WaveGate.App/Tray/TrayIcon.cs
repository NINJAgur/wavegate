using System.Drawing;
using System.Windows.Forms;
using WaveGate.Overlay.Setup;

namespace WaveGate.Overlay.Tray;

/// <summary>System tray icon. The menu is rebuilt each time it opens so the mic list is current.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Func<string?> _attachedMicId;

    public event Action? ToggleOverlayRequested;
    public event Action? StopAllRequested;
    public event Action<string>? AttachRequested;
    public event Action? DetachRequested;
    public event Action? ExitRequested;

    public TrayIcon(Func<string?> attachedMicId)
    {
        _attachedMicId = attachedMicId;

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        BuildMenu(menu);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // placeholder until the app ships its own .ico
            Text = "WaveGate",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleOverlayRequested?.Invoke();
        };
    }

    public void Notify(string title, string text, bool isError = false) =>
        _icon.ShowBalloonTip(4000, title, text, isError ? ToolTipIcon.Warning : ToolTipIcon.Info);

    private void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var toggle = new ToolStripMenuItem("Show / hide overlay", null, (_, _) => ToggleOverlayRequested?.Invoke());
        toggle.Font = new Font(toggle.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(toggle);
        menu.Items.Add("Stop all", null, (_, _) => StopAllRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildMicMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
    }

    private ToolStripMenuItem BuildMicMenu()
    {
        var root = new ToolStripMenuItem("Microphone");
        var attachedId = _attachedMicId();

        IReadOnlyList<Microphone> mics;
        try
        {
            mics = MicSetup.ListMicrophones();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            root.DropDownItems.Add(new ToolStripMenuItem("Could not list microphones") { Enabled = false });
            return root;
        }

        if (attachedId is not null)
        {
            var name = mics.FirstOrDefault(m => m.Id == attachedId)?.Name ?? "disconnected microphone";
            root.DropDownItems.Add(new ToolStripMenuItem($"Attached to {name}") { Enabled = false, Checked = true });
            root.DropDownItems.Add("Detach (restores original settings)", null, (_, _) => DetachRequested?.Invoke());
            return root;
        }

        if (mics.Count == 0)
            root.DropDownItems.Add(new ToolStripMenuItem("No active microphones") { Enabled = false });

        foreach (var mic in mics)
            root.DropDownItems.Add($"Attach to {mic.Name}", null, (_, _) => AttachRequested?.Invoke(mic.Id));

        return root;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
