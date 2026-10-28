using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PinDock;

public partial class DockWindow : Window
{
    private const int FadeMilliseconds = 150;

    private readonly ShellIconService icons;
    private readonly GroupPopupWindow groupPopup;
    private LibraryData data = new();
    private DockPlacement placement;
    private ScreenRect workArea;
    private List<Bookmark> overflow = [];
    private ScreenRect? dragStartCursor;
    private double dragOriginOffset;
    private int fadeVersion;

    public event Action<Bookmark>? LaunchRequested;
    public event Action? LibraryRequested;
    public event Action? OverflowRequested;
    public event Action? AddRequested;
    public event Action? SettingsRequested;
    public event Action? DragEnded;

    public bool IsDragging { get; private set; }
    public bool IsShown { get; private set; }
    public bool IsGroupOpen => groupPopup.IsVisible;
    public ScreenRect CurrentBounds => placement.Bounds;
    public ScreenRect EdgeHandleRect => placement.EdgeHandle;
    public ScreenRect HitArea => placement.Bounds.Union(placement.EdgeHandle);
    public ScreenRect? GroupBounds => groupPopup.IsVisible ? NativeWindow.GetPixelBounds(groupPopup) : null;
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
    }

    public void Bind(LibraryData library)
    {
        data = library;
        Topmost = library.Settings.Topmost;
        if (IsShown) BeginAnimation(OpacityProperty, null);
        if (IsShown || !IsVisible) Opacity = library.Settings.Opacity;
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
        int version = ++fadeVersion;
        IsShown = true;
        double target = data.Settings.Opacity;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        var fade = new DoubleAnimation(target, TimeSpan.FromMilliseconds(FadeMilliseconds));
        fade.Completed += (_, _) =>
        {
            if (version != fadeVersion) return;
            BeginAnimation(OpacityProperty, null);
            Opacity = target;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void HideDock()
    {
        int version = ++fadeVersion;
        IsShown = false;
        groupPopup.Hide();
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeMilliseconds));
        fade.Completed += (_, _) =>
        {
            if (version != fadeVersion) return;
            BeginAnimation(OpacityProperty, null);
            Hide();
            Opacity = data.Settings.Opacity;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void CloseGroup() => groupPopup.Hide();

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
        ItemsPanel.Orientation = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right ? Orientation.Vertical : Orientation.Horizontal;
        double icon = DockLayout.IconPixels(data.Settings.IconSize);

        var pinnedButtons = pinned.Select(item => (FrameworkElement)CreateBookmarkButton(item, icon)).ToList();
        var groupButtons = data.Groups
            .Where(group => data.Items.Any(i => string.Equals(i.Group, group, StringComparison.Ordinal)))
            .Select(group => (FrameworkElement)CreateGroupButton(group, icon))
            .ToList();
        var utilityButtons = new List<FrameworkElement>();
        if (overflow.Count > 0)
            utilityButtons.Add(CreateUtilityButton($"…{overflow.Count}", "更多固定收藏", icon, () => OverflowRequested?.Invoke()));
        utilityButtons.Add(CreateUtilityButton("▦", "全部收藏", icon, () => LibraryRequested?.Invoke()));
        utilityButtons.Add(CreateUtilityButton("＋", "添加收藏", icon, () => AddRequested?.Invoke()));
        utilityButtons.Add(CreateUtilityButton("⚙", "设置", icon, () => SettingsRequested?.Invoke()));

        foreach (var section in new[] { pinnedButtons, groupButtons, utilityButtons })
        {
            if (section.Count == 0) continue;
            if (ItemsPanel.Children.Count > 0) ItemsPanel.Children.Add(CreateSeparator(icon));
            foreach (var element in section) ItemsPanel.Children.Add(element);
        }
        ((Button)ItemsPanel.Children[^1]).Margin = new Thickness(0);
    }

    private Button CreateBookmarkButton(Bookmark item, double icon)
    {
        var button = CreateChrome(icon, item.Name);
        var image = new Image { Width = icon * 0.65, Height = icon * 0.65, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        ((Border)button.Content).Child = image;
        button.Click += (_, _) => LaunchRequested?.Invoke(item);
        _ = LoadIcon(image, item.Path);
        return button;
    }

    private Button CreateGroupButton(string group, double icon)
    {
        var button = CreateChrome(icon, group);
        var grid = new Grid { Width = icon * 0.45, Height = icon * 0.45, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 4; i++)
        {
            var cell = new Border { Background = (Brush)FindResource("AccentBrush"), CornerRadius = new CornerRadius(2), Margin = new Thickness(1.5) };
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
        ((Border)button.Content).Child = grid;
        button.Click += (_, _) =>
        {
            var members = data.Items.Where(i => string.Equals(i.Group, group, StringComparison.Ordinal));
            groupPopup.ShowGroup(group, members, placement.Bounds, workArea, data.Settings.DockEdge, data.Settings.Opacity);
        };
        return button;
    }

    private Button CreateUtilityButton(string glyph, string tip, double icon, Action action)
    {
        var button = CreateChrome(icon, tip);
        ((Border)button.Content).Child = new TextBlock
        {
            Text = glyph,
            FontSize = icon * 0.4,
            Foreground = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        button.Click += (_, _) => action();
        return button;
    }

    private FrameworkElement CreateSeparator(double icon)
    {
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        return new Rectangle
        {
            Width = vertical ? icon * 0.65 : 1,
            Height = vertical ? 1 : icon * 0.65,
            Fill = new SolidColorBrush(Color.FromArgb(0x60, 0x99, 0xAD, 0x92)),
            Margin = vertical ? new Thickness(0, 0, 0, 9) : new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private Button CreateChrome(double icon, string tip)
    {
        var border = new Border
        {
            Width = icon,
            Height = icon,
            CornerRadius = new CornerRadius(11),
            Background = (Brush)FindResource("SoftBrush"),
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        bool vertical = data.Settings.DockEdge is DockEdge.Left or DockEdge.Right;
        var button = new Button
        {
            Content = border,
            Style = (Style)FindResource("DockButton"),
            Margin = vertical ? new Thickness(0, 0, 0, 9) : new Thickness(0, 0, 9, 0),
            ToolTip = tip
        };
        button.MouseEnter += (_, _) => border.RenderTransform = HoverTransform(data.Settings.DockEdge);
        button.MouseLeave += (_, _) => border.RenderTransform = null;
        AttachDrag(button);
        return button;
    }

    private static Transform HoverTransform(DockEdge edge)
    {
        var scale = new ScaleTransform(1.07, 1.07);
        switch (edge)
        {
            case DockEdge.Bottom: return new TransformGroup { Children = { scale, new TranslateTransform(0, -5) } };
            case DockEdge.Top: return new TransformGroup { Children = { scale, new TranslateTransform(0, 5) } };
            case DockEdge.Left:
            case DockEdge.Right: return scale;
            default: throw new ArgumentOutOfRangeException(nameof(edge), edge, null);
        }
    }

    private async Task LoadIcon(Image image, string path)
    {
        try { image.Source = await icons.GetAsync(path); }
        catch (System.ComponentModel.Win32Exception) { }
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
