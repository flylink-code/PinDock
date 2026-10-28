using System.IO;
using System.Windows;

namespace PinDock;

public partial class BookmarkEditor : Window
{
    public Bookmark Result { get; }

    public BookmarkEditor(Bookmark item)
    {
        InitializeComponent();
        Result = new Bookmark { Id = item.Id, Name = item.Name, Path = item.Path, Arguments = item.Arguments, WorkingDirectory = item.WorkingDirectory, Pinned = item.Pinned };
        NameInput.Text = Result.Name;
        PathInput.Text = Result.Path;
        ArgumentsInput.Text = Result.Arguments;
        WorkingDirectoryInput.Text = Result.WorkingDirectory;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string path = PathInput.Text.Trim();
        string name = NameInput.Text.Trim();
        if (path.Length == 0) { MessageBox.Show(this, "请输入目标路径。"); return; }
        if ((!string.IsNullOrWhiteSpace(ArgumentsInput.Text) || !string.IsNullOrWhiteSpace(WorkingDirectoryInput.Text)) && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "启动参数和工作目录仅适用于 EXE；快捷方式使用其自身配置。");
            return;
        }
        Result.Path = path;
        Result.Name = name.Length == 0 ? Path.GetFileName(path.TrimEnd('\\', '/')) : name;
        if (Result.Name.Length == 0) Result.Name = path;
        Result.Arguments = ArgumentsInput.Text;
        Result.WorkingDirectory = WorkingDirectoryInput.Text.Trim();
        DialogResult = true;
    }
}
