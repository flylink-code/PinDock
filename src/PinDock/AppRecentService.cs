using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using static PinDock.RecentIo;

namespace PinDock;

public readonly record struct AppRecentItem(
    string Name,
    string FullPath,
    string Subtitle,
    AppRecentItemKind Kind,
    DateTime LastOpenedTime,
    string LaunchArguments = "",
    string LaunchUri = "",
    bool PinnedByUser = false,
    bool IsCurrentlyOpen = false)
{
    public bool CanBrowseDirectory =>
        (Kind == AppRecentItemKind.Folder && Directory.Exists(FullPath)) ||
        (Kind is AppRecentItemKind.Workspace or AppRecentItemKind.File &&
         !string.IsNullOrWhiteSpace(BrowsableDirectoryPath) &&
         Directory.Exists(BrowsableDirectoryPath));

    public string? BrowsableDirectoryPath => Kind switch
    {
        AppRecentItemKind.Folder => FullPath,
        AppRecentItemKind.Workspace or AppRecentItemKind.File => SafeGetDirectoryName(FullPath),
        _ => null
    };

    private static string? SafeGetDirectoryName(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException) { return null; }
    }
}

public readonly record struct ResolvedAppTarget(
    string BookmarkPath,
    string ExecutablePath,
    string ExecutableName,
    string BaseArguments,
    string WorkingDirectory,
    string AppUserModelId)
{
    public string AppKey => !string.IsNullOrWhiteSpace(ExecutablePath) ? ExecutablePath : BookmarkPath;
}

public static class AppRecentService
{
    private const long CacheTtlMs = 4_000;

    private sealed record CachedAppRecentList(
        DirectorySortMode SortMode,
        int MaxEntries,
        long CachedAtTick,
        IReadOnlyList<AppRecentItem> Items);

    private static readonly ConcurrentDictionary<string, CachedAppRecentList> RecentCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ResolvedAppTarget> TargetCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Lazy<Dictionary<string, string>> StartMenuAppIdIndex =
        new(BuildStartMenuAppIdIndex, isThreadSafe: true);

