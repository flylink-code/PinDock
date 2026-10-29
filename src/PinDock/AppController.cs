using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace PinDock;

public sealed class AppController : IDisposable
{
    private readonly LibraryStore store;
    private readonly ShellIconService icons = new();
    private readonly ThemeService theme = new();
    private readonly HotkeyService hotkeys = new();
    private readonly TrayService tray = new();
    private readonly FullscreenWatcher fullscreen = new();
    private readonly AutoHideController autoHide = new();
    private readonly DockWindow dock;
    private readonly EdgeHandleWindow edgeHandle = new();
    private readonly WebPanelWindow webPanel;
    private readonly TrayFlyoutWindow trayFlyout;
    private DisplayMode appliedMode;
    private bool dialogOpen;
    private bool suppressedByFullscreen;
    // 常驻模式下表示“被快捷键临时收起”，快捷键呼出模式下表示“被快捷键呼出”。
    private bool toggled;

    public LibraryData Data { get; }
    public InstalledAppsService InstalledApps { get; } = new();
    public IReadOnlyList<Bookmark> OverflowItems => dock.OverflowItems;
    public bool IsDockVisible => dock.IsShown;

    public AppController(string? initialAddPath = null)
    {
        store = new LibraryStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinDock"));
        try { Data = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or UnsupportedLibraryVersionException)
        {
            MessageBox.Show($"无法读取收藏，未覆盖配置：{ex.Message}");
            throw;
        }

        theme.Apply(Data.Settings.ThemeMode, Data.Settings.Palette);
        dock = new DockWindow(icons);
        webPanel = new WebPanelWindow(this, icons);
        trayFlyout = new TrayFlyoutWindow(this, icons);
        webPanel.Prewarm();

        dock.LaunchRequested += bookmark => _ = LaunchAsync(bookmark);
        dock.EditRequested += EditBookmarkInteractive;
        dock.UnpinRequested += TogglePin;
        dock.RemoveRequested += RemoveBookmarkWithConfirm;
        dock.ConfigureGroupRequested += groupName => webPanel.ShowGroupModal(groupName);
        dock.LibraryRequested += () => webPanel.ShowLibrary();
        dock.InstalledAppsRequested += () => webPanel.ShowLibrary(focusInstalledApps: true);
        dock.OverflowRequested += () => webPanel.ShowLibrary(true);
        dock.AddRequested += AddBookmarkInteractive;
        dock.AddFolderRequested += AddFolderInteractive;
        dock.AddFileRequested += AddFileInteractive;
        dock.FilesDropped += AddPaths;
        dock.SettingsRequested += () => webPanel.ShowSettings();
        dock.ExitRequested += RequestExitApplication;
        dock.DragEnded += () =>
        {
            Persist();
            webPanel.RefreshIfVisible();
        };

        edgeHandle.Hovered += () => autoHide.Reveal(false);
        autoHide.Configure(
            () => dock.IsGroupOpen || trayFlyout.IsVisible,
            () => dock.IsDragging,
            () => dialogOpen || webPanel.IsVisible,
            () => dock.HitArea,
            () => dock.GroupBounds);
        autoHide.StateChanged += _ => SyncVisuals();

        tray.FlyoutRequested += () => trayFlyout.ToggleFlyout();
        tray.ToggleRequested += ToggleVisibility;
        tray.LibraryRequested += () => webPanel.ShowLibrary();
        tray.SettingsRequested += () => webPanel.ShowSettings();
        tray.ExitRequested += RequestExitApplication;

        hotkeys.Pressed += ToggleVisibility;
        fullscreen.Changed += OnFullscreenChanged;
        theme.Changed += () =>
        {
            RelayoutDock();
            webPanel.RefreshIfVisible();
            trayFlyout.RefreshIfVisible();
        };

        dock.Bind(Data);
        dock.Relayout();
        ApplyDisplayMode();
        RevealDock();
        RegisterHotkey();
        fullscreen.SetEnabled(Data.Settings.AvoidFullscreen);

        if (!string.IsNullOrWhiteSpace(initialAddPath))
            AddPaths([initialAddPath]);
    }

    public void ToggleDockFromFlyout() => ToggleVisibility();

    public void OpenEditorForNew() => webPanel.ShowEditor(null);

    public void OpenLibraryPanel() => webPanel.ShowLibrary();

    public void OpenSettingsPanel(string? page = null) => webPanel.ShowSettings(page);

