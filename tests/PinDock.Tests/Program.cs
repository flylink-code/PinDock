using PinDock;
using System.IO;

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
    Console.WriteLine("PASS: round trip, duplicates, backup recovery, invalid items and future-version protection");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}
