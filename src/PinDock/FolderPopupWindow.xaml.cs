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
    private LibraryData data = new();
    private string rootDirectory = "";
    private string currentDirectory = "";
    private string defaultFooterTip = "";
    private DirectorySortMode currentSortMode = DirectorySortMode.NameAsc;
    private IReadOnlyList<DirectoryEntryItem> entries = [];
    private ScreenRect anchorRect;
    private ScreenRect workArea;
    private DockEdge dockEdge;
    private bool showingHidden;
    private int renderVersion;

    public event Action<Bookmark>? OpenRequested;
    public event Action<string>? PinFolderRequested;
    public event Action? HiddenFoldersChanged;

    public string CurrentRootPath => rootDirectory;
    public bool IsContextMenuOpen { get; private set; }

    public FolderPopupWindow(ShellIconService icons)
    {
        this.icons = icons;
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
    }

    public void Bind(LibraryData library)
    {
        data = library;
    }

    private string T(string zh, string en) => I18n.T(data.Settings, zh, en);

    public bool IsShowingRoot(string folderPath)
    {
        if (!IsVisible || string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(folderPath))
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

    public void ShowFolder(string folderPath, string displayName, ScreenRect anchor, ScreenRect monitorWorkArea, DockEdge edge, double opacity)
    {
        if (!Directory.Exists(folderPath)) return;
        string fullRoot = Path.GetFullPath(folderPath);
        anchorRect = anchor;
        workArea = monitorWorkArea;
        dockEdge = edge;
        Opacity = 1.0;

        if (IsVisible &&
            string.Equals(rootDirectory, fullRoot, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentDirectory, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            RepositionPopup();
            return;
        }

        rootDirectory = fullRoot;
        currentSortMode = data.GetFolderSortMode(rootDirectory);
        showingHidden = false;
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

    private void NavigateTo(string directoryPath, string? customTitle = null)
    {
        currentDirectory = directoryPath;
        showingHidden = false;
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

        var parent = Directory.GetParent(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        UpButton.Visibility = parent is not null ? Visibility.Visible : Visibility.Collapsed;

        entries = DirectoryBrowserService.ListEntries(directoryPath, currentSortMode);
        if (SearchBox.Text.Length > 0)
            SearchBox.Text = "";
        else
            RenderList("");
    }

    private void UpdateSortButtonLabel()
    {
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

        var modes = new (DirectorySortMode Mode, string Zh, string En)[]
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
                entries = DirectoryBrowserService.ListEntries(currentDirectory, currentSortMode);
                RenderList(SearchBox.Text.Trim());
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
        ResetFooterTip();

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
            Cursor = Cursors.Hand,
            ToolTip = entry.Name
        };
        ToolTipService.SetInitialShowDelay(mainBtn, 160);
        ToolTipService.SetBetweenShowDelay(mainBtn, 80);
        ToolTipService.SetShowDuration(mainBtn, 15000);
        ToolTipService.SetPlacement(mainBtn, PlacementMode.Top);
        ToolTipService.SetVerticalOffset(mainBtn, -4);
        mainBtn.ToolTipOpening += (_, _) =>
        {
            if (mainBtn.ToolTip is string)
                mainBtn.ToolTip = BuildEntryToolTip(entry, isHiddenEntry);
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

        rowBorder.MouseEnter += (_, _) =>
        {
            rowBorder.Background = res.SelectedBrush;
            if (hoverActionsPanel is not null)
            {
                hoverActionsPanel.Visibility = Visibility.Visible;
                if (timeBlock is not null) timeBlock.Visibility = Visibility.Collapsed;
            }
            SetFooterPreviewName(entry.Name);
        };
        rowBorder.MouseLeave += (_, _) =>
        {
            rowBorder.Background = Brushes.Transparent;
            if (hoverActionsPanel is not null)
            {
                hoverActionsPanel.Visibility = Visibility.Collapsed;
                if (timeBlock is not null) timeBlock.Visibility = Visibility.Visible;
            }
            ResetFooterTip();
        };

        mainBtn.Click += (_, _) =>
        {
            Hide();
            OpenRequested?.Invoke(new Bookmark { Name = entry.Name, Path = entry.FullPath });
        };

        mainBtn.MouseRightButtonUp += (_, e) =>
        {
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
        var panel = new StackPanel { MaxWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
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
        RenderList(SearchBox.Text.Trim());
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
        var parent = Directory.GetParent(currentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent is not null)
            NavigateTo(parent.FullName);
    }

    private void OpenCurrent_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenRequested?.Invoke(new Bookmark { Name = TitleText.Text, Path = currentDirectory });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderList(SearchBox.Text.Trim());

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