    public void RequestExitApplication()
    {
        if (Data.Settings.ConfirmBeforeExit)
        {
            dialogOpen = true;
            try
            {
                if (MessageBox.Show("确定要退出 PinDock 吗？", "退出 PinDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
            }
            finally
            {
                dialogOpen = false;
            }
        }
        Application.Current.Shutdown();
    }

    public void UpdateOpacityOnly()
    {
        dock.Bind(Data);
        Persist();
    }

    public void UpdateHotkey()
    {
        RegisterHotkey();
        Persist();
    }

    public void RevealDock()
    {
        if (appliedMode == DisplayMode.EdgeAutoHide)
        {
            autoHide.Reveal(true);
            return;
        }
        toggled = appliedMode == DisplayMode.Hotkey;
        SyncVisuals();
    }

    public void ApplySettings()
    {
        dock.Bind(Data);
        theme.Apply(Data.Settings.ThemeMode, Data.Settings.Palette);
        edgeHandle.Topmost = Data.Settings.Topmost;
        fullscreen.SetEnabled(Data.Settings.AvoidFullscreen);
        dock.Relayout();
        if (Data.Settings.DisplayMode != appliedMode) ApplyDisplayMode();
        else SyncVisuals();
        Persist();
    }

    public async Task LaunchAsync(Bookmark item)
    {
        try { await Task.Run(() => BookmarkLauncher.Open(item)); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show($"无法打开收藏“{item.Name}”（{item.Path}）：{ex.Message}");
            return;
        }
        if (Data.Items.Contains(item))
        {
            item.LastLaunchedTicks = DateTime.UtcNow.Ticks;
            item.LaunchCount++;
            Persist();
        }
        if (!Data.Settings.CollapseAfterLaunch) return;
        if (appliedMode == DisplayMode.EdgeAutoHide) autoHide.HideNow();
        else if (appliedMode == DisplayMode.Hotkey)
        {
            toggled = false;
            SyncVisuals();
        }
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var items = new List<Bookmark>(Data.Items);
        int duplicates = 0;
        foreach (string rawPath in paths)
        {
            string path = rawPath.Trim();
            if (path.Length == 0) continue;
            var item = new Bookmark
            {
                Name = BookmarkEditor.SuggestDisplayName(path),
                Path = path,
                Pinned = true
            };
            if (!LibraryStore.IsDuplicate(items, item)) items.Add(item);
            else duplicates++;
        }
        if (items.Count != Data.Items.Count) ReplaceItems(items);
        if (duplicates > 0) MessageBox.Show($"{duplicates} 项已收藏，未重复添加。");
    }

    public void SaveEdited(Bookmark result, Bookmark? original)
    {
        var items = new List<Bookmark>(Data.Items);
        int index = original is null ? items.Count : items.IndexOf(original);
        if (original is not null) items.Remove(original);
        if (LibraryStore.IsDuplicate(items, result)) { MessageBox.Show("该目标及启动配置已收藏。"); return; }
        items.Insert(index, result);
        if (result.Group.Length > 0 && !Data.Groups.Contains(result.Group, StringComparer.Ordinal))
            Data.Groups.Add(result.Group);
        ReplaceItems(items);
    }

    public void Remove(Bookmark item)
    {
        var items = new List<Bookmark>(Data.Items);
        items.Remove(item);
        ReplaceItems(items);
    }

    public void TogglePin(Bookmark item)
    {
        item.Pinned = !item.Pinned;
        Persist();
        RelayoutDock();
    }

    public void AddGroup(string name)
    {
        if (Data.Groups.Contains(name, StringComparer.Ordinal)) return;
        Data.Groups.Add(name);
        Persist();
        RelayoutDock();
    }

    public void SetBookmarkGroup(Bookmark item, string group)
    {
        item.Group = group;
        if (group.Length > 0 && !Data.Groups.Contains(group, StringComparer.Ordinal))
            Data.Groups.Add(group);
        Persist();
        RelayoutDock();
        webPanel.RefreshIfVisible();
    }

    public void SaveGroupConfig(string originalName, string newName, IReadOnlySet<Guid> memberIds, GroupIconConfig? iconConfig = null)
    {
        if (!string.IsNullOrEmpty(originalName) && !string.Equals(originalName, newName, StringComparison.Ordinal))
        {
            int idx = Data.Groups.IndexOf(originalName);
            if (idx >= 0) Data.Groups[idx] = newName;
            else if (!Data.Groups.Contains(newName, StringComparer.Ordinal)) Data.Groups.Add(newName);

            if (Data.GroupIcons.Remove(originalName, out var oldIcon))
                Data.GroupIcons[newName] = oldIcon;
            for (int i = 0; i < Data.DockOrder.Count; i++)
            {
                if (string.Equals(Data.DockOrder[i], "grp:" + originalName, StringComparison.Ordinal))
                    Data.DockOrder[i] = "grp:" + newName;
            }
        }
        else if (!Data.Groups.Contains(newName, StringComparer.Ordinal))
        {
            Data.Groups.Add(newName);
        }

        if (iconConfig is not null)
        {
            Data.GroupIcons[newName] = iconConfig;
        }

        foreach (var item in Data.Items)
        {
            if (memberIds.Contains(item.Id))
            {
                item.Group = newName;
            }
            else if ((!string.IsNullOrEmpty(originalName) && item.Group == originalName) || item.Group == newName)
            {
                item.Group = "";
            }
        }

        Data.NormalizeDockOrder();
        Persist();
        RelayoutDock();
        webPanel.RefreshIfVisible();
        trayFlyout.RefreshIfVisible();
    }

    public void AutoSeparateDockItems()
    {
        Data.AutoSeparateByKind();
        Persist();
        RelayoutDock();
        webPanel.RefreshIfVisible();
    }

    public void DeleteGroup(string groupName)
    {
        LibraryTransfer.RemoveGroup(Data, groupName);
        Persist();
        RelayoutDock();
        webPanel.RefreshIfVisible();
    }

    public (int Added, int Skipped) Import(string path)
    {
        var result = LibraryTransfer.Import(Data, path);
        Persist();
        RelayoutDock();
        webPanel.RefreshIfVisible();
        return result;
    }

    public void Export(string path) => LibraryTransfer.Export(Data, path);

    public void Dispose()
    {
        autoHide.Dispose();
        fullscreen.Dispose();
        hotkeys.Dispose();
        tray.Dispose();
        theme.Dispose();
    }

    private void AddBookmarkInteractive() => webPanel.ShowEditor(null);

    private void EditBookmarkInteractive(Bookmark target) => webPanel.ShowEditor(target);

    private void AddFolderInteractive()
    {
        dialogOpen = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择要添加到 PinDock 的文件夹",
                Multiselect = true
            };
            if (dialog.ShowDialog() == true) AddPaths(dialog.FolderNames);
        }
        finally { dialogOpen = false; }
    }

