using System.IO;
using System.Text.Json;
using static PinDock.RecentIo;

namespace PinDock;

public sealed class SublimeRecentProvider : IRecentProvider
{
    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        string roamingRoot = roots.Roaming;
        if (!string.Equals(target.ExecutableName, "sublime_text.exe", StringComparison.OrdinalIgnoreCase))
            yield break;

        foreach (string folderName in new[] { "Sublime Text", "Sublime Text 3" })
        {
            string localDir = Path.Combine(roamingRoot, folderName, "Local");
            if (!Directory.Exists(localDir)) continue;

            foreach (string sessionFile in new[] { "Auto Save Session.sublime_session", "Session.sublime_session" })
            {
                string fullSession = Path.Combine(localDir, sessionFile);
                if (!File.Exists(fullSession)) continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(ReadTextShared(fullSession)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("workspaces", out var wsEl) &&
                        wsEl.ValueKind == JsonValueKind.Object &&
                        wsEl.TryGetProperty("recent_workspaces", out var rwArr) &&
                        rwArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var w in rwArr.EnumerateArray())
                        {
                            if (w.ValueKind == JsonValueKind.String &&
                                TryConvertSublimePath(w.GetString(), out string winPath) &&
                                File.Exists(winPath))
                            {
                                yield return new AppRecentItem(
                                    Name: Path.GetFileNameWithoutExtension(winPath),
                                    FullPath: winPath,
                                    Subtitle: Path.GetDirectoryName(winPath) ?? winPath,
                                    Kind: AppRecentItemKind.Workspace,
                                    LastOpenedTime: SafeGetLastWriteTime(winPath, false));
                            }
                        }
                    }

                    if (root.TryGetProperty("folder_history", out var fhArr) && fhArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in fhArr.EnumerateArray())
                        {
                            if (f.ValueKind == JsonValueKind.String &&
                                TryConvertSublimePath(f.GetString(), out string winPath) &&
                                Directory.Exists(winPath))
                            {
                                string name = Path.GetFileName(winPath);
                                if (string.IsNullOrWhiteSpace(name)) name = winPath;
                                yield return new AppRecentItem(
                                    Name: name,
                                    FullPath: winPath,
                                    Subtitle: Path.GetDirectoryName(winPath) ?? winPath,
                                    Kind: AppRecentItemKind.Folder,
                                    LastOpenedTime: SafeGetLastWriteTime(winPath, true));
                            }
                        }
                    }
                }
            }
        }
    }

    private static bool TryConvertSublimePath(string? raw, out string windowsPath)
    {
        windowsPath = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string s = raw.Trim();
        if (s.Length >= 3 && s[0] == '/' && char.IsLetter(s[1]) && s[2] == '/')
        {
            windowsPath = $"{char.ToUpperInvariant(s[1])}:\\{s[3..].Replace('/', '\\')}";
            if (windowsPath.Length > 3) windowsPath = windowsPath.TrimEnd('\\');
            return true;
        }
        if (s.Length >= 2 && char.IsLetter(s[0]) && s[1] == ':')
        {
            windowsPath = s.Replace('/', '\\');
            if (windowsPath.Length > 3) windowsPath = windowsPath.TrimEnd('\\');
            return true;
        }
        return false;
    }
}
