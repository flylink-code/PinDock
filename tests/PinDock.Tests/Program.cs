using PinDock;
using System.IO;
using System.ComponentModel;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--launch-probe")
{
    string tempOutput = args[1] + ".tmp";
    File.WriteAllText(tempOutput, JsonSerializer.Serialize(new { Argument = args[2], WorkingDirectory = Environment.CurrentDirectory }));
    File.Move(tempOutput, args[1]);
    return;
}

string directory = Path.Combine(Path.GetTempPath(), "PinDock-tests-" + Guid.NewGuid());
try
{
    var store = new LibraryStore(directory);
    var item = new Bookmark { Name = "示例", Path = @"C:\Test\项目", Pinned = true, Group = "" };
    var data = new LibraryData();
    data.Items.Add(item);
    data.Settings.Opacity = 0.94;
    data.Settings.DisplayMode = DisplayMode.EdgeAutoHide;
    store.Save(data);
    var loaded = store.Load();
    if (loaded.Items.Single().Name != item.Name) throw new Exception("Round trip failed");
    if (loaded.Version != 2) throw new Exception("Saved version must be 2");
    if (loaded.Settings.DisplayMode != DisplayMode.EdgeAutoHide) throw new Exception("Settings round trip failed");
    if (!LibraryStore.IsDuplicate(data.Items, new Bookmark { Path = @"c:\test\项目" })) throw new Exception("Duplicate check failed");
    if (LibraryStore.IsDuplicate(data.Items, new Bookmark { Path = item.Path, Arguments = "--other" })) throw new Exception("Arguments not considered");
    data.Items.Add(new Bookmark { Name = "Second", Path = @"C:\Second" });
    store.Save(data);
    File.WriteAllText(Path.Combine(directory, "library.json"), "broken");
    var recovered = store.Load();
    if (recovered.Items.Count != 1) throw new Exception("Backup recovery failed");
    store.Save(recovered);
    if (store.Load().Items.Count != 1) throw new Exception("Recovery save failed");
    string path = Path.Combine(directory, "library.json");
    File.WriteAllText(path, "{\"Version\":3,\"Items\":[]}");
    try { store.Load(); throw new Exception("Future version must not use backup"); }
    catch (UnsupportedLibraryVersionException) { }
    if (!File.ReadAllText(path).Contains("\"Version\":3")) throw new Exception("Future version was changed");
    File.WriteAllText(path, "{\"Version\":1,\"Items\":[{\"Path\":null}]}");
    if (store.Load().Items.Count != 1) throw new Exception("Invalid item did not recover from backup");

    File.WriteAllText(path, "{\"Version\":1,\"Items\":[{\"Name\":\"旧\",\"Path\":\"C:\\\\Old\",\"Arguments\":\"\",\"WorkingDirectory\":\"\",\"Pinned\":true}]}");
    var migrated = store.Load();
    if (migrated.Version != 2 || migrated.Items.Single().Group != "" || migrated.Settings is null) throw new Exception("v1 migration failed");

    // 连续保存两次，使 .bak 也是 Opacity 0.9 的有效内容，再写入越界的主文件。
    store.Save(new LibraryData { Items = [new Bookmark { Name = "ok", Path = @"C:\ok" }], Settings = new AppSettings { Opacity = 0.9 } });
    store.Save(new LibraryData { Items = [new Bookmark { Name = "ok", Path = @"C:\ok" }], Settings = new AppSettings { Opacity = 0.9 } });
    File.WriteAllText(path, JsonSerializer.Serialize(new LibraryData { Settings = new AppSettings { Opacity = 0.2 } }));
    var opacityRecovered = store.Load();
    if (Math.Abs(opacityRecovered.Settings.Opacity - 0.9) > 0.001) throw new Exception("Opacity out of range did not recover from backup");

    var icons = new ShellIconService();
    var folderIcon = await icons.GetAsync(directory);
    var executableIcon = await icons.GetAsync(Environment.ProcessPath!);
    string textFile = Path.Combine(directory, "文档.txt");
    File.WriteAllText(textFile, "PinDock");
    var fileIcon = await icons.GetAsync(textFile);
    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
    dynamic shortcut = shell.CreateShortcut(Path.Combine(directory, "快捷方式.lnk"));
    shortcut.TargetPath = Environment.ProcessPath;
    shortcut.Save();
    var shortcutIcon = await icons.GetAsync(Path.Combine(directory, "快捷方式.lnk"));
    if (!folderIcon.IsFrozen || !executableIcon.IsFrozen || !fileIcon.IsFrozen || !shortcutIcon.IsFrozen) throw new Exception("Shell icon must be frozen for UI use");
    if (!ReferenceEquals(folderIcon, await icons.GetAsync(directory))) throw new Exception("Icon cache missed");
    string? exeDataUri = await icons.GetDataUriAsync(Environment.ProcessPath!);
    if (string.IsNullOrWhiteSpace(exeDataUri) || !exeDataUri.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
        throw new Exception("Icon DataURI generation failed");
    if (icons.TryGetCachedDataUri(Environment.ProcessPath!) != exeDataUri)
        throw new Exception("Icon DataURI cache missed");
    Console.WriteLine("PASS: Windows Shell folder, executable, file and shortcut icons, frozen images, DataURI and cache reuse");

    string workingDirectory = Path.Combine(directory, "工作 目录");
    Directory.CreateDirectory(workingDirectory);
    string output = Path.Combine(directory, "启动 结果.json");
    BookmarkLauncher.Open(new Bookmark { Path = Environment.ProcessPath!, Arguments = $"--launch-probe \"{output}\" \"中文 参数\"", WorkingDirectory = workingDirectory });
    await CheckLaunch(output, workingDirectory, "中文 参数");
    File.Delete(output);
    shortcut.Arguments = $"--launch-probe \"{output}\" \"快捷方式 参数\"";
    shortcut.WorkingDirectory = workingDirectory;
    shortcut.Save();
    BookmarkLauncher.Open(new Bookmark { Path = Path.Combine(directory, "快捷方式.lnk") });
    await CheckLaunch(output, workingDirectory, "快捷方式 参数");
    try { BookmarkLauncher.Open(new Bookmark { Path = Path.Combine(directory, "不存在.exe") }); throw new Exception("Missing executable unexpectedly started"); }
    catch (Win32Exception) { }
    Console.WriteLine("PASS: EXE and .lnk Shell launch, arguments, working directory, missing target");

    if (BookmarkKindHelper.FromPath(directory) != BookmarkKind.Folder) throw new Exception("Directory kind");
    if (BookmarkKindHelper.FromPath(Environment.ProcessPath!) != BookmarkKind.Application) throw new Exception("Exe kind");
    if (BookmarkKindHelper.FromPath(textFile) != BookmarkKind.File) throw new Exception("File kind");

    var work = new ScreenRect(0, 0, 1920, 1040);
    int capacity = DockLayout.ComputeCapacity(work.Width, 46);
    if (capacity is < 1 or > 9) throw new Exception($"Capacity out of range: {capacity}");
    if (DockLayout.ComputeCapacity(300, 46) != 1) throw new Exception("Narrow screen must keep at least one pinned slot");
    if (DockLayout.ComputeCapacity(100000, 46) != DockLayout.MaxPinnedVisible) throw new Exception("Capacity must be capped");
    var bottom = DockLayout.Fit(work, DockEdge.Bottom, 600, 68, 0, true);
    if (bottom.Y + bottom.Height > work.Y + work.Height) throw new Exception("Bottom dock outside work area");
    if (Math.Abs(bottom.X - (work.Width - 600) / 2) > 0.001) throw new Exception("Bottom dock must be centered");
    var handle = DockLayout.EdgeHandleFor(work, DockEdge.Bottom, bottom);
    if (handle.Height != DockLayout.HandleThickness || handle.Y + handle.Height != work.Y + work.Height) throw new Exception("Bottom handle placement");
    var left = DockLayout.Fit(work, DockEdge.Left, 68, 500, 100, false);
    if (left.X != work.X + DockLayout.DockMargin || left.Y < work.Y) throw new Exception("Left dock placement");
    var locked = DockLayout.Fit(work, DockEdge.Bottom, 600, 68, 200, true);
    if (locked.X != bottom.X) throw new Exception("Locked dock must ignore offset");
    var shifted = DockLayout.Fit(work, DockEdge.Bottom, 600, 68, 200, false);
    if (Math.Abs(shifted.X - bottom.X - 200) > 0.001) throw new Exception("Unlocked dock must apply offset");
    double clamped = DockLayout.ClampEdgeOffset(work, DockEdge.Bottom, 800, 99999);
    double maxOffset = (work.Width - 800 - DockLayout.DockMargin * 2) / 2;
    if (Math.Abs(clamped - maxOffset) > 0.001) throw new Exception("Offset clamp failed");
    var union = bottom.Union(handle);
    if (!union.Contains(bottom.X + 1, handle.Y)) throw new Exception("Hit area must include handle");

    if (!AutoHideLogic.ShouldStayVisible(true, false, false, false)) throw new Exception("Popup should keep visible");
    if (!AutoHideLogic.ShouldStayVisible(false, true, false, false)) throw new Exception("Drag should keep visible");
    if (!AutoHideLogic.ShouldStayVisible(false, false, true, false)) throw new Exception("Dialog should keep visible");
    if (AutoHideLogic.ShouldStayVisible(false, false, false, false)) throw new Exception("Idle should not keep visible");
    if (AutoHideLogic.NextState(AutoHideState.Visible, false, false) != AutoHideState.Visible) throw new Exception("Hide delay not elapsed");
    if (AutoHideLogic.NextState(AutoHideState.Visible, false, true) != AutoHideState.Hidden) throw new Exception("Should hide after delay");
    if (AutoHideLogic.NextState(AutoHideState.Visible, true, true) != AutoHideState.Visible) throw new Exception("Should stay when held");
    if (AutoHideLogic.NextState(AutoHideState.Hidden, true, true) != AutoHideState.Hidden) throw new Exception("Hidden must not reopen without user action");

    var transfer = new LibraryData
    {
        Items =
        [
            new Bookmark { Name = "A", Path = @"C:\A", Group = "开发项目", Pinned = true },
            new Bookmark { Name = "B", Path = @"C:\B", Group = "开发项目" }
        ],
        Groups = ["开发项目"]
    };
    string exportPath = Path.Combine(directory, "export.json");
    LibraryTransfer.Export(transfer, exportPath);
    var target = new LibraryData { Items = [new Bookmark { Name = "A", Path = @"C:\A" }], Groups = [] };
    var (added, skipped) = LibraryTransfer.Import(target, exportPath);
    if (added != 1 || skipped != 1) throw new Exception($"Import counts wrong: {added}/{skipped}");
    if (!target.Groups.Contains("开发项目")) throw new Exception("Import group missing");
    LibraryTransfer.RemoveGroup(target, "开发项目");
    if (target.Items.Any(i => i.Group == "开发项目")) throw new Exception("Group remove did not clear items");
    if (target.Groups.Contains("开发项目")) throw new Exception("Group still listed");

    // 快捷键清除（不使用）与扩展按键解析测试
    var clearedSettings = new AppSettings { HotkeyModifiers = "", HotkeyKey = "" };
    if (clearedSettings.HasHotkey) throw new Exception("Cleared hotkey must report HasHotkey = false");
    if (HotkeyService.FormatDisplay("", "") != "不使用") throw new Exception("Cleared hotkey display mismatch");
    if (HotkeyService.FormatDisplay("Ctrl+Alt", "F1") != "Ctrl + Alt + F1") throw new Exception("Hotkey display mismatch");
    if (HotkeyService.ParseKey("F1") != 0x70u || HotkeyService.ParseKey("F12") != 0x7Bu || HotkeyService.ParseKey("Space") != 0x20u)
        throw new Exception("Extended hotkey key parse failed");
    store.Save(new LibraryData { Settings = clearedSettings });
    var loadedCleared = store.Load();
    if (loadedCleared.Settings.HasHotkey) throw new Exception("Cleared hotkey round trip failed");

    // 本机已安装应用扫描与过滤测试
    string appDir = Path.Combine(directory, "StartMenuPrograms");
    Directory.CreateDirectory(appDir);
    File.WriteAllText(Path.Combine(appDir, "Visual Studio Code.lnk"), "");
    File.WriteAllText(Path.Combine(appDir, "Uninstall Tool.lnk"), "");
    File.WriteAllText(Path.Combine(appDir, "Add Design Tools or Devices 2023.lnk"), "");
    File.WriteAllText(Path.Combine(appDir, "卸载程序.exe"), "");
    File.WriteAllText(Path.Combine(appDir, "Everything.exe"), "");
    var discovered = InstalledAppsService.DiscoverFromDirectories([appDir]);
    if (discovered.Count != 2 || discovered[0].Name != "Everything" || discovered[1].Name != "Visual Studio Code")
        throw new Exception("InstalledAppsService discovery or filtering failed");

    // 文件夹悬停目录浏览、系统目录过滤与多维排序测试
    string browseRoot = Path.Combine(directory, "DriveRoot");
    Directory.CreateDirectory(Path.Combine(browseRoot, "Project10"));
    Directory.CreateDirectory(Path.Combine(browseRoot, "Project2"));
    Directory.CreateDirectory(Path.Combine(browseRoot, "$RECYCLE.BIN"));
    Directory.CreateDirectory(Path.Combine(browseRoot, "System Volume Information"));
    File.WriteAllText(Path.Combine(browseRoot, "readme.md"), "ok");
    File.WriteAllText(Path.Combine(browseRoot, "pagefile.sys"), "sys");
    Directory.SetLastWriteTime(Path.Combine(browseRoot, "Project2"), new DateTime(2025, 1, 1));
    Directory.SetLastWriteTime(Path.Combine(browseRoot, "Project10"), new DateTime(2026, 10, 29));
    var browseEntries = DirectoryBrowserService.ListEntries(browseRoot, DirectorySortMode.NameAsc);
    if (browseEntries.Count != 3 ||
        !browseEntries[0].IsDirectory || browseEntries[0].Name != "Project2" ||
        !browseEntries[1].IsDirectory || browseEntries[1].Name != "Project10" ||
        browseEntries[2].IsDirectory || browseEntries[2].Name != "readme.md")
    {
        throw new Exception("DirectoryBrowserService natural name sorting failed");
    }
    var byModified = DirectoryBrowserService.ListEntries(browseRoot, DirectorySortMode.ModifiedDesc);
    if (byModified[0].Name != "Project10" || byModified[1].Name != "Project2")
        throw new Exception("DirectoryBrowserService ModifiedDesc sorting failed");

    // 资源管理器右键命令与参数提取测试
    if (ExplorerMenuService.TryExtractAddPath(["--add", @"D:\Work\ProjectA"]) != @"D:\Work\ProjectA")
        throw new Exception("Explorer --add argument parse failed");
    if (ExplorerMenuService.TryExtractAddPath(["--add", "E:\\ "]) != @"E:\")
        throw new Exception("Explorer root drive trailing space argument parse failed");
    if (ExplorerMenuService.TryExtractAddPath(["--add", "E:\""]) != @"E:\")
        throw new Exception("Explorer root drive escaped quote argument parse failed");
    if (!ExplorerMenuService.BuildCommand(@"C:\App\PinDock.exe").Contains("--add \"%1 \""))
        throw new Exception("Explorer command build failed");

    // 名称推断与全色板解析测试
    if (BookmarkEditor.SuggestDisplayName(@"C:\Apps\WeChat.lnk") != "WeChat")
        throw new Exception("Shortcut display name suggestion failed");
    if (BookmarkEditor.SuggestDisplayName(directory) != Path.GetFileName(directory))
        throw new Exception("Directory display name suggestion failed");
    foreach (Palette p in Enum.GetValues<Palette>())
    {
        var light = Palettes.Resolve(p, false);
        var dark = Palettes.Resolve(p, true);
        if (light.Paper == dark.Paper || light.GlassBorder == dark.GlassBorder)
            throw new Exception($"Palette {p} light/dark differentiation failed");
    }

    // 1. 分组自定义图标配置、预设与导入导出测试
    if (string.IsNullOrWhiteSpace(GroupIconPresets.FindPathData("code")))
        throw new Exception("GroupIconPresets lookup failed");
    var customGroupData = new LibraryData
    {
        Items =
        [
            new Bookmark { Name = "App1", Path = Environment.ProcessPath!, Group = "开发工具", Pinned = true, IconKind = BookmarkIconKind.Preset, IconValue = "drive" },
            new Bookmark { Name = "Folder1", Path = directory, Group = "", Pinned = true, IconKind = BookmarkIconKind.CustomFile, IconValue = Environment.ProcessPath! }
        ],
        Groups = ["开发工具"],
        GroupIcons = new Dictionary<string, GroupIconConfig>
        {
            ["开发工具"] = new GroupIconConfig { Kind = GroupIconKind.Preset, Value = "code" }
        },
        Settings = new AppSettings
        {
            Language = "en-US",
            ShowIconLabels = true,
            UtilityCollapsed = true,
            ShowLibraryButton = false,
            ShowAddButton = true,
            ShowSettingsButton = false,
            AutoHideUtilityButtons = true,
            LaunchAtStartup = true,
            ClosePanelBehavior = CloseAction.ExitApplication,
            ConfirmBeforeExit = true,
            UseGithubMirror = true,
            GithubMirrorUrl = "https://gh-proxy.com/"
        }
    };
    customGroupData.SetFolderHidden(@"E:\W-STM32IDE\", true);
    customGroupData.SetFolderSortMode(@"E:\", DirectorySortMode.ModifiedDesc);
    if (!customGroupData.IsFolderHidden(@"e:\w-stm32ide"))
        throw new Exception("HiddenFolderPaths case-insensitive check failed");

    // 2. Dock 排序与自动按类型分隔测试
    customGroupData.AutoSeparateByKind();
    if (!customGroupData.DockOrder.Any(t => t.StartsWith("sep:", StringComparison.Ordinal)) ||
        customGroupData.DockOrder.Count < 4)
        throw new Exception("AutoSeparateByKind did not insert separators between apps, folders and groups");

    store.Save(customGroupData);
    var reloadedCustom = store.Load();
    if (!reloadedCustom.GroupIcons.TryGetValue("开发工具", out var loadedIconCfg) ||
        loadedIconCfg.Kind != GroupIconKind.Preset || loadedIconCfg.Value != "code")
        throw new Exception("GroupIcons round trip failed");
    if (reloadedCustom.Items[0].IconKind != BookmarkIconKind.Preset || reloadedCustom.Items[0].IconValue != "drive" ||
        reloadedCustom.Items[1].IconKind != BookmarkIconKind.CustomFile)
        throw new Exception("Bookmark custom icon round trip failed");
    if (reloadedCustom.GetFolderSortMode(@"e:") != DirectorySortMode.ModifiedDesc)
        throw new Exception("FolderSortModes round trip failed");
    if (!reloadedCustom.IsFolderHidden(@"E:\W-STM32IDE"))
        throw new Exception("HiddenFolderPaths round trip failed");
    reloadedCustom.SetFolderHidden(@"E:\W-STM32IDE", false);
    if (reloadedCustom.IsFolderHidden(@"E:\W-STM32IDE"))
        throw new Exception("Unhide folder failed");

    if (reloadedCustom.Settings.ShowLibraryButton ||
        !reloadedCustom.Settings.ShowIconLabels ||
        !reloadedCustom.Settings.UtilityCollapsed ||
        !reloadedCustom.Settings.IsEnglish ||
        I18n.T(reloadedCustom.Settings, "中文", "English") != "English" ||
        !reloadedCustom.Settings.AutoHideUtilityButtons ||
        reloadedCustom.Settings.ClosePanelBehavior != CloseAction.ExitApplication ||
        !reloadedCustom.Settings.ConfirmBeforeExit ||
        reloadedCustom.Settings.GithubMirrorUrl != "https://gh-proxy.com/")
        throw new Exception("Extended settings round trip failed");

    LibraryTransfer.RenameGroup(reloadedCustom, "开发工具", "核心研发");
    if (!reloadedCustom.GroupIcons.ContainsKey("核心研发") ||
        !reloadedCustom.DockOrder.Contains("grp:核心研发"))
        throw new Exception("RenameGroup did not update GroupIcons or DockOrder");

    // 3. 版本号、GitHub 版本比较、国内镜像 URL 拼接与开机启动命令测试
    if (UpdateService.GetCurrentVersion() != "0.1.3")
        throw new Exception($"Expected version 0.1.3, got {UpdateService.GetCurrentVersion()}");
    if (!UpdateService.IsNewerVersion("0.1.3", "v0.1.4") ||
        !UpdateService.IsNewerVersion("0.1.3", "0.2.0") ||
        UpdateService.IsNewerVersion("0.1.3", "v0.1.3"))
        throw new Exception("UpdateService version comparison failed");

    string rawReleaseAssetUrl = "https://github.com/flylink-code/PinDock/releases/download/v0.2.0/PinDock-Setup-0.2.0.exe";
    string mirroredUrl = UpdateService.ApplyMirror(rawReleaseAssetUrl, true, "https://ghfast.top");
    if (mirroredUrl != "https://ghfast.top/" + rawReleaseAssetUrl)
        throw new Exception($"Mirror URL rewrite failed: {mirroredUrl}");
    if (UpdateService.ApplyMirror(rawReleaseAssetUrl, false, "https://ghfast.top") != rawReleaseAssetUrl)
        throw new Exception("Disabled mirror should keep raw URL");
    if (StartupService.BuildCommand(@"C:\App\PinDock.exe") != "\"C:\\App\\PinDock.exe\"")
        throw new Exception("StartupService BuildCommand failed");

    Console.WriteLine("PASS: round trip, duplicates, backup recovery, v1 migration, opacity, DockLayout, auto-hide logic, import/export, groups, custom group icons, dock separators, utility hide, startup/close, updater mirror, palettes");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}

static async Task CheckLaunch(string output, string workingDirectory, string argument)
{
    for (int i = 0; i < 100 && !File.Exists(output); i++) await Task.Delay(100);
    if (!File.Exists(output)) throw new Exception("Launch probe did not produce output");
    using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output));
    if (result.RootElement.GetProperty("Argument").GetString() != argument ||
        !string.Equals(result.RootElement.GetProperty("WorkingDirectory").GetString(), workingDirectory, StringComparison.OrdinalIgnoreCase))
        throw new Exception("Launch arguments or working directory did not match");
}
