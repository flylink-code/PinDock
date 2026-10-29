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
    private Bookmark? pendingHoverFolder;
    private Button? pendingHoverButton;
    private LibraryData data = new();
    private DockPlacement placement;
    private ScreenRect workArea;
    private List<Bookmark> overflow = [];
    private ScreenRect? dragStartCursor;
    private double dragOriginOffset;

    public event Action<Bookmark>? LaunchRequested;
    public event Action<Bookmark>? EditRequested;
    public event Action<Bookmark>? UnpinRequested;
    public event Action<Bookmark>? RemoveRequested;
    public event Action? LibraryRequested;
    public event Action? InstalledAppsRequested;
    public event Action? OverflowRequested;
    public event Action? AddRequested;
    public event Action? AddFolderRequested;
    public event Action? AddFileRequested;
    public event Action<string[]>? FilesDropped;
    public event Action? SettingsRequested;
    public event Action? DragEnded;

    public bool IsDragging { get; private set; }
    public bool IsShown { get; private set; }
    public bool IsGroupOpen => groupPopup.IsVisible || folderPopup.IsVisible;
    public ScreenRect CurrentBounds => placement.Bounds;
    public ScreenRect EdgeHandleRect => placement.EdgeHandle;
    public ScreenRect HitArea => placement.Bounds.Union(placement.EdgeHandle);
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
        folderOpenTimer.Tick += (_, _) =>
        {
            folderOpenTimer.Stop();
            if (pendingHoverFolder is not null && pendingHoverButton is not null && IsShown && !IsDragging)
                OpenFolderPopupFor(pendingHoverFolder, pendingHoverButton);
        };
        folderCloseTimer.Tick += (_, _) => CheckFolderPopupLeave();
    }

    public void Bind(LibraryData library)
    {
        data = library;
        Topmost = library.Settings.Topmost;
        Opacity = library.Settings.Opacity;
    }

    public void Relayout()
    {
        var settings = data.Settings;
        var monitor = MonitorQuery.Resolve(settings);
        workArea = monitor.WorkingArea;
        bool vertical = settings.DockEdge is DockEdge.Left or DockEdge.Right;
        double available = vertical ? workArea.Height : workArea.Width;
        int capacity = DockLayout.ComputeCapacity(available, DockLayout.IconPixels(settings.IconSize));
        var pinned = data.Items.Where(i => i.Pinned).ToList();
        overflow = pinned.Skip(capacity).ToList();
        Rebuild(pinned.Take(capacity).ToList());
        Reposition(capacity);
    }

    public void ShowDock()
    {
        if (IsShown && IsVisible) return;
        IsShown = true;
        Opacity = data.Settings.Opacity;
        if (!IsVisible) Show();
    }

    public void HideDock()
    {
        IsShown = false;
        folderOpenTimer.Stop();
        folderCloseTimer.Stop();
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
        DockChrome.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = Math.Ceiling(DockChrome.DesiredSize.Width * scale);
        double height = Math.Ceiling(DockChrome.DesiredSize.Height * scale);
        var bounds = DockLayout.Fit(workArea, settings.DockEdge, width, height, settings.EdgeOffset, settings.LockPosition);
        placement = new DockPlacement(bounds, capacity, DockLayout.EdgeHandleFor(workArea, settings.DockEdge, bounds));
        NativeWindow.MoveTo(this, bounds.X, bounds.Y);
    }

    private void Rebuild(IReadOnlyList<Bookmark> pinned)
    {
        ItemsPanel.Children.Clear();
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        ItemsPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DockChrome.Padding = vertical ? new Thickness(10, 12, 10, 12) : new Thickness(12, 10, 12, 10);
        double icon = DockLayout.IconPixels(data.Settings.IconSize);

        var pinnedButtons = pinned.Select(item => (FrameworkElement)CreateBookmarkButton(item, icon)).ToList();
        var groupButtons = data.Groups
            .Where(group => data.Items.Any(i => string.Equals(i.Group, group, StringComparison.Ordinal)))
            .Select(group => (FrameworkElement)CreateGroupButton(group, icon))
            .ToList();
        var utilityButtons = new List<FrameworkElement>();
        if (overflow.Count > 0)
            utilityButtons.Add(CreateUtilityButton($"…{overflow.Count}", $"更多固定项 · {overflow.Count}", icon, () => OverflowRequested?.Invoke(), isGear: false));
        utilityButtons.Add(CreateUtilityButton("▦", "全部收藏", icon, () => LibraryRequested?.Invoke(), isGear: false));
        utilityButtons.Add(CreateUtilityButton("＋", "添加收藏（右键可快速选文件夹/本机应用）", icon, () => AddRequested?.Invoke(), isGear: false));

        var gearSection = new List<FrameworkElement>
        {
            CreateUtilityButton("⚙", "打开设置", icon, () => SettingsRequested?.Invoke(), isGear: true)
        };

        foreach (var section in new[] { pinnedButtons, groupButtons, utilityButtons, gearSection })
        {
            if (section.Count == 0) continue;
            if (ItemsPanel.Children.Count > 0) ItemsPanel.Children.Add(CreateSeparator(icon));
            foreach (var element in section) ItemsPanel.Children.Add(element);
        }
        ((Button)ItemsPanel.Children[^1]).Margin = new Thickness(0);
    }

    private Button CreateBookmarkButton(Bookmark item, double icon)
    {
        bool isFolder = BookmarkKindHelper.FromPath(item.Path) == BookmarkKind.Folder;
        var button = CreateChrome(icon, item.Name, isFolder ? (Brush)FindResource("FolderBgBrush") : (Brush)FindResource("SoftBrush"));
        if (isFolder)
        {
            ((Border)button.Content).Child = CreatePrototypeFolderIcon(icon * 0.62);
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
            button.MouseEnter += (_, _) => folderOpenTimer.Stop();
            var host = new Grid();
            var fallback = new TextBlock
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
            ((Border)button.Content).Child = host;
            _ = LoadIcon(image, fallback, item.Path);
        }
        button.Click += (_, _) =>
        {
            folderOpenTimer.Stop();
            folderPopup.Hide();
            LaunchRequested?.Invoke(item);
        };
        button.MouseRightButtonUp += (_, e) =>
        {
            folderOpenTimer.Stop();
            folderPopup.Hide();
            ShowBookmarkContextMenu(item);
            e.Handled = true;
        };
        return button;
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
        if (folderPopup.IsKeyboardFocusWithin) return;
        if (!AutoHideController.TryGetCursorPoint(out var pt)) return;

        var dockRect = ExpandRect(placement.Bounds, 16);
        var popRect = ExpandRect(NativeWindow.GetPixelBounds(folderPopup), 18);
        if (!dockRect.Contains(pt.X, pt.Y) && !popRect.Contains(pt.X, pt.Y))
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

    private Button CreateGroupButton(string group, double icon)
    {
        var button = CreateChrome(icon, group, (Brush)FindResource("SoftBrush"));
        button.MouseEnter += (_, _) => folderOpenTimer.Stop();
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
        ((Border)button.Content).Child = grid;
        button.Click += (_, _) =>
        {
            folderOpenTimer.Stop();
            folderPopup.Hide();
            var members = data.Items.Where(i => string.Equals(i.Group, group, StringComparison.Ordinal));
            groupPopup.ShowGroup(group, members, placement.Bounds, workArea, data.Settings.DockEdge, data.Settings.Opacity);
        };
        return button;
    }

    private Button CreateUtilityButton(string glyph, string tip, double icon, Action action, bool isGear)
    {
        var button = CreateChrome(icon, tip, isGear ? Brushes.Transparent : (Brush)FindResource("SoftBrush"));
        button.MouseEnter += (_, _) => folderOpenTimer.Stop();
        ((Border)button.Content).Child = new TextBlock
        {
            Text = glyph,
            FontSize = isGear ? icon * 0.46 : icon * 0.4,
            Foreground = (Brush)FindResource(isGear ? "MutedBrush" : "AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => action();
        return button;
    }

    private FrameworkElement CreateSeparator(double icon)
    {
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        return new Border
        {
            Width = vertical ? icon * 0.62 : 1,
            Height = vertical ? 1 : icon * 0.62,
            Background = (Brush)FindResource("LineBrush"),
            Margin = vertical ? new Thickness(0, 0, 0, 9) : new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private Button CreateChrome(double icon, string tip, Brush background)
    {
        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform(0, 0);
        var border = new Border
        {
            Width = icon,
            Height = icon,
            CornerRadius = new CornerRadius(11),
            Background = background,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, translate } }
        };
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        var button = new Button
        {
            Content = border,
            Style = (Style)FindResource("DockButton"),
            Margin = vertical ? new Thickness(0, 0, 0, 9) : new Thickness(0, 0, 9, 0),
            ToolTip = tip
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
        AttachDrag(button);
        return button;
    }

    private async Task LoadIcon(Image image, UIElement fallback, string path)
    {
        try
        {
            image.Source = await icons.GetAsync(path);
            fallback.Visibility = Visibility.Collapsed;
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void ShowBookmarkContextMenu(Bookmark item)
    {
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = $"打开“{item.Name}”" };
        openItem.Click += (_, _) => LaunchRequested?.Invoke(item);
        menu.Items.Add(openItem);

        var revealItem = new MenuItem { Header = "在资源管理器中显示" };
        revealItem.Click += (_, _) => LibraryWindow.RevealInExplorer(item.Path);
        menu.Items.Add(revealItem);

        menu.Items.Add(new Separator());

        var editItem = new MenuItem { Header = "编辑收藏…" };
        editItem.Click += (_, _) => EditRequested?.Invoke(item);
        menu.Items.Add(editItem);

        var unpinItem = new MenuItem { Header = "从 Dock 取消固定" };
        unpinItem.Click += (_, _) => UnpinRequested?.Invoke(item);
        menu.Items.Add(unpinItem);

        var removeItem = new MenuItem { Header = "从收藏中移除" };
        removeItem.Click += (_, _) => RemoveRequested?.Invoke(item);
        menu.Items.Add(removeItem);

        menu.IsOpen = true;
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();

        var addFolder = new MenuItem { Header = "＋ 添加文件夹到 Dock…" };
        addFolder.Click += (_, _) => AddFolderRequested?.Invoke();
        menu.Items.Add(addFolder);

        var addApps = new MenuItem { Header = "⌕ 检索本机已安装应用…" };
        addApps.Click += (_, _) => InstalledAppsRequested?.Invoke();
        menu.Items.Add(addApps);

        var addFile = new MenuItem { Header = "＋ 添加应用 / 文件…" };
        addFile.Click += (_, _) => AddFileRequested?.Invoke();
        menu.Items.Add(addFile);

        var addCustom = new MenuItem { Header = "自定义添加收藏…" };
        addCustom.Click += (_, _) => AddRequested?.Invoke();
        menu.Items.Add(addCustom);

        menu.Items.Add(new Separator());

        var allLibrary = new MenuItem { Header = "▦ 打开全部收藏" };
        allLibrary.Click += (_, _) => LibraryRequested?.Invoke();
        menu.Items.Add(allLibrary);

        var openSettings = new MenuItem { Header = "⚙ PinDock 设置" };
        openSettings.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(openSettings);

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            FilesDropped?.Invoke(files);
    }

    private void AttachDrag(Button button)
    {
        button.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (data.Settings.LockPosition) return;
            var cursor = button.PointToScreen(e.GetPosition(button));
            dragStartCursor = new ScreenRect(cursor.X, cursor.Y, 0, 0);
            dragOriginOffset = data.Settings.EdgeOffset;
        };
        button.PreviewMouseMove += (_, e) =>
        {
            if (dragStartCursor is not ScreenRect start || e.LeftButton != MouseButtonState.Pressed) return;
            var cursor = button.PointToScreen(e.GetPosition(button));
            bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
            double delta = vertical ? cursor.Y - start.Y : cursor.X - start.X;
            if (!IsDragging && Math.Abs(delta) < 4) return;
            IsDragging = true;
            double along = vertical ? placement.Bounds.Height : placement.Bounds.Width;
            data.Settings.EdgeOffset = DockLayout.ClampEdgeOffset(workArea, data.Settings.DockEdge, along, dragOriginOffset + delta);
            Reposition(placement.Capacity);
        };
        button.PreviewMouseLeftButtonUp += (_, e) =>
        {
            dragStartCursor = null;
            if (!IsDragging) return;
            IsDragging = false;
            e.Handled = true;
            button.ReleaseMouseCapture();
            DragEnded?.Invoke();
        };
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
