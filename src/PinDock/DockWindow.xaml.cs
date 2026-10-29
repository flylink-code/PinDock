using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PinDock;

public partial class DockWindow : Window
{
    private readonly ShellIconService icons;
    private readonly GroupPopupWindow groupPopup;
    private readonly FolderPopupWindow folderPopup;
    private readonly DispatcherTimer folderOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer folderCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer utilityHoverHideTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private Bookmark? pendingHoverFolder;
    private Button? pendingHoverButton;
    private ScreenRect activeFolderButtonRect;
    private LibraryData data = new();
    private DockPlacement placement;
    private ScreenRect workArea;
    private List<Bookmark> overflow = [];
    private ScreenRect? dragStartCursor;
    private double dragOriginOffset;
    private string? reorderToken;
    private FrameworkElement? reorderElement;
    private Point reorderStartPoint;
    private bool isReordering;
    private bool isUtilityHoverRevealed;
    private bool isContextMenuOpen;
    private bool isExternalDragOver;

    public event Action<Bookmark>? LaunchRequested;
    public event Action<Bookmark>? EditRequested;
    public event Action<Bookmark>? UnpinRequested;
    public event Action<Bookmark>? RemoveRequested;
    public event Action<string>? ConfigureGroupRequested;
    public event Action? LibraryRequested;
    public event Action? InstalledAppsRequested;
    public event Action? OverflowRequested;
    public event Action? AddRequested;
    public event Action? AddFolderRequested;
    public event Action? AddFileRequested;
    public event Action<string[]>? FilesDropped;
    public event Action<string[], string?, bool>? FilesDroppedAt;
    public event Action<string[], string>? FilesDroppedToGroup;
    public event Action? SettingsRequested;
    public event Action? ToggleThemeRequested;
    public event Action? DrawerToggled;
    public event Action? ExitRequested;
    public event Action? DragEnded;

    public bool IsDragging { get; private set; }
    public bool IsShown { get; private set; }
    public bool IsGroupOpen => groupPopup.IsVisible || folderPopup.IsVisible || isContextMenuOpen || folderPopup.IsContextMenuOpen || isExternalDragOver || IsMouseOver;
    public ScreenRect CurrentBounds => placement.Bounds;
    public ScreenRect EdgeHandleRect => placement.EdgeHandle;
    public ScreenRect HitArea
    {
        get
        {
            var baseRect = IsVisible ? placement.Bounds.Union(NativeWindow.GetPixelBounds(this)) : placement.Bounds;
            return ExpandRect(baseRect, 14).Union(ExpandRect(placement.EdgeHandle, 8));
        }
    }
    public ScreenRect? GroupBounds => groupPopup.IsVisible
        ? NativeWindow.GetPixelBounds(groupPopup)
        : folderPopup.IsVisible
            ? NativeWindow.GetPixelBounds(folderPopup)
            : null;
    public IReadOnlyList<Bookmark> OverflowItems => overflow;

    public DockWindow(ShellIconService icons)
    {
        this.icons = icons;
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
        groupPopup = new GroupPopupWindow(icons);
        groupPopup.LaunchRequested += bookmark =>
        {
            groupPopup.Hide();
            LaunchRequested?.Invoke(bookmark);
        };
        folderPopup = new FolderPopupWindow(icons);
        folderPopup.OpenRequested += bookmark =>
        {
            folderPopup.Hide();
            LaunchRequested?.Invoke(bookmark);
        };
        folderPopup.PinFolderRequested += folderPath =>
        {
            FilesDropped?.Invoke([folderPath]);
        };
        folderPopup.HiddenFoldersChanged += () => DragEnded?.Invoke();
        folderOpenTimer.Tick += (_, _) =>
        {
            folderOpenTimer.Stop();
            if (pendingHoverFolder is not null && pendingHoverButton is not null && IsShown && !IsDragging)
                OpenFolderPopupFor(pendingHoverFolder, pendingHoverButton);
        };
        folderCloseTimer.Tick += (_, _) => CheckFolderPopupLeave();
        utilityHoverHideTimer.Tick += (_, _) =>
        {
            utilityHoverHideTimer.Stop();
            if (!IsMouseOver && isUtilityHoverRevealed)
            {
                isUtilityHoverRevealed = false;
                if (data.Settings.AutoHideUtilityButtons)
                    Relayout();
            }
        };
        MouseEnter += (_, _) =>
        {
            utilityHoverHideTimer.Stop();
            if (data.Settings.AutoHideUtilityButtons && !isUtilityHoverRevealed && !IsDragging)
            {
                isUtilityHoverRevealed = true;
                Relayout();
            }
        };
        MouseLeave += (_, _) =>
        {
            if (data.Settings.AutoHideUtilityButtons && isUtilityHoverRevealed && !IsDragging)
            {
                utilityHoverHideTimer.Stop();
                utilityHoverHideTimer.Start();
            }
        };
    }

    private string T(string zh, string en) => I18n.T(data.Settings, zh, en);

