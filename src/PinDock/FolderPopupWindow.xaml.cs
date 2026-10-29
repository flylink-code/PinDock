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
    private string rootDirectory = "";
    private string currentDirectory = "";
    private IReadOnlyList<DirectoryEntryItem> entries = [];
    private ScreenRect anchorRect;
    private ScreenRect workArea;
    private DockEdge dockEdge;

    public event Action<Bookmark>? OpenRequested;
    public event Action<string>? PinFolderRequested;

    public string CurrentRootPath => rootDirectory;

    public FolderPopupWindow(ShellIconService icons)
    {
        this.icons = icons;
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
    }

    public void ShowFolder(string folderPath, string displayName, ScreenRect anchor, ScreenRect monitorWorkArea, DockEdge edge, double opacity)
    {
        if (!Directory.Exists(folderPath)) return;
        rootDirectory = Path.GetFullPath(folderPath);
        anchorRect = anchor;
        workArea = monitorWorkArea;
        dockEdge = edge;
        Opacity = opacity;
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
        var filtered = string.IsNullOrWhiteSpace(query)
            ? entries
            : entries.Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        int folderCount = filtered.Count(e => e.IsDirectory);
        int fileCount = filtered.Count - folderCount;
        CountText.Text = $"{folderCount} 个目录 · {fileCount} 个文件";

        if (filtered.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(query) ? "此目录为空" : "没有匹配的子目录或文件",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12,
                Margin = new Thickness(4, 14, 4, 14),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var entry in filtered)
        {
            var rowDock = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            if (entry.IsDirectory)
            {
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                DockPanel.SetDock(actions, Dock.Right);

                var pinBtn = new Button
                {
                    Content = "☆",
                    ToolTip = "固定此目录到 Dock",
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
                    Content = "展开 ▸",
                    ToolTip = "在面板内查看下一级子目录",
                    Padding = new Thickness(8, 4, 8, 4),
                    FontSize = 11,
                    Background = (Brush)FindResource("SoftBrush"),
                    Foreground = (Brush)FindResource("AccentBrush")
                };
                drillBtn.Click += (_, _) => NavigateTo(entry.FullPath);

                actions.Children.Add(pinBtn);
                actions.Children.Add(drillBtn);
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
                Text = entry.Name,
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

            if (!entry.IsDirectory)
                _ = LoadFileIconAsync(iconHost, entry.FullPath);

            rowDock.Children.Add(mainBtn);
            ListPanel.Children.Add(rowDock);
        }
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

    private void Window_Deactivated(object? sender, EventArgs e) => Hide();
}
