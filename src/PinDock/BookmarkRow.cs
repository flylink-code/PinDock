using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace PinDock;

public sealed class BookmarkRow : INotifyPropertyChanged
{
    public Bookmark Bookmark { get; }
    public bool IsInstalledCandidate { get; set; }
    public string CandidateCategory { get; }
    public ImageSource? Icon { get; private set; }
    public string? IconError { get; private set; }

    public BookmarkKind Kind => BookmarkKindHelper.FromPath(Bookmark.Path);

    public string FallbackGlyph => Kind switch
    {
        BookmarkKind.Folder => "▰",
        BookmarkKind.Application => "◇",
        _ => "▤"
    };

    public string Subtitle => IsInstalledCandidate
        ? $"本机已安装（{CandidateCategory}） · {Bookmark.Path}"
        : $"{(Bookmark.Group.Length == 0 ? BookmarkKindHelper.ToDisplayName(Kind) : Bookmark.Group)} · {Bookmark.Path}";

    public string PinLabel => IsInstalledCandidate
        ? "＋ 固定到 Dock"
        : Bookmark.Pinned ? "★ 已固定" : "☆ 固定";

    public bool IsPinnedOrCandidate => IsInstalledCandidate || Bookmark.Pinned;

    public Visibility SavedActionsVisibility => IsInstalledCandidate ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public BookmarkRow(Bookmark bookmark, ShellIconService icons, bool isInstalledCandidate = false, string candidateCategory = "")
    {
        Bookmark = bookmark;
        IsInstalledCandidate = isInstalledCandidate;
        CandidateCategory = candidateCategory;
        _ = LoadIconAsync(icons);
    }

    public void NotifyPinChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinnedOrCandidate)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SavedActionsVisibility)));
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
