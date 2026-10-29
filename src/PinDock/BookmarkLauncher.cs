using System.Diagnostics;
using System.IO;

namespace PinDock;

public static class BookmarkLauncher
{
    public static void Open(Bookmark bookmark)
    {
        string targetPath = bookmark.Path.Trim();
        string workDir = bookmark.WorkingDirectory.Trim();
        if (workDir.Length == 0 &&
            !targetPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(targetPath))
        {
            workDir = Path.GetDirectoryName(targetPath) ?? "";
        }

        Process.Start(new ProcessStartInfo(targetPath)
        {
            UseShellExecute = true,
            Arguments = bookmark.Arguments,
            WorkingDirectory = workDir
        });
    }
}
