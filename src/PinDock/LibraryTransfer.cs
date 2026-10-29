using System.IO;
using System.Text.Json;

namespace PinDock;

public static class LibraryTransfer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Export(LibraryData data, string filePath)
    {
        var copy = new LibraryData
        {
            Version = 2,
            Items = data.Items.Select(Clone).ToList(),
            Groups = [.. data.Groups],
            GroupIcons = data.GroupIcons.ToDictionary(k => k.Key, v => new GroupIconConfig { Kind = v.Value.Kind, Value = v.Value.Value }, StringComparer.Ordinal),
            DockOrder = [.. data.DockOrder],
            Settings = CloneSettings(data.Settings)
        };
        File.WriteAllText(filePath, JsonSerializer.Serialize(copy, Options));
    }

    public static (int Added, int Skipped) Import(LibraryData target, string filePath)
    {
        var imported = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(filePath), Options)
            ?? throw new InvalidDataException($"Empty import file: {filePath}");
        if (imported.Version is not (1 or 2))
            throw new UnsupportedLibraryVersionException(imported.Version);
        if (imported.Items is null)
            throw new InvalidDataException($"Import file has no items: {filePath}");

        int added = 0, skipped = 0;
        var items = new List<Bookmark>(target.Items);
        foreach (var item in imported.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Path))
                throw new InvalidDataException($"Invalid bookmark in import: {filePath}");
            item.Group ??= "";
            item.Name ??= Path.GetFileName(item.Path.TrimEnd('\\', '/'));
            item.Arguments ??= "";
            item.WorkingDirectory ??= "";
            if (LibraryStore.IsDuplicate(items, item)) { skipped++; continue; }
            items.Add(new Bookmark
            {
                Id = Guid.NewGuid(),
                Name = item.Name.Length == 0 ? Path.GetFileName(item.Path.TrimEnd('\\', '/')) : item.Name,
                Path = item.Path,
                Arguments = item.Arguments,
                WorkingDirectory = item.WorkingDirectory,
                Group = item.Group,
                Pinned = item.Pinned,
                LastLaunchedTicks = item.LastLaunchedTicks,
                LaunchCount = item.LaunchCount
            });
            if (item.Group.Length > 0 && !target.Groups.Contains(item.Group, StringComparer.Ordinal))
                target.Groups.Add(item.Group);
            added++;
        }
        if (imported.GroupIcons is not null)
        {
            foreach (var kv in imported.GroupIcons)
            {
                if (!target.GroupIcons.ContainsKey(kv.Key))
                    target.GroupIcons[kv.Key] = new GroupIconConfig { Kind = kv.Value.Kind, Value = kv.Value.Value ?? "" };
            }
        }
        target.Items = items;
        target.NormalizeDockOrder();
        return (added, skipped);
    }

    public static void RemoveGroup(LibraryData data, string group)
    {
        data.Groups.RemoveAll(g => string.Equals(g, group, StringComparison.Ordinal));
        data.GroupIcons.Remove(group);
        data.DockOrder.RemoveAll(t => string.Equals(t, "grp:" + group, StringComparison.Ordinal));
        foreach (var item in data.Items.Where(i => string.Equals(i.Group, group, StringComparison.Ordinal)))
            item.Group = "";
    }

    public static void RenameGroup(LibraryData data, string oldName, string newName)
    {
        int index = data.Groups.FindIndex(g => string.Equals(g, oldName, StringComparison.Ordinal));
        if (index < 0) return;
        data.Groups[index] = newName;
        if (data.GroupIcons.Remove(oldName, out var iconCfg))
            data.GroupIcons[newName] = iconCfg;
        for (int i = 0; i < data.DockOrder.Count; i++)
        {
            if (string.Equals(data.DockOrder[i], "grp:" + oldName, StringComparison.Ordinal))
                data.DockOrder[i] = "grp:" + newName;
        }
        foreach (var item in data.Items.Where(i => string.Equals(i.Group, oldName, StringComparison.Ordinal)))
            item.Group = newName;
    }

    private static Bookmark Clone(Bookmark item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Path = item.Path,
        Arguments = item.Arguments,
        WorkingDirectory = item.WorkingDirectory,
        Group = item.Group,
        Pinned = item.Pinned,
        LastLaunchedTicks = item.LastLaunchedTicks,
        LaunchCount = item.LaunchCount
    };

    private static AppSettings CloneSettings(AppSettings s) => new()
    {
        DisplayMode = s.DisplayMode,
        DockEdge = s.DockEdge,
        ThemeMode = s.ThemeMode,
        Palette = s.Palette,
        MonitorPolicy = s.MonitorPolicy,
        IconSize = s.IconSize,
        Opacity = s.Opacity,
        Topmost = s.Topmost,
        AvoidFullscreen = s.AvoidFullscreen,
        LockPosition = s.LockPosition,
        CollapseAfterLaunch = s.CollapseAfterLaunch,
        EdgeOffset = s.EdgeOffset,
        MonitorDeviceName = s.MonitorDeviceName,
        ExplorerContextMenu = s.ExplorerContextMenu,
        HotkeyModifiers = s.HotkeyModifiers,
        HotkeyKey = s.HotkeyKey,
        ShowLibraryButton = s.ShowLibraryButton,
        ShowAddButton = s.ShowAddButton,
        ShowSettingsButton = s.ShowSettingsButton,
        AutoHideUtilityButtons = s.AutoHideUtilityButtons,
        LaunchAtStartup = s.LaunchAtStartup,
        ClosePanelBehavior = s.ClosePanelBehavior,
        ConfirmBeforeExit = s.ConfirmBeforeExit,
        UseGithubMirror = s.UseGithubMirror,
        GithubMirrorUrl = s.GithubMirrorUrl
    };
}
