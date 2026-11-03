using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Win32;
using static PinDock.RecentIo;
using static PinDock.ShellLinkParser;

namespace PinDock;

public sealed class JumpListRecentProvider : IRecentProvider
{
    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        string roamingRoot = roots.Roaming;
        string recentRoot = Path.Combine(roamingRoot, "Microsoft", "Windows", "Recent");
        if (!Directory.Exists(recentRoot))
            yield break;

        var candidateHashes = ComputeCandidateJumpListHashes(target);
        string customDestDir = Path.Combine(recentRoot, "CustomDestinations");
        string autoDestDir = Path.Combine(recentRoot, "AutomaticDestinations");

        // A. Scan CustomDestinations (both by candidate hash and by embedded TargetPath matching our executable)
        if (Directory.Exists(customDestDir))
        {
            string[] customFiles;
            try { customFiles = Directory.GetFiles(customDestDir, "*.customDestinations-ms"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { customFiles = []; }

            foreach (string customFile in customFiles)
            {
                string hashPrefix = Path.GetFileNameWithoutExtension(customFile);
                bool hashMatched = candidateHashes.Contains(hashPrefix);

                List<ParsedShortcutLink> links;
                try { links = ParseEmbeddedLinks(ReadFileShared(customFile)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                bool targetMatched = hashMatched || links.Any(l => IsSameExecutable(l.TargetPath, target));
                if (!targetMatched) continue;

                candidateHashes.Add(hashPrefix);
                DateTime fileWrite = File.GetLastWriteTime(customFile);
                int idx = 0;

                foreach (var lnk in links)
                {
                    if (TryExtractRecentFromCustomLink(lnk, fileWrite.AddSeconds(-idx), out var item))
                    {
                        idx++;
                        yield return item;
                    }
                }
            }
        }

        // B. Read AutomaticDestinations for all matched JumpList hashes
        if (Directory.Exists(autoDestDir))
        {
            foreach (string hash in candidateHashes)
            {
                string autoFile = Path.Combine(autoDestDir, hash + ".automaticDestinations-ms");
                if (!File.Exists(autoFile)) continue;

                List<ParsedShortcutLink> links;
                try { links = ParseEmbeddedLinks(ReadFileShared(autoFile)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                DateTime baseTime = File.GetLastWriteTime(autoFile);
                int idx = 0;
                foreach (var lnk in links)
                {
                    string path = lnk.TargetPath;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    bool isDir = Directory.Exists(path);
                    bool isFile = !isDir && File.Exists(path);
                    if (!isDir && !isFile) continue;

                    string ext = Path.GetExtension(path);
                    if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string name = isDir ? Path.GetFileName(path) : Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(name)) name = path;

                    idx++;
                    yield return new AppRecentItem(
                        Name: name,
                        FullPath: path,
                        Subtitle: Path.GetDirectoryName(path) ?? path,
                        Kind: isDir ? AppRecentItemKind.Folder : AppRecentItemKind.File,
                        LastOpenedTime: baseTime.AddSeconds(-idx));
                }
            }
        }

        // C. Match Windows\Recent\*.lnk by file extension association
        if (!string.IsNullOrWhiteSpace(target.ExecutablePath) && File.Exists(target.ExecutablePath))
        {
            var supportedExts = GetRegisteredSupportedExtensions(target.ExecutableName);
            List<FileInfo> recentLinks;
            try
            {
                recentLinks = new DirectoryInfo(recentRoot)
                    .GetFiles("*.lnk")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(50)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                recentLinks = [];
            }

            foreach (var lnkInfo in recentLinks)
            {
                string innerName = Path.GetFileNameWithoutExtension(lnkInfo.Name);
                string ext = Path.GetExtension(innerName);
                if (string.IsNullOrWhiteSpace(ext)) continue;

                bool associated = supportedExts.Contains(ext);
                if (!associated)
                {
                    string assocExe = GetDefaultAssociatedExecutable(ext);
                    associated = !string.IsNullOrWhiteSpace(assocExe) && IsSameExecutable(assocExe, target);
                }
                if (!associated) continue;

                List<ParsedShortcutLink> parsed;
                try { parsed = ParseEmbeddedLinks(ReadFileShared(lnkInfo.FullName)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (parsed.Count == 0 || string.IsNullOrWhiteSpace(parsed[0].TargetPath))
                    continue;

                string targetFile = parsed[0].TargetPath;
                if (!File.Exists(targetFile)) continue;

                yield return new AppRecentItem(
                    Name: Path.GetFileName(targetFile),
                    FullPath: targetFile,
                    Subtitle: Path.GetDirectoryName(targetFile) ?? targetFile,
                    Kind: AppRecentItemKind.File,
                    LastOpenedTime: lnkInfo.LastWriteTime);
            }
        }
    }

    private static readonly ConcurrentDictionary<string, string> ExtensionAssocCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool TryExtractRecentFromCustomLink(ParsedShortcutLink lnk, DateTime fallbackTime, out AppRecentItem item)
    {
        item = default;
        string args = lnk.Arguments.Trim();

        // Check --folder-uri / --file-uri
        var uriMatch = Regex.Match(args, @"--(?:folder|file)-uri\s+(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase);
        if (uriMatch.Success)
        {
            string rawUri = !string.IsNullOrEmpty(uriMatch.Groups[1].Value)
                ? uriMatch.Groups[1].Value
                : uriMatch.Groups[2].Value;
            if (VsCodeFamilyRecentProvider.TryParseVsCodeUri(rawUri, out var parsedUri))
            {
                item = parsedUri with
                {
                    LastOpenedTime = parsedUri.LastOpenedTime > DateTime.MinValue ? parsedUri.LastOpenedTime : fallbackTime
                };
                return true;
            }
        }

        // Check if arguments or link name is a direct local path
        foreach (string candidate in new[] { TryExtractLocalPath(args), TryExtractLocalPath(lnk.Name) })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            bool isDir = Directory.Exists(candidate);
            bool isFile = !isDir && File.Exists(candidate);
            if (!isDir && !isFile) continue;

            string ext = Path.GetExtension(candidate);
            if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isWs = string.Equals(ext, ".code-workspace", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase);

            AppRecentItemKind kind = isWs
                ? AppRecentItemKind.Workspace
                : isDir
                    ? AppRecentItemKind.Folder
                    : AppRecentItemKind.File;

            string name = isWs ? Path.GetFileNameWithoutExtension(candidate) : Path.GetFileName(candidate);
            if (string.IsNullOrWhiteSpace(name)) name = candidate;

            item = new AppRecentItem(
                Name: name,
                FullPath: candidate,
                Subtitle: Path.GetDirectoryName(candidate) ?? candidate,
                Kind: kind,
                LastOpenedTime: fallbackTime);
            return true;
        }

        return false;
    }

    private static string TryExtractLocalPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string trimmed = input.Trim().Trim('"');
        if (trimmed.Length >= 3 && char.IsLetter(trimmed[0]) && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/'))
            return trimmed.Replace('/', '\\');

        var match = Regex.Match(input, @"""([A-Za-z]:\\[^""]+)""|([A-Za-z]:\\[^\s""]+)");
        if (match.Success)
        {
            return !string.IsNullOrEmpty(match.Groups[1].Value) ? match.Groups[1].Value : match.Groups[2].Value;
        }
        return "";
    }

    private static HashSet<string> ComputeCandidateJumpListHashes(ResolvedAppTarget target)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(target.AppUserModelId))
            hashes.Add(ComputeJumpListHash(target.AppUserModelId));

        if (!string.IsNullOrWhiteSpace(target.ExecutablePath))
        {
            string fullExe = target.ExecutablePath;
            hashes.Add(ComputeJumpListHash(fullExe));

            var knownFolders = new (Environment.SpecialFolder Folder, string GuidStr)[]
            {
                (Environment.SpecialFolder.ProgramFiles, "{6D809377-6AF0-444B-8957-A3773F02200E}"),
                (Environment.SpecialFolder.ProgramFilesX86, "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
                (Environment.SpecialFolder.System, "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
                (Environment.SpecialFolder.Windows, "{F38BF404-1D43-42F2-9305-67DE0B28FC23}"),
                (Environment.SpecialFolder.LocalApplicationData, "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"),
                (Environment.SpecialFolder.ApplicationData, "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}")
            };

            foreach (var (sf, guidStr) in knownFolders)
            {
                string folderPath = Environment.GetFolderPath(sf);
                if (!string.IsNullOrWhiteSpace(folderPath) &&
                    fullExe.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase))
                {
                    string substituted = guidStr + fullExe[folderPath.Length..];
                    hashes.Add(ComputeJumpListHash(substituted));
                }
            }
        }
        return hashes;
    }

    public static string ComputeJumpListHash(string appIdOrPath)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(appIdOrPath.ToUpperInvariant());
        ulong crc = 0xFFFFFFFFFFFFFFFFUL;
        const ulong poly = 0x92C64265D32139A4UL;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }
        }
        return crc.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static bool IsSameExecutable(string pathA, ResolvedAppTarget target)
    {
        if (string.IsNullOrWhiteSpace(pathA)) return false;
        if (string.Equals(pathA.Trim(), target.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return true;
        string nameA = Path.GetFileName(pathA);
        return !string.IsNullOrWhiteSpace(nameA) &&
               string.Equals(nameA, target.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetRegisteredSupportedExtensions(string exeName)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(exeName)) return set;

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"Software\Classes\Applications\{exeName}\SupportedTypes");
            if (key is null) continue;
            foreach (string valName in key.GetValueNames())
            {
                if (valName.StartsWith('.'))
                    set.Add(valName);
            }
        }
        return set;
    }

    private static string GetDefaultAssociatedExecutable(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return "";
        return ExtensionAssocCache.GetOrAdd(extension, ext =>
        {
            uint cch = 1024;
            var sb = new StringBuilder((int)cch);
            if (AssocQueryStringW(0, 2 /* ASSOCSTR_EXECUTABLE */, ext, null, sb, ref cch) == 0)
                return sb.ToString();
            return "";
        });
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryStringW(int flags, int str, string pszAssoc, string? pszExtra, StringBuilder pszOut, ref uint pcchOut);
}
