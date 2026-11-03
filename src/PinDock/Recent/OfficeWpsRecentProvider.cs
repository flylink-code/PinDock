using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using static PinDock.RecentIo;

namespace PinDock;

public sealed class OfficeWpsRecentProvider : IRecentProvider
{
    public IEnumerable<AppRecentItem> Collect(ResolvedAppTarget target, RecentRoots roots)
    {
        string? officeApp = target.ExecutableName.ToLowerInvariant() switch
        {
            "winword.exe" => "Word",
            "excel.exe" => "Excel",
            "powerpnt.exe" => "PowerPoint",
            "visio.exe" => "Visio",
            "msaccess.exe" => "Access",
            _ => null
        };

        if (officeApp is not null)
        {
            foreach (string ver in new[] { "16.0", "15.0", "14.0" })
            {
                foreach (var item in ReadOfficeRegistryMru($@"Software\Microsoft\Office\{ver}\{officeApp}\File MRU"))
                    yield return item;

                string userMruRoot = $@"Software\Microsoft\Office\{ver}\{officeApp}\User MRU";
                using var uKey = Registry.CurrentUser.OpenSubKey(userMruRoot);
                if (uKey is not null)
                {
                    foreach (string sub in uKey.GetSubKeyNames())
                    {
                        foreach (var item in ReadOfficeRegistryMru($@"{userMruRoot}\{sub}\File MRU"))
                            yield return item;
                    }
                }
            }
        }

        string? wpsSub = target.ExecutableName.ToLowerInvariant() switch
        {
            "wps.exe" => "wps",
            "et.exe" => "et",
            "wpp.exe" => "wpp",
            _ => null
        };
        if (wpsSub is not null)
        {
            using var wpsKey = Registry.CurrentUser.OpenSubKey($@"Software\Kingsoft\Office\6.0\{wpsSub}\RecentFiles\Sequence");
            if (wpsKey is not null)
            {
                foreach (string valName in wpsKey.GetValueNames())
                {
                    if (wpsKey.GetValue(valName) is string path && !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
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
    }

    private static IEnumerable<AppRecentItem> ReadOfficeRegistryMru(string subKeyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        if (key is null) yield break;

        foreach (string valName in key.GetValueNames())
        {
            if (!valName.StartsWith("Item ", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.GetValue(valName) is not string rawVal) continue;

            int starIdx = rawVal.IndexOf('*');
            if (starIdx < 0 || starIdx + 1 >= rawVal.Length) continue;
            string path = rawVal[(starIdx + 1)..].Trim();
            if (!File.Exists(path)) continue;

            DateTime ts = SafeGetLastWriteTime(path, false);
            var tMatch = Regex.Match(rawVal[..starIdx], @"\[T([0-9A-Fa-f]{16})\]");
            if (tMatch.Success && long.TryParse(tMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long fileTime) && fileTime > 0)
            {
                try { ts = DateTime.FromFileTimeUtc(fileTime).ToLocalTime(); }
                catch (ArgumentOutOfRangeException) { }
            }

            yield return new AppRecentItem(
                Name: Path.GetFileName(path),
                FullPath: path,
                Subtitle: Path.GetDirectoryName(path) ?? path,
                Kind: AppRecentItemKind.File,
                LastOpenedTime: ts);
        }
    }
}
