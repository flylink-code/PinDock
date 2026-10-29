namespace PinDock;

public enum DockEdge { Bottom, Top, Left, Right }
public enum DisplayMode { Persistent, Hotkey, EdgeAutoHide }
public enum ThemeMode { Light, Dark, System }
public enum Palette { Forest, Ocean, Sand, Clay, Graphite }
public enum MonitorPolicy { Fixed, Primary, FollowCursor }
public enum IconSize { Compact = 38, Standard = 46, Comfortable = 56 }
public enum CloseAction { MinimizeToTray, ExitApplication }

public sealed class AppSettings
{
    public const double MinOpacity = 0.4;
    public const double MaxOpacity = 1.0;
    public const double DefaultOpacity = 0.94;
    public const string DefaultGithubMirror = "https://ghfast.top/";

    public DisplayMode DisplayMode { get; set; } = DisplayMode.Persistent;
    public DockEdge DockEdge { get; set; } = DockEdge.Bottom;
    public ThemeMode ThemeMode { get; set; } = ThemeMode.Light;
    public Palette Palette { get; set; } = Palette.Forest;
    public MonitorPolicy MonitorPolicy { get; set; } = MonitorPolicy.Primary;
    public IconSize IconSize { get; set; } = IconSize.Standard;
    public double Opacity { get; set; } = DefaultOpacity;
    public bool Topmost { get; set; } = true;
    public bool AvoidFullscreen { get; set; } = true;
    public bool LockPosition { get; set; }
    public bool CollapseAfterLaunch { get; set; } = true;
    public double EdgeOffset { get; set; }
    public string? MonitorDeviceName { get; set; }
    public bool ExplorerContextMenu { get; set; }
    public string HotkeyModifiers { get; set; } = "Alt+Shift";
    public string HotkeyKey { get; set; } = "P";

    // 语言设置 (zh-CN / en-US)
    public string Language { get; set; } = "zh-CN";

    // Dock 功能按钮折叠开关（右侧/底部展开折叠抽屉）
    public bool UtilityCollapsed { get; set; }
    public bool ShowLibraryButton { get; set; } = true;
    public bool ShowAddButton { get; set; } = true;
    public bool ShowSettingsButton { get; set; } = true;
    public bool AutoHideUtilityButtons { get; set; }

    // Windows 11 一级右键菜单直显
    public bool Win11ClassicMenu { get; set; }

    // 开机自启与关闭/退出策略
    public bool LaunchAtStartup { get; set; }
    public CloseAction ClosePanelBehavior { get; set; } = CloseAction.MinimizeToTray;
    public bool ConfirmBeforeExit { get; set; }

    // GitHub 升级与国内镜像设置
    public bool UseGithubMirror { get; set; } = true;
    public string GithubMirrorUrl { get; set; } = DefaultGithubMirror;

    public bool HasHotkey => !string.IsNullOrWhiteSpace(HotkeyModifiers) && !string.IsNullOrWhiteSpace(HotkeyKey);
    public bool IsEnglish => string.Equals(Language, "en-US", StringComparison.OrdinalIgnoreCase);
}

public enum BookmarkKind { Application, Folder, File }
