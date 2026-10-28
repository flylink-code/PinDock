using System.IO;

namespace PinDock;

public static class BookmarkKindHelper
{
    public static BookmarkKind FromPath(string path)
    {
        if (Directory.Exists(path) || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
            return BookmarkKind.Folder;
        string extension = Path.GetExtension(path);
        if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
            return BookmarkKind.Application;
        return BookmarkKind.File;
    }

    public static string ToDisplayName(BookmarkKind kind) => kind switch
    {
        BookmarkKind.Application => "应用",
        BookmarkKind.Folder => "文件夹",
        BookmarkKind.File => "文件",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
