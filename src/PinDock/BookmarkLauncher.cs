using System.Diagnostics;

namespace PinDock;

public static class BookmarkLauncher
{
    public static void Open(Bookmark bookmark)
    {
        Process.Start(new ProcessStartInfo(bookmark.Path)
        {
            UseShellExecute = true,
            Arguments = bookmark.Arguments,
            WorkingDirectory = bookmark.WorkingDirectory
        });
    }
}
