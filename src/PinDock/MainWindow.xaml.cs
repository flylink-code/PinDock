using System.Diagnostics;
using System.IO;
using System.Windows;

namespace PinDock;

public partial class MainWindow : Window
{
    private readonly LibraryStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinDock"));
    private readonly LibraryData data;

    public MainWindow()
    {
        InitializeComponent();
        try { data = store.Load(); }
        catch (Exception ex) { MessageBox.Show($"无法读取收藏，未覆盖配置：{ex.Message}"); throw; }
        Refresh();
    }

    private void Refresh() { ItemsList.ItemsSource = null; ItemsList.ItemsSource = data.Items; }
    private void Add(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            var item = new Bookmark { Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Path = path, Pinned = true };
            if (string.IsNullOrWhiteSpace(item.Name)) item.Name = path;
            if (!LibraryStore.IsDuplicate(data.Items, item)) data.Items.Add(item);
        }
        Save();
    }
    private void Save()
    {
        try { store.Save(data); Refresh(); }
        catch (Exception ex) { MessageBox.Show($"保存失败：{ex.Message}"); }
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
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not Bookmark item) return;
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true, Arguments = item.Arguments, WorkingDirectory = item.WorkingDirectory }); }
        catch (Exception ex) { MessageBox.Show($"无法打开收藏：{ex.Message}"); }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsList.SelectedItem is not Bookmark item) return;
        if (MessageBox.Show($"移除收藏“{item.Name}”？不会删除目标。", "PinDock", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        data.Items.Remove(item); Save();
    }
}
