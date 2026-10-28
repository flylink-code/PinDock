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
    private readonly LibraryWindow library;
    private readonly SettingsWindow settings;
    private DisplayMode appliedMode;
    private bool dialogOpen;
    private bool suppressedByFullscreen;
    // 常驻模式下表示“被快捷键临时收起”，快捷键呼出模式下表示“被快捷键呼出”。
    private bool toggled;

    public LibraryData Data { get; }
    public IReadOnlyList<Bookmark> OverflowItems => dock.OverflowItems;

    public AppController()
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
        library = new LibraryWindow(this, icons);
        settings = new SettingsWindow(this);

        dock.LaunchRequested += bookmark => _ = LaunchAsync(bookmark);
        dock.LibraryRequested += () => library.ShowLibrary();
        dock.OverflowRequested += () => library.ShowLibrary(true);
        dock.AddRequested += AddBookmarkInteractive;
        dock.SettingsRequested += () => settings.ShowSettings();
        dock.DragEnded += Persist;

        edgeHandle.Hovered += () => autoHide.Reveal(false);
        autoHide.Configure(
            () => dock.IsGroupOpen,
            () => dock.IsDragging,
            () => dialogOpen || library.IsVisible || settings.IsVisible,
            () => dock.HitArea,
            () => dock.GroupBounds);
        autoHide.StateChanged += _ => SyncVisuals();

        tray.ToggleRequested += ToggleVisibility;
        tray.LibraryRequested += () => library.ShowLibrary();
        tray.SettingsRequested += () => settings.ShowSettings();
        tray.ExitRequested += () => Application.Current.Shutdown();

        hotkeys.Pressed += ToggleVisibility;
        fullscreen.Changed += OnFullscreenChanged;
        theme.Changed += RelayoutDock;

        dock.Bind(Data);
        dock.Relayout();
        ApplyDisplayMode();
        RegisterHotkey();
        fullscreen.SetEnabled(Data.Settings.AvoidFullscreen);
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
        foreach (string path in paths)
        {
            var item = new Bookmark { Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)), Path = path, Pinned = true };
            if (string.IsNullOrWhiteSpace(item.Name)) item.Name = path;
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

    public (int Added, int Skipped) Import(string path)
    {
        var result = LibraryTransfer.Import(Data, path);
        Persist();
        RelayoutDock();
        library.Refresh();
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

    private void AddBookmarkInteractive()
    {
        dialogOpen = true;
        try
        {
            var editor = new BookmarkEditor(new Bookmark { Pinned = true }, Data.Groups) { Owner = library.IsVisible ? library : null, Title = "添加收藏" };
            if (editor.ShowDialog() == true) SaveEdited(editor.Result, null);
        }
        finally { dialogOpen = false; }
    }

    private void ReplaceItems(List<Bookmark> items)
    {
        Data.Items = items;
        Persist();
        RelayoutDock();
        if (library.IsVisible) library.Refresh();
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
