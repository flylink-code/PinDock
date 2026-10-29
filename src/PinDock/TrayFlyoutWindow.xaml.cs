using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PinDock;

public partial class TrayFlyoutWindow : Window
{
    private readonly AppController controller;
    private readonly ShellIconService icons;
    private string activeTab = "pinned";
    private long lastDeactivatedTick;
    private Bookmark? topMatchBookmark;

    public TrayFlyoutWindow(AppController controller, ShellIconService icons)
    {
        this.controller = controller;
        this.icons = icons;
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
        VersionBadgeText.Text = "v" + UpdateService.GetCurrentVersion();
    }

    public void ToggleFlyout()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        if (Environment.TickCount64 - lastDeactivatedTick < 220)
            return;

        ShowFlyout();
    }

    public void ShowFlyout()
    {
        SearchBox.Text = "";
        SearchPlaceholder.Visibility = Visibility.Visible;
        RefreshContent();
        NativeWindow.MoveTo(this, -32000, 0);
        Show();
        UpdateLayout();
        PositionNearTray();
        Activate();
        SearchBox.Focus();
    }

    public void RefreshIfVisible()
    {
        if (IsVisible)
            RefreshContent();
    }

    private void PositionNearTray()
    {
        var allMonitors = MonitorQuery.All();
        var primary = allMonitors.FirstOrDefault(m => m.IsPrimary) ?? allMonitors[0];
        ScreenRect workArea = primary.WorkingArea;
        double cursorX = workArea.X + workArea.Width - 60;
        double cursorY = workArea.Y + workArea.Height - 20;

        if (AutoHideController.TryGetCursorPoint(out var pt))
        {
            cursorX = pt.X;
            cursorY = pt.Y;
            var mon = allMonitors.FirstOrDefault(m => m.Bounds.Contains(pt.X, pt.Y));
            if (mon is not null)
                workArea = mon.WorkingArea;
        }

        var actualSize = NativeWindow.GetPixelBounds(this);
        double pixelW = actualSize.Width > 0 ? actualSize.Width : Width * VisualTreeHelper.GetDpi(this).DpiScaleX;
        double pixelH = actualSize.Height > 0 ? actualSize.Height : Height * VisualTreeHelper.GetDpi(this).DpiScaleY;

        double x = Math.Clamp(cursorX - pixelW / 2, workArea.X + 8, Math.Max(workArea.X + 8, workArea.X + workArea.Width - pixelW - 8));
        double y = cursorY > workArea.Y + workArea.Height / 2
            ? Math.Max(workArea.Y + 8, workArea.Y + workArea.Height - pixelH - 8)
            : Math.Min(workArea.Y + workArea.Height - pixelH - 8, workArea.Y + 8);

        NativeWindow.MoveTo(this, x, y);
    }

    private string T(string zh, string en) => I18n.T(controller.Data.Settings, zh, en);

    private void RefreshContent()
    {
        var s = controller.Data.Settings;
        bool isDark = s.ThemeMode == PinDock.ThemeMode.Dark || (s.ThemeMode == PinDock.ThemeMode.System && ThemeService.IsSystemDark());
        FlyoutTitleText.Text = T("PinDock 快捷中心", "PinDock Hub");
        SearchPlaceholder.Text = T("搜索收藏、文件夹或本机应用 (回车直接打开)…", "Search bookmarks, folders or apps (Enter to open)…");
        RecentHeaderText.Text = T("最近使用", "Recent");
        TxtQuickTheme.Text = isDark ? "☀" : "🌙";
        BtnQuickTheme.ToolTip = T(isDark ? "切换为浅色主题" : "切换为深色主题", isDark ? "Switch to Light theme" : "Switch to Dark theme");
        TxtQuickLang.Text = s.IsEnglish ? "中" : "EN";
        BtnQuickLang.ToolTip = T("Switch to English", "切换为简体中文");

        TxtFooterAdd.Text = T("＋ 添加", "＋ Add");
        TxtFooterLib.Text = T("▦ 收藏库", "▦ Library");
        TxtFooterSettings.Text = T("⚙ 设置", "⚙ Settings");
        TxtFooterUpdate.Text = T("↑ 更新", "↑ Update");
        TxtFooterExit.Text = T("⏻ 退出", "⏻ Exit");

        HotkeyHintText.Text = s.HasHotkey
            ? HotkeyService.FormatDisplay(s.HotkeyModifiers, s.HotkeyKey)
            : T("无热键", "No Hotkey");
        TxtToggleDock.Text = controller.IsDockVisible
            ? T("Dock: 显示中", "Dock: Shown")
            : T("Dock: 已隐藏", "Dock: Hidden");
        TxtCycleMode.Text = s.DisplayMode switch
        {
            DisplayMode.Persistent => T("模式: 常驻", "Mode: Always"),
            DisplayMode.EdgeAutoHide => T("模式: 贴边", "Mode: Edge"),
            DisplayMode.Hotkey => T("模式: 热键", "Mode: Hotkey"),
            _ => T("模式: 常驻", "Mode: Always")
        };
        TxtToggleLock.Text = s.LockPosition
            ? T("位置: 已锁定", "Pos: Locked")
            : T("位置: 可拖动", "Pos: Free");

        RenderRecentSection();
        RenderTabs();
        RenderList();
    }

    private void RenderRecentSection()
    {
        string q = SearchBox.Text.Trim();
        var recents = controller.Data.Items
            .Where(i => i.LastLaunchedTicks > 0)
            .OrderByDescending(i => i.LastLaunchedTicks)
            .Take(4)
            .ToList();

        if (!string.IsNullOrEmpty(q) || recents.Count == 0)
        {
            RecentSection.Visibility = Visibility.Collapsed;
            return;
        }

        RecentSection.Visibility = Visibility.Visible;
        RecentPanel.Children.Clear();
        foreach (var item in recents)
        {
            var btn = new Button
            {
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 5, 4),
                ToolTip = item.Path,
                Content = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 11,
                    MaxWidth = 96,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            btn.Click += (_, _) =>
            {
                Hide();
                _ = controller.LaunchAsync(item);
            };
            RecentPanel.Children.Add(btn);
        }
    }

    private void RenderTabs()
    {
        TabPanel.Children.Clear();
        var tabs = new List<(string Key, string Title)>
        {
            ("pinned", T($"固定项 ({controller.Data.Items.Count(i => i.Pinned)})", $"Pinned ({controller.Data.Items.Count(i => i.Pinned)})")),
            ("all", T($"全部 ({controller.Data.Items.Count})", $"All ({controller.Data.Items.Count})"))
        };
        foreach (string g in controller.Data.Groups)
        {
            int count = controller.Data.Items.Count(i => string.Equals(i.Group, g, StringComparison.Ordinal));
            if (count > 0)
                tabs.Add(("group:" + g, $"{g} ({count})"));
        }

        if (tabs.All(t => t.Key != activeTab))
            activeTab = "pinned";

        foreach (var (key, title) in tabs)
        {
            bool selected = key == activeTab;
            var btn = new Button
            {
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 5, 0),
                Content = new TextBlock { Text = title, FontSize = 11 }
            };
            if (selected)
                btn.Style = (Style)FindResource("PrimaryButton");
            btn.Click += (_, _) =>
            {
                activeTab = key;
                RenderTabs();
                RenderList();
            };
            TabPanel.Children.Add(btn);
        }
    }

    private void RenderList()
    {
        ItemsListPanel.Children.Clear();
        topMatchBookmark = null;
        string q = SearchBox.Text.Trim();

        var results = new List<(Bookmark Item, string Badge)>();
        IEnumerable<Bookmark> source = controller.Data.Items;

        if (string.IsNullOrEmpty(q))
        {
            if (activeTab == "pinned")
                source = source.Where(i => i.Pinned);
            else if (activeTab.StartsWith("group:", StringComparison.Ordinal))
            {
                string grp = activeTab[6..];
                source = source.Where(i => string.Equals(i.Group, grp, StringComparison.Ordinal));
            }
        }
        else
        {
            source = source.Where(i => (i.Name + " " + i.Path + " " + i.Group).Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var bm in source)
        {
            string badge = string.IsNullOrEmpty(bm.Group)
                ? BookmarkKindHelper.ToDisplayName(BookmarkKindHelper.FromPath(bm.Path))
                : bm.Group;
            results.Add((bm, badge));
        }

        if (!string.IsNullOrEmpty(q))
        {
            var existingPaths = new HashSet<string>(controller.Data.Items.Select(i => i.Path.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var app in controller.InstalledApps.GetInstalledApps())
            {
                if (existingPaths.Contains(app.Path.Trim())) continue;
                if ((app.Name + " " + app.Path).Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add((new Bookmark { Name = app.Name, Path = app.Path }, "本机应用"));
                    if (results.Count >= 24) break;
                }
            }
        }

        if (results.Count == 0)
        {
            ItemsListPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(q)
                    ? T("当前分类暂无收藏项（可直接拖入文件或文件夹）", "No items in this view (Drag & drop files/folders here)")
                    : T("没有找到匹配的收藏或本机应用", "No matching bookmarks or installed apps"),
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 36, 0, 0)
            });
            return;
        }

        topMatchBookmark = results[0].Item;
        foreach (var (bm, badge) in results)
        {
            ItemsListPanel.Children.Add(CreateRowButton(bm, badge));
        }
    }

    private Button CreateRowButton(Bookmark item, string badge)
    {
        var button = new Button
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
            Background = (Brush)FindResource("SoftBrush"),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = item.Name.Length > 0 ? item.Name[..1] : "◇",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("AccentBrush")
            }
        };

        var badgeBlock = new Border
        {
            Background = (Brush)FindResource("SoftBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 2, 5, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Child = new TextBlock
            {
                Text = badge,
                FontSize = 10,
                Foreground = (Brush)FindResource("MutedBrush")
            }
        };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)FindResource("InkBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        texts.Children.Add(new TextBlock
        {
            Text = item.Path,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)FindResource("MutedBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var row = new DockPanel();
        DockPanel.SetDock(iconHost, Dock.Left);
        DockPanel.SetDock(badgeBlock, Dock.Right);
        row.Children.Add(iconHost);
        row.Children.Add(badgeBlock);
        row.Children.Add(texts);

        button.Content = row;
        button.Click += (_, _) =>
        {
            Hide();
            _ = controller.LaunchAsync(item);
        };
        if (item.IconKind == BookmarkIconKind.Preset && !string.IsNullOrWhiteSpace(item.IconValue) &&
            GroupIconPresets.FindPathData(item.IconValue) is string pathData && !string.IsNullOrWhiteSpace(pathData))
        {
            iconHost.Child = new Viewbox
            {
                Width = 18,
                Height = 18,
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(pathData),
                    Fill = (Brush)FindResource("AccentBrush"),
                    Stretch = Stretch.Uniform
                }
            };
        }
        else
        {
            string targetIconPath = item.IconKind == BookmarkIconKind.CustomFile && !string.IsNullOrWhiteSpace(item.IconValue)
                ? item.IconValue
                : item.Path;
            _ = LoadMiniIconAsync(iconHost, targetIconPath);
        }
        return button;
    }

    private async Task LoadMiniIconAsync(Border host, string path)
    {
        try
        {
            var img = await icons.GetAsync(path);
            host.Child = new Image
            {
                Source = img,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RenderRecentSection();
        RenderList();
    }

    private void BtnQuickTheme_Click(object sender, RoutedEventArgs e)
    {
        controller.QuickToggleLightDarkTheme();
        RefreshContent();
    }

    private void BtnQuickLang_Click(object sender, RoutedEventArgs e)
    {
        controller.ToggleLanguage();
        RefreshContent();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            controller.AddPaths(files);
            RefreshContent();
        }
    }

    private void BtnToggleDock_Click(object sender, RoutedEventArgs e)
    {
        controller.ToggleDockFromFlyout();
        RefreshContent();
    }

    private void BtnCycleMode_Click(object sender, RoutedEventArgs e)
    {
        var s = controller.Data.Settings;
        s.DisplayMode = s.DisplayMode switch
        {
            DisplayMode.Persistent => DisplayMode.EdgeAutoHide,
            DisplayMode.EdgeAutoHide => DisplayMode.Hotkey,
            _ => DisplayMode.Persistent
        };
        controller.ApplySettings();
        RefreshContent();
    }

    private void BtnToggleLock_Click(object sender, RoutedEventArgs e)
    {
        controller.Data.Settings.LockPosition = !controller.Data.Settings.LockPosition;
        controller.ApplySettings();
        RefreshContent();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        controller.OpenEditorForNew();
    }

    private void BtnLibrary_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        controller.OpenLibraryPanel();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        controller.OpenSettingsPanel();
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        controller.OpenSettingsPanel("about");
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        controller.RequestExitApplication();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        lastDeactivatedTick = Environment.TickCount64;
        Hide();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = "";
            }
            else
            {
                Hide();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && topMatchBookmark is not null)
        {
            var target = topMatchBookmark;
            Hide();
            _ = controller.LaunchAsync(target);
            e.Handled = true;
        }
    }
}