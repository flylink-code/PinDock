using System.IO;
using System.Text.Json;

namespace PinDock;

public enum GroupIconKind { DefaultGrid, MemberPreview, Preset, CustomFile }

public sealed class GroupIconConfig
{
    public GroupIconKind Kind { get; set; } = GroupIconKind.DefaultGrid;
    public string Value { get; set; } = "";
}

public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Group { get; set; } = "";
    public bool Pinned { get; set; }
    public long LastLaunchedTicks { get; set; }
    public int LaunchCount { get; set; }
}

public sealed class LibraryData
{
    public int Version { get; set; } = 2;
    public List<Bookmark> Items { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public Dictionary<string, GroupIconConfig> GroupIcons { get; set; } = new(StringComparer.Ordinal);
    public List<string> DockOrder { get; set; } = [];
    public List<string> HiddenFolderPaths { get; set; } = [];
    public AppSettings Settings { get; set; } = new();

    public bool IsFolderHidden(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || HiddenFolderPaths is null) return false;
        string normalized = NormalizePathKey(fullPath);
        return HiddenFolderPaths.Any(p => string.Equals(NormalizePathKey(p), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void SetFolderHidden(string fullPath, bool hidden)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        HiddenFolderPaths ??= [];
        string normalized = NormalizePathKey(fullPath);
        HiddenFolderPaths.RemoveAll(p => string.Equals(NormalizePathKey(p), normalized, StringComparison.OrdinalIgnoreCase));
        if (hidden)
            HiddenFolderPaths.Add(normalized);
    }

    private static string NormalizePathKey(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public void NormalizeDockOrder()
    {
        DockOrder ??= [];
        GroupIcons ??= new(StringComparer.Ordinal);
        HiddenFolderPaths ??= [];

        var pinnedIds = Items.Where(i => i.Pinned).Select(i => "bm:" + i.Id.ToString("D")).ToList();
        var pinnedSet = new HashSet<string>(pinnedIds, StringComparer.OrdinalIgnoreCase);

        var activeGroups = Groups
            .Where(g => Items.Any(i => string.Equals(i.Group, g, StringComparison.Ordinal)))
            .Select(g => "grp:" + g)
            .ToList();
        var groupSet = new HashSet<string>(activeGroups, StringComparer.Ordinal);

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string raw in DockOrder)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.StartsWith("bm:", StringComparison.OrdinalIgnoreCase))
            {
                string key = "bm:" + raw[3..].ToLowerInvariant();
                if (pinnedSet.Contains(key) && seen.Add(key))
                    normalized.Add(key);
            }
            else if (raw.StartsWith("grp:", StringComparison.Ordinal))
            {
                if (groupSet.Contains(raw) && seen.Add(raw))
                    normalized.Add(raw);
            }
            else if (raw.StartsWith("sep:", StringComparison.OrdinalIgnoreCase))
            {
                if (seen.Add(raw))
                    normalized.Add(raw);
            }
        }

        // 若此前还没有分组但现在有了分组，且在固定项与分组之间没有自定义分隔符，则在首次追加分组前可保持连贯顺序
        foreach (string pid in pinnedIds)
        {
            if (seen.Add(pid))
                normalized.Add(pid);
        }
        foreach (string gid in activeGroups)
        {
            if (seen.Add(gid))
                normalized.Add(gid);
        }

        DockOrder = normalized;
    }

