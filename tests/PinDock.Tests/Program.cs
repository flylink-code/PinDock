using PinDock;

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
    Console.WriteLine("PASS: round trip, duplicates and backup recovery");
}
finally
{
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}
