using System.IO;
using System.Xml.Linq;
using static PinDock.RecentIo;

namespace PinDock;

public sealed class JetBrainsRecentProvider : IRecentProvider
{
    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        string roamingRoot = roots.Roaming;
        if (string.IsNullOrWhiteSpace(target.ExecutableName))
            yield break;

        string exeBase = Path.GetFileNameWithoutExtension(target.ExecutableName);
        string[]? matchedPatterns = null;
        foreach (var (prefix, patterns) in JetBrainsFamilies)
        {
            if (exeBase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                matchedPatterns = patterns;
                break;
            }
        }
        if (matchedPatterns is null)
            yield break;

        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var productDirs = new List<DirectoryInfo>();

        foreach (string pattern in matchedPatterns)
        {
            string? parentRel = Path.GetDirectoryName(pattern);
            string searchMask = Path.GetFileName(pattern);
            string parentDir = string.IsNullOrWhiteSpace(parentRel) ? roamingRoot : Path.Combine(roamingRoot, parentRel);
            if (!Directory.Exists(parentDir)) continue;
            try
            {
                productDirs.AddRange(new DirectoryInfo(parentDir).GetDirectories(searchMask));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        productDirs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));

        foreach (var prodDir in productDirs)
        {
            foreach (string xmlFileName in new[] { "recentProjects.xml", "recentSolutions.xml" })
            {
                string xmlPath = Path.Combine(prodDir.FullName, "options", xmlFileName);
                if (!File.Exists(xmlPath)) continue;

                XDocument doc;
                try { doc = XDocument.Parse(ReadTextShared(xmlPath)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }

                foreach (var entry in doc.Descendants("entry"))
                {
                    string? rawKey = entry.Attribute("key")?.Value;
                    if (string.IsNullOrWhiteSpace(rawKey)) continue;

                    string fullPath = rawKey
                        .Replace("$USER_HOME$", userHome, StringComparison.OrdinalIgnoreCase)
                        .Replace('/', '\\');
                    if (fullPath.Length > 3)
                        fullPath = fullPath.TrimEnd('\\');

                    bool isDir = Directory.Exists(fullPath);
                    bool isFile = !isDir && File.Exists(fullPath);
                    if (!isDir && !isFile) continue;

                    var meta = entry.Descendants("RecentProjectMetaInfo").FirstOrDefault();
                    string? displayName = meta?.Attribute("displayName")?.Value;

                    long tsMs = 0;
                    foreach (var opt in entry.Descendants("option"))
                    {
                        string? optName = opt.Attribute("name")?.Value;
                        if (optName is "projectOpenTimestamp" or "buildTimestamp" &&
                            long.TryParse(opt.Attribute("value")?.Value, out long parsedMs) &&
                            parsedMs > tsMs)
                        {
                            tsMs = parsedMs;
                        }
                    }

                    DateTime openedTime = tsMs > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime
                        : SafeGetLastWriteTime(fullPath, isDir);

                    AppRecentItemKind kind = isDir ? AppRecentItemKind.Folder : AppRecentItemKind.Workspace;
                    string name = !string.IsNullOrWhiteSpace(displayName)
                        ? displayName
                        : (isDir ? Path.GetFileName(fullPath) : Path.GetFileNameWithoutExtension(fullPath));
                    if (string.IsNullOrWhiteSpace(name))
                        name = fullPath;

                    yield return new AppRecentItem(
                        Name: name,
                        FullPath: fullPath,
                        Subtitle: Path.GetDirectoryName(fullPath) ?? fullPath,
                        Kind: kind,
                        LastOpenedTime: openedTime);
                }
            }
        }
    }

    private static readonly (string ExePrefix, string[] RoamingDirs)[] JetBrainsFamilies =
    [
        ("clion", [@"JetBrains\CLion*"]),
        ("idea", [@"JetBrains\IntelliJIdea*", @"JetBrains\IdeaIC*"]),
        ("pycharm", [@"JetBrains\PyCharm*", @"JetBrains\PyCharmCE*"]),
        ("webstorm", [@"JetBrains\WebStorm*"]),
        ("goland", [@"JetBrains\GoLand*"]),
        ("rider", [@"JetBrains\Rider*"]),
        ("phpstorm", [@"JetBrains\PhpStorm*"]),
        ("rustrover", [@"JetBrains\RustRover*"]),
        ("datagrip", [@"JetBrains\DataGrip*"]),
        ("rubymine", [@"JetBrains\RubyMine*"]),
        ("studio", [@"Google\AndroidStudio*"])
    ];
}
