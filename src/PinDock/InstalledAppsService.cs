using System.IO;

namespace PinDock;

public readonly record struct InstalledApp(string Name, string Path, string Category);

public sealed class InstalledAppsService
{
    private static readonly string[] IgnoredKeywords =
        ["uninstall", "卸载", "remove", "readme", "说明", "帮助", "help", "website", "官网", "manual"];

    private readonly object sync = new();
    private IReadOnlyList<InstalledApp>? cached;

    public InstalledAppsService()
    {
        _ = Task.Run(() => GetInstalledApps());
    }

    public IReadOnlyList<InstalledApp> GetInstalledApps(bool forceRefresh = false)
    {
        lock (sync)
        {
            if (cached is not null && !forceRefresh) return cached;
            var roots = new List<(string Path, bool Recursive, string Category)>
            {
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"), true, "开始菜单"),
                (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"), true, "开始菜单"),
                (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), false, "桌面"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), false, "桌面")
            };
            cached = ScanRoots(roots);
            return cached;
        }
    }

    public static IReadOnlyList<InstalledApp> DiscoverFromDirectories(IEnumerable<string> directories, string category = "本机应用")
    {
        var roots = directories.Select(d => (d, true, category));
        return ScanRoots(roots);
    }

    private static IReadOnlyList<InstalledApp> ScanRoots(IEnumerable<(string Path, bool Recursive, string Category)> roots)
    {
        var results = new List<InstalledApp>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, recursive, category) in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            foreach (string file in EnumerateCandidateFiles(root, recursive))
            {
                string name = Path.GetFileNameWithoutExtension(file).Trim();
                if (name.Length == 0 || ShouldIgnore(name)) continue;
                if (!seenPaths.Add(file) || !seenNames.Add(name)) continue;
                results.Add(new InstalledApp(name, file, category));
            }
        }

        results.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string root, bool recursive)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(current); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file);
                if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }

            if (!recursive) continue;
            try
            {
                foreach (string sub in Directory.GetDirectories(current))
                    stack.Push(sub);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }
    }

    private static bool ShouldIgnore(string name)
    {
        foreach (string keyword in IgnoredKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
