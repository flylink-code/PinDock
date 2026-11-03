using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using static PinDock.RecentIo;

namespace PinDock;

public sealed class VisualStudioRecentProvider : IRecentProvider
{
    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        string localAppDataRoot = roots.Local;
        if (!string.Equals(target.ExecutableName, "devenv.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        string vsRoot = Path.Combine(localAppDataRoot, "Microsoft", "VisualStudio");
        if (!Directory.Exists(vsRoot))
            yield break;

        List<DirectoryInfo> vsDirs;
        try
        {
            vsDirs = new DirectoryInfo(vsRoot).GetDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in vsDirs)
        {
            string settingsXml = Path.Combine(dir.FullName, "ApplicationPrivateSettings.xml");
            if (!File.Exists(settingsXml)) continue;

            XDocument doc;
            try { doc = XDocument.Parse(ReadTextShared(settingsXml)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }

            var collection = doc.Descendants("collection")
                .FirstOrDefault(c => string.Equals(c.Attribute("name")?.Value, "CodeContainers.Offline", StringComparison.OrdinalIgnoreCase));
            string? jsonRaw = collection?.Descendants("value").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(jsonRaw)) continue;

            JsonDocument jsonDoc;
            try { jsonDoc = JsonDocument.Parse(jsonRaw); }
            catch (JsonException) { continue; }

            using (jsonDoc)
            {
                if (jsonDoc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in jsonDoc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("Key", out var kEl) || kEl.ValueKind != JsonValueKind.String)
                        continue;
                    string? path = kEl.GetString();
                    if (string.IsNullOrWhiteSpace(path)) continue;

                    bool isDir = Directory.Exists(path);
                    bool isFile = !isDir && File.Exists(path);
                    if (!isDir && !isFile) continue;

                    DateTime lastAccessed = DateTime.MinValue;
                    bool isFav = false;
                    if (el.TryGetProperty("Value", out var valObj) && valObj.ValueKind == JsonValueKind.Object)
                    {
                        if (valObj.TryGetProperty("LastAccessed", out var laEl) &&
                            laEl.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(laEl.GetString(), out var dto))
                        {
                            lastAccessed = dto.LocalDateTime;
                        }
                        if (valObj.TryGetProperty("IsFavorite", out var favEl) && favEl.ValueKind == JsonValueKind.True)
                        {
                            isFav = true;
                        }
                    }

                    AppRecentItemKind kind = isDir ? AppRecentItemKind.Folder : AppRecentItemKind.Workspace;
                    string name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(name)) name = path;

                    yield return new AppRecentItem(
                        Name: name,
                        FullPath: path,
                        Subtitle: Path.GetDirectoryName(path) ?? path,
                        Kind: kind,
                        LastOpenedTime: lastAccessed > DateTime.MinValue ? lastAccessed : SafeGetLastWriteTime(path, isDir),
                        PinnedByUser: isFav);
                }
            }
        }
    }
}
