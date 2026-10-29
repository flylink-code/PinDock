using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace PinDock;

public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseNotes,
    string ReleasePageUrl,
    string? AssetName,
    string? DownloadUrl);

public static class UpdateService
{
    public const string RepoSlug = "flylink-code/PinDock";
    public const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoSlug}/releases/latest";
    public const string ReleasesPageUrl = $"https://github.com/{RepoSlug}/releases";

    private static readonly HttpClient Http = CreateHttpClient();

    public static string GetCurrentVersion()
    {
        var infoVer = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            int plus = infoVer.IndexOf('+');
            return plus > 0 ? infoVer[..plus] : infoVer;
        }
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        return ver is not null ? $"{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}" : "0.1.0";
    }

    public static string ApplyMirror(string targetUrl, bool useMirror, string? mirrorPrefix)
    {
        if (!useMirror || string.IsNullOrWhiteSpace(mirrorPrefix))
            return targetUrl;
        if (!targetUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
            !targetUrl.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            return targetUrl;
        }
        string prefix = mirrorPrefix.Trim();
        if (!prefix.EndsWith('/')) prefix += "/";
        return prefix + targetUrl;
    }

    public static bool IsNewerVersion(string currentVersion, string latestTag)
    {
        var cur = ParseVersionCore(currentVersion);
        var lat = ParseVersionCore(latestTag);
        return lat > cur;
    }

    public static Version ParseVersionCore(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Version(0, 0, 0);
        string cleaned = raw.Trim().TrimStart('v', 'V');
        int dash = cleaned.IndexOf('-');
        if (dash > 0) cleaned = cleaned[..dash];
        int plus = cleaned.IndexOf('+');
        if (plus > 0) cleaned = cleaned[..plus];
        return Version.TryParse(cleaned, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool useMirror, string? mirrorUrl, CancellationToken cancellationToken = default)
    {
        string current = GetCurrentVersion();
        using var response = await Http.GetAsync(LatestReleaseApiUrl, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(false, current, current, "暂无已发布的 GitHub Release 版本", ReleasesPageUrl, null, null);
        }
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tagName = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? current) : current;
        string latestVer = tagName.TrimStart('v', 'V');
        string htmlUrl = root.TryGetProperty("html_url", out var urlEl) ? (urlEl.GetString() ?? ReleasesPageUrl) : ReleasesPageUrl;
        string notes = root.TryGetProperty("body", out var bodyEl) ? (bodyEl.GetString() ?? "") : "";

        string? assetName = null;
        string? rawDownloadUrl = null;
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                string? dl = asset.TryGetProperty("browser_download_url", out var dEl) ? dEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(dl) &&
                    name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    rawDownloadUrl = dl;
                    break;
                }
            }
        }

        string? finalDownloadUrl = rawDownloadUrl is not null
            ? ApplyMirror(rawDownloadUrl, useMirror, mirrorUrl)
            : null;

        bool hasUpdate = IsNewerVersion(current, latestVer);
        return new UpdateCheckResult(hasUpdate, current, latestVer, notes, htmlUrl, assetName, finalDownloadUrl);
    }

    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        string? assetName,
        Action<int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        string safeName = string.IsNullOrWhiteSpace(assetName) ? "PinDock-Setup-Update.exe" : Path.GetFileName(assetName);
        string updateDir = Path.Combine(Path.GetTempPath(), "PinDock", "Updates");
        Directory.CreateDirectory(updateDir);
        string targetPath = Path.Combine(updateDir, safeName);
        string tempPath = targetPath + ".downloading";

        using var response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;

        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long readTotal = 0;
            int lastPercent = -1;
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (totalBytes is > 0)
                {
                    int pct = (int)Math.Clamp(readTotal * 100L / totalBytes.Value, 0, 100);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        onProgress?.Invoke(pct);
                    }
                }
            }
        }

        File.Move(tempPath, targetPath, overwrite: true);
        onProgress?.Invoke(100);
        return targetPath;
    }

    public static void LaunchInstallerAndExit(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SILENT",
            UseShellExecute = true
        });
        Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PinDock-Updater/0.1.4");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}