    private void AddFileInteractive()
    {
        dialogOpen = true;
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要添加到 PinDock 的应用或文件",
                Filter = "常用目标 (*.exe;*.lnk;*.bat;*.cmd;*.*)|*.exe;*.lnk;*.bat;*.cmd;*.*|所有文件 (*.*)|*.*",
                Multiselect = true,
                DereferenceLinks = false
            };
            if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
        }
        finally { dialogOpen = false; }
    }

    private void RemoveBookmarkWithConfirm(Bookmark item)
    {
        dialogOpen = true;
        try
        {
            if (MessageBox.Show($"移除收藏“{item.Name}”？不会删除目标文件。", "PinDock", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                Remove(item);
        }
        finally { dialogOpen = false; }
    }

    private void ReplaceItems(List<Bookmark> items)
    {
        Data.Items = items;
        Persist();
        RelayoutDock();
        webPanel.RefreshIfVisible();
    }

    private void RelayoutDock()
    {
        dock.Relayout();
        SyncVisuals();
    }

    private void Persist()
    {
        try { store.Save(Data); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"保存失败，收藏未更改：{ex.Message}");
        }
    }

    private void ApplyDisplayMode()
    {
        appliedMode = Data.Settings.DisplayMode;
        toggled = false;
        autoHide.SetEnabled(appliedMode == DisplayMode.EdgeAutoHide);
        SyncVisuals();
    }

    private void SyncVisuals()
    {
        if (suppressedByFullscreen)
        {
            dock.HideDock();
            edgeHandle.Hide();
            return;
        }
        switch (appliedMode)
        {
            case DisplayMode.Persistent:
                edgeHandle.Hide();
                if (toggled) dock.HideDock();
                else dock.ShowDock();
                break;
            case DisplayMode.Hotkey:
                edgeHandle.Hide();
                if (toggled) dock.ShowDock();
                else dock.HideDock();
                break;
            case DisplayMode.EdgeAutoHide:
                if (autoHide.State == AutoHideState.Visible)
                {
                    edgeHandle.Hide();
                    dock.ShowDock();
                }
                else
                {
                    dock.HideDock();
                    edgeHandle.ShowAt(dock.EdgeHandleRect, Data.Settings.Topmost);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(appliedMode), appliedMode, null);
        }
    }

    private void ToggleVisibility()
    {
        if (appliedMode == DisplayMode.EdgeAutoHide)
        {
            if (autoHide.State == AutoHideState.Visible) autoHide.HideNow();
            else autoHide.Reveal(true);
            return;
        }
        toggled = !toggled;
        bool showing = appliedMode == DisplayMode.Persistent ? !toggled : toggled;
        if (showing && Data.Settings.MonitorPolicy == MonitorPolicy.FollowCursor)
            dock.Relayout();
        SyncVisuals();
    }

    private void OnFullscreenChanged(bool isFullscreen)
    {
        suppressedByFullscreen = isFullscreen && Data.Settings.AvoidFullscreen;
        SyncVisuals();
    }

    private void RegisterHotkey()
    {
        try { hotkeys.Register(Data.Settings.HotkeyModifiers, Data.Settings.HotkeyKey); }
        catch (InvalidOperationException ex) { MessageBox.Show(ex.Message); }
    }
}
