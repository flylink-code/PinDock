using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PinDock;

public partial class LibraryWindow : Window
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

    public void ShowLibrary(bool moreOverflow = false)
    {
        overflowOnly = moreOverflow;
        TitleText.Text = moreOverflow ? "更多固定收藏" : "全部收藏";
        filter = "all";
        SearchBox.Text = "";
        search = "";
        Refresh();
        Show();
        Activate();
    }

    public void Refresh()
    {
        IEnumerable<Bookmark> pool = controller.Data.Items;
        if (overflowOnly) pool = controller.OverflowItems;
        if (filter != "all")
            pool = pool.Where(i => BookmarkKindHelper.ToDisplayName(BookmarkKindHelper.FromPath(i.Path)) == filter);
        if (!string.IsNullOrWhiteSpace(search))
            pool = pool.Where(i => (i.Name + " " + i.Path + " " + i.Group).Contains(search, StringComparison.OrdinalIgnoreCase));
        var rows = pool.Select(item => new BookmarkRow(item, icons)).ToList();
        ItemsList.ItemsSource = rows;
        CountText.Text = $"显示 {rows.Count} 项 · 共 {controller.Data.Items.Count} 项收藏 · {controller.Data.Items.Count(i => i.Pinned)} 项固定";
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        search = SearchBox.Text.Trim();
        Refresh();
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            filter = tag;
            Refresh();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BookmarkEditor(new Bookmark { Pinned = true }, controller.Data.Groups) { Owner = this, Title = "添加收藏" };
        if (editor.ShowDialog() == true) controller.SaveEdited(editor.Result, null);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow row) return;
        var editor = new BookmarkEditor(row.Bookmark, controller.Data.Groups) { Owner = this };
        if (editor.ShowDialog() == true) controller.SaveEdited(editor.Result, row.Bookmark);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not BookmarkRow row) return;
        await controller.LaunchAsync(row.Bookmark);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow row) return;
        if (MessageBox.Show(this, $"移除收藏“{row.Bookmark.Name}”？不会删除目标。", "PinDock", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        controller.Remove(row.Bookmark);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookmarkRow row) return;
        controller.TogglePin(row.Bookmark);
        row.NotifyPinChanged();
        Refresh();
    }

    private void ManageGroups_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PromptDialog("管理分组", "输入新分组名称：") { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result.Length == 0) return;
        controller.AddGroup(dialog.Result);
        Refresh();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PinDock 收藏|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var (added, skipped) = controller.Import(dialog.FileName);
            MessageBox.Show(this, $"导入完成：新增 {added} 项，跳过重复 {skipped} 项。");
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or UnsupportedLibraryVersionException)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}");
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PinDock 收藏|*.json", FileName = "pindock-library.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            controller.Export(dialog.FileName);
            MessageBox.Show(this, "导出完成。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}");
        }
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
