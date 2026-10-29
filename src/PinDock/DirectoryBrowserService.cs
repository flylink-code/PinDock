using System.IO;
using System.Runtime.InteropServices;

namespace PinDock;

public readonly record struct DirectoryEntryItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    DateTime LastWriteTime,
    DateTime CreationTime);

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

    public static IReadOnlyList<DirectoryEntryItem> ListEntries(
        string directoryPath,
        DirectorySortMode sortMode = DirectorySortMode.NameAsc,
        int maxEntries = 200)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return [];

        var dirs = new List<DirectoryEntryItem>();
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            foreach (var subDir in dirInfo.EnumerateDirectories())
            {
                if (ShouldSkip(subDir.Name, subDir.Attributes)) continue;
                dirs.Add(new DirectoryEntryItem(
                    subDir.Name,
                    subDir.FullName,
                    IsDirectory: true,
                    SafeTime(() => subDir.LastWriteTime),
                    SafeTime(() => subDir.CreationTime)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        SortGroup(dirs, sortMode);

        var files = new List<DirectoryEntryItem>();
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            foreach (var file in dirInfo.EnumerateFiles())
            {
                if (ShouldSkip(file.Name, file.Attributes)) continue;
                files.Add(new DirectoryEntryItem(
                    file.Name,
                    file.FullName,
                    IsDirectory: false,
                    SafeTime(() => file.LastWriteTime),
                    SafeTime(() => file.CreationTime)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        SortGroup(files, sortMode);
        dirs.AddRange(files);

        if (dirs.Count > maxEntries)
            return dirs.Take(maxEntries).ToList();
        return dirs;
    }

    public static void SortGroup(List<DirectoryEntryItem> list, DirectorySortMode sortMode)
    {
        list.Sort((a, b) => sortMode switch
        {
            DirectorySortMode.NameDesc => CompareNatural(b.Name, a.Name),
            DirectorySortMode.ModifiedDesc => CompareTimeThenName(b.LastWriteTime, a.LastWriteTime, a.Name, b.Name),
            DirectorySortMode.ModifiedAsc => CompareTimeThenName(a.LastWriteTime, b.LastWriteTime, a.Name, b.Name),
            DirectorySortMode.CreatedDesc => CompareTimeThenName(b.CreationTime, a.CreationTime, a.Name, b.Name),
            _ => CompareNatural(a.Name, b.Name)
        });
    }

    public static int CompareNatural(string a, string b) => StrCmpLogicalW(a ?? "", b ?? "");

    private static int CompareTimeThenName(DateTime t1, DateTime t2, string nameA, string nameB)
    {
        int cmp = t1.CompareTo(t2);
        return cmp != 0 ? cmp : CompareNatural(nameA, nameB);
    }

    private static DateTime SafeTime(Func<DateTime> getter)
    {
        try { return getter(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static bool ShouldSkip(string name, FileAttributes attributes)
    {
        if (string.IsNullOrWhiteSpace(name) || IgnoredNames.Contains(name))
            return true;
        if ((attributes & FileAttributes.System) != 0 && (attributes & FileAttributes.Hidden) != 0)
            return true;
        return false;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
