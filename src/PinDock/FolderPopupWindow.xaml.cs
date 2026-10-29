using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PinDock;

public partial class FolderPopupWindow : Window
{
    private const double PopupGap = 12;
    private const double OffscreenX = -32000;

    private readonly ShellIconService icons;
    private LibraryData data = new();
    private string rootDirectory = "";
    private string currentDirectory = "";
    private IReadOnlyList<DirectoryEntryItem> entries = [];
    private ScreenRect anchorRect;
    private ScreenRect workArea;
    private DockEdge dockEdge;
    private bool showingHidden;

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

    public void ShowFolder(string folderPath, string displayName, ScreenRect anchor, ScreenRect monitorWorkArea, DockEdge edge, double opacity)
    {
        if (!Directory.Exists(folderPath)) return;
        rootDirectory = Path.GetFullPath(folderPath);
        anchorRect = anchor;
        workArea = monitorWorkArea;
        dockEdge = edge;
        Opacity = 1.0;
        showingHidden = false;
        UpButton.Content = T("⬅ 上一级", "⬅ Up");
        OpenCurrentButton.Content = T("↗ 打开当前目录", "↗ Open Folder");
        SearchHintText.Text = T("搜索当前目录下的文件夹或文件…", "Search folders or files…");
        FooterTipText.Text = T("单击直接打开 · 悬停或右键可隐藏", "Click to open · Hover or right-click to hide");
        NavigateTo(rootDirectory, displayName);

        if (!IsVisible)
        {
            NativeWindow.MoveTo(this, OffscreenX, 0);
            Show();
        }
        UpdateLayout();
        var size = NativeWindow.GetPixelBounds(this);
        var position = Place(anchorRect, workArea, dockEdge, size.Width, size.Height);
        NativeWindow.MoveTo(this, position.X, position.Y);
    }

    private void NavigateTo(string directoryPath, string? customTitle = null)
    {
        currentDirectory = directoryPath;
        showingHidden = false;
        string folderName = Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        TitleText.Text = !string.IsNullOrWhiteSpace(customTitle)
            ? customTitle
            : (folderName.Length > 0 ? folderName : directoryPath);
        PathText.Text = directoryPath;

        var parent = Directory.GetParent(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        UpButton.Visibility = parent is not null ? Visibility.Visible : Visibility.Collapsed;

        entries = DirectoryBrowserService.ListEntries(directoryPath);
        SearchBox.Text = "";
        RenderList("");
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

    private void RenderList(string query)
    {
        ListPanel.Children.Clear();
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
            ToggleHiddenBtn.Style = (Style)FindResource(showingHidden ? "PrimaryButton" : "SubtleButton");
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
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12,
                Margin = new Thickness(4, 14, 4, 14),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var entry in visibleList)
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

            Button? hoverHideBtn = null;

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
                        Style = (Style)FindResource("SubtleButton"),
                        Content = T("恢复 ↺", "Restore ↺"),
                        ToolTip = T("恢复在此目录预览中显示", "Restore folder visibility"),
                        Padding = new Thickness(8, 4, 8, 4),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("AccentBrush")
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
                    hoverHideBtn = new Button
                    {
                        Style = (Style)FindResource("SubtleButton"),
                        Content = T("隐藏", "Hide"),
                        ToolTip = T("在此预览中隐藏此文件夹", "Hide this folder in preview"),
                        Padding = new Thickness(7, 3.5, 7, 3.5),
                        FontSize = 10.5,
                        Margin = new Thickness(0, 0, 4, 0),
                        Foreground = (Brush)FindResource("MutedBrush"),
                        Visibility = Visibility.Collapsed
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
                        Padding = new Thickness(7, 4, 7, 4),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 4, 0),
                        Background = Brushes.Transparent,
                        Foreground = (Brush)FindResource("MutedBrush")
                    };
                    pinBtn.Click += (_, _) =>
                    {
                        PinFolderRequested?.Invoke(entry.FullPath);
                        pinBtn.Content = "★";
                        pinBtn.Foreground = (Brush)FindResource("AccentBrush");
                    };

                    var drillBtn = new Button
                    {
                        Style = (Style)FindResource("SubtleButton"),
                        Content = T("展开 ▸", "Expand ▸"),
                        ToolTip = T("在面板内查看下一级子目录", "Browse subfolders inside popup"),
                        Padding = new Thickness(8, 4, 8, 4),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("AccentBrush")
                    };
                    drillBtn.Click += (_, _) => NavigateTo(entry.FullPath);

                    actions.Children.Add(hoverHideBtn);
                    actions.Children.Add(pinBtn);
                    actions.Children.Add(drillBtn);
                }
                rowDock.Children.Add(actions);
            }

            rowBorder.MouseEnter += (_, _) =>
            {
                rowBorder.Background = (Brush)FindResource("SelectedBrush");
                if (hoverHideBtn is not null) hoverHideBtn.Visibility = Visibility.Visible;
            };
            rowBorder.MouseLeave += (_, _) =>
            {
                rowBorder.Background = Brushes.Transparent;
                if (hoverHideBtn is not null) hoverHideBtn.Visibility = Visibility.Collapsed;
            };

            var mainBtn = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 7, 6, 7),
                Cursor = Cursors.Hand
            };

            var iconHost = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(7),
                Background = (Brush)FindResource(entry.IsDirectory ? "FolderBgBrush" : "SoftBrush"),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = entry.IsDirectory ? "▰" : "▤",
                    FontSize = 14,
                    Foreground = (Brush)FindResource(entry.IsDirectory ? "FolderFgBrush" : "AccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var nameBlock = new TextBlock
            {
                Text = isHiddenEntry ? $"{entry.Name} ({T("已隐藏", "Hidden")})" : entry.Name,
                FontSize = 12,
                FontWeight = entry.IsDirectory ? FontWeights.Medium : FontWeights.Normal,
                Foreground = (Brush)FindResource("InkBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var innerRow = new DockPanel();
            DockPanel.SetDock(iconHost, Dock.Left);
            innerRow.Children.Add(iconHost);
            innerRow.Children.Add(nameBlock);
            mainBtn.Content = innerRow;

            mainBtn.Click += (_, _) =>
            {
                Hide();
                OpenRequested?.Invoke(new Bookmark { Name = entry.Name, Path = entry.FullPath });
            };

            if (entry.IsDirectory)
            {
                mainBtn.MouseRightButtonUp += (_, e) =>
                {
                    ShowEntryContextMenu(entry, isHiddenEntry);
                    e.Handled = true;
                };
            }
            else
            {
                _ = LoadFileIconAsync(iconHost, entry.FullPath);
            }

            rowDock.Children.Add(mainBtn);
            ListPanel.Children.Add(rowBorder);
        }
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

        menu.IsOpen = true;
    }

    private void ToggleHiddenBtn_Click(object sender, RoutedEventArgs e)
    {
        showingHidden = !showingHidden;
        RenderList(SearchBox.Text.Trim());
    }

    private async Task LoadFileIconAsync(Border host, string path)
    {
        try
        {
            var image = await icons.GetAsync(path);
            host.Child = new Image
            {
                Source = image,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
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
