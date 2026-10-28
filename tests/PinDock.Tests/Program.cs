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
    var item = new Bookmark { Name = "示例", Path = @"C:\Test\项目", Pinned = true };
    var data = new LibraryData();
    data.Items.Add(item);
    store.Save(data);
    if (store.Load().Items.Single().Name != item.Name) throw new Exception("Round trip failed");
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
    File.WriteAllText(path, "{\"Version\":2,\"Items\":[]}");
    try { store.Load(); throw new Exception("Future version must not use backup"); }
    catch (UnsupportedLibraryVersionException) { }
    if (!File.ReadAllText(path).Contains("\"Version\":2")) throw new Exception("Future version was changed");
    File.WriteAllText(path, "{\"Version\":1,\"Items\":[{\"Path\":null}]}");
    if (store.Load().Items.Count != 1) throw new Exception("Invalid item did not recover from backup");
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
    Console.WriteLine("PASS: round trip, duplicates, backup recovery, invalid items and future-version protection");
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
