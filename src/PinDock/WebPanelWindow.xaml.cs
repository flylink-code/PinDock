using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace PinDock;

public partial class WebPanelWindow : Window
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly AppController controller;
    private readonly ShellIconService icons;
    private Task? initTask;
    private bool webReady;
    private string? pendingView;
    private bool pendingOverflow;
    private string? pendingFilter;
    private Bookmark? pendingEditorItem;
    private bool hasPendingEditor;
    private string? pendingSettingsPage;
    private string? pendingGroupModalName;

    public WebPanelWindow(AppController controller, ShellIconService icons)
    {
        this.controller = controller;
        this.icons = icons;
        InitializeComponent();
        Width = 812;
        Height = 620;
        SourceInitialized += (_, _) => NativeWindow.EnableHardwareRoundedCorners(this);
        SizeChanged += (_, _) =>
        {
            Browser.InvalidateMeasure();
            Browser.UpdateLayout();
        };
    }

    public void Prewarm()
    {
        SyncBrowserBackgroundColor();
        _ = EnsureInitializedAsync();
    }

    public async void ShowLibrary(bool moreOverflow = false, bool focusInstalledApps = false)
    {
        pendingView = "library";
        pendingOverflow = moreOverflow;
        pendingFilter = focusInstalledApps ? "installed" : "all";
        hasPendingEditor = false;
        pendingSettingsPage = null;
        pendingGroupModalName = null;
        ShowAndFocus();
        await EnsureInitializedAsync();
        if (webReady) PushState("library", moreOverflow, pendingFilter);
    }

    public async void ShowSettings(string? page = null)
    {
        pendingView = "settings";
        hasPendingEditor = false;
        pendingSettingsPage = page;
        pendingGroupModalName = null;
        ShowAndFocus();
        await EnsureInitializedAsync();
        if (webReady) PushState("settings", false, null, settingsPage: page);
    }

    public async void ShowGroupModal(string groupName)
    {
        if (!IsVisible)
            pendingView = "library";
        hasPendingEditor = false;
        pendingGroupModalName = groupName;
        ShowAndFocus();
        await EnsureInitializedAsync();
        if (webReady)
        {
            PushState(pendingView, false, null, groupModalName: groupName);
            pendingGroupModalName = null;
        }
    }

    public async void ShowEditor(Bookmark? item)
    {
        if (!IsVisible)
            pendingView = "library";
        pendingEditorItem = item;
        hasPendingEditor = true;
        ShowAndFocus();
        await EnsureInitializedAsync();
        if (webReady) PushState(pendingView, false, null, openEditor: true, editorItem: item);
    }

    public void RefreshIfVisible()
    {
        SyncBrowserBackgroundColor();
        if (webReady)
            PushState(null, false, null);
    }

    private void SyncBrowserBackgroundColor()
    {
        var s = controller.Data.Settings;
        bool isDark = s.ThemeMode == PinDock.ThemeMode.Dark || (s.ThemeMode == PinDock.ThemeMode.System && ThemeService.IsSystemDark());
        var colors = Palettes.Resolve(s.Palette, isDark);
        Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, colors.Paper.R, colors.Paper.G, colors.Paper.B);
    }

    private void ShowAndFocus()
    {
        SyncBrowserBackgroundColor();
        Width = 812;
        Height = 620;
        Show();
        UpdateLayout();
        Browser.UpdateLayout();
        Activate();
    }

    private Task EnsureInitializedAsync() => initTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        new WindowInteropHelper(this).EnsureHandle();
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PinDock",
            "WebView2");
        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
        await Browser.EnsureCoreWebView2Async(env);

        var settings = Browser.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;

        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ui.html"))!.Stream;
        using var reader = new StreamReader(stream);
        string html = await reader.ReadToEndAsync();
        Browser.NavigateToString(html);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("cmd", out var cmdProp)) return;
        string? cmd = cmdProp.GetString();

        switch (cmd)
        {
            case "ready":
                webReady = true;
                PushState(
                    pendingView ?? "library",
                    pendingOverflow,
                    pendingFilter,
                    hasPendingEditor,
                    pendingEditorItem,
                    pendingSettingsPage,
                    pendingGroupModalName);
                hasPendingEditor = false;
                pendingSettingsPage = null;
                pendingGroupModalName = null;
                break;

            case "dragWindow":
                BeginNativeDrag();
                break;

            case "closeWindow":
                if (controller.Data.Settings.ClosePanelBehavior == CloseAction.ExitApplication)
                    controller.RequestExitApplication();
                else
                    Hide();
                break;

            case "exitApp":
                controller.RequestExitApplication();
                break;

            case "viewChanged":
                Browser.InvalidateMeasure();
                Browser.UpdateLayout();
                break;

            case "launch":
                HandleLaunch(root);
                break;

            case "reveal":
                if (root.TryGetProperty("path", out var revPath) && revPath.GetString() is string rp)
                    LibraryWindow.RevealInExplorer(rp);
                break;

            case "togglePin":
                if (TryFindBookmark(root, out var pinTarget))
                {
                    controller.TogglePin(pinTarget);
                    PushState(null, false, null);
                }
                break;

            case "pinInstalled":
                string name = root.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                string path = root.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(path))
                {
                    controller.SaveEdited(new Bookmark
                    {
                        Name = string.IsNullOrWhiteSpace(name) ? BookmarkEditor.SuggestDisplayName(path) : name,
                        Path = path,
                        Pinned = true
                    }, null);
                    PushState(null, false, null);
                }
                break;

            case "remove":
                if (TryFindBookmark(root, out var removeTarget))
                {
                    controller.Remove(removeTarget);
                    PushState(null, false, null);
                    PostToast($"已移除收藏：{removeTarget.Name}");
                }
                break;

            case "saveBookmark":
                HandleSaveBookmark(root);
                break;

            case "addGroup":
                if (root.TryGetProperty("name", out var gName) && gName.GetString() is string gn && !string.IsNullOrWhiteSpace(gn))
                {
                    controller.AddGroup(gn.Trim());
                    PushState(null, false, null);
                }
                break;

            case "setBookmarkGroup":
                if (TryFindBookmark(root, out var grpTarget))
                {
                    string grp = root.TryGetProperty("group", out var grpEl) ? (grpEl.GetString() ?? "").Trim() : "";
                    controller.SetBookmarkGroup(grpTarget, grp);
                }
                break;

            case "saveGroupConfig":
                string origName = root.TryGetProperty("originalName", out var onEl) ? (onEl.GetString() ?? "").Trim() : "";
                string targetName = root.TryGetProperty("name", out var tnEl) ? (tnEl.GetString() ?? "").Trim() : "";
                if (targetName.Length > 0)
                {
                    var ids = new HashSet<Guid>();
                    if (root.TryGetProperty("memberIds", out var mIds) && mIds.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in mIds.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g))
                                ids.Add(g);
                        }
                    }
                    GroupIconConfig? iconCfg = null;
                    if (root.TryGetProperty("iconKind", out var ikEl) && Enum.TryParse<GroupIconKind>(ikEl.GetString(), ignoreCase: true, out var parsedKind))
                    {
                        string iconVal = root.TryGetProperty("iconValue", out var ivEl) ? (ivEl.GetString() ?? "").Trim() : "";
                        iconCfg = new GroupIconConfig { Kind = parsedKind, Value = iconVal };
                    }
                    controller.SaveGroupConfig(origName, targetName, ids, iconCfg);
                }
                break;

            case "pickIconForGroup":
                HandlePickIconForGroup();
                break;

            case "pickIconForBookmark":
                HandlePickIconForBookmark();
                break;

            case "autoSeparateDock":
                controller.AutoSeparateDockItems();
                PostToast("已按类型（应用 / 文件夹 / 分组）插入分隔符");
                break;

            case "deleteGroup":
                if (root.TryGetProperty("name", out var delGrpEl) && delGrpEl.GetString() is string delGrp && !string.IsNullOrWhiteSpace(delGrp))
                {
                    controller.DeleteGroup(delGrp.Trim());
                }
                break;

            case "pickPathForEditor":
                bool isFolder = root.TryGetProperty("folder", out var fEl) && fEl.GetBoolean();
                HandlePickPathForEditor(isFolder);
                break;

            case "quickAddFolder":
                var folderDlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择要添加到 PinDock 的文件夹",
                    Multiselect = true
                };
                if (folderDlg.ShowDialog(this) == true)
                {
                    controller.AddPaths(folderDlg.FolderNames);
                    PushState(null, false, null);
                    PostToast($"已添加 {folderDlg.FolderNames.Length} 个文件夹并固定到 Dock");
                }
                break;

            case "updateSettings":
                if (root.TryGetProperty("settings", out var sEl))
                    HandleUpdateSettings(sEl);
                break;

            case "updateOpacity":
                if (root.TryGetProperty("opacity", out var opEl))
                {
                    controller.Data.Settings.Opacity = Math.Clamp(opEl.GetDouble(), AppSettings.MinOpacity, AppSettings.MaxOpacity);
                    controller.UpdateOpacityOnly();
                }
                break;

            case "updateHotkey":
                string mods = root.TryGetProperty("modifiers", out var mEl) ? mEl.GetString() ?? "" : "";
                string hk = root.TryGetProperty("key", out var kEl) ? kEl.GetString() ?? "" : "";
                controller.Data.Settings.HotkeyModifiers = mods;
                controller.Data.Settings.HotkeyKey = hk;
                controller.UpdateHotkey();
                break;

            case "toggleExplorerMenu":
                bool enabled = root.TryGetProperty("enabled", out var enEl) && enEl.GetBoolean();
                try
                {
                    ExplorerMenuService.SetRegistered(enabled, null, controller.Data.Settings.IsEnglish);
                    controller.Data.Settings.ExplorerContextMenu = enabled;
                    controller.ApplySettings();
                    PostToast(enabled
                        ? I18n.T(controller.Data.Settings, "已开启右键菜单与“发送到 PinDock”", "Explorer context menu & SendTo enabled")
                        : I18n.T(controller.Data.Settings, "已关闭资源管理器右键菜单", "Explorer context menu disabled"));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    PostToast($"设置失败：{ex.Message}");
                    PushState(null, false, null);
                }
                break;

            case "toggleWin11Menu":
                bool win11Enabled = root.TryGetProperty("enabled", out var w11El) && w11El.GetBoolean();
                bool restartExp = root.TryGetProperty("restartExplorer", out var reEl) && reEl.GetBoolean();
                try
                {
                    ExplorerMenuService.SetWin11ClassicMenuEnabled(win11Enabled, restartExp);
                    controller.Data.Settings.Win11ClassicMenu = win11Enabled;
                    controller.ApplySettings();
                    PostToast(win11Enabled
                        ? I18n.T(controller.Data.Settings, "已开启 Win11 一级右键菜单直显", "Win11 direct context menu enabled")
                        : I18n.T(controller.Data.Settings, "已恢复 Win11 默认折叠右键菜单", "Restored Win11 default context menu"));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    PostToast($"设置失败：{ex.Message}");
                    PushState(null, false, null);
                }
                break;

            case "quickToggleTheme":
                controller.QuickToggleLightDarkTheme();
                break;

            case "toggleLanguage":
                controller.ToggleLanguage();
                break;

            case "toggleStartup":
                bool startEnabled = root.TryGetProperty("enabled", out var stEl) && stEl.GetBoolean();
                try
                {
                    StartupService.SetRegistered(startEnabled);
                    controller.Data.Settings.LaunchAtStartup = startEnabled;
                    controller.ApplySettings();
                    PostToast(startEnabled ? "已开启开机自动启动" : "已关闭开机自动启动");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
                {
                    PostToast($"开机启动设置失败：{ex.Message}");
                    PushState(null, false, null);
                }
                break;

            case "checkUpdate":
                _ = HandleCheckUpdateAsync();
                break;

            case "downloadAndInstallUpdate":
                string dlUrl = root.TryGetProperty("downloadUrl", out var dlEl) ? (dlEl.GetString() ?? "") : "";
                string assetName = root.TryGetProperty("assetName", out var anEl) ? (anEl.GetString() ?? "") : "";
                if (!string.IsNullOrWhiteSpace(dlUrl))
                    _ = HandleDownloadAndInstallUpdateAsync(dlUrl, assetName);
                break;

            case "openExternalUrl":
                if (root.TryGetProperty("url", out var urlEl) && urlEl.GetString() is string extUrl && extUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(extUrl) { UseShellExecute = true });
                }
                break;

            case "importLibrary":
                HandleImport();
                break;

            case "exportLibrary":
                HandleExport();
                break;

            case "requestIcons":
                if (root.TryGetProperty("paths", out var pathsEl) && pathsEl.ValueKind == JsonValueKind.Array)
                {
                    var list = pathsEl.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
                    _ = LoadIconsAsync(list);
                }
                break;
        }
    }

    private async void HandleLaunch(JsonElement root)
    {
        if (TryFindBookmark(root, out var bm))
        {
            await controller.LaunchAsync(bm);
            return;
        }
        if (root.TryGetProperty("path", out var pEl) && pEl.GetString() is string path && !string.IsNullOrWhiteSpace(path))
        {
            await controller.LaunchAsync(new Bookmark { Name = BookmarkEditor.SuggestDisplayName(path), Path = path });
        }
    }

    private void HandleSaveBookmark(JsonElement root)
    {
        Bookmark? original = null;
        if (TryFindBookmark(root, out var found))
            original = found;

        string path = root.TryGetProperty("path", out var pEl) ? (pEl.GetString() ?? "").Trim() : "";
        if (path.Length == 0) return;

        string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "").Trim() : "";
        string group = root.TryGetProperty("group", out var gEl) ? (gEl.GetString() ?? "").Trim() : "";
        string args = root.TryGetProperty("arguments", out var aEl) ? aEl.GetString() ?? "" : "";
        string workDir = root.TryGetProperty("workingDirectory", out var wEl) ? (wEl.GetString() ?? "").Trim() : "";
        bool pinned = !root.TryGetProperty("pinned", out var pinEl) || pinEl.GetBoolean();

        var iconKind = original?.IconKind ?? BookmarkIconKind.Default;
        string iconValue = original?.IconValue ?? "";
        if (root.TryGetProperty("iconKind", out var ikEl) && Enum.TryParse<BookmarkIconKind>(ikEl.GetString(), ignoreCase: true, out var parsedIk))
        {
            iconKind = parsedIk;
            iconValue = root.TryGetProperty("iconValue", out var ivEl) ? (ivEl.GetString() ?? "").Trim() : "";
        }

        var result = new Bookmark
        {
            Id = original?.Id ?? Guid.NewGuid(),
            Name = name.Length == 0 ? BookmarkEditor.SuggestDisplayName(path) : name,
            Path = path,
            Group = group is "未分组" ? "" : group,
            Arguments = args,
            WorkingDirectory = workDir,
            Pinned = pinned,
            IconKind = iconKind,
            IconValue = iconValue,
            LastLaunchedTicks = original?.LastLaunchedTicks ?? 0,
            LaunchCount = original?.LaunchCount ?? 0
        };
        controller.SaveEdited(result, original);
        PushState(null, false, null);
        PostToast($"已保存收藏：{result.Name}");
    }

    private void HandlePickPathForEditor(bool isFolder)
    {
        if (isFolder)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择文件夹" };
            if (dlg.ShowDialog(this) != true) return;
            PostJson(new
            {
                type = "pickedPath",
                path = dlg.FolderName,
                suggestedName = BookmarkEditor.SuggestDisplayName(dlg.FolderName)
            });
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择应用或文件",
                Filter = "常用目标 (*.exe;*.lnk;*.bat;*.cmd;*.*)|*.exe;*.lnk;*.bat;*.cmd;*.*|所有文件 (*.*)|*.*",
                DereferenceLinks = false
            };
            if (dlg.ShowDialog(this) != true) return;
            PostJson(new
            {
                type = "pickedPath",
                path = dlg.FileName,
                suggestedName = BookmarkEditor.SuggestDisplayName(dlg.FileName)
            });
        }
    }

    private async void HandlePickIconForBookmark()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择收藏项自定义图标或程序文件",
            Filter = "图标、图片或程序 (*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.lnk)|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.lnk|所有文件 (*.*)|*.*",
            DereferenceLinks = false
        };
        if (dlg.ShowDialog(this) != true) return;
        string? dataUri = await icons.GetDataUriAsync(dlg.FileName);
        PostJson(new
        {
            type = "pickedBookmarkIcon",
            path = dlg.FileName,
            dataUri
        });
    }

    private async void HandlePickIconForGroup()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择分组自定义图标或程序文件",
            Filter = "图标、图片或程序 (*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.lnk)|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.exe;*.lnk|所有文件 (*.*)|*.*",
            DereferenceLinks = false
        };
        if (dlg.ShowDialog(this) != true) return;
        string? dataUri = await icons.GetDataUriAsync(dlg.FileName);
        PostJson(new
        {
            type = "pickedGroupIcon",
            path = dlg.FileName,
            dataUri
        });
    }

    private async Task HandleCheckUpdateAsync()
    {
        var s = controller.Data.Settings;
        PostJson(new { type = "updateChecking" });
        try
        {
            var result = await UpdateService.CheckForUpdateAsync(s.UseGithubMirror, s.GithubMirrorUrl);
            PostJson(new
            {
                type = "updateStatus",
                ok = true,
                hasUpdate = result.HasUpdate,
                currentVersion = result.CurrentVersion,
                latestVersion = result.LatestVersion,
                releaseNotes = result.ReleaseNotes,
                releasePageUrl = result.ReleasePageUrl,
                assetName = result.AssetName,
                downloadUrl = result.DownloadUrl
            });
            PostToast(result.HasUpdate ? $"发现新版本 v{result.LatestVersion}" : $"当前已是最新版本 v{result.CurrentVersion}");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or JsonException)
        {
            PostJson(new
            {
                type = "updateStatus",
                ok = false,
                error = ex.Message
            });
            PostToast($"检查更新失败：{ex.Message}");
        }
    }

    private async Task HandleDownloadAndInstallUpdateAsync(string downloadUrl, string assetName)
    {
        try
        {
            PostJson(new { type = "updateProgress", percent = 0 });
            string installerPath = await UpdateService.DownloadInstallerAsync(
                downloadUrl,
                assetName,
                pct => _ = Dispatcher.InvokeAsync(() => PostJson(new { type = "updateProgress", percent = pct })));
            PostToast("安装包下载完成，正在启动升级安装程序…");
            await Task.Delay(500);
            UpdateService.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or TaskCanceledException or Win32Exception)
        {
            PostJson(new { type = "updateError", error = ex.Message });
            PostToast($"下载或启动升级包失败：{ex.Message}");
        }
    }

    private void HandleUpdateSettings(JsonElement sEl)
    {
        var s = controller.Data.Settings;
        if (sEl.TryGetProperty("displayMode", out var dm) && Enum.TryParse<DisplayMode>(dm.GetString(), out var parsedDm))
            s.DisplayMode = parsedDm;
        if (sEl.TryGetProperty("palette", out var pal) && Enum.TryParse<Palette>(pal.GetString(), out var parsedPal))
            s.Palette = parsedPal;
        if (sEl.TryGetProperty("themeMode", out var tm) && Enum.TryParse<PinDock.ThemeMode>(tm.GetString(), out var parsedTm))
            s.ThemeMode = parsedTm;
        if (sEl.TryGetProperty("iconSize", out var sz) && Enum.TryParse<IconSize>(sz.GetString(), out var parsedSz))
            s.IconSize = parsedSz;
        if (sEl.TryGetProperty("dockEdge", out var de) && Enum.TryParse<DockEdge>(de.GetString(), out var parsedDe))
            s.DockEdge = parsedDe;
        if (sEl.TryGetProperty("monitorPolicy", out var mp) && Enum.TryParse<MonitorPolicy>(mp.GetString(), out var parsedMp))
            s.MonitorPolicy = parsedMp;
        if (sEl.TryGetProperty("monitorDeviceName", out var mdn))
            s.MonitorDeviceName = mdn.GetString();
        if (sEl.TryGetProperty("collapseAfterLaunch", out var cal))
            s.CollapseAfterLaunch = cal.GetBoolean();
        if (sEl.TryGetProperty("topmost", out var top))
            s.Topmost = top.GetBoolean();
        if (sEl.TryGetProperty("avoidFullscreen", out var af))
            s.AvoidFullscreen = af.GetBoolean();
        if (sEl.TryGetProperty("lockPosition", out var lp))
            s.LockPosition = lp.GetBoolean();
        if (sEl.TryGetProperty("language", out var langEl) && langEl.GetString() is string langStr && !string.IsNullOrWhiteSpace(langStr))
        {
            s.Language = langStr;
            if (s.ExplorerContextMenu)
            {
                try { ExplorerMenuService.SetRegistered(true, null, s.IsEnglish); }
                catch (Exception) { }
            }
        }
        if (sEl.TryGetProperty("showIconLabels", out var silEl))
            s.ShowIconLabels = silEl.GetBoolean();
        if (sEl.TryGetProperty("utilityCollapsed", out var ucEl))
            s.UtilityCollapsed = ucEl.GetBoolean();
        if (sEl.TryGetProperty("showLibraryButton", out var slb))
            s.ShowLibraryButton = slb.GetBoolean();
        if (sEl.TryGetProperty("showAddButton", out var sab))
            s.ShowAddButton = sab.GetBoolean();
        if (sEl.TryGetProperty("showSettingsButton", out var ssb))
            s.ShowSettingsButton = ssb.GetBoolean();
        if (sEl.TryGetProperty("autoHideUtilityButtons", out var ahub))
            s.AutoHideUtilityButtons = ahub.GetBoolean();
        if (sEl.TryGetProperty("closePanelBehavior", out var cpb) && Enum.TryParse<CloseAction>(cpb.GetString(), out var parsedCpb))
            s.ClosePanelBehavior = parsedCpb;
        if (sEl.TryGetProperty("confirmBeforeExit", out var cbe))
            s.ConfirmBeforeExit = cbe.GetBoolean();
        if (sEl.TryGetProperty("useGithubMirror", out var ugm))
            s.UseGithubMirror = ugm.GetBoolean();
        if (sEl.TryGetProperty("githubMirrorUrl", out var gmu) && gmu.GetString() is string mirrorStr)
            s.GithubMirrorUrl = string.IsNullOrWhiteSpace(mirrorStr) ? AppSettings.DefaultGithubMirror : mirrorStr.Trim();

        controller.ApplySettings();
        SyncBrowserBackgroundColor();
        PushState(null, false, null);
    }

    private void HandleImport()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PinDock 收藏|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var (added, skipped) = controller.Import(dialog.FileName);
            PushState(null, false, null);
            PostToast($"导入完成：新增 {added} 项，跳过重复 {skipped} 项");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or UnsupportedLibraryVersionException)
        {
            PostToast($"导入失败：{ex.Message}");
        }
    }

    private void HandleExport()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PinDock 收藏|*.json", FileName = "pindock-library.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            controller.Export(dialog.FileName);
            PostToast("导出完成");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PostToast($"导出失败：{ex.Message}");
        }
    }

    private async Task LoadIconsAsync(IReadOnlyList<string> paths)
    {
        var dict = new Dictionary<string, string>();
        foreach (string path in paths)
        {
            string? uri = await icons.GetDataUriAsync(path);
            if (uri is not null) dict[path] = uri;
        }
        if (dict.Count > 0 && webReady)
        {
            PostJson(new { type = "icons", icons = dict });
        }
    }

    private bool TryFindBookmark(JsonElement root, out Bookmark bookmark)
    {
        bookmark = null!;
        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var guid))
        {
            var found = controller.Data.Items.FirstOrDefault(i => i.Id == guid);
            if (found is not null)
            {
                bookmark = found;
                return true;
            }
        }
        return false;
    }

    private void PushState(
        string? openView,
        bool overflowOnly,
        string? filter,
        bool openEditor = false,
        Bookmark? editorItem = null,
        string? settingsPage = null,
        string? groupModalName = null)
    {
        if (!webReady || Browser.CoreWebView2 is null) return;
        var s = controller.Data.Settings;
        bool isDark = s.ThemeMode == PinDock.ThemeMode.Dark || (s.ThemeMode == PinDock.ThemeMode.System && ThemeService.IsSystemDark());
        var cachedIcons = new Dictionary<string, string>();
        foreach (var item in controller.Data.Items)
        {
            if (icons.TryGetCachedDataUri(item.Path) is string uri)
                cachedIcons[item.Path] = uri;
            if (item.IconKind == BookmarkIconKind.CustomFile && !string.IsNullOrWhiteSpace(item.IconValue) &&
                icons.TryGetCachedDataUri(item.IconValue) is string customUri)
            {
                cachedIcons[item.IconValue] = customUri;
            }
        }
        foreach (var gIcon in controller.Data.GroupIcons.Values)
        {
            if (gIcon.Kind == GroupIconKind.CustomFile && !string.IsNullOrWhiteSpace(gIcon.Value) &&
                icons.TryGetCachedDataUri(gIcon.Value) is string gUri)
            {
                cachedIcons[gIcon.Value] = gUri;
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["type"] = "state",
            ["version"] = UpdateService.GetCurrentVersion(),
            ["isDark"] = isDark,
            ["items"] = controller.Data.Items.Select(i => new
            {
                id = i.Id.ToString(),
                name = i.Name,
                path = i.Path,
                group = i.Group,
                arguments = i.Arguments,
                workingDirectory = i.WorkingDirectory,
                pinned = i.Pinned,
                iconKind = i.IconKind.ToString(),
                iconValue = i.IconValue,
                kind = BookmarkKindHelper.ToDisplayName(BookmarkKindHelper.FromPath(i.Path))
            }).ToList(),
            ["overflowIds"] = controller.OverflowItems.Select(i => i.Id.ToString()).ToList(),
            ["groups"] = controller.Data.Groups,
            ["groupIcons"] = controller.Data.GroupIcons.ToDictionary(
                kv => kv.Key,
                kv => new { kind = kv.Value.Kind.ToString(), value = kv.Value.Value }),
            ["groupIconPresets"] = GroupIconPresets.All.Select(p => new
            {
                key = p.Key,
                label = p.Label,
                pathData = p.PathData
            }).ToList(),
            ["textBadgePresets"] = GroupIconPresets.DefaultTextBadges,
            ["dockOrder"] = controller.Data.DockOrder,
            ["installedApps"] = controller.InstalledApps.GetInstalledApps().Select(a => new
            {
                name = a.Name,
                path = a.Path,
                category = a.Category
            }).ToList(),
            ["monitors"] = MonitorQuery.All().Select(m => new
            {
                deviceName = m.DeviceName,
                isPrimary = m.IsPrimary,
                width = (int)Math.Round(m.Bounds.Width),
                height = (int)Math.Round(m.Bounds.Height)
            }).ToList(),
            ["settings"] = new
            {
                displayMode = s.DisplayMode.ToString(),
                dockEdge = s.DockEdge.ToString(),
                themeMode = s.ThemeMode.ToString(),
                palette = s.Palette.ToString(),
                monitorPolicy = s.MonitorPolicy.ToString(),
                iconSize = s.IconSize.ToString(),
                opacity = s.Opacity,
                topmost = s.Topmost,
                avoidFullscreen = s.AvoidFullscreen,
                lockPosition = s.LockPosition,
                collapseAfterLaunch = s.CollapseAfterLaunch,
                monitorDeviceName = s.MonitorDeviceName,
                explorerContextMenu = s.ExplorerContextMenu || ExplorerMenuService.IsRegistered(),
                win11ClassicMenu = s.Win11ClassicMenu || ExplorerMenuService.IsWin11ClassicMenuEnabled(),
                hotkeyModifiers = s.HotkeyModifiers,
                hotkeyKey = s.HotkeyKey,
                language = s.Language,
                showIconLabels = s.ShowIconLabels,
                utilityCollapsed = s.UtilityCollapsed,
                showLibraryButton = s.ShowLibraryButton,
                showAddButton = s.ShowAddButton,
                showSettingsButton = s.ShowSettingsButton,
                autoHideUtilityButtons = s.AutoHideUtilityButtons,
                launchAtStartup = s.LaunchAtStartup || StartupService.IsRegistered(),
                closePanelBehavior = s.ClosePanelBehavior.ToString(),
                confirmBeforeExit = s.ConfirmBeforeExit,
                useGithubMirror = s.UseGithubMirror,
                githubMirrorUrl = s.GithubMirrorUrl
            },
            ["icons"] = cachedIcons
        };

        if (openView is not null)
        {
            payload["openView"] = openView;
            payload["overflowOnly"] = overflowOnly;
            payload["filter"] = filter;
        }
        if (settingsPage is not null)
        {
            payload["settingsPage"] = settingsPage;
        }
        if (groupModalName is not null)
        {
            payload["openGroupModalName"] = groupModalName;
        }
        if (openEditor)
        {
            payload["openEditorItem"] = editorItem is null ? null : new
            {
                id = editorItem.Id.ToString(),
                name = editorItem.Name,
                path = editorItem.Path,
                group = editorItem.Group,
                arguments = editorItem.Arguments,
                workingDirectory = editorItem.WorkingDirectory,
                pinned = editorItem.Pinned,
                iconKind = editorItem.IconKind.ToString(),
                iconValue = editorItem.IconValue
            };
        }

        PostJson(payload);
    }

    private void PostToast(string message) => PostJson(new { type = "toast", message });

    private void PostJson(object payload)
    {
        if (!webReady || Browser.CoreWebView2 is null) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, JsonOpts));
    }

    private void BeginNativeDrag()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ReleaseCapture();
        SendMessage(hwnd, 0x00A1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            controller.AddPaths(files);
            RefreshIfVisible();
            PostToast($"已添加 {files.Length} 项收藏");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (controller.Data.Settings.ClosePanelBehavior == CloseAction.ExitApplication)
        {
            e.Cancel = true;
            controller.RequestExitApplication();
            return;
        }
        e.Cancel = true;
        Hide();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
}