    public void AutoSeparateByKind()
    {
        NormalizeDockOrder();
        var pinned = Items.Where(i => i.Pinned).ToList();
        var apps = pinned.Where(i => BookmarkKindHelper.FromPath(i.Path) == BookmarkKind.Application).ToList();
        var folders = pinned.Where(i => BookmarkKindHelper.FromPath(i.Path) == BookmarkKind.Folder).ToList();
        var files = pinned.Where(i => BookmarkKindHelper.FromPath(i.Path) == BookmarkKind.File).ToList();
        var activeGroups = Groups.Where(g => Items.Any(i => string.Equals(i.Group, g, StringComparison.Ordinal))).ToList();

        var order = new List<string>();
        void AppendSection(IEnumerable<string> tokens)
        {
            var list = tokens.ToList();
            if (list.Count == 0) return;
            if (order.Count > 0) order.Add("sep:" + Guid.NewGuid().ToString("N")[..8]);
            order.AddRange(list);
        }

        AppendSection(apps.Select(i => "bm:" + i.Id.ToString("D")));
        AppendSection(folders.Select(i => "bm:" + i.Id.ToString("D")));
        AppendSection(files.Select(i => "bm:" + i.Id.ToString("D")));
        AppendSection(activeGroups.Select(g => "grp:" + g));

        DockOrder = order;
    }
}

public sealed class UnsupportedLibraryVersionException : Exception
{
    public UnsupportedLibraryVersionException(int version) : base($"Unsupported library version: {version}") { }
}

public sealed class LibraryStore(string directory)
{
    private readonly string path = Path.Combine(directory, "library.json");
    private bool recoveredFromBackup;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public LibraryData Load()
    {
        if (!File.Exists(path)) return new();
        try { return Read(path); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            if (!File.Exists(path + ".bak")) throw;
            var backup = Read(path + ".bak");
            recoveredFromBackup = true;
            return backup;
        }
    }

    private static LibraryData Read(string file)
    {
        var data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(file), Options) ?? throw new InvalidDataException("Empty library");
        if (data.Version is not (1 or 2)) throw new UnsupportedLibraryVersionException(data.Version);
        if (data.Items is null || data.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Path) || item.Name is null || item.Arguments is null || item.WorkingDirectory is null || item.Group is null))
            throw new InvalidDataException("Invalid library items");
        data.Groups ??= [];
        data.GroupIcons ??= new(StringComparer.Ordinal);
        data.DockOrder ??= [];
        data.HiddenFolderPaths ??= [];
        data.Settings ??= new();
        ValidateSettings(data.Settings);
        if (data.Version == 1)
        {
            data.Version = 2;
            foreach (var item in data.Items)
                item.Group ??= "";
        }
        data.NormalizeDockOrder();
        return data;
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.Opacity < AppSettings.MinOpacity || settings.Opacity > AppSettings.MaxOpacity)
            throw new InvalidDataException($"Opacity out of range: {settings.Opacity}");
        if (!Enum.IsDefined(settings.DisplayMode) || !Enum.IsDefined(settings.DockEdge) || !Enum.IsDefined(settings.ThemeMode) ||
            !Enum.IsDefined(settings.Palette) || !Enum.IsDefined(settings.MonitorPolicy) || !Enum.IsDefined(settings.IconSize) ||
            !Enum.IsDefined(settings.ClosePanelBehavior))
            throw new InvalidDataException("Invalid settings enum value");
        if (settings.HotkeyModifiers is null || settings.HotkeyKey is null)
            throw new InvalidDataException("Hotkey settings missing");
        settings.GithubMirrorUrl ??= AppSettings.DefaultGithubMirror;
        settings.Language = string.IsNullOrWhiteSpace(settings.Language) ? "zh-CN" : settings.Language;
    }

    public void Save(LibraryData data)
    {
        data.Version = 2;
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Options));
        if (File.Exists(path) && recoveredFromBackup) File.Move(temp, path, true);
        else if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
        else File.Move(temp, path);
        recoveredFromBackup = false;
    }

    public static bool IsDuplicate(IEnumerable<Bookmark> items, Bookmark item) => items.Any(x =>
        string.Equals(x.Path.Trim(), item.Path.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Arguments, item.Arguments, StringComparison.Ordinal) &&
        string.Equals(x.WorkingDirectory.Trim(), item.WorkingDirectory.Trim(), StringComparison.OrdinalIgnoreCase));
}
