using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PinDock;

public partial class GroupPopupWindow : Window
{
    private const double PopupGap = 16;
    private const double OffscreenX = -32000;

    private readonly ShellIconService icons;
    private List<Bookmark> items = [];

    public event Action<Bookmark>? LaunchRequested;

    public GroupPopupWindow(ShellIconService icons)
    {
        this.icons = icons;
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
    }

    public void ShowGroup(string groupName, IEnumerable<Bookmark> bookmarks, ScreenRect dock, ScreenRect workArea, DockEdge edge, double opacity)
    {
        items = bookmarks.ToList();
        TitleText.Text = groupName;
        CountText.Text = $"{items.Count} 个收藏";
        SearchBox.Text = "";
        Opacity = opacity;
        RenderList("");

        NativeWindow.MoveTo(this, OffscreenX, 0);
        Show();
        UpdateLayout();
        var size = NativeWindow.GetPixelBounds(this);
        var position = Place(dock, workArea, edge, size.Width, size.Height);
        NativeWindow.MoveTo(this, position.X, position.Y);
        Activate();
        SearchBox.Focus();
    }

    private static Point Place(ScreenRect dock, ScreenRect workArea, DockEdge edge, double width, double height)
    {
        double x, y;
        switch (edge)
        {
            case DockEdge.Bottom:
                x = dock.X + dock.Width / 2 - width / 2;
                y = dock.Y - PopupGap - height;
                break;
            case DockEdge.Top:
                x = dock.X + dock.Width / 2 - width / 2;
                y = dock.Y + dock.Height + PopupGap;
                break;
            case DockEdge.Left:
                x = dock.X + dock.Width + PopupGap;
                y = dock.Y + dock.Height / 2 - height / 2;
                break;
            case DockEdge.Right:
                x = dock.X - PopupGap - width;
                y = dock.Y + dock.Height / 2 - height / 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(edge), edge, null);
        }
        x = Math.Clamp(x, workArea.X, Math.Max(workArea.X, workArea.X + workArea.Width - width));
        y = Math.Clamp(y, workArea.Y, Math.Max(workArea.Y, workArea.Y + workArea.Height - height));
        return new Point(x, y);
    }

    private void RenderList(string query)
    {
        ListPanel.Children.Clear();
        var filtered = items.Where(i => (i.Name + " " + i.Path).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filtered.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = "没有匹配的收藏",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0)
            });
            return;
        }
        foreach (var item in filtered)
        {
            var button = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(5, 10, 5, 10),
                Cursor = Cursors.Hand,
                Tag = item
            };
            var iconHost = new Border
            {
                Width = 35,
                Height = 35,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("SoftBrush"),
                Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock
                {
                    Text = item.Name.Length > 0 ? item.Name[..1] : "?",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Georgia"),
                    FontSize = 17,
                    Foreground = (Brush)FindResource("AccentBrush")
                }
            };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = item.Name, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = (Brush)FindResource("InkBrush") });
            texts.Children.Add(new TextBlock { Text = item.Path, FontSize = 10, Margin = new Thickness(0, 4, 0, 0), Foreground = (Brush)FindResource("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new DockPanel();
            DockPanel.SetDock(iconHost, Dock.Left);
            row.Children.Add(iconHost);
            row.Children.Add(texts);
            button.Content = row;
            button.Click += (_, _) => LaunchRequested?.Invoke(item);
            ListPanel.Children.Add(button);
            _ = LoadMiniIcon(iconHost, item.Path);
        }
    }

    private async Task LoadMiniIcon(Border host, string path)
    {
        try
        {
            var image = await icons.GetAsync(path);
            host.Child = new Image { Source = image, Width = 24, Height = 24, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        catch (System.ComponentModel.Win32Exception) { }
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
