using System.ComponentModel;
using System.Windows.Media;

namespace PinDock;

public sealed class BookmarkRow : INotifyPropertyChanged
{
    public Bookmark Bookmark { get; }
    public ImageSource? Icon { get; private set; }
    public string? IconError { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public BookmarkRow(Bookmark bookmark, ShellIconService icons)
    {
        Bookmark = bookmark;
        _ = LoadIconAsync(icons);
    }

    private async Task LoadIconAsync(ShellIconService icons)
    {
        try { Icon = await icons.GetAsync(Bookmark.Path); }
        catch (Win32Exception ex) { IconError = ex.Message; }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconError)));
    }
}
