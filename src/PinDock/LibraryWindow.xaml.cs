using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PinDock;

public partial class LibraryWindow : CardWindowBase
{
    private readonly AppController controller;
    private readonly ShellIconService icons;
    private string filter = "all";
    private string? search;
    private bool overflowOnly;

    public LibraryWindow(AppController controller, ShellIconService icons)
    {
        this.controller = controller;
        this.icons = icons;
        InitializeComponent();
    }

    public void ShowLibrary(bool moreOverflow = false, bool focusInstalledApps = false)
    {
        overflowOnly = moreOverflow;
        Title = moreOverflow ? "更多固定收藏" : "全部收藏";
        SearchBox.Text = "";
        search = "";
        if (focusInstalledApps)
        {
            filter = "installed";
            FilterInstalled.IsChecked = true;
        }
        else
        {
            filter = "all";
            FilterAll.IsChecked = true;
        }
        Refresh();
        Show();
        Activate();
        SearchBox.Focus();
    }

    public void Refresh()
    {
        var rows = new List<BookmarkRow>();
        string query = search ?? "";

        if (filter != "installed")
        {
            IEnumerable<Bookmark> pool = controller.Data.Items;
            if (overflowOnly) pool = controller.OverflowItems;
            if (filter != "all")
                pool = pool.Where(i => BookmarkKindHelper.ToDisplayName(BookmarkKindHelper.FromPath(i.Path)) == filter);
            if (!string.IsNullOrWhiteSpace(query))
                pool = pool.Where(i => (i.Name + " " + i.Path + " " + i.Group).Contains(query, StringComparison.OrdinalIgnoreCase));

            rows.AddRange(pool.Select(item => new BookmarkRow(item, icons)));
        }

        // 在“本机应用”标签下，或在“全部/应用”中主动搜索关键字时，检索本机已安装应用并展示未收藏项
        if (!overflowOnly && (filter == "installed" || (!string.IsNullOrWhiteSpace(query) && filter is "all" or "应用")))
        {
            var installed = controller.InstalledApps.GetInstalledApps();
            foreach (var app in installed)
            {
                if (!string.IsNullOrWhiteSpace(query) &&
                    !(app.Name + " " + app.Path + " " + app.Category).Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (controller.Data.Items.Any(existing =>
                        string.Equals(existing.Path.Trim(), app.Path.Trim(), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.Name.Trim(), app.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var candidate = new Bookmark { Name = app.Name, Path = app.Path, Pinned = false };
                rows.Add(new BookmarkRow(candidate, icons, isInstalledCandidate: true, candidateCategory: app.Category));
            }
        }

        ItemsList.ItemsSource = rows;
        EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = !string.IsNullOrWhiteSpace(query)
            ? "没有匹配的收藏或本机应用"
            : "还没有收藏项，可点击上方「＋ 添加文件夹」或切换到「本机应用」一键固定常用软件";
        CountText.Text = $"显示 {rows.Count} 项 · 共 {controller.Data.Items.Count} 项收藏 · {controller.Data.Items.Count(i => i.Pinned)} 项固定";
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        search = SearchBox.Text.Trim();
        Refresh();
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        filter = tag;
        Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BookmarkEditor(new Bookmark { Pinned = true }, controller.Data.Groups, controller.InstalledApps.GetInstalledApps())
        {
            Owner = this,
            Title = "添加收藏"
        };
        if (editor.ShowDialog() == true)
        {
            controller.SaveEdited(editor.Result, null);
            Refresh();
        }
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要添加到 PinDock 的文件夹",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        controller.AddPaths(dialog.FolderNames);
        Refresh();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow { IsInstalledCandidate: false } row) return;
        var editor = new BookmarkEditor(row.Bookmark, controller.Data.Groups, controller.InstalledApps.GetInstalledApps()) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            controller.SaveEdited(editor.Result, row.Bookmark);
            Refresh();
        }
    }

    private async void RowOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow row) return;
        await controller.LaunchAsync(row.Bookmark);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not BookmarkRow row) return;
        await controller.LaunchAsync(row.Bookmark);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow { IsInstalledCandidate: false } row) return;
        if (MessageBox.Show(this, $"移除收藏“{row.Bookmark.Name}”？不会删除目标文件。", "PinDock", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        controller.Remove(row.Bookmark);
        Refresh();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow row) return;
        if (row.IsInstalledCandidate)
        {
            row.Bookmark.Pinned = true;
            row.IsInstalledCandidate = false;
            controller.SaveEdited(row.Bookmark, null);
            Refresh();
            return;
        }
        controller.TogglePin(row.Bookmark);
        row.NotifyPinChanged();
        Refresh();
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow row) return;
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "打开" };
        openItem.Click += async (_, _) => await controller.LaunchAsync(row.Bookmark);
        menu.Items.Add(openItem);

        var pinItem = new MenuItem { Header = row.PinLabel };
        pinItem.Click += (_, _) => Pin_Click(sender, e);
        menu.Items.Add(pinItem);

        var revealItem = new MenuItem { Header = "在资源管理器中显示" };
        revealItem.Click += (_, _) => RevealInExplorer(row.Bookmark.Path);
        menu.Items.Add(revealItem);

        if (!row.IsInstalledCandidate)
        {
            menu.Items.Add(new Separator());
            var editItem = new MenuItem { Header = "编辑收藏…" };
            editItem.Click += (_, _) => Edit_Click(sender, e);
            menu.Items.Add(editItem);

            var removeItem = new MenuItem { Header = "从收藏中移除" };
            removeItem.Click += (_, _) => Remove_Click(sender, e);
            menu.Items.Add(removeItem);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    public static void RevealInExplorer(string targetPath)
    {
        if (Directory.Exists(targetPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{targetPath}\"") { UseShellExecute = true });
        }
        else if (File.Exists(targetPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{targetPath}\"") { UseShellExecute = true });
        }
    }

    private void ManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PromptDialog("管理分组", "输入新分组名称：") { Owner = this, UseLargeHeader = true, Tag = "收藏组用于归类项目与工具" };
        if (dialog.ShowDialog() != true || dialog.Result.Length == 0) return;
        controller.AddGroup(dialog.Result);
        Refresh();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            controller.AddPaths(files);
            Refresh();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