    private void OpenContextMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => isContextMenuOpen = true;
        menu.Closed += (_, _) => isContextMenuOpen = false;
        isContextMenuOpen = true;
        menu.IsOpen = true;
    }

    public void Bind(LibraryData library)
    {
        data = library;
        folderPopup.Bind(library);
        Topmost = library.Settings.Topmost;
        Opacity = library.Settings.Opacity;
    }

    public void Relayout()
    {
        var settings = data.Settings;
        data.NormalizeDockOrder();
        var monitor = MonitorQuery.Resolve(settings);
        workArea = monitor.WorkingArea;
        bool vertical = settings.DockEdge is DockEdge.Left or DockEdge.Right;
        double available = vertical ? workArea.Height : workArea.Width;
        int capacity = DockLayout.ComputeCapacity(available, DockLayout.IconPixels(settings.IconSize));

        var pinnedById = data.Items.Where(i => i.Pinned).ToDictionary(i => "bm:" + i.Id.ToString("D"), StringComparer.OrdinalIgnoreCase);
        var orderedPinned = new List<Bookmark>();
        foreach (string token in data.DockOrder)
        {
            if (pinnedById.TryGetValue(token, out var bm))
                orderedPinned.Add(bm);
        }
        overflow = orderedPinned.Skip(capacity).ToList();
        var visiblePinnedSet = new HashSet<Guid>(orderedPinned.Take(capacity).Select(i => i.Id));

        Rebuild(visiblePinnedSet);
        Reposition(capacity);
    }

    public void ShowDock()
    {
        if (IsShown && IsVisible) return;
        IsShown = true;
        Opacity = data.Settings.Opacity;
        if (!IsVisible) Show();
        UpdateLayout();
        Reposition(placement.Capacity);
    }

    public void HideDock()
    {
        IsShown = false;
        dragStartCursor = null;
        IsDragging = false;
        isReordering = false;
        reorderToken = null;
        reorderElement = null;
        folderOpenTimer.Stop();
        folderCloseTimer.Stop();
        utilityHoverHideTimer.Stop();
        isUtilityHoverRevealed = false;
        groupPopup.Hide();
        folderPopup.Hide();
        if (IsVisible) Hide();
    }

    public void CloseGroup()
    {
        groupPopup.Hide();
        folderPopup.Hide();
    }

    private void Reposition(int capacity)
    {
        var settings = data.Settings;
        UpdateLayout();
        DockChrome.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = Math.Ceiling(Math.Max(DockChrome.DesiredSize.Width, DockChrome.ActualWidth) * scale);
        double height = Math.Ceiling(Math.Max(DockChrome.DesiredSize.Height, DockChrome.ActualHeight) * scale);
        var bounds = DockLayout.Fit(workArea, settings.DockEdge, width, height, settings.EdgeOffset, settings.LockPosition);
        placement = new DockPlacement(bounds, capacity, DockLayout.EdgeHandleFor(workArea, settings.DockEdge, bounds));
        NativeWindow.MoveTo(this, bounds.X, bounds.Y);
    }

    private void Rebuild(IReadOnlySet<Guid> visiblePinnedIds)
    {
        dragStartCursor = null;
        IsDragging = false;
        isReordering = false;
        reorderToken = null;
        reorderElement = null;
        ItemsPanel.Children.Clear();
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        ItemsPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DockChrome.Padding = vertical ? new Thickness(10, 12, 10, 12) : new Thickness(12, 10, 12, 10);
        double icon = DockLayout.IconPixels(data.Settings.IconSize);

        var pinnedLookup = data.Items
            .Where(i => i.Pinned && visiblePinnedIds.Contains(i.Id))
            .ToDictionary(i => "bm:" + i.Id.ToString("D"), StringComparer.OrdinalIgnoreCase);
        var activeGroupSet = new HashSet<string>(
            data.Groups.Where(g => data.Items.Any(i => string.Equals(i.Group, g, StringComparison.Ordinal))),
            StringComparer.Ordinal);

        bool hasCustomSeparators = data.DockOrder.Any(t => t.StartsWith("sep:", StringComparison.OrdinalIgnoreCase));
        var mainElements = new List<FrameworkElement>();
        string? prevTokenKind = null;

        foreach (string token in data.DockOrder)
        {
            if (pinnedLookup.TryGetValue(token, out var bm))
            {
                var btn = CreateBookmarkButton(bm, icon, token);
                mainElements.Add(btn);
                prevTokenKind = "bm";
            }
            else if (token.StartsWith("grp:", StringComparison.Ordinal))
            {
                string groupName = token[4..];
                if (!activeGroupSet.Contains(groupName)) continue;
                // 当用户尚未插入任何自定义分隔符时，默认在固定项与分组之间保留一条柔和分隔线
                if (!hasCustomSeparators && prevTokenKind == "bm")
                    mainElements.Add(CreateSeparator(icon));
                var gBtn = CreateGroupButton(groupName, icon, token);
                mainElements.Add(gBtn);
                prevTokenKind = "grp";
            }
            else if (token.StartsWith("sep:", StringComparison.OrdinalIgnoreCase))
            {
                mainElements.Add(CreateCustomSeparator(icon, token));
                prevTokenKind = "sep";
            }
        }

        bool collapsed = data.Settings.UtilityCollapsed;
        var utilityButtons = new List<FrameworkElement>();
        if (overflow.Count > 0)
            utilityButtons.Add(CreateUtilityButton($"…{overflow.Count}", T($"更多固定项 · {overflow.Count}", $"More pinned · {overflow.Count}"), icon, () => OverflowRequested?.Invoke(), isGear: false, T("更多", "More")));
        if (!collapsed)
        {
            utilityButtons.Add(CreateUtilityButton("▦", T("全部收藏（支持直接拖入文件夹或应用）", "All Bookmarks (Drag & drop folders/apps)"), icon, () => LibraryRequested?.Invoke(), isGear: false, T("收藏库", "Library")));
            utilityButtons.Add(CreateUtilityButton("＋", T("添加收藏（右键可快速选文件夹/本机应用）", "Add Bookmark (Right-click for quick options)"), icon, () => AddRequested?.Invoke(), isGear: false, T("添加", "Add")));
            utilityButtons.Add(CreateUtilityButton("⚙", T("打开设置", "Settings"), icon, () => SettingsRequested?.Invoke(), isGear: true, T("设置", "Settings")));
        }

        // 若主区域为空且功能区被折叠，自动展开功能区防止 Dock 仅剩一个折叠箭头
        if (mainElements.Count == 0 && utilityButtons.Count == 0)
        {
            utilityButtons.Add(CreateUtilityButton("▦", T("全部收藏", "All Bookmarks"), icon, () => LibraryRequested?.Invoke(), isGear: false, T("收藏库", "Library")));
            utilityButtons.Add(CreateUtilityButton("＋", T("添加收藏", "Add Bookmark"), icon, () => AddRequested?.Invoke(), isGear: false, T("添加", "Add")));
            utilityButtons.Add(CreateUtilityButton("⚙", T("打开设置", "Settings"), icon, () => SettingsRequested?.Invoke(), isGear: true, T("设置", "Settings")));
        }

        foreach (var element in mainElements)
            ItemsPanel.Children.Add(element);

        if (utilityButtons.Count > 0)
        {
            if (ItemsPanel.Children.Count > 0) ItemsPanel.Children.Add(CreateSeparator(icon));
            foreach (var element in utilityButtons) ItemsPanel.Children.Add(element);
        }

        // 右侧 / 底部末端展开折叠抽屉按钮
        if (mainElements.Count > 0)
        {
            if (collapsed && ItemsPanel.Children.Count > 0)
                ItemsPanel.Children.Add(CreateSeparator(icon));
            ItemsPanel.Children.Add(CreateDrawerToggleButton(icon, vertical, collapsed));
        }

        if (ItemsPanel.Children.Count > 0)
            ((FrameworkElement)ItemsPanel.Children[^1]).Margin = new Thickness(0);
    }

    private Button CreateDrawerToggleButton(double icon, bool vertical, bool collapsed)
    {
        string glyph = vertical
            ? (collapsed ? "▾" : "▴")
            : (collapsed ? "›" : "‹");
        string tip = collapsed
            ? T("展开功能按钮（全部收藏 / 添加 / 设置）", "Expand tools (Library / Add / Settings)")
            : T("折叠右侧功能按钮", "Collapse tool buttons");

        var border = new Border
        {
            Width = vertical ? icon : Math.Max(28, Math.Round(icon * 0.58)),
            Height = vertical ? Math.Max(28, Math.Round(icon * 0.58)) : icon,
            CornerRadius = new CornerRadius(10),
            Background = collapsed ? (Brush)FindResource("SoftBrush") : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = Math.Max(15, icon * 0.4),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource(collapsed ? "AccentBrush" : "MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var button = new Button
        {
            Content = border,
            Style = (Style)FindResource("DockButton"),
            VerticalAlignment = !vertical && data.Settings.ShowIconLabels ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = vertical ? new Thickness(0, 0, 0, 9) : new Thickness(0, 0, 9, 0),
            ToolTip = tip
        };
        ToolTipService.SetInitialShowDelay(button, 120);
        button.MouseEnter += (_, _) =>
        {
            CloseFolderPopupOnOtherHover();
            border.Background = (Brush)FindResource("SoftBrush");
        };
        button.MouseLeave += (_, _) =>
        {
            border.Background = collapsed ? (Brush)FindResource("SoftBrush") : Brushes.Transparent;
        };
        button.Click += (_, _) =>
        {
            data.Settings.UtilityCollapsed = !data.Settings.UtilityCollapsed;
            Relayout();
            DrawerToggled?.Invoke();
            DragEnded?.Invoke();
        };
        return button;
    }

    private void CloseFolderPopupOnOtherHover()
    {
        folderOpenTimer.Stop();
        pendingHoverFolder = null;
        pendingHoverButton = null;
        if (folderPopup.IsVisible)
        {
            folderPopup.Hide();
            folderCloseTimer.Stop();
        }
    }

    private Button CreateBookmarkButton(Bookmark item, double icon, string token)
    {
        bool isFolder = BookmarkKindHelper.FromPath(item.Path) == BookmarkKind.Folder;
        var button = CreateChrome(
            icon,
            item.Name,
            isFolder && item.IconKind == BookmarkIconKind.Default ? (Brush)FindResource("FolderBgBrush") : (Brush)FindResource("SoftBrush"),
            token,
            out var iconBorder,
            item.Name);

        iconBorder.Child = BuildBookmarkIconVisual(item, icon, isFolder);

        if (isFolder)
        {
            button.ToolTip = null; // 文件夹悬停直接弹出子目录面板，不叠加文字 ToolTip 遮挡
            button.MouseEnter += (_, _) =>
            {
                pendingHoverFolder = item;
                pendingHoverButton = button;
                if (folderPopup.IsVisible)
                {
                    OpenFolderPopupFor(item, button);
                }
                else
                {
                    folderOpenTimer.Stop();
                    folderOpenTimer.Start();
                }
            };
            button.MouseLeave += (_, _) =>
            {
                folderOpenTimer.Stop();
            };
        }
        else
        {
            button.MouseEnter += (_, _) => CloseFolderPopupOnOtherHover();
        }
        button.Click += (_, _) =>
        {
            if (isReordering || IsDragging) return;
            folderOpenTimer.Stop();
            folderPopup.Hide();
            LaunchRequested?.Invoke(item);
        };
        button.MouseRightButtonUp += (_, e) =>
        {
            folderOpenTimer.Stop();
            folderPopup.Hide();
            ShowBookmarkContextMenu(item, token);
            e.Handled = true;
        };
        return button;
    }

    private UIElement BuildTextBadgeVisual(string rawText, double icon)
    {
        string text = GroupIconPresets.NormalizeBadgeText(rawText);
        double fontSize = text.Length switch
        {
            1 => icon * 0.44,
            2 => icon * 0.34,
            _ => icon * 0.27
        };
        return new Border
        {
            Width = icon * 0.66,
            Height = icon * 0.66,
            CornerRadius = new CornerRadius(icon * 0.16),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Bahnschrift, Segoe UI, Microsoft YaHei UI"),
                FontSize = fontSize,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
    }

    private UIElement BuildBookmarkIconVisual(Bookmark item, double icon, bool isFolder)
    {
        if (item.IconKind == BookmarkIconKind.TextBadge && !string.IsNullOrWhiteSpace(item.IconValue))
        {
            return BuildTextBadgeVisual(item.IconValue, icon);
        }
        if (item.IconKind == BookmarkIconKind.Preset && !string.IsNullOrWhiteSpace(item.IconValue))
        {
            string? pathData = GroupIconPresets.FindPathData(item.IconValue);
            if (!string.IsNullOrWhiteSpace(pathData))
            {
                var pathShape = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(pathData),
                    Fill = (Brush)FindResource("AccentBrush"),
                    Stretch = Stretch.Uniform
                };
                return new Viewbox
                {
                    Width = icon * 0.54,
                    Height = icon * 0.54,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = pathShape
                };
            }
        }
        else if (item.IconKind == BookmarkIconKind.CustomFile && !string.IsNullOrWhiteSpace(item.IconValue))
        {
            var host = new Grid();
            var fallback = isFolder
                ? CreatePrototypeFolderIcon(icon * 0.62)
                : new TextBlock
                {
                    Text = "◇",
                    FontSize = icon * 0.42,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            var image = new Image
            {
                Width = icon * 0.65,
                Height = icon * 0.65,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            host.Children.Add(fallback);
            host.Children.Add(image);
            _ = LoadIcon(image, fallback, item.IconValue);
            return host;
        }

        if (isFolder)
        {
            return CreatePrototypeFolderIcon(icon * 0.62);
        }

        var defaultHost = new Grid();
        var defaultFallback = new TextBlock
        {
            Text = "◇",
            FontSize = icon * 0.42,
            Foreground = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var defaultImage = new Image
        {
            Width = icon * 0.65,
            Height = icon * 0.65,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(defaultImage, BitmapScalingMode.HighQuality);
        defaultHost.Children.Add(defaultFallback);
        defaultHost.Children.Add(defaultImage);
        _ = LoadIcon(defaultImage, defaultFallback, item.Path);
        return defaultHost;
    }

    private void OpenFolderPopupFor(Bookmark folderItem, Button anchorButton)
    {
        if (groupPopup.IsVisible) groupPopup.Hide();
        var topLeft = anchorButton.PointToScreen(new Point(0, 0));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var buttonRect = new ScreenRect(
            topLeft.X,
            topLeft.Y,
            anchorButton.ActualWidth * scale,
            anchorButton.ActualHeight * scale);
        activeFolderButtonRect = buttonRect;
        var anchor = data.Settings.DockEdge is DockEdge.Bottom or DockEdge.Top
            ? new ScreenRect(buttonRect.X, placement.Bounds.Y, buttonRect.Width, placement.Bounds.Height)
            : new ScreenRect(placement.Bounds.X, buttonRect.Y, placement.Bounds.Width, buttonRect.Height);

        folderPopup.ShowFolder(folderItem.Path, folderItem.Name, anchor, workArea, data.Settings.DockEdge, data.Settings.Opacity);
        if (!folderCloseTimer.IsEnabled) folderCloseTimer.Start();
    }

    private void CheckFolderPopupLeave()
    {
        if (!folderPopup.IsVisible)
        {
            folderCloseTimer.Stop();
            return;
        }
        if (folderPopup.IsKeyboardFocusWithin || folderPopup.IsContextMenuOpen) return;
        if (!AutoHideController.TryGetCursorPoint(out var pt)) return;

        var btnRect = ExpandRect(activeFolderButtonRect, 14);
        var popRect = ExpandRect(NativeWindow.GetPixelBounds(folderPopup), 18);
        if (!btnRect.Contains(pt.X, pt.Y) && !popRect.Contains(pt.X, pt.Y))
        {
            folderPopup.Hide();
            folderCloseTimer.Stop();
        }
    }

    private static ScreenRect ExpandRect(ScreenRect r, double pad) =>
        new(r.X - pad, r.Y - pad, r.Width + pad * 2, r.Height + pad * 2);

    private UIElement CreatePrototypeFolderIcon(double size)
    {
        var fg = (Brush)FindResource("FolderFgBrush");
        var backPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,8 A3,3 0 0 1 6,5 H13 L16,9 H26 A3,3 0 0 1 29,12 V24 A3,3 0 0 1 26,27 H6 A3,3 0 0 1 3,24 Z"),
            Fill = fg,
            Opacity = 0.65
        };
        var frontPath = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,13 H29 V24 A3,3 0 0 1 26,27 H6 A3,3 0 0 1 3,24 Z"),
            Fill = fg
        };
        var canvas = new Canvas { Width = 32, Height = 32 };
        canvas.Children.Add(backPath);
        canvas.Children.Add(frontPath);
        return new Viewbox
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = canvas
        };
    }

    private Button CreateGroupButton(string group, double icon, string token)
    {
        var button = CreateChrome(icon, group, (Brush)FindResource("SoftBrush"), token, out var iconBorder, group);
        button.MouseEnter += (_, _) => CloseFolderPopupOnOtherHover();
        iconBorder.Child = BuildGroupIconVisual(group, icon);
        button.Click += (_, _) =>
        {
            if (isReordering || IsDragging) return;
            folderOpenTimer.Stop();
            folderPopup.Hide();
            var members = data.Items.Where(i => string.Equals(i.Group, group, StringComparison.Ordinal));
            groupPopup.ShowGroup(group, members, placement.Bounds, workArea, data.Settings.DockEdge, data.Settings.Opacity, data.Settings.IsEnglish);
        };
        button.MouseRightButtonUp += (_, e) =>
        {
            folderOpenTimer.Stop();
            folderPopup.Hide();
            ShowGroupContextMenu(group, token);
            e.Handled = true;
        };
        return button;
    }

    private UIElement BuildGroupIconVisual(string group, double icon)
    {
        data.GroupIcons.TryGetValue(group, out var cfg);
        var kind = cfg?.Kind ?? GroupIconKind.DefaultGrid;
        string val = cfg?.Value ?? "";

        if (kind == GroupIconKind.TextBadge && !string.IsNullOrWhiteSpace(val))
        {
            return BuildTextBadgeVisual(val, icon);
        }
        if (kind == GroupIconKind.Preset)
        {
            string? pathData = GroupIconPresets.FindPathData(val);
            if (!string.IsNullOrWhiteSpace(pathData))
            {
                var pathShape = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(pathData),
                    Fill = (Brush)FindResource("AccentBrush"),
                    Stretch = Stretch.Uniform
                };
                return new Viewbox
                {
                    Width = icon * 0.54,
                    Height = icon * 0.54,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = pathShape
                };
            }
        }
        else if (kind == GroupIconKind.CustomFile && !string.IsNullOrWhiteSpace(val))
        {
            var host = new Grid();
            var fallback = BuildDefaultFourCellGrid(icon);
            var image = new Image
            {
                Width = icon * 0.65,
                Height = icon * 0.65,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            host.Children.Add(fallback);
            host.Children.Add(image);
            _ = LoadIcon(image, fallback, val);
            return host;
        }
        else if (kind == GroupIconKind.MemberPreview)
        {
            var members = data.Items.Where(i => string.Equals(i.Group, group, StringComparison.Ordinal)).Take(4).ToList();
            if (members.Count > 0)
            {
                var grid = new Grid
                {
                    Width = icon * 0.64,
                    Height = icon * 0.64,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                grid.RowDefinitions.Add(new RowDefinition());
                grid.RowDefinitions.Add(new RowDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                double miniSize = icon * 0.27;
                for (int i = 0; i < 4; i++)
                {
                    UIElement cellChild;
                    if (i < members.Count)
                    {
                        var m = members[i];
                        if (BookmarkKindHelper.FromPath(m.Path) == BookmarkKind.Folder)
                        {
                            cellChild = CreatePrototypeFolderIcon(miniSize);
                        }
                        else
                        {
                            var cellHost = new Grid();
                            var dot = new Border
                            {
                                Width = miniSize * 0.7,
                                Height = miniSize * 0.7,
                                CornerRadius = new CornerRadius(2),
                                Background = (Brush)FindResource("AccentBrush"),
                                Opacity = 0.5
                            };
                            var img = new Image
                            {
                                Width = miniSize,
                                Height = miniSize,
                                Stretch = Stretch.Uniform
                            };
                            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                            cellHost.Children.Add(dot);
                            cellHost.Children.Add(img);
                            _ = LoadIcon(img, dot, m.Path);
                            cellChild = cellHost;
                        }
                    }
                    else
                    {
                        cellChild = new Border
                        {
                            Background = (Brush)FindResource("AccentBrush"),
                            Opacity = 0.22,
                            CornerRadius = new CornerRadius(2),
                            Margin = new Thickness(1.5)
                        };
                    }
                    Grid.SetRow(cellChild, i / 2);
                    Grid.SetColumn(cellChild, i % 2);
                    grid.Children.Add(cellChild);
                }
                return grid;
            }
        }

        return BuildDefaultFourCellGrid(icon);
    }

    private UIElement BuildDefaultFourCellGrid(double icon)
    {
        var grid = new Grid { Width = icon * 0.44, Height = icon * 0.44, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 4; i++)
        {
            var cell = new Border { Background = (Brush)FindResource("AccentBrush"), CornerRadius = new CornerRadius(2), Margin = new Thickness(1.6) };
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Button CreateUtilityButton(string glyph, string tip, double icon, Action action, bool isGear, string? labelText = null)
    {
        var button = CreateChrome(icon, tip, isGear ? Brushes.Transparent : (Brush)FindResource("SoftBrush"), reorderItemToken: null, out var iconBorder, labelText);
        button.MouseEnter += (_, _) => CloseFolderPopupOnOtherHover();
        iconBorder.Child = new TextBlock
        {
            Text = glyph,
            FontSize = isGear ? icon * 0.46 : icon * 0.4,
            Foreground = (Brush)FindResource(isGear ? "MutedBrush" : "AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) =>
        {
            if (IsDragging) return;
            action();
        };
        return button;
    }

    private FrameworkElement CreateSeparator(double icon)
    {
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        double lineH = icon * 0.62;
        double topPad = (!vertical && data.Settings.ShowIconLabels) ? Math.Round((icon - lineH) / 2) : 0;
        return new Border
        {
            Width = vertical ? lineH : 1,
            Height = vertical ? 1 : lineH,
            Background = (Brush)FindResource("LineBrush"),
            Margin = vertical
                ? new Thickness(0, 0, 0, 9)
                : new Thickness(0, topPad, 9, 0),
            VerticalAlignment = (!vertical && data.Settings.ShowIconLabels) ? VerticalAlignment.Top : VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private FrameworkElement CreateCustomSeparator(double icon, string token)
    {
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        var line = new Border
        {
            Width = vertical ? icon * 0.66 : 2,
            Height = vertical ? 2 : icon * 0.66,
            CornerRadius = new CornerRadius(1),
            Background = (Brush)FindResource("LineBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var hitBox = new Border
        {
            Width = vertical ? icon : 10,
            Height = vertical ? 10 : icon,
            Background = Brushes.Transparent,
            Margin = vertical ? new Thickness(0, 0, 0, 9) : new Thickness(0, 0, 9, 0),
            VerticalAlignment = (!vertical && data.Settings.ShowIconLabels) ? VerticalAlignment.Top : VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = T("分隔符（可拖拽调整位置 · 右键删除）", "Separator (Drag to move · Right-click to remove)"),
            Tag = token,
            Child = line
        };
        hitBox.MouseEnter += (_, _) => line.Background = (Brush)FindResource("AccentBrush");
        hitBox.MouseLeave += (_, _) => line.Background = (Brush)FindResource("LineBrush");
        hitBox.MouseRightButtonUp += (_, e) =>
        {
            ShowSeparatorContextMenu(token);
            e.Handled = true;
        };
        AttachReorderOrDrag(hitBox, token);
        return hitBox;
    }

    private Button CreateChrome(double icon, string tip, Brush background, string? reorderItemToken, out Border iconBorder, string? labelText = null)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        var animGroup = new TransformGroup { Children = { scale, translate } };
        bool showLabel = data.Settings.ShowIconLabels && !string.IsNullOrWhiteSpace(labelText);

        iconBorder = new Border
        {
            Width = icon,
            Height = icon,
            CornerRadius = new CornerRadius(12),
            Background = background,
            BorderThickness = new Thickness(1.5),
            BorderBrush = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = showLabel ? Transform.Identity : animGroup
        };

        object contentElement = iconBorder;
        if (showLabel)
        {
            double cellWidth = icon + 16;
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Width = cellWidth,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = animGroup
            };
            stack.Children.Add(iconBorder);
            stack.Children.Add(new Border
            {
                Height = 16,
                Margin = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = labelText,
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = (Brush)FindResource("InkBrush"),
                    Opacity = 0.92,
                    MaxWidth = cellWidth,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            contentElement = stack;
        }

        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        double gap = showLabel && !vertical ? 10 : 9;
        var button = new Button
        {
            Content = contentElement,
            Style = (Style)FindResource("DockButton"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = vertical ? new Thickness(0, 0, 0, gap) : new Thickness(0, 0, gap, 0),
            ToolTip = tip,
            Tag = reorderItemToken
        };
        ToolTipService.SetInitialShowDelay(button, 120);
        var dur = new Duration(TimeSpan.FromMilliseconds(140));
        button.MouseEnter += (_, _) =>
        {
            double dy = data.Settings.DockEdge == DockEdge.Bottom ? -4 : data.Settings.DockEdge == DockEdge.Top ? 4 : 0;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.07, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.07, dur));
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, dur));
        };
        button.MouseLeave += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur));
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur));
        };
        AttachReorderOrDrag(button, reorderItemToken);
        return button;
    }

    private static Border? ExtractIconBorder(Button btn) =>
        btn.Content as Border ?? (btn.Content as Panel)?.Children.OfType<Border>().FirstOrDefault();

    private async Task LoadIcon(Image image, UIElement fallback, string path)
    {
        try
        {
            image.Source = await icons.GetAsync(path);
            fallback.Visibility = Visibility.Collapsed;
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void ShowBookmarkContextMenu(Bookmark item, string token)
    {
        var menu = new ContextMenu();
        var openItem = new MenuItem { Tag = "↗", Header = T($"打开“{item.Name}”", $"Open \"{item.Name}\"") };
        openItem.Click += (_, _) => LaunchRequested?.Invoke(item);
        menu.Items.Add(openItem);

        var revealItem = new MenuItem { Tag = "📂", Header = T("在资源管理器中显示", "Show in Explorer") };
        revealItem.Click += (_, _) => LibraryWindow.RevealInExplorer(item.Path);
        menu.Items.Add(revealItem);

        menu.Items.Add(new Separator());

        var sepBefore = new MenuItem { Tag = "｜", Header = T("在前面插入分隔符", "Insert separator before") };
        sepBefore.Click += (_, _) => InsertSeparatorAround(token, before: true);
        menu.Items.Add(sepBefore);

        var sepAfter = new MenuItem { Tag = "｜", Header = T("在后面插入分隔符", "Insert separator after") };
        sepAfter.Click += (_, _) => InsertSeparatorAround(token, before: false);
        menu.Items.Add(sepAfter);

        var movePrev = new MenuItem { Tag = "←", Header = T("向前移动一位", "Move backward") };
        movePrev.Click += (_, _) => MoveTokenByStep(token, -1);
        menu.Items.Add(movePrev);

        var moveNext = new MenuItem { Tag = "→", Header = T("向后移动一位", "Move forward") };
        moveNext.Click += (_, _) => MoveTokenByStep(token, 1);
        menu.Items.Add(moveNext);

        menu.Items.Add(new Separator());

        var editItem = new MenuItem { Tag = "✎", Header = T("编辑收藏 / 修改图标…", "Edit bookmark / Change icon…") };
        editItem.Click += (_, _) => EditRequested?.Invoke(item);
        menu.Items.Add(editItem);

        var unpinItem = new MenuItem { Tag = "☆", Header = T("从 Dock 取消固定", "Unpin from Dock") };
        unpinItem.Click += (_, _) => UnpinRequested?.Invoke(item);
        menu.Items.Add(unpinItem);

        var removeItem = new MenuItem { Tag = "✕", Header = T("从收藏中移除", "Remove bookmark") };
        removeItem.Click += (_, _) => RemoveRequested?.Invoke(item);
        menu.Items.Add(removeItem);

        OpenContextMenu(menu);
    }

    private void ShowGroupContextMenu(string group, string token)
    {
        var menu = new ContextMenu();
        var openGrp = new MenuItem { Tag = "▦", Header = T($"打开分组“{group}”", $"Open group \"{group}\"") };
        openGrp.Click += (_, _) =>
        {
            var members = data.Items.Where(i => string.Equals(i.Group, group, StringComparison.Ordinal));
            groupPopup.ShowGroup(group, members, placement.Bounds, workArea, data.Settings.DockEdge, data.Settings.Opacity, data.Settings.IsEnglish);
        };
        menu.Items.Add(openGrp);

        var configGrp = new MenuItem { Tag = "⚙", Header = T("配置分组与自定义图标…", "Configure group & icon…") };
        configGrp.Click += (_, _) => ConfigureGroupRequested?.Invoke(group);
        menu.Items.Add(configGrp);

        menu.Items.Add(new Separator());

        var sepBefore = new MenuItem { Tag = "｜", Header = T("在前面插入分隔符", "Insert separator before") };
        sepBefore.Click += (_, _) => InsertSeparatorAround(token, before: true);
        menu.Items.Add(sepBefore);

        var sepAfter = new MenuItem { Tag = "｜", Header = T("在后面插入分隔符", "Insert separator after") };
        sepAfter.Click += (_, _) => InsertSeparatorAround(token, before: false);
        menu.Items.Add(sepAfter);

        var movePrev = new MenuItem { Tag = "←", Header = T("向前移动一位", "Move backward") };
        movePrev.Click += (_, _) => MoveTokenByStep(token, -1);
        menu.Items.Add(movePrev);

        var moveNext = new MenuItem { Tag = "→", Header = T("向后移动一位", "Move forward") };
        moveNext.Click += (_, _) => MoveTokenByStep(token, 1);
        menu.Items.Add(moveNext);

        OpenContextMenu(menu);
    }

    private void ShowSeparatorContextMenu(string sepToken)
    {
        var menu = new ContextMenu();
        var removeSep = new MenuItem { Tag = "✕", Header = T("删除此分隔符", "Remove separator") };
        removeSep.Click += (_, _) =>
        {
            data.DockOrder.Remove(sepToken);
            Relayout();
            DragEnded?.Invoke();
        };
        menu.Items.Add(removeSep);
        OpenContextMenu(menu);
    }

    private void InsertSeparatorAround(string anchorToken, bool before)
    {
        data.NormalizeDockOrder();
        int idx = data.DockOrder.IndexOf(anchorToken);
        string sep = "sep:" + Guid.NewGuid().ToString("N")[..8];
        if (idx < 0)
            data.DockOrder.Add(sep);
        else
            data.DockOrder.Insert(before ? idx : idx + 1, sep);
        Relayout();
        DragEnded?.Invoke();
    }

    private void MoveTokenByStep(string token, int delta)
    {
        data.NormalizeDockOrder();
        int idx = data.DockOrder.IndexOf(token);
        if (idx < 0) return;
        int target = Math.Clamp(idx + delta, 0, data.DockOrder.Count - 1);
        if (target == idx) return;
        data.DockOrder.RemoveAt(idx);
        data.DockOrder.Insert(target, token);
        SyncItemsOrderFromDockOrder();
        Relayout();
        DragEnded?.Invoke();
    }

    private void SyncItemsOrderFromDockOrder()
    {
        var rank = new Dictionary<Guid, int>();
        int pos = 0;
        foreach (string token in data.DockOrder)
        {
            if (token.StartsWith("bm:", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(token[3..], out var g))
                rank[g] = pos++;
        }
        data.Items.Sort((a, b) =>
        {
            int ra = rank.GetValueOrDefault(a.Id, int.MaxValue);
            int rb = rank.GetValueOrDefault(b.Id, int.MaxValue);
            return ra.CompareTo(rb);
        });
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var addFolder = new MenuItem { Tag = "＋", Header = T("添加文件夹到 Dock…", "Add folder to Dock…") };
        addFolder.Click += (_, _) => AddFolderRequested?.Invoke();
        menu.Items.Add(addFolder);

        var addApps = new MenuItem { Tag = "⌕", Header = T("检索本机已安装应用…", "Browse installed apps…") };
        addApps.Click += (_, _) => InstalledAppsRequested?.Invoke();
        menu.Items.Add(addApps);

        var addFile = new MenuItem { Tag = "＋", Header = T("添加应用 / 文件…", "Add app / file…") };
        addFile.Click += (_, _) => AddFileRequested?.Invoke();
        menu.Items.Add(addFile);

        var addCustom = new MenuItem { Tag = "✎", Header = T("自定义添加收藏…", "Custom bookmark…") };
        addCustom.Click += (_, _) => AddRequested?.Invoke();
        menu.Items.Add(addCustom);

        menu.Items.Add(new Separator());

        var autoSep = new MenuItem { Tag = "｜", Header = T("按类型自动分隔（应用 / 文件夹 / 分组）", "Auto-separate by type (Apps / Folders / Groups)") };
        autoSep.Click += (_, _) =>
        {
            data.AutoSeparateByKind();
            SyncItemsOrderFromDockOrder();
            Relayout();
            DragEnded?.Invoke();
        };
        menu.Items.Add(autoSep);

        var addSepEnd = new MenuItem { Tag = "｜", Header = T("插入分隔符", "Insert separator") };
        addSepEnd.Click += (_, _) =>
        {
            data.NormalizeDockOrder();
            data.DockOrder.Add("sep:" + Guid.NewGuid().ToString("N")[..8]);
            Relayout();
            DragEnded?.Invoke();
        };
        menu.Items.Add(addSepEnd);

        if (data.DockOrder.Any(t => t.StartsWith("sep:", StringComparison.OrdinalIgnoreCase)))
        {
            var clearSeps = new MenuItem { Tag = "✕", Header = T("清除所有自定义分隔符", "Clear custom separators") };
            clearSeps.Click += (_, _) =>
            {
                data.DockOrder.RemoveAll(t => t.StartsWith("sep:", StringComparison.OrdinalIgnoreCase));
                Relayout();
                DragEnded?.Invoke();
            };
            menu.Items.Add(clearSeps);
        }

        var toggleLabels = new MenuItem
        {
            Tag = "Aa",
            Header = data.Settings.ShowIconLabels
                ? T("切换为纯图标显示", "Switch to Icon Only mode")
                : T("切换为图标 + 文字显示", "Switch to Icon + Label mode")
        };
        toggleLabels.Click += (_, _) =>
        {
            data.Settings.ShowIconLabels = !data.Settings.ShowIconLabels;
            Relayout();
            DragEnded?.Invoke();
        };
        menu.Items.Add(toggleLabels);

        var toggleDrawer = new MenuItem
        {
            Tag = data.Settings.UtilityCollapsed ? "›" : "‹",
            Header = data.Settings.UtilityCollapsed
                ? T("展开右侧功能按钮", "Expand tool buttons")
                : T("折叠右侧功能按钮", "Collapse tool buttons")
        };
        toggleDrawer.Click += (_, _) =>
        {
            data.Settings.UtilityCollapsed = !data.Settings.UtilityCollapsed;
            Relayout();
            DragEnded?.Invoke();
        };
        menu.Items.Add(toggleDrawer);

        menu.Items.Add(new Separator());

        bool isDarkNow = data.Settings.ThemeMode == PinDock.ThemeMode.Dark ||
                         (data.Settings.ThemeMode == PinDock.ThemeMode.System && ThemeService.IsSystemDark());
        var toggleTheme = new MenuItem
        {
            Tag = isDarkNow ? "☀" : "🌙",
            Header = T(isDarkNow ? "切换为浅色主题" : "切换为深色主题", isDarkNow ? "Switch to Light theme" : "Switch to Dark theme")
        };
        toggleTheme.Click += (_, _) => ToggleThemeRequested?.Invoke();
        menu.Items.Add(toggleTheme);

        var allLibrary = new MenuItem { Tag = "▦", Header = T("打开全部收藏", "All Bookmarks") };
        allLibrary.Click += (_, _) => LibraryRequested?.Invoke();
        menu.Items.Add(allLibrary);

        var openSettings = new MenuItem { Tag = "⚙", Header = T("PinDock 设置", "PinDock Settings") };
        openSettings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(openSettings);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Tag = "⏻", Header = T("退出 PinDock", "Exit PinDock") };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        OpenContextMenu(menu);
        e.Handled = true;
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            isExternalDragOver = true;
            e.Effects = DragDropEffects.Copy | DragDropEffects.Link;
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        isExternalDragOver = true;
        e.Effects = DragDropEffects.Copy | DragDropEffects.Link;
        bool isVert = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        HighlightDropTarget(e.GetPosition(ItemsPanel), isVert);
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        isExternalDragOver = false;
        ClearDropHighlights();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        isExternalDragOver = false;
        ClearDropHighlights();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;

        bool isVert = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        var posInPanel = e.GetPosition(ItemsPanel);

        // 检查是否直接拖到了某个收藏组按钮上
        foreach (UIElement child in ItemsPanel.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is string token && token.StartsWith("grp:", StringComparison.Ordinal))
            {
                var pt = e.GetPosition(fe);
                if (pt.X >= 0 && pt.Y >= 0 && pt.X <= fe.ActualWidth && pt.Y <= fe.ActualHeight)
                {
                    string grpName = token[4..];
                    FilesDroppedToGroup?.Invoke(files, grpName);
                    e.Handled = true;
                    return;
                }
            }
        }

        var targetFe = FindTargetElementAt(posInPanel, isVert, out bool insertAfter);
        string? targetToken = targetFe?.Tag as string;
        if (FilesDroppedAt is not null)
            FilesDroppedAt.Invoke(files, targetToken, insertAfter);
        else
            FilesDropped?.Invoke(files);
        e.Handled = true;
    }

    private void DockChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != DockChrome && e.OriginalSource != ItemsPanel) return;
        if (data.Settings.LockPosition || isReordering) return;
        var cursor = PointToScreen(e.GetPosition(this));
        dragStartCursor = new ScreenRect(cursor.X, cursor.Y, 0, 0);
        dragOriginOffset = data.Settings.EdgeOffset;
        DockChrome.CaptureMouse();
    }

    private void DockChrome_MouseMove(object sender, MouseEventArgs e)
    {
        if (dragStartCursor is not ScreenRect start || e.LeftButton != MouseButtonState.Pressed) return;
        var cursor = PointToScreen(e.GetPosition(this));
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        double delta = vertical ? cursor.Y - start.Y : cursor.X - start.X;
        if (!IsDragging && Math.Abs(delta) < 4) return;
        IsDragging = true;
        double along = vertical ? placement.Bounds.Height : placement.Bounds.Width;
        data.Settings.EdgeOffset = DockLayout.ClampEdgeOffset(workArea, data.Settings.DockEdge, along, dragOriginOffset + delta);
        Reposition(placement.Capacity);
    }

    private void DockChrome_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        dragStartCursor = null;
        if (DockChrome.IsMouseCaptured) DockChrome.ReleaseMouseCapture();
        if (!IsDragging) return;
        IsDragging = false;
        e.Handled = true;
        DragEnded?.Invoke();
    }

    private void AttachReorderOrDrag(FrameworkElement element, string? itemToken)
    {
        element.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragStartCursor = null;
            IsDragging = false;
            isReordering = false;
            reorderToken = null;
            reorderElement = null;

            bool altHeld = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
            if (altHeld || string.IsNullOrEmpty(itemToken))
            {
                if (data.Settings.LockPosition) return;
                var cursor = element.PointToScreen(e.GetPosition(element));
                dragStartCursor = new ScreenRect(cursor.X, cursor.Y, 0, 0);
                dragOriginOffset = data.Settings.EdgeOffset;
                return;
            }
            reorderToken = itemToken;
            reorderElement = element;
            reorderStartPoint = e.GetPosition(ItemsPanel);
        };
        element.PreviewMouseMove += (_, e) =>
        {
            if (dragStartCursor is ScreenRect start && e.LeftButton == MouseButtonState.Pressed)
            {
                var cursor = element.PointToScreen(e.GetPosition(element));
                bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
                double delta = vertical ? cursor.Y - start.Y : cursor.X - start.X;
                if (!IsDragging && Math.Abs(delta) < 10) return;
                IsDragging = true;
                double along = vertical ? placement.Bounds.Height : placement.Bounds.Width;
                data.Settings.EdgeOffset = DockLayout.ClampEdgeOffset(workArea, data.Settings.DockEdge, along, dragOriginOffset + delta);
                Reposition(placement.Capacity);
                return;
            }

            if (reorderToken is null || reorderElement != element || e.LeftButton != MouseButtonState.Pressed) return;
            var currentPos = e.GetPosition(ItemsPanel);
            bool isVert = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
            double moveDelta = isVert ? currentPos.Y - reorderStartPoint.Y : currentPos.X - reorderStartPoint.X;
            if (!isReordering && Math.Abs(moveDelta) < 14) return;

            if (!isReordering)
            {
                isReordering = true;
                folderOpenTimer.Stop();
                folderPopup.Hide();
                element.CaptureMouse();
                element.Opacity = 0.55;
            }

            HighlightDropTarget(currentPos, isVert);
        };
        element.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (dragStartCursor is not null)
            {
                dragStartCursor = null;
                if (IsDragging)
                {
                    IsDragging = false;
                    e.Handled = true;
                    element.ReleaseMouseCapture();
                    DragEnded?.Invoke();
                }
                return;
            }

            if (reorderToken is not null && reorderElement == element)
            {
                string activeToken = reorderToken;
                reorderToken = null;
                reorderElement = null;
                element.Opacity = 1.0;
                ClearDropHighlights();
                if (element.IsMouseCaptured) element.ReleaseMouseCapture();

                if (isReordering)
                {
                    isReordering = false;
                    e.Handled = true;
                    var dropPos = e.GetPosition(ItemsPanel);
                    bool isVert = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
                    CommitReorder(activeToken, dropPos, isVert);
                }
            }
        };
    }

    private void HighlightDropTarget(Point posInPanel, bool vertical)
    {
        ClearDropHighlights();
        var target = FindTargetElementAt(posInPanel, vertical, out _);
        if (target is Button btn && ExtractIconBorder(btn) is Border b)
        {
            b.BorderBrush = (Brush)FindResource("AccentBrush");
        }
        else if (target is Border sepHit)
        {
            sepHit.Opacity = 0.6;
        }
    }

    private void ClearDropHighlights()
    {
        foreach (UIElement child in ItemsPanel.Children)
        {
            if (child is Button btn && ExtractIconBorder(btn) is Border b)
                b.BorderBrush = Brushes.Transparent;
            else if (child is Border sepHit && sepHit != reorderElement)
                sepHit.Opacity = 1.0;
        }
    }

    private FrameworkElement? FindTargetElementAt(Point posInPanel, bool vertical, out bool insertAfter)
    {
        insertAfter = false;
        FrameworkElement? lastReorderable = null;
        foreach (UIElement child in ItemsPanel.Children)
        {
            if (child is not FrameworkElement fe || fe.Tag is not string) continue;
            lastReorderable = fe;
            var topLeft = fe.TranslatePoint(new Point(0, 0), ItemsPanel);
            double center = vertical ? topLeft.Y + fe.ActualHeight / 2 : topLeft.X + fe.ActualWidth / 2;
            double cursor = vertical ? posInPanel.Y : posInPanel.X;
            if (cursor < center)
            {
                insertAfter = false;
                return fe;
            }
        }
        insertAfter = true;
        return lastReorderable;
    }

    private void CommitReorder(string draggedToken, Point posInPanel, bool vertical)
    {
        var targetFe = FindTargetElementAt(posInPanel, vertical, out bool insertAfter);
        if (targetFe?.Tag is not string targetToken || string.Equals(draggedToken, targetToken, StringComparison.Ordinal))
            return;

        data.NormalizeDockOrder();
        if (!data.DockOrder.Remove(draggedToken)) return;
        int targetIdx = data.DockOrder.IndexOf(targetToken);
        if (targetIdx < 0)
        {
            data.DockOrder.Add(draggedToken);
        }
        else
        {
            data.DockOrder.Insert(insertAfter ? targetIdx + 1 : targetIdx, draggedToken);
        }
        SyncItemsOrderFromDockOrder();
        Relayout();
        DragEnded?.Invoke();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && groupPopup.IsVisible)
        {
            groupPopup.Hide();
            e.Handled = true;
        }
    }
}
