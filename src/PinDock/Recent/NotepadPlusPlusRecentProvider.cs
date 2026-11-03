using System.IO;
using System.Xml.Linq;
using static PinDock.RecentIo;

namespace PinDock;

public sealed class NotepadPlusPlusRecentProvider : IRecentProvider
{
    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        string roamingRoot = roots.Roaming;
        if (!string.Equals(target.ExecutableName, "notepad++.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        var configPaths = new List<string> { Path.Combine(roamingRoot, "Notepad++", "config.xml") };
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            string? exeDir = Path.GetDirectoryName(target.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(exeDir))
                configPaths.Add(Path.Combine(exeDir, "config.xml"));
        }

        foreach (string cfg in configPaths)
        {
            if (!File.Exists(cfg)) continue;
            XDocument doc;
            try { doc = XDocument.Parse(ReadTextShared(cfg)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }

            foreach (var fileEl in doc.Descendants("History").Elements("File"))
            {
                string? path = fileEl.Attribute("filename")?.Value;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                yield return new AppRecentItem(
                    Name: Path.GetFileName(path),
                    FullPath: path,
                    Subtitle: Path.GetDirectoryName(path) ?? path,
                    Kind: AppRecentItemKind.File,
                    LastOpenedTime: SafeGetLastWriteTime(path, false));
            }
        }
    }
}
