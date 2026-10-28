using System.Drawing;

namespace PinDock;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon icon;

    public event Action? ToggleRequested;
    public event Action? LibraryRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/PinDock.ico")).Stream;
        icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "PinDock",
            Visible = true,
            Icon = new Icon(iconStream)
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏 Dock", null, (_, _) => ToggleRequested?.Invoke());
        menu.Items.Add("全部收藏", null, (_, _) => LibraryRequested?.Invoke());
        menu.Items.Add("设置", null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
