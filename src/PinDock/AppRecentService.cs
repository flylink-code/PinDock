using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

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

    public sealed class ParsedShortcutLink
    {
        public string TargetPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string IconLocation { get; set; } = "";
        public string AppUserModelId { get; set; } = "";
    }

    private static readonly ConcurrentDictionary<string, CachedAppRecentList> RecentCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, ResolvedAppTarget> TargetCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, string> ExtensionAssocCache =
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

    private static readonly Dictionary<string, string> VsCodeFamilyAppDataDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code.exe"] = "Code",
        ["code - insiders.exe"] = "Code - Insiders",
        ["cursor.exe"] = "Cursor",
        ["windsurf.exe"] = "Windsurf",
        ["kiro.exe"] = "Kiro",
        ["trae.exe"] = "Trae",
        ["vscodium.exe"] = "VSCodium",
        ["antigravity.exe"] = "Antigravity"
    };

    private static readonly (string ExePrefix, string[] RoamingDirs)[] JetBrainsFamilies =
    [
        ("clion", [@"JetBrains\CLion*"]),
        ("idea", [@"JetBrains\IntelliJIdea*", @"JetBrains\IdeaIC*"]),
        ("pycharm", [@"JetBrains\PyCharm*", @"JetBrains\PyCharmCE*"]),
        ("webstorm", [@"JetBrains\WebStorm*"]),
        ("goland", [@"JetBrains\GoLand*"]),
        ("rider", [@"JetBrains\Rider*"]),
        ("phpstorm", [@"JetBrains\PhpStorm*"]),
        ("rustrover", [@"JetBrains\RustRover*"]),
        ("datagrip", [@"JetBrains\DataGrip*"]),
        ("rubymine", [@"JetBrains\RubyMine*"]),
        ("studio", [@"Google\AndroidStudio*"])
    ];

    private static readonly byte[] ShellLinkHeaderSig =
    [
        0x4C, 0x00, 0x00, 0x00,
        0x01, 0x14, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x46
    ];

    private static readonly byte[] AppUserModelIdFmtId =
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3").ToByteArray();

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
                var links = ParseEmbeddedLinks(bytes);
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

        // 2. VS Code 家族（VS Code / Insiders / Cursor / Windsurf / Kiro / Trae / VSCodium / Antigravity）
        foreach (var item in CollectVsCodeFamilyRecents(target, roamingRoot))
            AddOrMerge(item);

        // 3. JetBrains IDE 家族（CLion / IDEA / PyCharm / WebStorm / GoLand / Rider / RustRover / Android Studio）
        foreach (var item in CollectJetBrainsRecents(target, roamingRoot))
            AddOrMerge(item);

        // 4. Visual Studio (devenv.exe)
        foreach (var item in CollectVisualStudioRecents(target, localRoot))
            AddOrMerge(item);

        // 5. Sublime Text / Obsidian / Notepad++ / Typora / Qt Creator / Office / WPS
        foreach (var item in CollectSublimeRecents(target, roamingRoot))
            AddOrMerge(item);
        foreach (var item in CollectObsidianRecents(target, roamingRoot))
            AddOrMerge(item);
        foreach (var item in CollectNotepadPlusPlusRecents(target, roamingRoot))
            AddOrMerge(item);
        foreach (var item in CollectTyporaRecents(target, roamingRoot))
            AddOrMerge(item);
        foreach (var item in CollectOfficeAndWpsRecents(target))
            AddOrMerge(item);

        // 6. Windows 原生任务栏跳转列表 (CustomDestinations / AutomaticDestinations) 与关联的 Recent 文档
        if (customAppDataRoot is null)
        {
            foreach (var item in CollectWindowsJumpListAndRecent(target, roamingRoot))
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
            if (TryParseVsCodeUri(path, out var parsedRemote))
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

    // =========================================================================
    // Provider 1: VS Code & Electron IDE Family
    // =========================================================================

    private static IEnumerable<AppRecentItem> CollectVsCodeFamilyRecents(ResolvedAppTarget target, string roamingRoot)
    {
        var userDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.ExecutableName) &&
            VsCodeFamilyAppDataDirs.TryGetValue(target.ExecutableName, out string? appDataFolder))
        {
            string roamingUserDir = Path.Combine(roamingRoot, appDataFolder, "User");
            if (Directory.Exists(roamingUserDir))
                userDirs.Add(roamingUserDir);
        }

        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && File.Exists(target.ExecutablePath))
        {
            string? exeDir = Path.GetDirectoryName(target.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                string portableUserDir = Path.Combine(exeDir, "data", "user-data", "User");
                if (Directory.Exists(portableUserDir) && !userDirs.Contains(portableUserDir, StringComparer.OrdinalIgnoreCase))
                    userDirs.Add(portableUserDir);
            }
        }

        if (userDirs.Count == 0)
            yield break;

        foreach (string userDir in userDirs)
        {
            var workspaceTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var currentlyOpenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderedUris = new List<string>();
            var discoveredUris = new List<string>();

            // A. workspaceStorage\*\workspace.json + state.vscdb timestamp
            string wsStorageDir = Path.Combine(userDir, "workspaceStorage");
            if (Directory.Exists(wsStorageDir))
            {
                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(wsStorageDir))
                    {
                        string wjPath = Path.Combine(subDir, "workspace.json");
                        if (!File.Exists(wjPath)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(ReadTextShared(wjPath));
                            var root = doc.RootElement;
                            string? uri = null;
                            if (root.TryGetProperty("workspace", out var wEl) && wEl.ValueKind == JsonValueKind.String)
                                uri = wEl.GetString();
                            else if (root.TryGetProperty("folder", out var fEl) && fEl.ValueKind == JsonValueKind.String)
                                uri = fEl.GetString();

                            if (string.IsNullOrWhiteSpace(uri)) continue;

                            string stateDbPath = Path.Combine(subDir, "state.vscdb");
                            DateTime ts = File.Exists(stateDbPath)
                                ? File.GetLastWriteTime(stateDbPath)
                                : File.GetLastWriteTime(wjPath);

                            string normUri = NormalizeVsCodeUriKey(uri);
                            if (!workspaceTimestamps.TryGetValue(normUri, out var prevTs) || ts > prevTs)
                                workspaceTimestamps[normUri] = ts;

                            discoveredUris.Add(uri);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        {
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            // B. globalStorage\storage.json
            string storageJsonPath = Path.Combine(userDir, "globalStorage", "storage.json");
            if (File.Exists(storageJsonPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(ReadTextShared(storageJsonPath));
                    var root = doc.RootElement;

                    void AddWindowUri(string? u, bool active)
                    {
                        if (string.IsNullOrWhiteSpace(u)) return;
                        orderedUris.Add(u);
                        if (active)
                            currentlyOpenUris.Add(NormalizeVsCodeUriKey(u));
                    }

                    if (root.TryGetProperty("windowsState", out var winState) && winState.ValueKind == JsonValueKind.Object)
                    {
                        if (winState.TryGetProperty("lastActiveWindow", out var lastWin) && lastWin.ValueKind == JsonValueKind.Object)
                        {
                            AddWindowUri(ExtractWindowUri(lastWin), active: true);
                        }
                        if (winState.TryGetProperty("openedWindows", out var openedWins) && openedWins.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in openedWins.EnumerateArray())
                            {
                                if (w.ValueKind == JsonValueKind.Object)
                                    AddWindowUri(ExtractWindowUri(w), active: true);
                            }
                        }
                    }

                    if (root.TryGetProperty("backupWorkspaces", out var bw) && bw.ValueKind == JsonValueKind.Object)
                    {
                        if (bw.TryGetProperty("workspaces", out var bwWs) && bwWs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in bwWs.EnumerateArray())
                            {
                                if (w.ValueKind == JsonValueKind.Object &&
                                    w.TryGetProperty("configURIPath", out var cup) &&
                                    cup.ValueKind == JsonValueKind.String)
                                {
                                    AddWindowUri(cup.GetString(), active: true);
                                }
                            }
                        }
                        if (bw.TryGetProperty("folders", out var bwFolders) && bwFolders.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in bwFolders.EnumerateArray())
                            {
                                if (f.ValueKind == JsonValueKind.Object &&
                                    f.TryGetProperty("folderUri", out var fu) &&
                                    fu.ValueKind == JsonValueKind.String)
                                {
                                    AddWindowUri(fu.GetString(), active: true);
                                }
                            }
                        }
                    }

                    if (root.TryGetProperty("profileAssociations", out var pa) &&
                        pa.ValueKind == JsonValueKind.Object &&
                        pa.TryGetProperty("workspaces", out var paWs) &&
                        paWs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in paWs.EnumerateObject())
                        {
                            discoveredUris.Add(prop.Name);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }

            // C. globalStorage\state.vscdb -> history.recentlyOpenedPathsList
            string stateVscdbPath = Path.Combine(userDir, "globalStorage", "state.vscdb");
            if (File.Exists(stateVscdbPath))
            {
                string? recentJson = TryQuerySqliteSingleValue(
                    stateVscdbPath,
                    "SELECT value FROM ItemTable WHERE key = 'history.recentlyOpenedPathsList'");
                if (!string.IsNullOrWhiteSpace(recentJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(recentJson);
                        if (doc.RootElement.TryGetProperty("entries", out var entriesEl) &&
                            entriesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entryEl in entriesEl.EnumerateArray())
                            {
                                if (entryEl.ValueKind != JsonValueKind.Object) continue;
                                if (entryEl.TryGetProperty("folderUri", out var fu) && fu.ValueKind == JsonValueKind.String)
                                {
                                    orderedUris.Add(fu.GetString()!);
                                }
                                else if (entryEl.TryGetProperty("workspace", out var wsObj) &&
                                         wsObj.ValueKind == JsonValueKind.Object &&
                                         wsObj.TryGetProperty("configPath", out var cp) &&
                                         cp.ValueKind == JsonValueKind.String)
                                {
                                    orderedUris.Add(cp.GetString()!);
                                }
                                else if (entryEl.TryGetProperty("fileUri", out var fileU) && fileU.ValueKind == JsonValueKind.String)
                                {
                                    orderedUris.Add(fileU.GetString()!);
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }

            // D. Merge orderedUris + discoveredUris
            DateTime syntheticBase = DateTime.Now;
            int rankIndex = 0;

            foreach (string rawUri in orderedUris.Concat(discoveredUris))
            {
                if (!TryParseVsCodeUri(rawUri, out var parsed))
                    continue;

                string normUri = NormalizeVsCodeUriKey(rawUri);
                bool isOpen = currentlyOpenUris.Contains(normUri);

                DateTime ts = DateTime.MinValue;
                if (workspaceTimestamps.TryGetValue(normUri, out var wsTs))
                {
                    ts = wsTs;
                }
                else if (rankIndex < orderedUris.Count)
                {
                    ts = parsed.LastOpenedTime > DateTime.MinValue
                        ? parsed.LastOpenedTime
                        : syntheticBase.AddMinutes(-rankIndex);
                }
                else
                {
                    ts = parsed.LastOpenedTime;
                }

                rankIndex++;
                yield return parsed with
                {
                    LastOpenedTime = ts,
                    IsCurrentlyOpen = isOpen
                };
            }
        }
    }

    private static string? ExtractWindowUri(JsonElement winObj)
    {
        if (winObj.TryGetProperty("folder", out var fEl) && fEl.ValueKind == JsonValueKind.String)
            return fEl.GetString();
        if (winObj.TryGetProperty("workspaceIdentifier", out var wsEl) &&
            wsEl.ValueKind == JsonValueKind.Object &&
            wsEl.TryGetProperty("configURIPath", out var cupEl) &&
            cupEl.ValueKind == JsonValueKind.String)
            return cupEl.GetString();
        return null;
    }

    private static string NormalizeVsCodeUriKey(string rawUri)
    {
        try
        {
            return Uri.UnescapeDataString(rawUri.Trim()).TrimEnd('/');
        }
        catch (UriFormatException)
        {
            return rawUri.Trim().TrimEnd('/');
        }
    }

    public static bool TryParseVsCodeUri(string rawUri, out AppRecentItem item)
    {
        item = default;
        if (string.IsNullOrWhiteSpace(rawUri))
            return false;

        string trimmed = rawUri.Trim();

        // 1. file:// URI
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(trimmed); }
            catch (UriFormatException) { unescaped = trimmed; }

            string localPath = unescaped.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                ? unescaped[8..]
                : unescaped[7..];

            localPath = localPath.Replace('/', '\\');
            if (localPath.Length >= 2 && char.IsLetter(localPath[0]) && localPath[1] == ':')
            {
                localPath = char.ToUpperInvariant(localPath[0]) + localPath[1..];
            }
            if (localPath.Length > 3)
            {
                localPath = localPath.TrimEnd('\\');
            }

            bool isWorkspaceFile = localPath.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
            bool isDir = !isWorkspaceFile && Directory.Exists(localPath);
            bool isFile = !isDir && File.Exists(localPath);

            if (!isDir && !isFile)
                return false;

            AppRecentItemKind kind = isWorkspaceFile
                ? AppRecentItemKind.Workspace
                : isDir
                    ? AppRecentItemKind.Folder
                    : AppRecentItemKind.File;

            string name = isWorkspaceFile
                ? Path.GetFileNameWithoutExtension(localPath)
                : Path.GetFileName(localPath);
            if (string.IsNullOrWhiteSpace(name))
                name = localPath;

            string subtitle = Path.GetDirectoryName(localPath) ?? localPath;
            DateTime lastWrite = SafeGetLastWriteTime(localPath, isDir);

            item = new AppRecentItem(
                Name: name,
                FullPath: localPath,
                Subtitle: subtitle,
                Kind: kind,
                LastOpenedTime: lastWrite);
            return true;
        }

        // 2. vscode-remote:// URI
        if (trimmed.StartsWith("vscode-remote://", StringComparison.OrdinalIgnoreCase))
        {
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(trimmed); }
            catch (UriFormatException) { unescaped = trimmed; }

            string withoutScheme = unescaped["vscode-remote://".Length..];
            int slashIdx = withoutScheme.IndexOf('/');
            if (slashIdx <= 0)
                return false;

            string authority = withoutScheme[..slashIdx];
            string remotePath = withoutScheme[slashIdx..];
            if (remotePath.Length > 1)
                remotePath = remotePath.TrimEnd('/');

            string hostBadge = DecodeRemoteAuthorityBadge(authority);
            bool isWorkspace = remotePath.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
            string baseName = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? remotePath;
            if (isWorkspace && baseName.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^".code-workspace".Length];

            string displayName = string.IsNullOrWhiteSpace(hostBadge)
                ? baseName
                : $"{baseName} [{hostBadge}]";

            string launchArg = isWorkspace
                ? $"--file-uri \"{trimmed}\""
                : $"--folder-uri \"{trimmed}\"";

            item = new AppRecentItem(
                Name: displayName,
                FullPath: trimmed,
                Subtitle: remotePath,
                Kind: AppRecentItemKind.Remote,
                LastOpenedTime: DateTime.MinValue,
                LaunchArguments: launchArg);
            return true;
        }

        return false;
    }

    private static string DecodeRemoteAuthorityBadge(string authority)
    {
        int plusIdx = authority.IndexOf('+');
        if (plusIdx < 0)
            return authority;

        string prefix = authority[..plusIdx];
        string payload = authority[(plusIdx + 1)..];

        string decodedPayload = TryDecodeHexUtf8(payload) ?? payload;
        if (decodedPayload.StartsWith('{') && decodedPayload.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(decodedPayload);
                if (doc.RootElement.TryGetProperty("hostName", out var hn) && hn.ValueKind == JsonValueKind.String)
                    decodedPayload = hn.GetString() ?? decodedPayload;
                else if (doc.RootElement.TryGetProperty("ContainerName", out var cn) && cn.ValueKind == JsonValueKind.String)
                    decodedPayload = cn.GetString() ?? decodedPayload;
            }
            catch (JsonException)
            {
            }
        }

        string kindLabel = prefix.ToLowerInvariant() switch
        {
            "ssh-remote" => "SSH",
            "wsl" => "WSL",
            "dev-container" or "attached-container" => "Container",
            "tunnel" => "Tunnel",
            _ => prefix
        };
        return $"{kindLabel}: {decodedPayload}";
    }

    private static string? TryDecodeHexUtf8(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
            return null;
        for (int i = 0; i < hex.Length; i++)
        {
            if (!Uri.IsHexDigit(hex[i]))
                return null;
        }
        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // =========================================================================
    // Provider 2: JetBrains IDE Family
    // =========================================================================

    private static IEnumerable<AppRecentItem> CollectJetBrainsRecents(ResolvedAppTarget target, string roamingRoot)
    {
        if (string.IsNullOrWhiteSpace(target.ExecutableName))
            yield break;

        string exeBase = Path.GetFileNameWithoutExtension(target.ExecutableName);
        string[]? matchedPatterns = null;
        foreach (var (prefix, patterns) in JetBrainsFamilies)
        {
            if (exeBase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                matchedPatterns = patterns;
                break;
            }
        }
        if (matchedPatterns is null)
            yield break;

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var productDirs = new List<DirectoryInfo>();

        foreach (string pattern in matchedPatterns)
        {
            string? parentRel = Path.GetDirectoryName(pattern);
            string searchMask = Path.GetFileName(pattern);
            string parentDir = string.IsNullOrWhiteSpace(parentRel) ? roamingRoot : Path.Combine(roamingRoot, parentRel);
            if (!Directory.Exists(parentDir)) continue;
            try
            {
                productDirs.AddRange(new DirectoryInfo(parentDir).GetDirectories(searchMask));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        productDirs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        foreach (var prodDir in productDirs)
        {
            foreach (string xmlFileName in new[] { "recentProjects.xml", "recentSolutions.xml" })
            {
                string xmlPath = Path.Combine(prodDir.FullName, "options", xmlFileName);
                if (!File.Exists(xmlPath)) continue;

                XDocument doc;
                try { doc = XDocument.Parse(ReadTextShared(xmlPath)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }

                foreach (var entry in doc.Descendants("entry"))
                {
                    string? rawKey = entry.Attribute("key")?.Value;
                    if (string.IsNullOrWhiteSpace(rawKey)) continue;

                    string fullPath = rawKey
                        .Replace("$USER_HOME$", userHome, StringComparison.OrdinalIgnoreCase)
                        .Replace('/', '\\');
                    if (fullPath.Length > 3)
                        fullPath = fullPath.TrimEnd('\\');

                    bool isDir = Directory.Exists(fullPath);
                    bool isFile = !isDir && File.Exists(fullPath);
                    if (!isDir && !isFile) continue;

                    var meta = entry.Descendants("RecentProjectMetaInfo").FirstOrDefault();
                    string? displayName = meta?.Attribute("displayName")?.Value;

                    long tsMs = 0;
                    foreach (var opt in entry.Descendants("option"))
                    {
                        string? optName = opt.Attribute("name")?.Value;
                        if (optName is "projectOpenTimestamp" or "buildTimestamp" &&
                            long.TryParse(opt.Attribute("value")?.Value, out long parsedMs) &&
                            parsedMs > tsMs)
                        {
                            tsMs = parsedMs;
                        }
                    }

                    DateTime openedTime = tsMs > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime
                        : SafeGetLastWriteTime(fullPath, isDir);

                    AppRecentItemKind kind = isDir ? AppRecentItemKind.Folder : AppRecentItemKind.Workspace;
                    string name = !string.IsNullOrWhiteSpace(displayName)
                        ? displayName
                        : (isDir ? Path.GetFileName(fullPath) : Path.GetFileNameWithoutExtension(fullPath));
                    if (string.IsNullOrWhiteSpace(name))
                        name = fullPath;

                    yield return new AppRecentItem(
                        Name: name,
                        FullPath: fullPath,
                        Subtitle: Path.GetDirectoryName(fullPath) ?? fullPath,
                        Kind: kind,
                        LastOpenedTime: openedTime);
                }
            }
        }
    }

    // =========================================================================
    // Provider 3: Visual Studio (devenv.exe)
    // =========================================================================

    private static IEnumerable<AppRecentItem> CollectVisualStudioRecents(ResolvedAppTarget target, string localAppDataRoot)
    {
        if (!string.Equals(target.ExecutableName, "devenv.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        string vsRoot = Path.Combine(localAppDataRoot, "Microsoft", "VisualStudio");
        if (!Directory.Exists(vsRoot))
            yield break;

        List<DirectoryInfo> vsDirs;
        try
        {
            vsDirs = new DirectoryInfo(vsRoot).GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in vsDirs)
        {
            string settingsXml = Path.Combine(dir.FullName, "ApplicationPrivateSettings.xml");
            if (!File.Exists(settingsXml)) continue;

            XDocument doc;
            try { doc = XDocument.Parse(ReadTextShared(settingsXml)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }

            var collection = doc.Descendants("collection")
                .FirstOrDefault(c => string.Equals(c.Attribute("name")?.Value, "CodeContainers.Offline", StringComparison.OrdinalIgnoreCase));
            string? jsonRaw = collection?.Descendants("value").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(jsonRaw)) continue;

            JsonDocument jsonDoc;
            try { jsonDoc = JsonDocument.Parse(jsonRaw); }
            catch (JsonException) { continue; }

            using (jsonDoc)
            {
                if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in jsonDoc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("Key", out var kEl) || kEl.ValueKind != JsonValueKind.String)
                        continue;
                    string? path = kEl.GetString();
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    bool isDir = Directory.Exists(path);
                    bool isFile = !isDir && File.Exists(path);
                    if (!isDir && !isFile) continue;

                    DateTime lastAccessed = DateTime.MinValue;
                    bool isFav = false;
                    if (el.TryGetProperty("Value", out var valObj) && valObj.ValueKind == JsonValueKind.Object)
                    {
                        if (valObj.TryGetProperty("LastAccessed", out var laEl) &&
                            laEl.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(laEl.GetString(), out var dto))
                        {
                            lastAccessed = dto.LocalDateTime;
                        }
                        if (valObj.TryGetProperty("IsFavorite", out var favEl) && favEl.ValueKind == JsonValueKind.True)
                        {
                            isFav = true;
                        }
                    }

                    AppRecentItemKind kind = isDir ? AppRecentItemKind.Folder : AppRecentItemKind.Workspace;
                    string name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(name)) name = path;

                    yield return new AppRecentItem(
                        Name: name,
                        FullPath: path,
                        Subtitle: Path.GetDirectoryName(path) ?? path,
                        Kind: kind,
                        LastOpenedTime: lastAccessed > DateTime.MinValue ? lastAccessed : SafeGetLastWriteTime(path, isDir),
                        PinnedByUser: isFav);
                }
            }
        }
    }

    // =========================================================================
    // Provider 4: Sublime Text, Obsidian, Notepad++, Typora, Office & WPS
    // =========================================================================

    private static IEnumerable<AppRecentItem> CollectSublimeRecents(ResolvedAppTarget target, string roamingRoot)
    {
        if (!string.Equals(target.ExecutableName, "sublime_text.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (string folderName in new[] { "Sublime Text", "Sublime Text 3" })
        {
            string localDir = Path.Combine(roamingRoot, folderName, "Local");
            if (!Directory.Exists(localDir)) continue;

            foreach (string sessionFile in new[] { "Auto Save Session.sublime_session", "Session.sublime_session" })
            {
                string fullSession = Path.Combine(localDir, sessionFile);
                if (!File.Exists(fullSession)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(ReadTextShared(fullSession)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("workspaces", out var wsEl) &&
                        wsEl.ValueKind == JsonValueKind.Object &&
                        wsEl.TryGetProperty("recent_workspaces", out var rwArr) &&
                        rwArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var w in rwArr.EnumerateArray())
                        {
                            if (w.ValueKind == JsonValueKind.String &&
                                TryConvertSublimePath(w.GetString(), out string winPath) &&
                                File.Exists(winPath))
                            {
                                yield return new AppRecentItem(
                                    Name: Path.GetFileNameWithoutExtension(winPath),
                                    FullPath: winPath,
                                    Subtitle: Path.GetDirectoryName(winPath) ?? winPath,
                                    Kind: AppRecentItemKind.Workspace,
                                    LastOpenedTime: SafeGetLastWriteTime(winPath, false));
                            }
                        }
                    }

                    if (root.TryGetProperty("folder_history", out var fhArr) && fhArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in fhArr.EnumerateArray())
                        {
                            if (f.ValueKind == JsonValueKind.String &&
                                TryConvertSublimePath(f.GetString(), out string winPath) &&
                                Directory.Exists(winPath))
                            {
                                string name = Path.GetFileName(winPath);
                                if (string.IsNullOrWhiteSpace(name)) name = winPath;
                                yield return new AppRecentItem(
                                    Name: name,
                                    FullPath: winPath,
                                    Subtitle: Path.GetDirectoryName(winPath) ?? winPath,
                                    Kind: AppRecentItemKind.Folder,
                                    LastOpenedTime: SafeGetLastWriteTime(winPath, true));
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool TryConvertSublimePath(string? raw, out string windowsPath)
    {
        windowsPath = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        if (s.Length >= 3 && s[0] == '/' && char.IsLetter(s[1]) && s[2] == '/')
        {
            windowsPath = $"{char.ToUpperInvariant(s[1])}:\\{s[3..].Replace('/', '\\')}";
            if (windowsPath.Length > 3) windowsPath = windowsPath.TrimEnd('\\');
            return true;
        }
        if (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':')
        {
            windowsPath = s.Replace('/', '\\');
            if (windowsPath.Length > 3) windowsPath = windowsPath.TrimEnd('\\');
            return true;
        }
        return false;
    }

    private static IEnumerable<AppRecentItem> CollectObsidianRecents(ResolvedAppTarget target, string roamingRoot)
    {
        if (!string.Equals(target.ExecutableName, "obsidian.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        string obsJson = Path.Combine(roamingRoot, "obsidian", "obsidian.json");
        if (!File.Exists(obsJson))
            yield break;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(ReadTextShared(obsJson)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { yield break; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("vaults", out var vaults) || vaults.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (var prop in vaults.EnumerateObject())
            {
                string vaultId = prop.Name;
                var val = prop.Value;
                if (val.ValueKind != JsonValueKind.Object ||
                    !val.TryGetProperty("path", out var pEl) ||
                    pEl.ValueKind != JsonValueKind.String)
                    continue;

                string? path = pEl.GetString();
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    continue;

                long tsMs = val.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out long parsedTs)
                    ? parsedTs
                    : 0;
                bool isOpen = val.TryGetProperty("open", out var openEl) && openEl.ValueKind == JsonValueKind.True;

                DateTime openedTime = tsMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime
                    : SafeGetLastWriteTime(path, true);

                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(name)) name = path;

                yield return new AppRecentItem(
                    Name: name,
                    FullPath: path,
                    Subtitle: Path.GetDirectoryName(path) ?? path,
                    Kind: AppRecentItemKind.Folder,
                    LastOpenedTime: openedTime,
                    LaunchUri: $"obsidian://open?vault={Uri.EscapeDataString(vaultId)}",
                    IsCurrentlyOpen: isOpen);
            }
        }
    }

    private static IEnumerable<AppRecentItem> CollectNotepadPlusPlusRecents(ResolvedAppTarget target, string roamingRoot)
    {
        if (!string.Equals(target.ExecutableName, "notepad++.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        var configPaths = new List<string> { Path.Combine(roamingRoot, "Notepad++", "config.xml") };
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            string? exeDir = Path.GetDirectoryName(target.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(exeDir))
                configPaths.Add(Path.Combine(exeDir, "config.xml"));
        }

        foreach (string cfg in configPaths)
        {
            if (!File.Exists(cfg)) continue;
            XDocument doc;
            try { doc = XDocument.Parse(ReadTextShared(cfg)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }

            foreach (var fileEl in doc.Descendants("History").Elements("File"))
            {
                string? path = fileEl.Attribute("filename")?.Value;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                yield return new AppRecentItem(
                    Name: Path.GetFileName(path),
                    FullPath: path,
                    Subtitle: Path.GetDirectoryName(path) ?? path,
                    Kind: AppRecentItemKind.File,
                    LastOpenedTime: SafeGetLastWriteTime(path, false));
            }
        }
    }

    private static IEnumerable<AppRecentItem> CollectTyporaRecents(ResolvedAppTarget target, string roamingRoot)
    {
        if (!string.Equals(target.ExecutableName, "typora.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        string historyPath = Path.Combine(roamingRoot, "Typora", "history.data");
        if (!File.Exists(historyPath))
            yield break;

        string? jsonText;
        try
        {
            string rawHex = ReadTextShared(historyPath).Trim();
            jsonText = TryDecodeHexUtf8(rawHex) ?? rawHex;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonText); }
        catch (JsonException) { yield break; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("recentFolder", out var rfArr) && rfArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in rfArr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object ||
                        !el.TryGetProperty("path", out var pEl) ||
                        pEl.ValueKind != JsonValueKind.String)
                        continue;
                    string? path = pEl.GetString();
                    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                    string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (string.IsNullOrWhiteSpace(name)) name = path;
                    yield return new AppRecentItem(
                        Name: name,
                        FullPath: path,
                        Subtitle: Path.GetDirectoryName(path) ?? path,
                        Kind: AppRecentItemKind.Folder,
                        LastOpenedTime: SafeGetLastWriteTime(path, true));
                }
            }
            if (root.TryGetProperty("recentDocument", out var rdArr) && rdArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in rdArr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object ||
                        !el.TryGetProperty("path", out var pEl) ||
                        pEl.ValueKind != JsonValueKind.String)
                        continue;
                    string? path = pEl.GetString();
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                    yield return new AppRecentItem(
                        Name: Path.GetFileName(path),
                        FullPath: path,
                        Subtitle: Path.GetDirectoryName(path) ?? path,
                        Kind: AppRecentItemKind.File,
                        LastOpenedTime: SafeGetLastWriteTime(path, false));
                }
            }
        }
    }

    private static IEnumerable<AppRecentItem> CollectOfficeAndWpsRecents(ResolvedAppTarget target)
    {
        string? officeApp = target.ExecutableName.ToLowerInvariant() switch
        {
            "winword.exe" => "Word",
            "excel.exe" => "Excel",
            "powerpnt.exe" => "PowerPoint",
            "visio.exe" => "Visio",
            "msaccess.exe" => "Access",
            _ => null
        };

        if (officeApp is not null)
        {
            foreach (string ver in new[] { "16.0", "15.0", "14.0" })
            {
                foreach (var item in ReadOfficeRegistryMru($@"Software\Microsoft\Office\{ver}\{officeApp}\File MRU"))
                    yield return item;

                string userMruRoot = $@"Software\Microsoft\Office\{ver}\{officeApp}\User MRU";
                using var uKey = Registry.CurrentUser.OpenSubKey(userMruRoot);
                if (uKey is not null)
                {
                    foreach (string sub in uKey.GetSubKeyNames())
                    {
                        foreach (var item in ReadOfficeRegistryMru($@"{userMruRoot}\{sub}\File MRU"))
                            yield return item;
                    }
                }
            }
        }

        string? wpsSub = target.ExecutableName.ToLowerInvariant() switch
        {
            "wps.exe" => "wps",
            "et.exe" => "et",
            "wpp.exe" => "wpp",
            _ => null
        };
        if (wpsSub is not null)
        {
            using var wpsKey = Registry.CurrentUser.OpenSubKey($@"Software\Kingsoft\Office\6.0\{wpsSub}\RecentFiles\Sequence");
            if (wpsKey is not null)
            {
                foreach (string valName in wpsKey.GetValueNames())
                {
                    if (wpsKey.GetValue(valName) is string path && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        yield return new AppRecentItem(
                            Name: Path.GetFileName(path),
                            FullPath: path,
                            Subtitle: Path.GetDirectoryName(path) ?? path,
                            Kind: AppRecentItemKind.File,
                            LastOpenedTime: SafeGetLastWriteTime(path, false));
                    }
                }
            }
        }
    }

    private static IEnumerable<AppRecentItem> ReadOfficeRegistryMru(string subKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        if (key is null) yield break;

        foreach (string valName in key.GetValueNames())
        {
            if (!valName.StartsWith("Item ", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.GetValue(valName) is not string rawVal) continue;

            int starIdx = rawVal.IndexOf('*');
            if (starIdx < 0 || starIdx + 1 >= rawVal.Length) continue;
            string path = rawVal[(starIdx + 1)..].Trim();
            if (!File.Exists(path)) continue;

            DateTime ts = SafeGetLastWriteTime(path, false);
            var tMatch = Regex.Match(rawVal[..starIdx], @"\[T([0-9A-Fa-f]{16})\]");
            if (tMatch.Success && long.TryParse(tMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long fileTime) && fileTime > 0)
            {
                try { ts = DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); }
                catch (ArgumentOutOfRangeException) { }
            }

            yield return new AppRecentItem(
                Name: Path.GetFileName(path),
                FullPath: path,
                Subtitle: Path.GetDirectoryName(path) ?? path,
                Kind: AppRecentItemKind.File,
                LastOpenedTime: ts);
        }
    }

    // =========================================================================
    // Provider 5: Universal Windows Jump Lists & Shell Recent
    // =========================================================================

    private static IEnumerable<AppRecentItem> CollectWindowsJumpListAndRecent(ResolvedAppTarget target, string roamingRoot)
    {
        string recentRoot = Path.Combine(roamingRoot, "Microsoft", "Windows", "Recent");
        if (!Directory.Exists(recentRoot))
            yield break;

        var candidateHashes = ComputeCandidateJumpListHashes(target);
        string customDestDir = Path.Combine(recentRoot, "CustomDestinations");
        string autoDestDir = Path.Combine(recentRoot, "AutomaticDestinations");

        // A. Scan CustomDestinations (both by candidate hash and by embedded TargetPath matching our executable)
        if (Directory.Exists(customDestDir))
        {
            string[] customFiles;
            try { customFiles = Directory.GetFiles(customDestDir, "*.customDestinations-ms"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { customFiles = []; }

            foreach (string customFile in customFiles)
            {
                string hashPrefix = Path.GetFileNameWithoutExtension(customFile);
                bool hashMatched = candidateHashes.Contains(hashPrefix);

                List<ParsedShortcutLink> links;
                try { links = ParseEmbeddedLinks(ReadFileShared(customFile)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                bool targetMatched = hashMatched || links.Any(l => IsSameExecutable(l.TargetPath, target));
                if (!targetMatched) continue;

                candidateHashes.Add(hashPrefix);
                DateTime fileWrite = File.GetLastWriteTime(customFile);
                int idx = 0;

                foreach (var lnk in links)
                {
                    if (TryExtractRecentFromCustomLink(lnk, fileWrite.AddSeconds(-idx), out var item))
                    {
                        idx++;
                        yield return item;
                    }
                }
            }
        }

        // B. Read AutomaticDestinations for all matched JumpList hashes
        if (Directory.Exists(autoDestDir))
        {
            foreach (string hash in candidateHashes)
            {
                string autoFile = Path.Combine(autoDestDir, hash + ".automaticDestinations-ms");
                if (!File.Exists(autoFile)) continue;

                List<ParsedShortcutLink> links;
                try { links = ParseEmbeddedLinks(ReadFileShared(autoFile)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                DateTime baseTime = File.GetLastWriteTime(autoFile);
                int idx = 0;
                foreach (var lnk in links)
                {
                    string path = lnk.TargetPath;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    bool isDir = Directory.Exists(path);
                    bool isFile = !isDir && File.Exists(path);
                    if (!isDir && !isFile) continue;

                    string ext = Path.GetExtension(path);
                    if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = isDir ? Path.GetFileName(path) : Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(name)) name = path;

                    idx++;
                    yield return new AppRecentItem(
                        Name: name,
                        FullPath: path,
                        Subtitle: Path.GetDirectoryName(path) ?? path,
                        Kind: isDir ? AppRecentItemKind.Folder : AppRecentItemKind.File,
                        LastOpenedTime: baseTime.AddSeconds(-idx));
                }
            }
        }

        // C. Match Windows\Recent\*.lnk by file extension association
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && File.Exists(target.ExecutablePath))
        {
            var supportedExts = GetRegisteredSupportedExtensions(target.ExecutableName);
            List<FileInfo> recentLinks;
            try
            {
                recentLinks = new DirectoryInfo(recentRoot)
                    .GetFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(50)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                recentLinks = [];
            }

            foreach (var lnkInfo in recentLinks)
            {
                string innerName = Path.GetFileNameWithoutExtension(lnkInfo.Name);
                string ext = Path.GetExtension(innerName);
                if (string.IsNullOrWhiteSpace(ext)) continue;

                bool associated = supportedExts.Contains(ext);
                if (!associated)
                {
                    string assocExe = GetDefaultAssociatedExecutable(ext);
                    associated = !string.IsNullOrWhiteSpace(assocExe) && IsSameExecutable(assocExe, target);
                }
                if (!associated) continue;

                List<ParsedShortcutLink> parsed;
                try { parsed = ParseEmbeddedLinks(ReadFileShared(lnkInfo.FullName)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (parsed.Count == 0 || string.IsNullOrWhiteSpace(parsed[0].TargetPath))
                    continue;

                string targetFile = parsed[0].TargetPath;
                if (!File.Exists(targetFile)) continue;

                yield return new AppRecentItem(
                    Name: Path.GetFileName(targetFile),
                    FullPath: targetFile,
                    Subtitle: Path.GetDirectoryName(targetFile) ?? targetFile,
                    Kind: AppRecentItemKind.File,
                    LastOpenedTime: lnkInfo.LastWriteTime);
            }
        }
    }

    private static bool TryExtractRecentFromCustomLink(ParsedShortcutLink lnk, DateTime fallbackTime, out AppRecentItem item)
    {
        item = default;
        string args = lnk.Arguments.Trim();

        // Check --folder-uri / --file-uri
        var uriMatch = Regex.Match(args, @"--(?:folder|file)-uri\s+(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase);
        if (uriMatch.Success)
        {
            string rawUri = !string.IsNullOrEmpty(uriMatch.Groups[1].Value)
                ? uriMatch.Groups[1].Value
                : uriMatch.Groups[2].Value;
            if (TryParseVsCodeUri(rawUri, out var parsedUri))
            {
                item = parsedUri with
                {
                    LastOpenedTime = parsedUri.LastOpenedTime > DateTime.MinValue ? parsedUri.LastOpenedTime : fallbackTime
                };
                return true;
            }
        }

        // Check if arguments or link name is a direct local path
        foreach (string candidate in new[] { TryExtractLocalPath(args), TryExtractLocalPath(lnk.Name) })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            bool isDir = Directory.Exists(candidate);
            bool isFile = !isDir && File.Exists(candidate);
            if (!isDir && !isFile) continue;

            string ext = Path.GetExtension(candidate);
            if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isWs = string.Equals(ext, ".code-workspace", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase);

            AppRecentItemKind kind = isWs
                ? AppRecentItemKind.Workspace
                : isDir
                    ? AppRecentItemKind.Folder
                    : AppRecentItemKind.File;

            string name = isWs ? Path.GetFileNameWithoutExtension(candidate) : Path.GetFileName(candidate);
            if (string.IsNullOrWhiteSpace(name)) name = candidate;

            item = new AppRecentItem(
                Name: name,
                FullPath: candidate,
                Subtitle: Path.GetDirectoryName(candidate) ?? candidate,
                Kind: kind,
                LastOpenedTime: fallbackTime);
            return true;
        }

        return false;
    }

    private static string TryExtractLocalPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string trimmed = input.Trim().Trim('"');
        if (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/'))
            return trimmed.Replace('/', '\\');

        var match = Regex.Match(input, @"""([A-Za-z]:\\[^""]+)""|([A-Za-z]:\\[^\s""]+)");
        if (match.Success)
        {
            return !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
        }
        return "";
    }

    private static HashSet<string> ComputeCandidateJumpListHashes(ResolvedAppTarget target)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(target.AppUserModelId))
            hashes.Add(ComputeJumpListHash(target.AppUserModelId));

        if (!string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            string fullExe = target.ExecutablePath;
            hashes.Add(ComputeJumpListHash(fullExe));

            var knownFolders = new (Environment.SpecialFolder Folder, string GuidStr)[]
            {
                (Environment.SpecialFolder.ProgramFiles, "{6D809377-6AF0-444B-8957-A3773F02200E}"),
                (Environment.SpecialFolder.ProgramFilesX86, "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
                (Environment.SpecialFolder.System, "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
                (Environment.SpecialFolder.Windows, "{F38BF404-1D43-42F2-9305-67DE0B28FC23}"),
                (Environment.SpecialFolder.LocalApplicationData, "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"),
                (Environment.SpecialFolder.ApplicationData, "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}")
            };

            foreach (var (sf, guidStr) in knownFolders)
            {
                string folderPath = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(folderPath) &&
                    fullExe.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    string substituted = guidStr + fullExe[folderPath.Length..];
                    hashes.Add(ComputeJumpListHash(substituted));
                }
            }
        }
        return hashes;
    }

    public static string ComputeJumpListHash(string appIdOrPath)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(appIdOrPath.ToUpperInvariant());
        ulong crc = 0xFFFFFFFFFFFFFFFFUL;
        const ulong poly = 0x92C64265D32139A4UL;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }
        }
        return crc.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static bool IsSameExecutable(string pathA, ResolvedAppTarget target)
    {
        if (string.IsNullOrWhiteSpace(pathA)) return false;
        if (string.Equals(pathA.Trim(), target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return true;
        string nameA = Path.GetFileName(pathA);
        return !string.IsNullOrWhiteSpace(nameA) &&
               string.Equals(nameA, target.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetRegisteredSupportedExtensions(string exeName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(exeName)) return set;

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"Software\Classes\Applications\{exeName}\SupportedTypes");
            if (key is null) continue;
            foreach (string valName in key.GetValueNames())
            {
                if (valName.StartsWith('.'))
                    set.Add(valName);
            }
        }
        return set;
    }

    private static string GetDefaultAssociatedExecutable(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "";
        return ExtensionAssocCache.GetOrAdd(extension, ext =>
        {
            uint cch = 1024;
            var sb = new StringBuilder((int)cch);
            if (AssocQueryStringW(0, 2 /* ASSOCSTR_EXECUTABLE */, ext, null, sb, ref cch) == 0)
                return sb.ToString();
            return "";
        });
    }

    // =========================================================================
    // Binary MS-SHLLINK (.lnk & JumpList) Parser + Start Menu AppID Index
    // =========================================================================

    public static List<ParsedShortcutLink> ParseEmbeddedLinks(byte[] data)
    {
        var list = new List<ParsedShortcutLink>();
        if (data is null || data.Length < 76) return list;

        for (int i = 0; i <= data.Length - 76; i++)
        {
            bool match = true;
            for (int j = 0; j < ShellLinkHeaderSig.Length; j++)
            {
                if (data[i + j] != ShellLinkHeaderSig[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;
            if (TryParseLnkAt(data, i, out var parsed, out int nextPos))
            {
                list.Add(parsed);
                if (nextPos > i + 76)
                    i = nextPos - 1;
            }
        }
        return list;
    }

    private static bool TryParseLnkAt(byte[] data, int offset, out ParsedShortcutLink result, out int endOffset)
    {
        result = new ParsedShortcutLink();
        endOffset = offset + 76;
        try
        {
            uint flags = BitConverter.ToUInt32(data, offset + 20);
            int pos = offset + 76;
            bool isUnicode = (flags & 0x80) != 0;
            string pidlPath = "";

            if ((flags & 0x01) != 0) // HasLinkTargetIDList
            {
                if (pos + 2 > data.Length) return false;
                ushort idListSize = BitConverter.ToUInt16(data, pos);
                pos += 2;
                if (pos + idListSize > data.Length) return false;
                pidlPath = DecodePidlPath(data, pos);
                pos += idListSize;
            }

            if ((flags & 0x02) != 0) // HasLinkInfo
            {
                if (pos + 12 > data.Length) return false;
                uint infoSize = BitConverter.ToUInt32(data, pos);
                if (infoSize < 12 || pos + infoSize > data.Length) return false;
                uint infoHeaderSize = BitConverter.ToUInt32(data, pos + 4);
                uint infoFlags = BitConverter.ToUInt32(data, pos + 8);
                if ((infoFlags & 0x01) != 0) // VolumeIDAndLocalBasePath
                {
                    if (infoHeaderSize >= 0x24 && pos + 32 <= data.Length)
                    {
                        uint uniOffset = BitConverter.ToUInt32(data, pos + 28);
                        if (uniOffset > 0 && pos + uniOffset < pos + infoSize)
                            result.TargetPath = ReadNullTerminatedUnicode(data, (int)(pos + uniOffset));
                    }
                    if (string.IsNullOrWhiteSpace(result.TargetPath) && pos + 20 <= data.Length)
                    {
                        uint ansiOffset = BitConverter.ToUInt32(data, pos + 16);
                        if (ansiOffset > 0 && pos + ansiOffset < pos + infoSize)
                            result.TargetPath = ReadNullTerminatedAnsi(data, (int)(pos + ansiOffset));
                    }
                }
                pos += (int)infoSize;
            }

            if (!string.IsNullOrWhiteSpace(pidlPath) &&
                (string.IsNullOrWhiteSpace(result.TargetPath) || (!File.Exists(result.TargetPath) && !Directory.Exists(result.TargetPath))))
            {
                result.TargetPath = pidlPath;
            }

            if ((flags & 0x04) != 0) result.Name = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x08) != 0) result.RelativePath = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x10) != 0) result.WorkingDirectory = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x20) != 0) result.Arguments = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x40) != 0) result.IconLocation = ReadCountedString(data, ref pos, isUnicode);

            // Parse ExtraData blocks (EnvironmentVariableDataBlock & PropertyStoreDataBlock)
            while (pos + 4 <= data.Length)
            {
                uint blockSize = BitConverter.ToUInt32(data, pos);
                if (blockSize < 4 || pos + blockSize > data.Length)
                    break;
                if (pos + 8 > data.Length)
                    break;
                uint blockSig = BitConverter.ToUInt32(data, pos + 4);
                if (blockSig == 0xA0000001 && blockSize >= 788 && string.IsNullOrWhiteSpace(result.TargetPath))
                {
                    string envPath = ReadNullTerminatedUnicode(data, pos + 268);
                    if (!string.IsNullOrWhiteSpace(envPath))
                        result.TargetPath = Environment.ExpandEnvironmentVariables(envPath);
                }
                else if (blockSig == 0xA0000009)
                {
                    string appId = ExtractAppUserModelIdFromBuffer(data, pos + 8, (int)(blockSize - 8));
                    if (!string.IsNullOrWhiteSpace(appId))
                        result.AppUserModelId = appId;
                }
                pos += (int)blockSize;
            }

            endOffset = pos;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ExtractAppUserModelIdFromBuffer(byte[] data, int start, int length)
    {
        int end = Math.Min(data.Length, start + length);
        for (int i = start; i <= end - 28; i++)
        {
            bool match = true;
            for (int j = 0; j < 16; j++)
            {
                if (data[i + j] != AppUserModelIdFmtId[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;

            int pos = i + 16;
            while (pos + 13 < end)
            {
                uint valSize = BitConverter.ToUInt32(data, pos);
                if (valSize < 13 || pos + valSize > end) break;
                uint propId = BitConverter.ToUInt32(data, pos + 4);
                ushort vt = BitConverter.ToUInt16(data, pos + 9);
                if (propId == 5 && vt == 31 && pos + 17 <= end)
                {
                    uint cch = BitConverter.ToUInt32(data, pos + 13);
                    int byteLen = (int)Math.Min(cch * 2, valSize - 17);
                    if (byteLen > 0 && pos + 17 + byteLen <= end)
                        return Encoding.Unicode.GetString(data, pos + 17, byteLen).TrimEnd('\0');
                }
                pos += (int)valSize;
            }
        }
        return "";
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
                    var parsed = ParseEmbeddedLinks(ReadFileShared(lnkFile));
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

    private static string DecodePidlPath(byte[] data, int offset)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = IntPtr.Add(handle.AddrOfPinnedObject(), offset);
            var sb = new StringBuilder(1024);
            return SHGetPathFromIDListW(ptr, sb) ? sb.ToString() : "";
        }
        finally
        {
            handle.Free();
        }
    }

    private static string ReadCountedString(byte[] data, ref int pos, bool isUnicode)
    {
        if (pos + 2 > data.Length) return "";
        ushort count = BitConverter.ToUInt16(data, pos);
        pos += 2;
        int byteCount = isUnicode ? count * 2 : count;
        if (pos + byteCount > data.Length)
        {
            pos = data.Length;
            return "";
        }
        string s = isUnicode
            ? Encoding.Unicode.GetString(data, pos, byteCount)
            : Encoding.Default.GetString(data, pos, byteCount);
        pos += byteCount;
        return s.TrimEnd('\0');
    }

    private static string ReadNullTerminatedUnicode(byte[] data, int pos)
    {
        int end = pos;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
            end += 2;
        return end > pos ? Encoding.Unicode.GetString(data, pos, end - pos) : "";
    }

    private static string ReadNullTerminatedAnsi(byte[] data, int pos)
    {
        int end = pos;
        while (end < data.Length && data[end] != 0)
            end++;
        return end > pos ? Encoding.Default.GetString(data, pos, end - pos) : "";
    }

    // =========================================================================
    // WinSQLite3 Interop (Built into Windows 10/11 System32\winsqlite3.dll)
    // =========================================================================

    private static string? TryQuerySqliteSingleValue(string dbPath, string sql)
    {
        try
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
            if (sqlite3_open_v2(pathBytes, out IntPtr db, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero) != 0)
                return null;
            try
            {
                byte[] sqlBytes = Encoding.UTF8.GetBytes(sql + "\0");
                if (sqlite3_prepare_v2(db, sqlBytes, sqlBytes.Length, out IntPtr stmt, IntPtr.Zero) != 0)
                    return null;
                try
                {
                    if (sqlite3_step(stmt) == 100 /* SQLITE_ROW */)
                    {
                        IntPtr textPtr = sqlite3_column_text(stmt, 0);
                        int byteLen = sqlite3_column_bytes(stmt, 0);
                        if (textPtr == IntPtr.Zero || byteLen <= 0)
                            return "";
                        byte[] buf = new byte[byteLen];
                        Marshal.Copy(textPtr, buf, 0, byteLen);
                        return Encoding.UTF8.GetString(buf);
                    }
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close(db);
            }
        }
        catch (DllNotFoundException)
        {
        }
        return null;
    }

    private static byte[] ReadFileShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream((int)Math.Min(fs.Length, 16 * 1024 * 1024));
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadTextShared(string path) =>
        Encoding.UTF8.GetString(ReadFileShared(path));

    private static DateTime SafeGetLastWriteTime(string path, bool isDirectory)
    {
        try
        {
            return isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static string NormalizeItemKey(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr zVfs);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close(IntPtr db);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(IntPtr db, byte[] zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(IntPtr stmt);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr sqlite3_column_text(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_column_bytes(IntPtr stmt, int iCol);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(IntPtr stmt);
}
