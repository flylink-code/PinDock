using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PinDock;

public partial class BookmarkEditor : CardWindowBase
{
    public Bookmark Result { get; }

    public BookmarkEditor(Bookmark item, IEnumerable<string> groups, IReadOnlyList<InstalledApp>? installedApps = null)
    {
        InitializeComponent();
        Result = new Bookmark
        {
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            Arguments = item.Arguments,
            WorkingDirectory = item.WorkingDirectory,
            Group = item.Group,
            Pinned = item.Pinned
        };
        NameInput.Text = Result.Name;
        PathInput.Text = Result.Path;
        ArgumentsInput.Text = Result.Arguments;
        WorkingDirectoryInput.Text = Result.WorkingDirectory;
        GroupInput.Items.Clear();
        GroupInput.Items.Add("未分组");
        foreach (string group in groups) GroupInput.Items.Add(group);
        GroupInput.Text = string.IsNullOrEmpty(Result.Group) ? "未分组" : Result.Group;
        PinnedInput.IsChecked = Result.Pinned;

        if (installedApps is { Count: > 0 })
        {
            foreach (var app in installedApps)
            {
                InstalledAppCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{app.Name}  ({app.Category})",
                    Tag = app
                });
            }
        }
        else
        {
            QuickAppSection.Visibility = Visibility.Collapsed;
        }
    }

    private void InstalledAppCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstalledAppCombo.SelectedItem is ComboBoxItem { Tag: InstalledApp app })
        {
            NameInput.Text = app.Name;
            PathInput.Text = app.Path;
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择要添加到 PinDock 的文件夹" };
        if (dialog.ShowDialog(this) != true) return;
        ApplyPickedPath(dialog.FolderName);
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要添加到 PinDock 的应用或文件",
            Filter = "常用目标 (*.exe;*.lnk;*.bat;*.cmd;*.*)|*.exe;*.lnk;*.bat;*.cmd;*.*|应用程序与快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            DereferenceLinks = false
        };
        if (dialog.ShowDialog(this) != true) return;
        ApplyPickedPath(dialog.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            ApplyPickedPath(files[0]);
    }

    private void ApplyPickedPath(string fullPath)
    {
        PathInput.Text = fullPath;
        if (string.IsNullOrWhiteSpace(NameInput.Text))
            NameInput.Text = SuggestDisplayName(fullPath);
    }

    public static string SuggestDisplayName(string fullPath)
    {
        string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(trimmed))
        {
            string folderName = Path.GetFileName(trimmed);
            return folderName.Length > 0 ? folderName : trimmed;
        }
        string ext = Path.GetExtension(trimmed);
        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            string baseName = Path.GetFileNameWithoutExtension(trimmed);
            if (baseName.Length > 0) return baseName;
        }
        string fileName = Path.GetFileName(trimmed);
        return fileName.Length > 0 ? fileName : trimmed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string path = PathInput.Text.Trim();
        string name = NameInput.Text.Trim();
        if (path.Length == 0) { MessageBox.Show(this, "请选择或输入目标路径。"); return; }
        if ((!string.IsNullOrWhiteSpace(ArgumentsInput.Text) || !string.IsNullOrWhiteSpace(WorkingDirectoryInput.Text)) && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "启动参数和工作目录仅适用于 EXE；快捷方式使用其自身配置。");
            return;
        }
        Result.Path = path;
        Result.Name = name.Length == 0 ? SuggestDisplayName(path) : name;
        Result.Arguments = ArgumentsInput.Text;
        Result.WorkingDirectory = WorkingDirectoryInput.Text.Trim();
        string group = GroupInput.Text.Trim();
        Result.Group = group is "" or "未分组" ? "" : group;
        Result.Pinned = PinnedInput.IsChecked == true;
        DialogResult = true;
    }
}
