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
    public string Group { get; set; } = "";
    public bool Pinned { get; set; }
}

public sealed class LibraryData
{
    public int Version { get; set; } = 2;
    public List<Bookmark> Items { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}

public sealed class UnsupportedLibraryVersionException : Exception
{
    public UnsupportedLibraryVersionException(int version) : base($"Unsupported library version: {version}") { }
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
        if (data.Version is not (1 or 2)) throw new UnsupportedLibraryVersionException(data.Version);
        if (data.Items is null || data.Items.Any(item => item is null || string.IsNullOrWhiteSpace(item.Path) || item.Name is null || item.Arguments is null || item.WorkingDirectory is null || item.Group is null))
            throw new InvalidDataException("Invalid library items");
        data.Groups ??= [];
        data.Settings ??= new();
        ValidateSettings(data.Settings);
        if (data.Version == 1)
        {
            data.Version = 2;
            foreach (var item in data.Items)
                item.Group ??= "";
        }
        return data;
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.Opacity < AppSettings.MinOpacity || settings.Opacity > AppSettings.MaxOpacity)
            throw new InvalidDataException($"Opacity out of range: {settings.Opacity}");
        if (!Enum.IsDefined(settings.DisplayMode) || !Enum.IsDefined(settings.DockEdge) || !Enum.IsDefined(settings.ThemeMode) ||
            !Enum.IsDefined(settings.Palette) || !Enum.IsDefined(settings.MonitorPolicy) || !Enum.IsDefined(settings.IconSize))
            throw new InvalidDataException("Invalid settings enum value");
        if (settings.HotkeyModifiers is null || settings.HotkeyKey is null)
            throw new InvalidDataException("Hotkey settings missing");
    }

    public void Save(LibraryData data)
    {
        data.Version = 2;
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
