using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using static PinDock.RecentIo;

namespace PinDock;

public sealed class DeclarativeRecentProvider : IRecentProvider
{
    private const string BuiltInResourcePrefix = "PinDock.Recent.Descriptors.";
    private const string HexUtf8Encoding = "hex-utf8";

    private static readonly JsonSerializerOptions DescriptorJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Lazy<RecentDescriptor[]> BuiltIn = new(LoadBuiltIn);

    private static readonly ConcurrentDictionary<string, string> Errors = new(StringComparer.OrdinalIgnoreCase);

    // 用户描述文件的加载失败原因，键为文件路径；文件修复后下次读取时移除
    public static IReadOnlyDictionary<string, string> DescriptorErrors => Errors;

    public static string UserDirectory(string localRoot) =>
        Path.Combine(localRoot, "PinDock", "recent-providers");

    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        foreach (var descriptor in BuiltIn.Value.Concat(LoadUserDescriptors(roots.Local)))
        {
            if (!descriptor.Exes.Contains(target.ExecutableName, StringComparer.OrdinalIgnoreCase))
                continue;
            foreach (var item in Read(descriptor, roots.Roaming))
                yield return item;
        }
    }

    private static RecentDescriptor[] LoadBuiltIn()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(BuiltInResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd(), name);
            })
            .ToArray();
    }

    private static List<RecentDescriptor> LoadUserDescriptors(string localRoot)
    {
        var result = new List<RecentDescriptor>();
        string dir = UserDirectory(localRoot);
        if (!Directory.Exists(dir))
            return result;

        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                result.Add(Parse(ReadTextShared(file), file));
                Errors.TryRemove(file, out _);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                Errors[file] = ex.Message;
            }
        }
        return result;
    }

    private static RecentDescriptor Parse(string json, string origin)
    {
        var descriptor = JsonSerializer.Deserialize<RecentDescriptor>(json, DescriptorJson)
            ?? throw new InvalidDataException($"{origin}: 描述文件为空");

        if (descriptor.Exes.Length == 0 || descriptor.Exes.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"{origin}: exes 必须是非空的可执行文件名列表");
        if (descriptor.Sources.Length == 0)
            throw new InvalidDataException($"{origin}: sources 不能为空");
        if (Path.IsPathRooted(descriptor.SourceFile) || descriptor.SourceFile.Split('/', '\\').Contains(".."))
            throw new InvalidDataException($"{origin}: sourceFile 必须是相对 %APPDATA% 的路径且不含 \"..\"（当前值 \"{descriptor.SourceFile}\"）");
        if (descriptor.Encoding.Length > 0 && descriptor.Encoding != HexUtf8Encoding)
            throw new InvalidDataException($"{origin}: encoding 只支持 \"{HexUtf8Encoding}\"（当前值 \"{descriptor.Encoding}\"）");
        foreach (var source in descriptor.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Collection))
                throw new InvalidDataException($"{origin}: source.collection 不能为空");
            if (source.Kind is not (AppRecentItemKind.Folder or AppRecentItemKind.Workspace or AppRecentItemKind.File))
                throw new InvalidDataException($"{origin}: source.kind 只支持 Folder、Workspace、File（当前值 {source.Kind}）");
        }
        return descriptor;
    }

    private static IEnumerable<AppRecentItem> Read(RecentDescriptor descriptor, string roamingRoot)
    {
        string path = Path.Combine(roamingRoot, descriptor.SourceFile);
        if (!File.Exists(path))
            yield break;

        JsonDocument doc;
        try
        {
            string text = ReadTextShared(path);
            if (descriptor.Encoding == HexUtf8Encoding)
                text = TryDecodeHexUtf8(text.Trim()) ?? text;
            doc = JsonDocument.Parse(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                yield break;

            foreach (var source in descriptor.Sources)
            {
                if (!doc.RootElement.TryGetProperty(source.Collection, out var collection))
                    continue;

                foreach (var (key, element) in Entries(collection))
                {
                    if (TryBuildItem(source, key, element, out var item))
                        yield return item;
                }
            }
        }
    }

    private static IEnumerable<(string Key, JsonElement Element)> Entries(JsonElement collection) =>
        collection.ValueKind switch
        {
            JsonValueKind.Object => collection.EnumerateObject().Select(p => (p.Name, p.Value)),
            JsonValueKind.Array => collection.EnumerateArray().Select(e => ("", e)),
            _ => []
        };

    private static bool TryBuildItem(RecentSource source, string key, JsonElement element, out AppRecentItem item)
    {
        item = default;

        string? path = source.PathField.Length == 0
            ? (element.ValueKind == JsonValueKind.String ? element.GetString() : null)
            : (element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(source.PathField, out var pathEl) &&
               pathEl.ValueKind == JsonValueKind.String ? pathEl.GetString() : null);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        bool isDir = source.Kind == AppRecentItemKind.Folder;
        if (isDir ? !Directory.Exists(path) : !File.Exists(path))
            return false;

        DateTime opened = SafeGetLastWriteTime(path, isDir);
        if (source.TimeField.Length > 0 &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(source.TimeField, out var timeEl) &&
            timeEl.ValueKind == JsonValueKind.Number &&
            timeEl.TryGetInt64(out long unixMs) && unixMs > 0)
        {
            opened = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        }

        bool isOpen = source.OpenField.Length > 0 &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(source.OpenField, out var openEl) &&
            openEl.ValueKind == JsonValueKind.True;

        string name = source.Kind == AppRecentItemKind.Workspace
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        item = new AppRecentItem(
            Name: name,
            FullPath: path,
            Subtitle: Path.GetDirectoryName(path) ?? path,
            Kind: source.Kind,
            LastOpenedTime: opened,
            LaunchUri: source.LaunchUri.Replace("{key}", Uri.EscapeDataString(key), StringComparison.Ordinal),
            IsCurrentlyOpen: isOpen);
        return true;
    }
}
