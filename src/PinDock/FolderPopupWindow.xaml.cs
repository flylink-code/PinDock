using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PinDock;

public partial class FolderPopupWindow : Window
{
    private const double PopupGap = 12;
    private const double OffscreenX = -32000;
    private const int InitialBatchSize = 12;
    private const int BackgroundBatchSize = 24;

    private sealed record RowResources(
        Brush InkBrush,
        Brush MutedBrush,
        Brush AccentBrush,
        Brush FolderBgBrush,
        Brush FolderFgBrush,
        Brush SoftBrush,
        Brush SelectedBrush,
        Style SubtleButtonStyle,
        Style PrimaryButtonStyle);

    private readonly ShellIconService icons;
    private readonly DispatcherTimer hoverDetailTimer = new() { Interval = TimeSpan.FromMilliseconds(580) };
    private LibraryData data = new();
    private string rootDirectory = "";
    private string currentDirectory = "";
    private string defaultFooterTip = "";
    private DirectorySortMode currentSortMode = DirectorySortMode.NameAsc;
    private IReadOnlyList<DirectoryEntryItem> entries = [];
    private IReadOnlyList<AppRecentItem> appRecentEntries = [];
    private Bookmark? currentAppBookmark;
    private bool isAppRecentView;
    private string appDrillRootDirectory = "";
    private ScreenRect anchorRect;
    private ScreenRect workArea;
    private DockEdge dockEdge;
    private bool showingHidden;
    private int renderVersion;
    private long lastScrollTick;
    private ToolTip? activeDetailToolTip;
    private FrameworkElement? hoveredRowElement;
    private Action? pendingDetailRevealAction;
    private Point lastHoverMousePoint;

    public event Action<Bookmark>? OpenRequested;
    public event Action<Bookmark, AppRecentItem>? AppRecentLaunchRequested;
    public event Action<string>? PinFolderRequested;
    public event Action? HiddenFoldersChanged;

    public string CurrentRootPath => rootDirectory;
    public bool IsContextMenuOpen { get; private set; }