    private static readonly Dictionary<string, string> KnownAppUserModelIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code.exe"] = "Microsoft.VisualStudioCode",
        ["code - insiders.exe"] = "Microsoft.VisualStudioCodeInsiders",
        ["cursor.exe"] = "Anysphere.Cursor",
        ["kiro.exe"] = "Kiro",
        ["windsurf.exe"] = "Codeium.Windsurf",
        ["trae.exe"] = "ByteDance.Trae",
        ["antigravity.exe"] = "electron.app.Antigravity",
        ["obsidian.exe"] = "md.obsidian",
        ["winword.exe"] = "Microsoft.Office.WINWORD.EXE.15",
        ["excel.exe"] = "Microsoft.Office.EXCEL.EXE.15",
        ["powerpnt.exe"] = "Microsoft.Office.POWERPNT.EXE.15",
        ["msaccess.exe"] = "Microsoft.Office.MSACCESS.EXE.15",
        ["visio.exe"] = "Microsoft.Office.VISIO.EXE.15",
        ["chrome.exe"] = "Chrome",
        ["msedge.exe"] = "MSEdge",
        ["draw.io.exe"] = "com.jgraph.drawio.desktop",
        ["xmind.exe"] = "net.xmind.vana.app",
        ["marktext.exe"] = "com.github.marktext.marktext"
    };

    private static readonly IRecentProvider[] AppSpecificProviders =
    [
        new VsCodeFamilyRecentProvider(),
        new JetBrainsRecentProvider(),
        new VisualStudioRecentProvider(),
        new SublimeRecentProvider(),
        new NotepadPlusPlusRecentProvider(),
        new OfficeWpsRecentProvider(),
        new DeclarativeRecentProvider()
    ];

    private static readonly IRecentProvider[] Providers = [.. AppSpecificProviders, new JumpListRecentProvider()];

    public static void InvalidateCache(string? appPath = null)
    {
        if (string.IsNullOrWhiteSpace(appPath))
        {
            RecentCache.Clear();
            return;
        }
        RecentCache.Clear();
    }

    public static ResolvedAppTarget ResolveAppTarget(string bookmarkPath)
    {
        if (string.IsNullOrWhiteSpace(bookmarkPath))
            return new ResolvedAppTarget("", "", "", "", "", "");

        string trimmed = bookmarkPath.Trim();
        if (TargetCache.TryGetValue(trimmed, out var cached))
            return cached;

        string exePath = trimmed;
        string baseArgs = "";
        string workDir = "";
        string appId = "";

        if (trimmed.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
        {
            try
            {
                byte[] bytes = ReadFileShared(trimmed);
                var links = ShellLinkParser.ParseEmbeddedLinks(bytes);
                if (links.Count > 0)
                {
                    var lnk = links[0];
                    if (!string.IsNullOrWhiteSpace(lnk.TargetPath))
                        exePath = lnk.TargetPath;
                    baseArgs = lnk.Arguments;
                    workDir = lnk.WorkingDirectory;
                    appId = lnk.AppUserModelId;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        string exeName = Path.GetFileName(exePath);
        if (string.IsNullOrWhiteSpace(appId))
        {
            var index = StartMenuAppIdIndex.Value;
            if (!index.TryGetValue(exePath, out appId!) && !string.IsNullOrWhiteSpace(exeName))
            {
                if (!index.TryGetValue(exeName, out appId!))
                    KnownAppUserModelIds.TryGetValue(exeName, out appId!);
            }
            appId ??= "";
        }

        var resolved = new ResolvedAppTarget(trimmed, exePath, exeName, baseArgs, workDir, appId);
        TargetCache[trimmed] = resolved;
        return resolved;
    }

    public static bool HasRecentItems(Bookmark appBookmark, LibraryData? data = null)
    {
        if (BookmarkKindHelper.FromPath(appBookmark.Path) != BookmarkKind.Application)
            return false;
        return ListRecentItems(appBookmark, data, maxEntries: 5).Count > 0;
    }

    public static IReadOnlyList<AppRecentItem> ListRecentItems(
        Bookmark appBookmark,
        LibraryData? data = null,
        DirectorySortMode sortMode = DirectorySortMode.ModifiedDesc,
        int maxEntries = 100,
        string? customAppDataRoot = null,
        string? customLocalAppDataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(appBookmark.Path))
            return [];

        var target = ResolveAppTarget(appBookmark.Path);
        string cacheKey = $"{target.AppKey}|{appBookmark.Path}";
        long nowTick = Environment.TickCount64;

        if (customAppDataRoot is null &&
            customLocalAppDataRoot is null &&
            RecentCache.TryGetValue(cacheKey, out var cached) &&
            cached.SortMode == sortMode &&
            cached.MaxEntries == maxEntries &&
            nowTick - cached.CachedAtTick <= CacheTtlMs)
        {
            return cached.Items;
        }

        string roamingRoot = customAppDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localRoot = customLocalAppDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var collected = new List<AppRecentItem>();
        var seenPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void AddOrMerge(AppRecentItem candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate.FullPath))
                return;
            string normKey = NormalizeItemKey(candidate.FullPath);
            if (seenPaths.TryGetValue(normKey, out int idx))
            {
                var existing = collected[idx];
                DateTime bestTime = candidate.LastOpenedTime > existing.LastOpenedTime
                    ? candidate.LastOpenedTime
                    : existing.LastOpenedTime;
                string bestArgs = !string.IsNullOrWhiteSpace(existing.LaunchArguments)
                    ? existing.LaunchArguments
                    : candidate.LaunchArguments;
                string bestUri = !string.IsNullOrWhiteSpace(existing.LaunchUri)
                    ? existing.LaunchUri
                    : candidate.LaunchUri;
                collected[idx] = existing with
                {
                    LastOpenedTime = bestTime,
                    LaunchArguments = bestArgs,
                    LaunchUri = bestUri,
                    PinnedByUser = existing.PinnedByUser || candidate.PinnedByUser,
                    IsCurrentlyOpen = existing.IsCurrentlyOpen || candidate.IsCurrentlyOpen
                };
                return;
            }

            seenPaths[normKey] = collected.Count;
            collected.Add(candidate);
        }

        // 1. 用户手动关联或通过 PinDock 打开记录的项目
        if (data is not null)
        {
            var records = data.GetAppRecentRecords(target.AppKey)
                .Concat(data.GetAppRecentRecords(appBookmark.Path))
                .ToList();
            foreach (var rec in records)
            {
                if (TryBuildFromRecord(rec, out var item))
                    AddOrMerge(item);
            }
        }

        // 2. 各应用适配与 Windows 任务栏跳转列表（JumpListRecentProvider 仅在使用真实 AppData 时参与）
        var roots = new RecentRoots(roamingRoot, localRoot);
        foreach (var provider in customAppDataRoot is null ? Providers : AppSpecificProviders)
        {
            foreach (var item in provider.Collect(target, roots))
                AddOrMerge(item);
        }

        SortRecentItems(collected, sortMode);

        IReadOnlyList<AppRecentItem> result = collected.Count > maxEntries
            ? collected.Take(maxEntries).ToList()
            : collected;

        if (customAppDataRoot is null && customLocalAppDataRoot is null)
        {
            RecentCache[cacheKey] = new CachedAppRecentList(sortMode, maxEntries, nowTick, result);
        }

        return result;
    }

    public static void LaunchRecentItem(Bookmark appBookmark, AppRecentItem item, LibraryData? data = null)
    {
        var target = ResolveAppTarget(appBookmark.Path);

        if (data is not null)
        {
            data.UpsertAppRecentRecord(
                target.AppKey,
                item.FullPath,
                item.Name,
                item.Kind,
                item.LaunchArguments,
                pinnedByUser: null,
                touchTimestamp: true);
        }
        InvalidateCache(appBookmark.Path);

        if (!string.IsNullOrWhiteSpace(item.LaunchUri))
        {
            Process.Start(new ProcessStartInfo(item.LaunchUri)
            {
                UseShellExecute = true
            });
            return;
        }

        string launchExe = File.Exists(target.ExecutablePath) ? target.ExecutablePath : appBookmark.Path.Trim();
        string itemArg = !string.IsNullOrWhiteSpace(item.LaunchArguments)
            ? item.LaunchArguments.Trim()
            : $"\"{item.FullPath}\"";

        string combinedArgs = string.IsNullOrWhiteSpace(appBookmark.Arguments)
            ? itemArg
            : $"{appBookmark.Arguments.Trim()} {itemArg}";

        string workDir = item.BrowsableDirectoryPath ?? "";
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
        {
            workDir = !string.IsNullOrWhiteSpace(appBookmark.WorkingDirectory) && Directory.Exists(appBookmark.WorkingDirectory)
                ? appBookmark.WorkingDirectory
                : (File.Exists(launchExe) ? Path.GetDirectoryName(launchExe) ?? "" : "");
        }

        Process.Start(new ProcessStartInfo(launchExe)
        {
            UseShellExecute = true,
            Arguments = combinedArgs,
            WorkingDirectory = workDir
        });
    }

    public static void SortRecentItems(List<AppRecentItem> list, DirectorySortMode sortMode)
    {
        list.Sort((a, b) =>
        {
            if (a.PinnedByUser != b.PinnedByUser)
                return a.PinnedByUser ? -1 : 1;

            return sortMode switch
            {
                DirectorySortMode.NameAsc => DirectoryBrowserService.CompareNatural(a.Name, b.Name),
                DirectorySortMode.NameDesc => DirectoryBrowserService.CompareNatural(b.Name, a.Name),
                DirectorySortMode.ModifiedAsc => CompareTimeThenName(a.LastOpenedTime, b.LastOpenedTime, a.Name, b.Name),
                _ => CompareRecentDesc(a, b)
            };
        });
    }

    private static int CompareRecentDesc(AppRecentItem a, AppRecentItem b)
    {
        if (a.IsCurrentlyOpen != b.IsCurrentlyOpen)
            return a.IsCurrentlyOpen ? -1 : 1;
        return CompareTimeThenName(b.LastOpenedTime, a.LastOpenedTime, a.Name, b.Name);
    }

    private static int CompareTimeThenName(DateTime t1, DateTime t2, string nameA, string nameB)
    {
        int cmp = t1.CompareTo(t2);
        return cmp != 0 ? cmp : DirectoryBrowserService.CompareNatural(nameA, nameB);
    }

    private static bool TryBuildFromRecord(AppRecentEntryRecord rec, out AppRecentItem item)
    {
        item = default;
        if (string.IsNullOrWhiteSpace(rec.Path))
            return false;

        string path = rec.Path.Trim();
        DateTime ts = rec.LastOpenedTicks > 0
            ? new DateTime(rec.LastOpenedTicks, DateTimeKind.Utc).ToLocalTime()
            : DateTime.MinValue;

        if (path.StartsWith("vscode-remote://", StringComparison.OrdinalIgnoreCase))
        {
            if (VsCodeFamilyRecentProvider.TryParseVsCodeUri(path, out var parsedRemote))
            {
                item = parsedRemote with
                {
                    LastOpenedTime = ts > DateTime.MinValue ? ts : parsedRemote.LastOpenedTime,
                    PinnedByUser = rec.PinnedByUser
                };
                return true;
            }
            return false;
        }

        bool isDir = Directory.Exists(path);
        bool isFile = !isDir && File.Exists(path);
        if (!isDir && !isFile)
            return false;

        AppRecentItemKind kind = rec.Kind;
        if (isDir)
            kind = AppRecentItemKind.Folder;
        else if (path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            kind = AppRecentItemKind.Workspace;

        string name = !string.IsNullOrWhiteSpace(rec.Name)
            ? rec.Name
            : (kind == AppRecentItemKind.Workspace
                ? Path.GetFileNameWithoutExtension(path)
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        if (ts == DateTime.MinValue)
            ts = SafeGetLastWriteTime(path, isDir);

        string subtitle = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? path;
        item = new AppRecentItem(
            Name: name,
            FullPath: path,
            Subtitle: subtitle,
            Kind: kind,
            LastOpenedTime: ts,
            LaunchArguments: rec.Arguments ?? "",
            PinnedByUser: rec.PinnedByUser);
        return true;
    }

    private static Dictionary<string, string> BuildStartMenuAppIdIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
        };

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string lnkFile in files)
            {
                try
                {
                    var parsed = ShellLinkParser.ParseEmbeddedLinks(ReadFileShared(lnkFile));
                    if (parsed.Count == 0) continue;
                    var lnk = parsed[0];
                    if (string.IsNullOrWhiteSpace(lnk.AppUserModelId)) continue;
                    if (!string.IsNullOrWhiteSpace(lnk.TargetPath))
                    {
                        map.TryAdd(lnk.TargetPath, lnk.AppUserModelId);
                        string exeName = Path.GetFileName(lnk.TargetPath);
                        if (!string.IsNullOrWhiteSpace(exeName))
                            map.TryAdd(exeName, lnk.AppUserModelId);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return map;
    }

    private static string NormalizeItemKey(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
