using System.ComponentModel;
using System.Windows.Media;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace PinDock;

public sealed class BookmarkRow : INotifyPropertyChanged
{
    public Bookmark Bookmark { get; }
    public ImageSource? Icon { get; private set; }
    public string? IconError { get; private set; }
    public string Subtitle => $"{(Bookmark.Group.Length == 0 ? "未分组" : Bookmark.Group)} · {BookmarkKindHelper.ToDisplayName(BookmarkKindHelper.FromPath(Bookmark.Path))} · {Bookmark.Path}";
    public string PinLabel => Bookmark.Pinned ? "★ 已固定" : "☆ 固定";
    public event PropertyChangedEventHandler? PropertyChanged;

    public BookmarkRow(Bookmark bookmark, ShellIconService icons)
    {
        Bookmark = bookmark;
        _ = LoadIconAsync(icons);
    }

    public void NotifyPinChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
    }

    private async Task LoadIconAsync(ShellIconService icons)
    {
        try { Icon = await icons.GetAsync(Bookmark.Path); }
        catch (Win32Exception ex) { IconError = ex.Message; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconError)));
    }
}
