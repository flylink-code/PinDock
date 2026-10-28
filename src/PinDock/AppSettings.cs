namespace PinDock;

public enum DockEdge { Bottom, Top, Left, Right }
public enum DisplayMode { Persistent, Hotkey, EdgeAutoHide }
public enum ThemeMode { Light, Dark, System }
public enum Palette { Forest, Ocean, Sand, Clay, Graphite }
public enum MonitorPolicy { Fixed, Primary, FollowCursor }
public enum IconSize { Compact = 38, Standard = 46, Comfortable = 56 }

public sealed class AppSettings
{
    public const double MinOpacity = 0.4;
    public const double MaxOpacity = 1.0;
    public const double DefaultOpacity = 0.94;

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

    public bool HasHotkey => !string.IsNullOrWhiteSpace(HotkeyModifiers) && !string.IsNullOrWhiteSpace(HotkeyKey);
}

public enum BookmarkKind { Application, Folder, File }
