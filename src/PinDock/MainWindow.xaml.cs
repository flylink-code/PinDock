using System.IO;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;

namespace PinDock;

public partial class MainWindow : Window
{
    private readonly LibraryStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinDock"));
    private readonly LibraryData data;
    private readonly ShellIconService icons = new();

    public MainWindow()
    {
        InitializeComponent();
        try { data = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UnsupportedLibraryVersionException)
        { MessageBox.Show($"无法读取收藏，未覆盖配置：{ex.Message}"); throw; }
        Refresh();
    }

    private void Refresh() { ItemsList.ItemsSource = data.Items.Select(item => new BookmarkRow(item, icons)).ToList(); }
    private void Add(IEnumerable<string> paths)
    {
        var items = new List<Bookmark>(data.Items);
        int duplicates = 0;
        foreach (string path in paths)
        {
            var item = new Bookmark { Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Path = path, Pinned = true };
            if (string.IsNullOrWhiteSpace(item.Name)) item.Name = path;
            if (!LibraryStore.IsDuplicate(items, item)) items.Add(item);
            else duplicates++;
        }
        if (items.Count != data.Items.Count) Save(items);
        if (duplicates > 0) MessageBox.Show($"{duplicates} 项已收藏，未重复添加。");
    }
    private void Save(List<Bookmark> items)
    {
        try { store.Save(new LibraryData { Items = items }); data.Items = items; Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show($"保存失败，收藏未更改：{ex.Message}"); }
    }
    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) Add(dialog.FileNames);
    }
    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) Add([dialog.FolderName]);
    }
    private void AddPath_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BookmarkEditor(new Bookmark()) { Owner = this, Title = "添加路径" };
        if (editor.ShowDialog() == true) SaveEdited(editor.Result, null);
    }
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not BookmarkRow row) return;
        var item = row.Bookmark;
        var editor = new BookmarkEditor(item) { Owner = this };
        if (editor.ShowDialog() == true) SaveEdited(editor.Result, item);
    }
    private void SaveEdited(Bookmark result, Bookmark? original)
    {
        var items = new List<Bookmark>(data.Items);
        if (original is not null) items.Remove(original);
        if (LibraryStore.IsDuplicate(items, result)) { MessageBox.Show("该目标及启动配置已收藏。"); return; }
        int index = original is null ? items.Count : data.Items.IndexOf(original);
        items.Insert(index, result);
        Save(items);
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
    }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not BookmarkRow row) return;
        var item = row.Bookmark;
        try { await Task.Run(() => BookmarkLauncher.Open(item)); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { MessageBox.Show($"无法打开收藏“{item.Name}”（{item.Path}）：{ex.Message}"); }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not BookmarkRow row) return;
        var item = row.Bookmark;
        if (MessageBox.Show($"移除收藏“{item.Name}”？不会删除目标。", "PinDock", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var items = new List<Bookmark>(data.Items); items.Remove(item); Save(items);
    }
}
