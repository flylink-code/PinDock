using System.IO;
using System.Runtime.InteropServices;

namespace PinDock;

public readonly record struct DirectoryEntryItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    DateTime LastWriteTime,
    DateTime CreationTime,
    long FileSize = 0);

public static class DirectoryBrowserService
{
    private const long CacheTtlMs = 12_000;

    private sealed record CachedDirectoryListing(
        DateTime DirectoryLastWriteUtc,
        DirectorySortMode SortMode,
        int MaxEntries,
        long CachedAtTick,
        IReadOnlyList<DirectoryEntryItem> Entries);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedDirectoryListing> ListingCache =
        new(StringComparer.OrdinalIgnoreCase);

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

        string cacheKey = NormalizeCacheKey(directoryPath);
        DateTime dirWriteUtc = SafeDirectoryLastWriteUtc(directoryPath);
        long nowTick = Environment.TickCount64;

        if (ListingCache.TryGetValue(cacheKey, out var cached) &&
            cached.DirectoryLastWriteUtc == dirWriteUtc &&
            cached.SortMode == sortMode &&
            cached.MaxEntries == maxEntries &&
            nowTick - cached.CachedAtTick <= CacheTtlMs)
        {
            return cached.Entries;
        }

        var dirs = new List<DirectoryEntryItem>();
        var files = new List<DirectoryEntryItem>();
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            foreach (var entry in dirInfo.EnumerateFileSystemInfos())
            {
                var attributes = entry.Attributes;
                if (ShouldSkip(entry.Name, attributes)) continue;

                bool isDir = (attributes & FileAttributes.Directory) != 0;
                DateTime lastWrite = SafeLastWriteTime(entry);
                DateTime created = SafeCreationTime(entry);

                if (isDir)
                {
                    dirs.Add(new DirectoryEntryItem(
                        entry.Name,
                        entry.FullName,
                        IsDirectory: true,
                        lastWrite,
                        created));
                }
                else
                {
                    long size = entry is FileInfo fi ? SafeFileLength(fi) : 0;
                    files.Add(new DirectoryEntryItem(
                        entry.Name,
                        entry.FullName,
                        IsDirectory: false,
                        lastWrite,
                        created,
                        size));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
        }

        SortGroup(dirs, sortMode);
        SortGroup(files, sortMode);
        dirs.AddRange(files);

        IReadOnlyList<DirectoryEntryItem> result = dirs.Count > maxEntries
            ? dirs.Take(maxEntries).ToList()
            : dirs;

        ListingCache[cacheKey] = new CachedDirectoryListing(
            dirWriteUtc,
            sortMode,
            maxEntries,
            nowTick,
            result);

        return result;
    }

    public static void InvalidateCache(string? directoryPath = null)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            ListingCache.Clear();
            return;
        }
        ListingCache.TryRemove(NormalizeCacheKey(directoryPath), out _);
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0) return "";
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return kb >= 100 ? $"{kb:F0} KB" : $"{kb:F1} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb >= 100 ? $"{mb:F0} MB" : $"{mb:F1} MB";
        double gb = mb / 1024.0;
        return $"{gb:F2} GB";
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

    private static string NormalizeCacheKey(string directoryPath)
    {
        try
        {
            return Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static DateTime SafeDirectoryLastWriteUtc(string directoryPath)
    {
        try { return Directory.GetLastWriteTimeUtc(directoryPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static DateTime SafeLastWriteTime(FileSystemInfo info)
    {
        try { return info.LastWriteTime; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static DateTime SafeCreationTime(FileSystemInfo info)
    {
        try { return info.CreationTime; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static long SafeFileLength(FileInfo file)
    {
        try { return file.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
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
