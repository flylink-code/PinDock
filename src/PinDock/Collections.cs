using System.IO;
using System.Text.Json;

namespace PinDock;

public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public bool Pinned { get; set; }
}

public sealed class LibraryData
{
    public int Version { get; set; } = 1;
    public List<Bookmark> Items { get; set; } = [];
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
        if (data.Version != 1 || data.Items is null) throw new InvalidDataException("Unsupported or invalid library");
        return data;
    }

    public void Save(LibraryData data)
    {
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