    public FolderPopupWindow(ShellIconService icons)
    {
        this.icons = icons;
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
        hoverDetailTimer.Tick += (_, _) => OnHoverDetailTimerTick();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
                CancelHoverDetail(clearTarget: true);
        };
    }

    public void Bind(LibraryData library)
    {
        data = library;
    }

    private string T(string zh, string en) => I18n.T(data.Settings, zh, en);

    public bool IsShowingRoot(string folderPath)
    {
        if (!IsVisible || isAppRecentView || string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(folderPath))
            return false;
        if (!string.Equals(rootDirectory, currentDirectory, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            string full = Path.GetFullPath(folderPath);
            return string.Equals(rootDirectory, full, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public bool IsShowingAppRecent(Bookmark appBookmark)
    {
        if (!IsVisible || !isAppRecentView || currentAppBookmark is null || string.IsNullOrWhiteSpace(appBookmark.Path))
            return false;
        return string.Equals(currentAppBookmark.Path, appBookmark.Path, StringComparison.OrdinalIgnoreCase);
    }

    public void ShowFolder(string folderPath, string displayName, ScreenRect anchor, ScreenRect monitorWorkArea, DockEdge edge, double opacity)
    {
        if (!Directory.Exists(folderPath)) return;
        string fullRoot = Path.GetFullPath(folderPath);
        anchorRect = anchor;
        workArea = monitorWorkArea;
        dockEdge = edge;
        Opacity = 1.0;

        if (IsVisible &&
            !isAppRecentView &&
            currentAppBookmark is null &&
            string.Equals(rootDirectory, fullRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentDirectory, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            RepositionPopup();
            return;
        }

        isAppRecentView = false;
        currentAppBookmark = null;
        appDrillRootDirectory = "";
        rootDirectory = fullRoot;
        currentSortMode = data.GetFolderSortMode(rootDirectory);
        showingHidden = false;
        AddRecentFolderButton.Visibility = Visibility.Collapsed;
        AddRecentFileButton.Visibility = Visibility.Collapsed;
        UpButton.Content = T("⬅ 上一级", "⬅ Up");
        OpenCurrentButton.Content = T("↗ 打开当前目录", "↗ Open Folder");
        SearchHintText.Text = T("搜索当前目录下的文件夹或文件…", "Search folders or files…");
        defaultFooterTip = T("单击直接打开 · 悬停查看全名 · 右键更多操作", "Click to open · Hover for full name · Right-click for more");
        ResetFooterTip();
        UpdateSortButtonLabel();
        NavigateTo(rootDirectory, displayName);

        if (!IsVisible)
        {
            NativeWindow.MoveTo(this, OffscreenX, 0);
            Show();
        }
        RepositionPopup();
    }

    public bool ShowAppRecent(
        Bookmark appBookmark,
        ScreenRect anchor,
        ScreenRect monitorWorkArea,
        DockEdge edge,
        double opacity,
        bool forceShowEmpty = false)
    {
        string sortKey = "app:" + AppRecentService.ResolveAppTarget(appBookmark.Path).AppKey;
        var savedSort = data.FolderSortModes.TryGetValue(sortKey, out var sm) && Enum.IsDefined(sm)
            ? sm
            : DirectorySortMode.ModifiedDesc;

        var recents = AppRecentService.ListRecentItems(appBookmark, data, savedSort);
        if (recents.Count == 0 && !forceShowEmpty)
            return false;

        anchorRect = anchor;
        workArea = monitorWorkArea;
        dockEdge = edge;
        Opacity = 1.0;

        if (IsVisible && IsShowingAppRecent(appBookmark))
        {
            appRecentEntries = recents;
            RenderAppRecentList(SearchBox.Text.Trim());
            RepositionPopup();
            return true;
        }

        currentAppBookmark = appBookmark;
        isAppRecentView = true;
        appDrillRootDirectory = "";
        rootDirectory = sortKey;
        currentDirectory = "";
        currentSortMode = savedSort;
        appRecentEntries = recents;
        showingHidden = false;

        ShowAppRecentRootUi();

        if (!IsVisible)
        {
            NativeWindow.MoveTo(this, OffscreenX, 0);
            Show();
        }
        RepositionPopup();
        return true;
    }

    private void ShowAppRecentRootUi()
    {
        if (currentAppBookmark is null) return;
        isAppRecentView = true;
        appDrillRootDirectory = "";
        currentDirectory = "";

        string appName = currentAppBookmark.Name;
        TitleText.Text = T($"{appName} · 最近打开", $"{appName} · Recent");
        TitleText.ToolTip = currentAppBookmark.Path;
        PathText.Text = T("工作区、目录与最近项目 · 支持拖拽文件夹/文件到此面板添加", "Workspaces, folders & recent items · Drag & drop here to add");
        PathText.ToolTip = AppRecentService.ResolveAppTarget(currentAppBookmark.Path).ExecutablePath;

        UpButton.Visibility = Visibility.Collapsed;
        AddRecentFolderButton.Visibility = Visibility.Visible;
        AddRecentFolderButton.Content = T("＋ 目录", "＋ Folder");
        AddRecentFolderButton.ToolTip = T("手动添加常用文件夹到此应用的最近列表", "Add folder to this app's recent list");
        AddRecentFileButton.Visibility = Visibility.Visible;
        AddRecentFileButton.Content = T("＋ 文件", "＋ File");
        AddRecentFileButton.ToolTip = T("手动添加工作区或文件到此应用的最近列表", "Add workspace or file to this app's recent list");
        OpenCurrentButton.Content = T("↗ 启动应用", "↗ Launch App");
        SearchHintText.Text = T("搜索最近打开的工作区、文件夹或路径…", "Search recent workspaces, folders or paths…");
        defaultFooterTip = T($"单击直接用 {appName} 打开 · 悬停可展开浏览目录 · 右键更多", $"Click to open in {appName} · Expand to browse · Right-click for more");
        ResetFooterTip();
        UpdateSortButtonLabel();

        appRecentEntries = AppRecentService.ListRecentItems(currentAppBookmark, data, currentSortMode);
        if (SearchBox.Text.Length > 0)
            SearchBox.Text = "";
        else
            RenderAppRecentList("");
    }

    private void RepositionPopup()
    {
        UpdateLayout();
        var size = NativeWindow.GetPixelBounds(this);
        var position = Place(anchorRect, workArea, dockEdge, size.Width, size.Height);
        NativeWindow.MoveTo(this, position.X, position.Y);
    }

    private void ResetFooterTip()
    {
        FooterTipText.Text = defaultFooterTip;
        FooterTipText.Foreground = (Brush)FindResource("MutedBrush");
        FooterTipText.ToolTip = null;
    }

    private void SetFooterPreviewName(string fullName)
    {
        FooterTipText.Text = fullName;
        FooterTipText.Foreground = (Brush)FindResource("InkBrush");
        FooterTipText.ToolTip = fullName;
    }

    private void CancelHoverDetail(bool clearTarget = false)
    {
        hoverDetailTimer.Stop();
        if (activeDetailToolTip is not null)
        {
            activeDetailToolTip.IsOpen = false;
            activeDetailToolTip = null;
        }
        ResetFooterTip();
        if (clearTarget)
        {
            hoveredRowElement = null;
            pendingDetailRevealAction = null;
        }
    }

    private void OnHoverDetailTimerTick()
    {
        hoverDetailTimer.Stop();
        if (!IsVisible || IsContextMenuOpen || hoveredRowElement is null || pendingDetailRevealAction is null || !hoveredRowElement.IsMouseOver)
            return;

        if (Environment.TickCount64 - lastScrollTick < 320)
        {
            hoverDetailTimer.Start();
            return;
        }

        pendingDetailRevealAction.Invoke();
    }

    private void AttachRowHoverDetail(
        Border rowBorder,
        Button mainBtn,
        StackPanel? hoverActionsPanel,
        TextBlock? timeBlock,
        RowResources res,
        string footerPreviewText,
        Func<ToolTip> buildToolTip)
    {
        void RevealDetailNow()
        {
            SetFooterPreviewName(footerPreviewText);
            if (activeDetailToolTip is not null)
                activeDetailToolTip.IsOpen = false;
            var tip = buildToolTip();
            tip.PlacementTarget = mainBtn;
            tip.Placement = PlacementMode.Top;
            tip.VerticalOffset = -4;
            tip.IsOpen = true;
            activeDetailToolTip = tip;
        }

        rowBorder.MouseEnter += (_, e) =>
        {
            rowBorder.Background = res.SelectedBrush;
            if (hoverActionsPanel is not null)
            {
                hoverActionsPanel.Visibility = Visibility.Visible;
                if (timeBlock is not null) timeBlock.Visibility = Visibility.Collapsed;
            }
            CancelHoverDetail(clearTarget: false);
            hoveredRowElement = rowBorder;
            pendingDetailRevealAction = RevealDetailNow;
            lastHoverMousePoint = e.GetPosition(this);
            hoverDetailTimer.Start();
        };

        rowBorder.MouseMove += (_, e) =>
        {
            var pt = e.GetPosition(this);
            if (Math.Abs(pt.X - lastHoverMousePoint.X) > 3 || Math.Abs(pt.Y - lastHoverMousePoint.Y) > 3)
            {
                lastHoverMousePoint = pt;
                if (activeDetailToolTip is null)
                {
                    hoverDetailTimer.Stop();
                    hoverDetailTimer.Start();
                }
            }
        };

        rowBorder.MouseLeave += (_, _) =>
        {
            rowBorder.Background = Brushes.Transparent;
            if (hoverActionsPanel is not null)
            {
                hoverActionsPanel.Visibility = Visibility.Collapsed;
                if (timeBlock is not null) timeBlock.Visibility = Visibility.Visible;
            }
            if (ReferenceEquals(hoveredRowElement, rowBorder))
                CancelHoverDetail(clearTarget: true);
            else
                CancelHoverDetail(clearTarget: false);
        };
    }

    private void ListScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        lastScrollTick = Environment.TickCount64;
        CancelHoverDetail(clearTarget: false);
        if (hoveredRowElement is not null && pendingDetailRevealAction is not null)
            hoverDetailTimer.Start();
    }

    private void ListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.1 && Math.Abs(e.HorizontalChange) < 0.1)
            return;
        lastScrollTick = Environment.TickCount64;
        CancelHoverDetail(clearTarget: false);
        if (hoveredRowElement is not null && pendingDetailRevealAction is not null)
            hoverDetailTimer.Start();
    }

    private void NavigateTo(string directoryPath, string? customTitle = null, bool fromAppRecent = false)
    {
        isAppRecentView = false;
        currentDirectory = directoryPath;
        if (fromAppRecent)
            appDrillRootDirectory = directoryPath;
        showingHidden = false;
        AddRecentFolderButton.Visibility = Visibility.Collapsed;
        AddRecentFileButton.Visibility = Visibility.Collapsed;

        string folderName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string resolvedTitle = !string.IsNullOrWhiteSpace(customTitle)
            ? customTitle
            : (folderName.Length > 0 ? folderName : directoryPath);
        TitleText.Text = resolvedTitle;
        TitleText.ToolTip = folderName.Length > 0 && !string.Equals(resolvedTitle, folderName, StringComparison.Ordinal)
            ? $"{resolvedTitle} ({folderName})"
            : resolvedTitle;
        PathText.Text = directoryPath;
        PathText.ToolTip = directoryPath;

        if (currentAppBookmark is not null)
        {
            UpButton.Visibility = Visibility.Visible;
            bool atDrillRoot = string.Equals(currentDirectory, appDrillRootDirectory, StringComparison.OrdinalIgnoreCase);
            UpButton.Content = atDrillRoot
                ? T("⬅ 返回最近", "⬅ Recent")
                : T("⬅ 上一级", "⬅ Up");
            OpenCurrentButton.Content = T("↗ 用此应用打开", "↗ Open in App");
        }
        else
        {
            var parent = Directory.GetParent(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            UpButton.Visibility = parent is not null ? Visibility.Visible : Visibility.Collapsed;
            UpButton.Content = T("⬅ 上一级", "⬅ Up");
            OpenCurrentButton.Content = T("↗ 打开当前目录", "↗ Open Folder");
        }

        UpdateSortButtonLabel();
        entries = DirectoryBrowserService.ListEntries(directoryPath, currentSortMode);
        if (SearchBox.Text.Length > 0)
            SearchBox.Text = "";
        else
            RenderList("");
    }

    private void UpdateSortButtonLabel()
    {
        if (isAppRecentView)
        {
            SortButton.Content = currentSortMode switch
            {
                DirectorySortMode.NameAsc => T("⇅ 名称 A-Z", "⇅ Name A-Z"),
                DirectorySortMode.NameDesc => T("⇅ 名称 Z-A", "⇅ Name Z-A"),
                DirectorySortMode.ModifiedAsc => T("⇅ 最早打开", "⇅ Oldest ↑"),
                _ => T("⇅ 最近打开", "⇅ Recent ↓")
            };
            return;
        }

        SortButton.Content = currentSortMode switch
        {
            DirectorySortMode.NameAsc => T("⇅ 名称 A-Z", "⇅ Name A-Z"),
            DirectorySortMode.NameDesc => T("⇅ 名称 Z-A", "⇅ Name Z-A"),
            DirectorySortMode.ModifiedDesc => T("⇅ 最近修改", "⇅ Modified ↓"),
            DirectorySortMode.ModifiedAsc => T("⇅ 最早修改", "⇅ Modified ↑"),
            DirectorySortMode.CreatedDesc => T("⇅ 最近创建", "⇅ Created ↓"),
            _ => T("⇅ 名称 A-Z", "⇅ Name A-Z")
        };
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => IsContextMenuOpen = true;
        menu.Closed += (_, _) => IsContextMenuOpen = false;

        var modes = isAppRecentView
            ? new (DirectorySortMode Mode, string Zh, string En)[]
            {
                (DirectorySortMode.ModifiedDesc, "按最近打开时间 (最新优先)", "Recently Opened (Newest first)"),
                (DirectorySortMode.ModifiedAsc, "按打开时间 (最早优先)", "Recently Opened (Oldest first)"),
                (DirectorySortMode.NameAsc, "按名称 (A → Z，自然排序)", "Name (A → Z, Natural)"),
                (DirectorySortMode.NameDesc, "按名称 (Z → A)", "Name (Z → A)")
            }
            : new (DirectorySortMode Mode, string Zh, string En)[]
            {
                (DirectorySortMode.NameAsc, "按名称 (A → Z，自然排序)", "Name (A → Z, Natural)"),
                (DirectorySortMode.NameDesc, "按名称 (Z → A)", "Name (Z → A)"),
                (DirectorySortMode.ModifiedDesc, "按修改时间 (最新优先)", "Date Modified (Newest first)"),
                (DirectorySortMode.ModifiedAsc, "按修改时间 (最早优先)", "Date Modified (Oldest first)"),
                (DirectorySortMode.CreatedDesc, "按创建时间 (最新优先)", "Date Created (Newest first)")
            };

        foreach (var (mode, zh, en) in modes)
        {
            bool active = mode == currentSortMode;
            var item = new MenuItem
            {
                Tag = active ? "✓" : "",
                Header = T(zh, en),
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
            };
            item.Click += (_, _) =>
            {
                currentSortMode = mode;
                data.SetFolderSortMode(rootDirectory, mode);
                HiddenFoldersChanged?.Invoke();
                UpdateSortButtonLabel();
                if (isAppRecentView && currentAppBookmark is not null)
                {
                    appRecentEntries = AppRecentService.ListRecentItems(currentAppBookmark, data, currentSortMode);
                    RenderAppRecentList(SearchBox.Text.Trim());
                }
                else
                {
                    entries = DirectoryBrowserService.ListEntries(currentDirectory, currentSortMode);
                    RenderList(SearchBox.Text.Trim());
                }
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private static Point Place(ScreenRect anchor, ScreenRect workArea, DockEdge edge, double width, double height)
    {
        double x, y;
        switch (edge)
        {
            case DockEdge.Bottom:
                x = anchor.X + anchor.Width / 2 - width / 2;
                y = anchor.Y - PopupGap - height;
                break;
            case DockEdge.Top:
                x = anchor.X + anchor.Width / 2 - width / 2;
                y = anchor.Y + anchor.Height + PopupGap;
                break;
            case DockEdge.Left:
                x = anchor.X + anchor.Width + PopupGap;
                y = anchor.Y + anchor.Height / 2 - height / 2;
                break;
            case DockEdge.Right:
                x = anchor.X - PopupGap - width;
                y = anchor.Y + anchor.Height / 2 - height / 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(edge), edge, null);
        }
        x = Math.Clamp(x, workArea.X + 8, Math.Max(workArea.X + 8, workArea.X + workArea.Width - width - 8));
        y = Math.Clamp(y, workArea.Y + 8, Math.Max(workArea.Y + 8, workArea.Y + workArea.Height - height - 8));
        return new Point(x, y);
    }

    private RowResources GetRowResources() => new(
        InkBrush: (Brush)FindResource("InkBrush"),
        MutedBrush: (Brush)FindResource("MutedBrush"),
        AccentBrush: (Brush)FindResource("AccentBrush"),
        FolderBgBrush: (Brush)FindResource("FolderBgBrush"),
        FolderFgBrush: (Brush)FindResource("FolderFgBrush"),
        SoftBrush: (Brush)FindResource("SoftBrush"),
        SelectedBrush: (Brush)FindResource("SelectedBrush"),
        SubtleButtonStyle: (Style)FindResource("SubtleButton"),
        PrimaryButtonStyle: (Style)FindResource("PrimaryButton"));

    private void RenderList(string query)
    {
        int version = ++renderVersion;
        ListPanel.Children.Clear();
        ListScrollViewer.ScrollToTop();
        CancelHoverDetail(clearTarget: true);

        var res = GetRowResources();
        var queryMatched = string.IsNullOrWhiteSpace(query)
            ? entries
            : entries.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        int hiddenInDirCount = queryMatched.Count(e => e.IsDirectory && data.IsFolderHidden(e.FullPath));
        if (hiddenInDirCount > 0)
        {
            ToggleHiddenBtn.Visibility = Visibility.Visible;
            ToggleHiddenBtn.Content = showingHidden
                ? T($"收起隐藏 ({hiddenInDirCount})", $"Hide ({hiddenInDirCount})")
                : T($"已隐藏 {hiddenInDirCount} 项", $"{hiddenInDirCount} Hidden");
            ToggleHiddenBtn.Style = showingHidden ? res.PrimaryButtonStyle : res.SubtleButtonStyle;
        }
        else
        {
            showingHidden = false;
            ToggleHiddenBtn.Visibility = Visibility.Collapsed;
        }

        var visibleList = showingHidden
            ? queryMatched
            : queryMatched.Where(e => !e.IsDirectory || !data.IsFolderHidden(e.FullPath)).ToList();

        int folderCount = visibleList.Count(e => e.IsDirectory);
        int fileCount = visibleList.Count - folderCount;
        CountText.Text = T($"{folderCount} 个目录 · {fileCount} 个文件", $"{folderCount} folders · {fileCount} files");

        if (visibleList.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(query)
                    ? T("此目录为空（或已全部隐藏）", "Folder is empty (or all hidden)")
                    : T("没有匹配的子目录或文件", "No matching subfolders or files"),
                Foreground = res.MutedBrush,
                FontSize = 12,
                Margin = new Thickness(4, 14, 4, 14),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        int initialCount = Math.Min(InitialBatchSize, visibleList.Count);
        for (int i = 0; i < initialCount; i++)
        {
            ListPanel.Children.Add(CreateEntryRow(visibleList[i], res, version));
        }

        if (visibleList.Count > initialCount)
        {
            _ = AppendRemainingRowsAsync(visibleList, initialCount, res, version);
        }
    }

    private async Task AppendRemainingRowsAsync(
        IReadOnlyList<DirectoryEntryItem> visibleList,
        int startIndex,
        RowResources res,
        int version)
    {
        int index = startIndex;
        while (index < visibleList.Count)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != renderVersion || !IsVisible)
                    return;

                int end = Math.Min(index + BackgroundBatchSize, visibleList.Count);
                for (int i = index; i < end; i++)
                {
                    ListPanel.Children.Add(CreateEntryRow(visibleList[i], res, version));
                }
                index = end;
            }, DispatcherPriority.Background);

            if (version != renderVersion || !IsVisible)
                return;
        }
    }

    private UIElement CreateEntryRow(DirectoryEntryItem entry, RowResources res, int version)
    {
        bool isHiddenEntry = entry.IsDirectory && data.IsFolderHidden(entry.FullPath);
        var rowDock = new DockPanel();
        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(0, 1, 0, 1),
            Opacity = isHiddenEntry ? 0.56 : 1.0,
            Child = rowDock
        };

        StackPanel? hoverActionsPanel = null;

        if (entry.IsDirectory)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            DockPanel.SetDock(actions, Dock.Right);

            if (isHiddenEntry)
            {
                var restoreBtn = new Button
                {
                    Style = res.SubtleButtonStyle,
                    Content = T("恢复 ↺", "Restore ↺"),
                    ToolTip = T("恢复在此目录预览中显示", "Restore folder visibility"),
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 11,
                    Foreground = res.AccentBrush
                };
                restoreBtn.Click += (_, _) =>
                {
                    data.SetFolderHidden(entry.FullPath, false);
                    HiddenFoldersChanged?.Invoke();
                    RenderList(SearchBox.Text.Trim());
                };
                actions.Children.Add(restoreBtn);
            }
            else
            {
                hoverActionsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };

                var hoverHideBtn = new Button
                {
                    Style = res.SubtleButtonStyle,
                    Content = T("隐藏", "Hide"),
                    ToolTip = T("在此预览中隐藏此文件夹", "Hide this folder in preview"),
                    Padding = new Thickness(7, 3.5, 7, 3.5),
                    FontSize = 10.5,
                    Margin = new Thickness(0, 0, 4, 0),
                    Foreground = res.MutedBrush
                };
                hoverHideBtn.Click += (_, _) =>
                {
                    data.SetFolderHidden(entry.FullPath, true);
                    HiddenFoldersChanged?.Invoke();
                    RenderList(SearchBox.Text.Trim());
                };

                var pinBtn = new Button
                {
                    Content = "☆",
                    ToolTip = T("固定此目录到 Dock", "Pin folder to Dock"),
                    Padding = new Thickness(6, 4, 6, 4),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 4, 0),
                    Background = Brushes.Transparent,
                    Foreground = res.MutedBrush
                };
                pinBtn.Click += (_, _) =>
                {
                    PinFolderRequested?.Invoke(entry.FullPath);
                    pinBtn.Content = "★";
                    pinBtn.Foreground = res.AccentBrush;
                };

                hoverActionsPanel.Children.Add(hoverHideBtn);
                hoverActionsPanel.Children.Add(pinBtn);

                var drillBtn = new Button
                {
                    Style = res.SubtleButtonStyle,
                    Content = T("展开 ▸", "Expand ▸"),
                    ToolTip = T("在面板内查看下一级子目录", "Browse subfolders inside popup"),
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 11,
                    Foreground = res.AccentBrush
                };
                drillBtn.Click += (_, _) => NavigateTo(entry.FullPath);

                actions.Children.Add(hoverActionsPanel);
                actions.Children.Add(drillBtn);
            }
            rowDock.Children.Add(actions);
        }

        var mainBtn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 7, 6, 7),
            Cursor = Cursors.Hand
        };

        UIElement iconChild;
        if (entry.IsDirectory)
        {
            iconChild = new TextBlock
            {
                Text = "▰",
                FontSize = 14,
                Foreground = res.FolderFgBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (icons.TryGetCachedFileIcon(entry.FullPath) is { } cachedImg)
        {
            iconChild = CreateFileIconImage(cachedImg);
        }
        else
        {
            iconChild = new TextBlock
            {
                Text = "▤",
                FontSize = 14,
                Foreground = res.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconHost = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = entry.IsDirectory ? res.FolderBgBrush : res.SoftBrush,
            Margin = new Thickness(0, 0, 10, 0),
            Child = iconChild
        };

        var nameBlock = new TextBlock
        {
            Text = isHiddenEntry ? $"{entry.Name} ({T("已隐藏", "Hidden")})" : entry.Name,
            FontSize = 12,
            FontWeight = entry.IsDirectory ? FontWeights.Medium : FontWeights.Normal,
            Foreground = res.InkBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var innerRow = new DockPanel();
        DockPanel.SetDock(iconHost, Dock.Left);
        innerRow.Children.Add(iconHost);

        TextBlock? timeBlock = null;
        if (currentSortMode is DirectorySortMode.ModifiedDesc or DirectorySortMode.ModifiedAsc or DirectorySortMode.CreatedDesc)
        {
            DateTime ts = currentSortMode == DirectorySortMode.CreatedDesc ? entry.CreationTime : entry.LastWriteTime;
            if (ts > DateTime.MinValue)
            {
                string timeStr = ts.Year == DateTime.Now.Year
                    ? ts.ToString("MM-dd HH:mm")
                    : ts.ToString("yyyy-MM-dd");
                timeBlock = new TextBlock
                {
                    Text = timeStr,
                    FontSize = 10,
                    Foreground = res.MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 2, 0)
                };
                DockPanel.SetDock(timeBlock, Dock.Right);
                innerRow.Children.Add(timeBlock);
            }
        }

        innerRow.Children.Add(nameBlock);
        mainBtn.Content = innerRow;

        AttachRowHoverDetail(
            rowBorder,
            mainBtn,
            hoverActionsPanel,
            timeBlock,
            res,
            entry.FullPath,
            () => BuildEntryToolTip(entry, isHiddenEntry));

        mainBtn.Click += (_, _) =>
        {
            CancelHoverDetail(clearTarget: true);
            Hide();
            OpenRequested?.Invoke(new Bookmark { Name = entry.Name, Path = entry.FullPath });
        };

        mainBtn.MouseRightButtonUp += (_, e) =>
        {
            CancelHoverDetail(clearTarget: true);
            ShowEntryContextMenu(entry, isHiddenEntry);
            e.Handled = true;
        };

        if (!entry.IsDirectory && iconChild is TextBlock)
        {
            _ = LoadFileIconAsync(iconHost, entry.FullPath, version);
        }

        rowDock.Children.Add(mainBtn);
        return rowBorder;
    }

    private ToolTip BuildEntryToolTip(DirectoryEntryItem entry, bool isHiddenEntry)
    {
        var panel = new StackPanel { MaxWidth = 390 };
        panel.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = entry.FullPath,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var metaParts = new List<string>(3)
        {
            entry.IsDirectory
                ? (isHiddenEntry ? T("文件夹（已隐藏）", "Folder (Hidden)") : T("文件夹", "Folder"))
                : T("文件", "File")
        };

        if (!entry.IsDirectory && entry.FileSize > 0)
            metaParts.Add(DirectoryBrowserService.FormatFileSize(entry.FileSize));

        DateTime ts = currentSortMode == DirectorySortMode.CreatedDesc ? entry.CreationTime : entry.LastWriteTime;
        if (ts > DateTime.MinValue)
        {
            string label = currentSortMode == DirectorySortMode.CreatedDesc
                ? T("创建于", "Created")
                : T("修改于", "Modified");
            metaParts.Add($"{label} {ts:yyyy-MM-dd HH:mm}");
        }

        panel.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", metaParts),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(196, 255, 255, 255)),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return new ToolTip
        {
            Content = panel,
            Placement = PlacementMode.Top,
            VerticalOffset = -4,
            IsHitTestVisible = false
        };
    }

    private void ShowEntryContextMenu(DirectoryEntryItem entry, bool isHiddenEntry)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => IsContextMenuOpen = true;
        menu.Closed += (_, _) => IsContextMenuOpen = false;

        var openItem = new MenuItem { Tag = "↗", Header = T($"打开“{entry.Name}”", $"Open \"{entry.Name}\"") };
        openItem.Click += (_, _) =>
        {
            Hide();
            OpenRequested?.Invoke(new Bookmark { Name = entry.Name, Path = entry.FullPath });
        };
        menu.Items.Add(openItem);

        var copyNameItem = new MenuItem { Tag = "⎘", Header = T("复制完整名称", "Copy Full Name") };
        copyNameItem.Click += (_, _) =>
        {
            try { Clipboard.SetText(entry.Name); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        menu.Items.Add(copyNameItem);

        var copyPathItem = new MenuItem { Tag = "⌖", Header = T("复制完整路径", "Copy Full Path") };
        copyPathItem.Click += (_, _) =>
        {
            try { Clipboard.SetText(entry.FullPath); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        menu.Items.Add(copyPathItem);

        if (entry.IsDirectory)
        {
            var pinItem = new MenuItem { Tag = "★", Header = T("固定此文件夹到 Dock", "Pin folder to Dock") };
            pinItem.Click += (_, _) => PinFolderRequested?.Invoke(entry.FullPath);
            menu.Items.Add(pinItem);

            menu.Items.Add(new Separator());

            var toggleHide = new MenuItem
            {
                Tag = isHiddenEntry ? "↺" : "◌",
                Header = isHiddenEntry
                    ? T($"恢复显示“{entry.Name}”", $"Restore \"{entry.Name}\"")
                    : T($"在此预览中隐藏“{entry.Name}”", $"Hide \"{entry.Name}\" in preview")
            };
            toggleHide.Click += (_, _) =>
            {
                data.SetFolderHidden(entry.FullPath, !isHiddenEntry);
                HiddenFoldersChanged?.Invoke();
                RenderList(SearchBox.Text.Trim());
            };
            menu.Items.Add(toggleHide);
        }

        menu.IsOpen = true;
    }

    private void ToggleHiddenBtn_Click(object sender, RoutedEventArgs e)
    {
        showingHidden = !showingHidden;
        if (isAppRecentView)
            RenderAppRecentList(SearchBox.Text.Trim());
        else
            RenderList(SearchBox.Text.Trim());
    }

    private void RenderAppRecentList(string query)
    {
        int version = ++renderVersion;
        ListPanel.Children.Clear();
        ListScrollViewer.ScrollToTop();
        CancelHoverDetail(clearTarget: true);

        var res = GetRowResources();
        var queryMatched = string.IsNullOrWhiteSpace(query)
            ? appRecentEntries
            : appRecentEntries.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        int hiddenCount = queryMatched.Count(e => data.IsFolderHidden(e.FullPath));
        if (hiddenCount > 0)
        {
            ToggleHiddenBtn.Visibility = Visibility.Visible;
            ToggleHiddenBtn.Content = showingHidden
                ? T($"收起隐藏 ({hiddenCount})", $"Hide ({hiddenCount})")
                : T($"已隐藏 {hiddenCount} 项", $"{hiddenCount} Hidden");
            ToggleHiddenBtn.Style = showingHidden ? res.PrimaryButtonStyle : res.SubtleButtonStyle;
        }
        else
        {
            showingHidden = false;
            ToggleHiddenBtn.Visibility = Visibility.Collapsed;
        }

        var visibleList = showingHidden
            ? queryMatched
            : queryMatched.Where(e => !data.IsFolderHidden(e.FullPath)).ToList();

        int wsCount = visibleList.Count(e => e.Kind == AppRecentItemKind.Workspace);
        int folderCount = visibleList.Count(e => e.Kind is AppRecentItemKind.Folder or AppRecentItemKind.Remote);
        int fileCount = visibleList.Count(e => e.Kind == AppRecentItemKind.File);

        var countParts = new List<string>(3);
        if (wsCount > 0) countParts.Add(T($"{wsCount} 个工作区", $"{wsCount} workspaces"));
        if (folderCount > 0) countParts.Add(T($"{folderCount} 个目录", $"{folderCount} folders"));
        if (fileCount > 0) countParts.Add(T($"{fileCount} 个文件", $"{fileCount} files"));
        CountText.Text = countParts.Count > 0
            ? string.Join(" · ", countParts)
            : T("0 个最近项目", "0 recent items");

        if (visibleList.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(query)
                    ? T("暂无最近打开记录（可点击右上角“＋目录 / ＋文件”或直接拖入添加）", "No recent items yet (Click '+ Folder / + File' or drag & drop here)")
                    : T("没有匹配的最近工作区、目录或文件", "No matching recent workspaces, folders or files"),
                Foreground = res.MutedBrush,
                FontSize = 12,
                Margin = new Thickness(4, 16, 4, 16),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        int initialCount = Math.Min(InitialBatchSize, visibleList.Count);
        for (int i = 0; i < initialCount; i++)
        {
            ListPanel.Children.Add(CreateAppRecentRow(visibleList[i], res, version));
        }

        if (visibleList.Count > initialCount)
        {
            _ = AppendRemainingAppRecentRowsAsync(visibleList, initialCount, res, version);
        }
    }

    private async Task AppendRemainingAppRecentRowsAsync(
        IReadOnlyList<AppRecentItem> visibleList,
        int startIndex,
        RowResources res,
        int version)
    {
        int index = startIndex;
        while (index < visibleList.Count)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (version != renderVersion || !IsVisible || !isAppRecentView)
                    return;

                int end = Math.Min(index + BackgroundBatchSize, visibleList.Count);
                for (int i = index; i < end; i++)
                {
                    ListPanel.Children.Add(CreateAppRecentRow(visibleList[i], res, version));
                }
                index = end;
            }, DispatcherPriority.Background);

            if (version != renderVersion || !IsVisible || !isAppRecentView)
                return;
        }
    }

    private UIElement CreateAppRecentRow(AppRecentItem entry, RowResources res, int version)
    {
        bool isHiddenEntry = data.IsFolderHidden(entry.FullPath);
        var rowDock = new DockPanel();
        var rowBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 1, 2, 1),
            Margin = new Thickness(0, 1, 0, 1),
            Opacity = isHiddenEntry ? 0.56 : 1.0,
            Child = rowDock
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        };
        DockPanel.SetDock(actions, Dock.Right);

        StackPanel? hoverActionsPanel = null;
        if (isHiddenEntry)
        {
            var restoreBtn = new Button
            {
                Style = res.SubtleButtonStyle,
                Content = T("恢复 ↺", "Restore ↺"),
                ToolTip = T("恢复在此最近打开预览中显示", "Restore visibility"),
                Padding = new Thickness(8, 4, 8, 4),
                FontSize = 11,
                Foreground = res.AccentBrush
            };
            restoreBtn.Click += (_, _) =>
            {
                data.SetFolderHidden(entry.FullPath, false);
                HiddenFoldersChanged?.Invoke();
                RenderAppRecentList(SearchBox.Text.Trim());
            };
            actions.Children.Add(restoreBtn);
        }
        else
        {
            hoverActionsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var hoverHideBtn = new Button
            {
                Style = res.SubtleButtonStyle,
                Content = T("隐藏", "Hide"),
                ToolTip = T("在此预览中隐藏此项", "Hide this item in preview"),
                Padding = new Thickness(7, 3.5, 7, 3.5),
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 4, 0),
                Foreground = res.MutedBrush
            };
            hoverHideBtn.Click += (_, _) =>
            {
                data.SetFolderHidden(entry.FullPath, true);
                HiddenFoldersChanged?.Invoke();
                RenderAppRecentList(SearchBox.Text.Trim());
            };
            hoverActionsPanel.Children.Add(hoverHideBtn);

            if (entry.Kind != AppRecentItemKind.Remote)
            {
                var pinBtn = new Button
                {
                    Content = "☆",
                    ToolTip = T("固定此项目到 Dock", "Pin item to Dock"),
                    Padding = new Thickness(6, 4, 6, 4),
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 4, 0),
                    Background = Brushes.Transparent,
                    Foreground = res.MutedBrush
                };
                pinBtn.Click += (_, _) =>
                {
                    PinFolderRequested?.Invoke(entry.FullPath);
                    pinBtn.Content = "★";
                    pinBtn.Foreground = res.AccentBrush;
                };
                hoverActionsPanel.Children.Add(pinBtn);
            }

            actions.Children.Add(hoverActionsPanel);

            if (entry.CanBrowseDirectory && !string.IsNullOrWhiteSpace(entry.BrowsableDirectoryPath))
            {
                string browseTarget = entry.BrowsableDirectoryPath;
                var drillBtn = new Button
                {
                    Style = res.SubtleButtonStyle,
                    Content = T("展开 ▸", "Expand ▸"),
                    ToolTip = T("在面板内展开浏览此目录的内容", "Browse folder contents inside popup"),
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 11,
                    Foreground = res.AccentBrush
                };
                drillBtn.Click += (_, _) => NavigateTo(browseTarget, entry.Name, fromAppRecent: true);
                actions.Children.Add(drillBtn);
            }
        }
        rowDock.Children.Add(actions);

        var mainBtn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 7, 6, 7),
            Cursor = Cursors.Hand
        };

        UIElement iconChild;
        bool isFolderStyle = entry.Kind == AppRecentItemKind.Folder;
        if (entry.Kind == AppRecentItemKind.Workspace)
        {
            iconChild = new TextBlock
            {
                Text = "❖",
                FontSize = 14,
                Foreground = res.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (entry.Kind == AppRecentItemKind.Folder)
        {
            iconChild = new TextBlock
            {
                Text = "▰",
                FontSize = 14,
                Foreground = res.FolderFgBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (entry.Kind == AppRecentItemKind.Remote)
        {
            iconChild = new TextBlock
            {
                Text = "⇄",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = res.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        else if (icons.TryGetCachedFileIcon(entry.FullPath) is { } cachedImg)
        {
            iconChild = CreateFileIconImage(cachedImg);
        }
        else
        {
            iconChild = new TextBlock
            {
                Text = "▤",
                FontSize = 14,
                Foreground = res.AccentBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var iconHost = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = isFolderStyle ? res.FolderBgBrush : res.SoftBrush,
            Margin = new Thickness(0, 0, 10, 0),
            Child = iconChild
        };

        var topLine = new DockPanel { VerticalAlignment = VerticalAlignment.Center };

        if (entry.Kind == AppRecentItemKind.Workspace || entry.Kind == AppRecentItemKind.Remote || entry.IsCurrentlyOpen || entry.PinnedByUser)
        {
            var badgePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            DockPanel.SetDock(badgePanel, Dock.Right);

            if (entry.PinnedByUser)
            {
                badgePanel.Children.Add(CreateTagPill(T("置顶", "Pinned"), res.AccentBrush, res.SoftBrush));
            }
            if (entry.IsCurrentlyOpen)
            {
                badgePanel.Children.Add(CreateTagPill(T("打开中", "Open"), res.AccentBrush, res.SoftBrush));
            }
            if (entry.Kind == AppRecentItemKind.Workspace)
            {
                badgePanel.Children.Add(CreateTagPill(T("工作区", "Workspace"), res.AccentBrush, res.SoftBrush));
            }
            else if (entry.Kind == AppRecentItemKind.Remote)
            {
                badgePanel.Children.Add(CreateTagPill(T("远程", "Remote"), res.AccentBrush, res.SoftBrush));
            }
            topLine.Children.Add(badgePanel);
        }

        var nameBlock = new TextBlock
        {
            Text = isHiddenEntry ? $"{entry.Name} ({T("已隐藏", "Hidden")})" : entry.Name,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = res.InkBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        topLine.Children.Add(nameBlock);

        var innerRow = new DockPanel();
        DockPanel.SetDock(iconHost, Dock.Left);
        innerRow.Children.Add(iconHost);

        TextBlock? timeBlock = null;
        if (entry.LastOpenedTime > DateTime.MinValue)
        {
            string timeStr = entry.LastOpenedTime.Year == DateTime.Now.Year
                ? entry.LastOpenedTime.ToString("MM-dd HH:mm")
                : entry.LastOpenedTime.ToString("yyyy-MM-dd");
            timeBlock = new TextBlock
            {
                Text = timeStr,
                FontSize = 10,
                Foreground = res.MutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0)
            };
            DockPanel.SetDock(timeBlock, Dock.Right);
            innerRow.Children.Add(timeBlock);
        }

        innerRow.Children.Add(topLine);
        mainBtn.Content = innerRow;

        AttachRowHoverDetail(
            rowBorder,
            mainBtn,
            hoverActionsPanel,
            timeBlock,
            res,
            entry.FullPath,
            () => BuildAppRecentToolTip(entry, isHiddenEntry));

        mainBtn.Click += (_, _) =>
        {
            CancelHoverDetail(clearTarget: true);
            if (currentAppBookmark is null) return;
            var app = currentAppBookmark;
            Hide();
            AppRecentLaunchRequested?.Invoke(app, entry);
        };

        mainBtn.MouseRightButtonUp += (_, e) =>
        {
            CancelHoverDetail(clearTarget: true);
            ShowAppRecentContextMenu(entry, isHiddenEntry);
            e.Handled = true;
        };

        if (entry.Kind == AppRecentItemKind.File && iconChild is TextBlock)
        {
            _ = LoadFileIconAsync(iconHost, entry.FullPath, version);
        }

        rowDock.Children.Add(mainBtn);
        return rowBorder;
    }

    private static Border CreateTagPill(string text, Brush fg, Brush bg) => new()
    {
        Background = bg,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(4, 1, 4, 1),
        Margin = new Thickness(3, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            FontWeight = FontWeights.Medium,
            Foreground = fg
        }
    };

    private ToolTip BuildAppRecentToolTip(AppRecentItem entry, bool isHiddenEntry)
    {
        var panel = new StackPanel { MaxWidth = 390 };
        panel.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });

        panel.Children.Add(new TextBlock
        {
            Text = entry.FullPath,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var metaParts = new List<string>(3)
        {
            entry.Kind switch
            {
                AppRecentItemKind.Workspace => T("工作区 (.code-workspace / 工程)", "Workspace"),
                AppRecentItemKind.Folder => T("项目文件夹", "Folder"),
                AppRecentItemKind.Remote => T("远程连接工作区", "Remote Workspace"),
                _ => T("最近文件", "Recent File")
            }
        };
        if (isHiddenEntry)
            metaParts.Add(T("已隐藏", "Hidden"));
        if (entry.LastOpenedTime > DateTime.MinValue)
            metaParts.Add($"{T("最近使用", "Recent")} {entry.LastOpenedTime:yyyy-MM-dd HH:mm}");

        panel.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", metaParts),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)),
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        return new ToolTip
        {
            Content = panel,
            Placement = PlacementMode.Top,
            VerticalOffset = -4,
            IsHitTestVisible = false
        };
    }

    private void ShowAppRecentContextMenu(AppRecentItem entry, bool isHiddenEntry)
    {
        if (currentAppBookmark is null) return;
        var app = currentAppBookmark;
        string appKey = AppRecentService.ResolveAppTarget(app.Path).AppKey;

        var menu = new ContextMenu();
        menu.Opened += (_, _) => IsContextMenuOpen = true;
        menu.Closed += (_, _) => IsContextMenuOpen = false;

        var openWithApp = new MenuItem
        {
            Tag = "↗",
            Header = T($"用“{app.Name}”打开“{entry.Name}”", $"Open \"{entry.Name}\" in {app.Name}")
        };
        openWithApp.Click += (_, _) =>
        {
            Hide();
            AppRecentLaunchRequested?.Invoke(app, entry);
        };
        menu.Items.Add(openWithApp);

        if (entry.CanBrowseDirectory && !string.IsNullOrWhiteSpace(entry.BrowsableDirectoryPath))
        {
            string browseTarget = entry.BrowsableDirectoryPath;
            var browseInPopup = new MenuItem
            {
                Tag = "▸",
                Header = T("在面板内展开浏览此目录", "Browse directory inside popup")
            };
            browseInPopup.Click += (_, _) => NavigateTo(browseTarget, entry.Name, fromAppRecent: true);
            menu.Items.Add(browseInPopup);
        }

        if (entry.Kind != AppRecentItemKind.Remote && (Directory.Exists(entry.FullPath) || File.Exists(entry.FullPath)))
        {
            var revealItem = new MenuItem
            {
                Tag = "📂",
                Header = T("在资源管理器中显示", "Show in Explorer")
            };
            revealItem.Click += (_, _) =>
            {
                Hide();
                LibraryWindow.RevealInExplorer(entry.FullPath);
            };
            menu.Items.Add(revealItem);
        }

        menu.Items.Add(new Separator());

        var togglePinTop = new MenuItem
        {
            Tag = "📌",
            Header = entry.PinnedByUser
                ? T("取消在此应用最近列表中置顶", "Unpin from top of recent list")
                : T("置顶到此应用最近列表顶部", "Pin to top of recent list")
        };
        togglePinTop.Click += (_, _) =>
        {
            data.UpsertAppRecentRecord(
                appKey,
                entry.FullPath,
                entry.Name,
                entry.Kind,
                entry.LaunchArguments,
                pinnedByUser: !entry.PinnedByUser,
                touchTimestamp: false);
            AppRecentService.InvalidateCache(app.Path);
            HiddenFoldersChanged?.Invoke();
            appRecentEntries = AppRecentService.ListRecentItems(app, data, currentSortMode);
            RenderAppRecentList(SearchBox.Text.Trim());
        };
        menu.Items.Add(togglePinTop);

        var copyNameItem = new MenuItem { Tag = "⎘", Header = T("复制完整名称", "Copy Full Name") };
        copyNameItem.Click += (_, _) =>
        {
            try { Clipboard.SetText(entry.Name); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        menu.Items.Add(copyNameItem);

        var copyPathItem = new MenuItem { Tag = "⌖", Header = T("复制完整路径", "Copy Full Path") };
        copyPathItem.Click += (_, _) =>
        {
            try { Clipboard.SetText(entry.FullPath); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        };
        menu.Items.Add(copyPathItem);

        if (entry.Kind != AppRecentItemKind.Remote)
        {
            var pinToDock = new MenuItem { Tag = "★", Header = T("固定此项目到 Dock", "Pin to Dock") };
            pinToDock.Click += (_, _) => PinFolderRequested?.Invoke(entry.FullPath);
            menu.Items.Add(pinToDock);
        }

        menu.Items.Add(new Separator());

        var toggleHide = new MenuItem
        {
            Tag = isHiddenEntry ? "↺" : "◌",
            Header = isHiddenEntry
                ? T($"恢复显示“{entry.Name}”", $"Restore \"{entry.Name}\"")
                : T($"在此预览中隐藏“{entry.Name}”", $"Hide \"{entry.Name}\" in preview")
        };
        toggleHide.Click += (_, _) =>
        {
            data.SetFolderHidden(entry.FullPath, !isHiddenEntry);
            HiddenFoldersChanged?.Invoke();
            RenderAppRecentList(SearchBox.Text.Trim());
        };
        menu.Items.Add(toggleHide);

        if (entry.PinnedByUser)
        {
            var removeCustom = new MenuItem
            {
                Tag = "✕",
                Header = T("从应用关联列表中移除", "Remove from app recent list")
            };
            removeCustom.Click += (_, _) =>
            {
                data.RemoveAppRecentRecord(appKey, entry.FullPath);
                data.RemoveAppRecentRecord(app.Path, entry.FullPath);
                AppRecentService.InvalidateCache(app.Path);
                HiddenFoldersChanged?.Invoke();
                appRecentEntries = AppRecentService.ListRecentItems(app, data, currentSortMode);
                RenderAppRecentList(SearchBox.Text.Trim());
            };
            menu.Items.Add(removeCustom);
        }

        menu.IsOpen = true;
    }

    private void AddRecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (currentAppBookmark is null) return;
        IsContextMenuOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = T($"选择要关联到“{currentAppBookmark.Name}”的文件夹", $"Select folders to associate with {currentAppBookmark.Name}"),
                Multiselect = true
            };
            if (dlg.ShowDialog(this) == true)
            {
                AddCustomPathsToCurrentApp(dlg.FolderNames);
            }
        }
        finally
        {
            IsContextMenuOpen = false;
        }
    }

    private void AddRecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (currentAppBookmark is null) return;
        IsContextMenuOpen = true;
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T($"选择要关联到“{currentAppBookmark.Name}”的工作区或文件", $"Select workspaces or files for {currentAppBookmark.Name}"),
                Filter = T(
                    "工作区与工程文件 (*.code-workspace;*.sln;*.slnx;*.uvprojx;*.pro;*.CMakeLists.txt;*.*)|*.code-workspace;*.sln;*.slnx;*.uvprojx;*.pro;*.*|所有文件 (*.*)|*.*",
                    "Workspaces & Files (*.code-workspace;*.sln;*.slnx;*.*)|*.code-workspace;*.sln;*.slnx;*.*|All Files (*.*)|*.*"),
                Multiselect = true,
                DereferenceLinks = false
            };
            if (dlg.ShowDialog(this) == true)
            {
                AddCustomPathsToCurrentApp(dlg.FileNames);
            }
        }
        finally
        {
            IsContextMenuOpen = false;
        }
    }

    private void AddCustomPathsToCurrentApp(IEnumerable<string> paths)
    {
        if (currentAppBookmark is null) return;
        string appKey = AppRecentService.ResolveAppTarget(currentAppBookmark.Path).AppKey;
        foreach (string p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            data.UpsertAppRecentRecord(appKey, p, pinnedByUser: true, touchTimestamp: true);
        }
        AppRecentService.InvalidateCache(currentAppBookmark.Path);
        HiddenFoldersChanged?.Invoke();
        appRecentEntries = AppRecentService.ListRecentItems(currentAppBookmark, data, currentSortMode);
        RenderAppRecentList(SearchBox.Text.Trim());
        RepositionPopup();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (currentAppBookmark is not null && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy | DragDropEffects.Link;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.None;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (currentAppBookmark is not null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            if (!isAppRecentView)
                ShowAppRecentRootUi();
            AddCustomPathsToCurrentApp(files);
            e.Handled = true;
        }
    }

    private static Image CreateFileIconImage(ImageSource source) => new()
    {
        Source = source,
        Width = 20,
        Height = 20,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private async Task LoadFileIconAsync(Border host, string path, int version)
    {
        try
        {
            var image = await icons.GetFileIconAsync(path);
            if (version != renderVersion || !IsVisible)
                return;
            host.Child = CreateFileIconImage(image);
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (currentAppBookmark is not null &&
            !isAppRecentView &&
            string.Equals(currentDirectory, appDrillRootDirectory, StringComparison.OrdinalIgnoreCase))
        {
            ShowAppRecentRootUi();
            RepositionPopup();
            return;
        }

        var parent = Directory.GetParent(currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is not null)
            NavigateTo(parent.FullName);
        else if (currentAppBookmark is not null)
            ShowAppRecentRootUi();
    }

    private void OpenCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (isAppRecentView && currentAppBookmark is not null)
        {
            var app = currentAppBookmark;
            Hide();
            OpenRequested?.Invoke(app);
            return;
        }

        if (currentAppBookmark is not null && !string.IsNullOrWhiteSpace(currentDirectory))
        {
            var app = currentAppBookmark;
            string dir = currentDirectory;
            string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = dir;
            Hide();
            AppRecentLaunchRequested?.Invoke(app, new AppRecentItem(
                Name: name,
                FullPath: dir,
                Subtitle: Path.GetDirectoryName(dir) ?? dir,
                Kind: AppRecentItemKind.Folder,
                LastOpenedTime: DateTime.Now));
            return;
        }

        Hide();
        OpenRequested?.Invoke(new Bookmark { Name = TitleText.Text, Path = currentDirectory });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        if (isAppRecentView)
            RenderAppRecentList(q);
        else
            RenderList(q);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsContextMenuOpen) return;
        Hide();
    }
}
