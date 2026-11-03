using System.IO;
using System.Text.Json;
using static PinDock.RecentIo;
using static PinDock.SqliteReader;

namespace PinDock;

public sealed class VsCodeFamilyRecentProvider : IRecentProvider
{
    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        string roamingRoot = roots.Roaming;
        var userDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(target.ExecutableName) &&
            VsCodeFamilyAppDataDirs.TryGetValue(target.ExecutableName, out string? appDataFolder))
        {
            string roamingUserDir = Path.Combine(roamingRoot, appDataFolder, "User");
            if (Directory.Exists(roamingUserDir))
                userDirs.Add(roamingUserDir);
        }

        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && File.Exists(target.ExecutablePath))
        {
            string? exeDir = Path.GetDirectoryName(target.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                string portableUserDir = Path.Combine(exeDir, "data", "user-data", "User");
                if (Directory.Exists(portableUserDir) && !userDirs.Contains(portableUserDir, StringComparer.OrdinalIgnoreCase))
                    userDirs.Add(portableUserDir);
            }
        }

        if (userDirs.Count == 0)
            yield break;

        foreach (string userDir in userDirs)
        {
            var workspaceTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var currentlyOpenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var orderedUris = new List<string>();
            var discoveredUris = new List<string>();

            // A. workspaceStorage\*\workspace.json + state.vscdb timestamp
            string wsStorageDir = Path.Combine(userDir, "workspaceStorage");
            if (Directory.Exists(wsStorageDir))
            {
                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(wsStorageDir))
                    {
                        string wjPath = Path.Combine(subDir, "workspace.json");
                        if (!File.Exists(wjPath)) continue;
                        try
                        {
                            using var doc = JsonDocument.Parse(ReadTextShared(wjPath));
                            var root = doc.RootElement;
                            string? uri = null;
                            if (root.TryGetProperty("workspace", out var wEl) && wEl.ValueKind == JsonValueKind.String)
                                uri = wEl.GetString();
                            else if (root.TryGetProperty("folder", out var fEl) && fEl.ValueKind == JsonValueKind.String)
                                uri = fEl.GetString();

                            if (string.IsNullOrWhiteSpace(uri)) continue;

                            string stateDbPath = Path.Combine(subDir, "state.vscdb");
                            DateTime ts = File.Exists(stateDbPath)
                                ? File.GetLastWriteTime(stateDbPath)
                                : File.GetLastWriteTime(wjPath);

                            string normUri = NormalizeVsCodeUriKey(uri);
                            if (!workspaceTimestamps.TryGetValue(normUri, out var prevTs) || ts > prevTs)
                                workspaceTimestamps[normUri] = ts;

                            discoveredUris.Add(uri);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                        {
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            // B. globalStorage\storage.json
            string storageJsonPath = Path.Combine(userDir, "globalStorage", "storage.json");
            if (File.Exists(storageJsonPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(ReadTextShared(storageJsonPath));
                    var root = doc.RootElement;

                    void AddWindowUri(string? u, bool active)
                    {
                        if (string.IsNullOrWhiteSpace(u)) return;
                        orderedUris.Add(u);
                        if (active)
                            currentlyOpenUris.Add(NormalizeVsCodeUriKey(u));
                    }

                    if (root.TryGetProperty("windowsState", out var winState) && winState.ValueKind == JsonValueKind.Object)
                    {
                        if (winState.TryGetProperty("lastActiveWindow", out var lastWin) && lastWin.ValueKind == JsonValueKind.Object)
                        {
                            AddWindowUri(ExtractWindowUri(lastWin), active: true);
                        }
                        if (winState.TryGetProperty("openedWindows", out var openedWins) && openedWins.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in openedWins.EnumerateArray())
                            {
                                if (w.ValueKind == JsonValueKind.Object)
                                    AddWindowUri(ExtractWindowUri(w), active: true);
                            }
                        }
                    }

                    if (root.TryGetProperty("backupWorkspaces", out var bw) && bw.ValueKind == JsonValueKind.Object)
                    {
                        if (bw.TryGetProperty("workspaces", out var bwWs) && bwWs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in bwWs.EnumerateArray())
                            {
                                if (w.ValueKind == JsonValueKind.Object &&
                                    w.TryGetProperty("configURIPath", out var cup) &&
                                    cup.ValueKind == JsonValueKind.String)
                                {
                                    AddWindowUri(cup.GetString(), active: true);
                                }
                            }
                        }
                        if (bw.TryGetProperty("folders", out var bwFolders) && bwFolders.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var f in bwFolders.EnumerateArray())
                            {
                                if (f.ValueKind == JsonValueKind.Object &&
                                    f.TryGetProperty("folderUri", out var fu) &&
                                    fu.ValueKind == JsonValueKind.String)
                                {
                                    AddWindowUri(fu.GetString(), active: true);
                                }
                            }
                        }
                    }

                    if (root.TryGetProperty("profileAssociations", out var pa) &&
                        pa.ValueKind == JsonValueKind.Object &&
                        pa.TryGetProperty("workspaces", out var paWs) &&
                        paWs.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in paWs.EnumerateObject())
                        {
                            discoveredUris.Add(prop.Name);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                }
            }

            // C. globalStorage\state.vscdb -> history.recentlyOpenedPathsList
            string stateVscdbPath = Path.Combine(userDir, "globalStorage", "state.vscdb");
            if (File.Exists(stateVscdbPath))
            {
                string? recentJson = TryQuerySqliteSingleValue(
                    stateVscdbPath,
                    "SELECT value FROM ItemTable WHERE key = 'history.recentlyOpenedPathsList'");
                if (!string.IsNullOrWhiteSpace(recentJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(recentJson);
                        if (doc.RootElement.TryGetProperty("entries", out var entriesEl) &&
                            entriesEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entryEl in entriesEl.EnumerateArray())
                            {
                                if (entryEl.ValueKind != JsonValueKind.Object) continue;
                                if (entryEl.TryGetProperty("folderUri", out var fu) && fu.ValueKind == JsonValueKind.String)
                                {
                                    orderedUris.Add(fu.GetString()!);
                                }
                                else if (entryEl.TryGetProperty("workspace", out var wsObj) &&
                                         wsObj.ValueKind == JsonValueKind.Object &&
                                         wsObj.TryGetProperty("configPath", out var cp) &&
                                         cp.ValueKind == JsonValueKind.String)
                                {
                                    orderedUris.Add(cp.GetString()!);
                                }
                                else if (entryEl.TryGetProperty("fileUri", out var fileU) && fileU.ValueKind == JsonValueKind.String)
                                {
                                    orderedUris.Add(fileU.GetString()!);
                                }
                            }
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
            }

            // D. Merge orderedUris + discoveredUris
            DateTime syntheticBase = DateTime.Now;
            int rankIndex = 0;

            foreach (string rawUri in orderedUris.Concat(discoveredUris))
            {
                if (!TryParseVsCodeUri(rawUri, out var parsed))
                    continue;

                string normUri = NormalizeVsCodeUriKey(rawUri);
                bool isOpen = currentlyOpenUris.Contains(normUri);

                DateTime ts = DateTime.MinValue;
                if (workspaceTimestamps.TryGetValue(normUri, out var wsTs))
                {
                    ts = wsTs;
                }
                else if (rankIndex < orderedUris.Count)
                {
                    ts = parsed.LastOpenedTime > DateTime.MinValue
                        ? parsed.LastOpenedTime
                        : syntheticBase.AddMinutes(-rankIndex);
                }
                else
                {
                    ts = parsed.LastOpenedTime;
                }

                rankIndex++;
                yield return parsed with
                {
                    LastOpenedTime = ts,
                    IsCurrentlyOpen = isOpen
                };
            }
        }
    }

    private static readonly Dictionary<string, string> VsCodeFamilyAppDataDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code.exe"] = "Code",
        ["code - insiders.exe"] = "Code - Insiders",
        ["cursor.exe"] = "Cursor",
        ["windsurf.exe"] = "Windsurf",
        ["kiro.exe"] = "Kiro",
        ["trae.exe"] = "Trae",
        ["vscodium.exe"] = "VSCodium",
        ["antigravity.exe"] = "Antigravity"
    };

    private static string? ExtractWindowUri(JsonElement winObj)
    {
        if (winObj.TryGetProperty("folder", out var fEl) && fEl.ValueKind == JsonValueKind.String)
            return fEl.GetString();
        if (winObj.TryGetProperty("workspaceIdentifier", out var wsEl) &&
            wsEl.ValueKind == JsonValueKind.Object &&
            wsEl.TryGetProperty("configURIPath", out var cupEl) &&
            cupEl.ValueKind == JsonValueKind.String)
            return cupEl.GetString();
        return null;
    }

    private static string NormalizeVsCodeUriKey(string rawUri)
    {
        try
        {
            return Uri.UnescapeDataString(rawUri.Trim()).TrimEnd('/');
        }
        catch (UriFormatException)
        {
            return rawUri.Trim().TrimEnd('/');
        }
    }

    public static bool TryParseVsCodeUri(string rawUri, out AppRecentItem item)
    {
        item = default;
        if (string.IsNullOrWhiteSpace(rawUri))
            return false;

        string trimmed = rawUri.Trim();

        // 1. file:// URI
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(trimmed); }
            catch (UriFormatException) { unescaped = trimmed; }

            string localPath = unescaped.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                ? unescaped[8..]
                : unescaped[7..];

            localPath = localPath.Replace('/', '\\');
            if (localPath.Length >= 2 && char.IsLetter(localPath[0]) && localPath[1] == ':')
            {
                localPath = char.ToUpperInvariant(localPath[0]) + localPath[1..];
            }
            if (localPath.Length > 3)
            {
                localPath = localPath.TrimEnd('\\');
            }

            bool isWorkspaceFile = localPath.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
            bool isDir = !isWorkspaceFile && Directory.Exists(localPath);
            bool isFile = !isDir && File.Exists(localPath);

            if (!isDir && !isFile)
                return false;

            AppRecentItemKind kind = isWorkspaceFile
                ? AppRecentItemKind.Workspace
                : isDir
                    ? AppRecentItemKind.Folder
                    : AppRecentItemKind.File;

            string name = isWorkspaceFile
                ? Path.GetFileNameWithoutExtension(localPath)
                : Path.GetFileName(localPath);
            if (string.IsNullOrWhiteSpace(name))
                name = localPath;

            string subtitle = Path.GetDirectoryName(localPath) ?? localPath;
            DateTime lastWrite = SafeGetLastWriteTime(localPath, isDir);

            item = new AppRecentItem(
                Name: name,
                FullPath: localPath,
                Subtitle: subtitle,
                Kind: kind,
                LastOpenedTime: lastWrite);
            return true;
        }

        // 2. vscode-remote:// URI
        if (trimmed.StartsWith("vscode-remote://", StringComparison.OrdinalIgnoreCase))
        {
            string unescaped;
            try { unescaped = Uri.UnescapeDataString(trimmed); }
            catch (UriFormatException) { unescaped = trimmed; }

            string withoutScheme = unescaped["vscode-remote://".Length..];
            int slashIdx = withoutScheme.IndexOf('/');
            if (slashIdx <= 0)
                return false;

            string authority = withoutScheme[..slashIdx];
            string remotePath = withoutScheme[slashIdx..];
            if (remotePath.Length > 1)
                remotePath = remotePath.TrimEnd('/');

            string hostBadge = DecodeRemoteAuthorityBadge(authority);
            bool isWorkspace = remotePath.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase);
            string baseName = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? remotePath;
            if (isWorkspace && baseName.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase))
                baseName = baseName[..^".code-workspace".Length];

            string displayName = string.IsNullOrWhiteSpace(hostBadge)
                ? baseName
                : $"{baseName} [{hostBadge}]";

            string launchArg = isWorkspace
                ? $"--file-uri \"{trimmed}\""
                : $"--folder-uri \"{trimmed}\"";

            item = new AppRecentItem(
                Name: displayName,
                FullPath: trimmed,
                Subtitle: remotePath,
                Kind: AppRecentItemKind.Remote,
                LastOpenedTime: DateTime.MinValue,
                LaunchArguments: launchArg);
            return true;
        }

        return false;
    }

    private static string DecodeRemoteAuthorityBadge(string authority)
    {
        int plusIdx = authority.IndexOf('+');
        if (plusIdx < 0)
            return authority;

        string prefix = authority[..plusIdx];
        string payload = authority[(plusIdx + 1)..];

        string decodedPayload = TryDecodeHexUtf8(payload) ?? payload;
        if (decodedPayload.StartsWith('{') && decodedPayload.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(decodedPayload);
                if (doc.RootElement.TryGetProperty("hostName", out var hn) && hn.ValueKind == JsonValueKind.String)
                    decodedPayload = hn.GetString() ?? decodedPayload;
                else if (doc.RootElement.TryGetProperty("ContainerName", out var cn) && cn.ValueKind == JsonValueKind.String)
                    decodedPayload = cn.GetString() ?? decodedPayload;
            }
            catch (JsonException)
            {
            }
        }

        string kindLabel = prefix.ToLowerInvariant() switch
        {
            "ssh-remote" => "SSH",
            "wsl" => "WSL",
            "dev-container" or "attached-container" => "Container",
            "tunnel" => "Tunnel",
            _ => prefix
        };
        return $"{kindLabel}: {decodedPayload}";
    }
}
