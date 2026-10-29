using System.Drawing;

namespace PinDock;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon icon;
    private readonly System.Windows.Forms.ToolStripMenuItem itemFlyout;
    private readonly System.Windows.Forms.ToolStripMenuItem itemToggle;
    private readonly System.Windows.Forms.ToolStripMenuItem itemLibrary;
    private readonly System.Windows.Forms.ToolStripMenuItem itemSettings;
    private readonly System.Windows.Forms.ToolStripMenuItem itemExit;

    public event Action? FlyoutRequested;
    public event Action? ToggleRequested;
    public event Action? LibraryRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayService()
    {
        using var iconStream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/PinDock.ico")).Stream;
        icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "PinDock · 左键打开快捷浮窗",
            Visible = true,
            Icon = new Icon(iconStream)
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        itemFlyout = new System.Windows.Forms.ToolStripMenuItem("打开快捷浮窗", null, (_, _) => FlyoutRequested?.Invoke());
        itemToggle = new System.Windows.Forms.ToolStripMenuItem("显示 / 隐藏 Dock", null, (_, _) => ToggleRequested?.Invoke());
        itemLibrary = new System.Windows.Forms.ToolStripMenuItem("全部收藏", null, (_, _) => LibraryRequested?.Invoke());
        itemSettings = new System.Windows.Forms.ToolStripMenuItem("设置", null, (_, _) => SettingsRequested?.Invoke());
        itemExit = new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke());
        menu.Items.Add(itemFlyout);
        menu.Items.Add(itemToggle);
        menu.Items.Add(itemLibrary);
        menu.Items.Add(itemSettings);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(itemExit);
        icon.ContextMenuStrip = menu;
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                FlyoutRequested?.Invoke();
        };
    }

    public void ApplyLanguage(bool english)
    {
        icon.Text = english ? "PinDock · Left-click for Quick Flyout" : "PinDock · 左键打开快捷浮窗";
        itemFlyout.Text = english ? "Quick Flyout" : "打开快捷浮窗";
        itemToggle.Text = english ? "Show / Hide Dock" : "显示 / 隐藏 Dock";
        itemLibrary.Text = english ? "All Bookmarks" : "全部收藏";
        itemSettings.Text = english ? "Settings" : "设置";
        itemExit.Text = english ? "Exit" : "退出";
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
