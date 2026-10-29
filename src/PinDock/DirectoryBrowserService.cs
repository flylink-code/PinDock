using System.IO;

namespace PinDock;

public readonly record struct DirectoryEntryItem(string Name, string FullPath, bool IsDirectory);

public static class DirectoryBrowserService
{
    private static readonly HashSet<string> IgnoredNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN",
        "System Volume Information",
        "Recovery",
        "Config.Msi",
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "DumpStack.log.tmp",
        "desktop.ini",
        "thumbs.db"
    };

    public static IReadOnlyList<DirectoryEntryItem> ListEntries(string directoryPath, int maxEntries = 200)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return [];

        var results = new List<DirectoryEntryItem>();

        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                if (ShouldSkip(subDir.Name, subDir.Attributes)) continue;
                results.Add(new DirectoryEntryItem(subDir.Name, subDir.FullName, IsDirectory: true));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        var files = new List<DirectoryEntryItem>();
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            foreach (var file in dirInfo.EnumerateFiles())
            {
                if (ShouldSkip(file.Name, file.Attributes)) continue;
                files.Add(new DirectoryEntryItem(file.Name, file.FullName, IsDirectory: false));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        results.AddRange(files);

        if (results.Count > maxEntries)
            return results.Take(maxEntries).ToList();
        return results;
    }

    private static bool ShouldSkip(string name, FileAttributes attributes)
    {
        if (string.IsNullOrWhiteSpace(name) || IgnoredNames.Contains(name))
            return true;
        if ((attributes & FileAttributes.System) != 0 && (attributes & FileAttributes.Hidden) != 0)
            return true;
        return false;
    }
}
