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
    Console.WriteLine("PASS: Windows Shell folder, executable, file and shortcut icons, frozen images and cache reuse");

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

    Console.WriteLine("PASS: round trip, duplicates, backup recovery, v1 migration, opacity, DockLayout, auto-hide logic, import/export, groups");
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
