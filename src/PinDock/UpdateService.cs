using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public const string LatestReleaseWebUrl = $"https://github.com/{RepoSlug}/releases/latest";
    public const string ReleasesAtomUrl = $"https://github.com/{RepoSlug}/releases.atom";
    public const string RawChangelogUrl = $"https://raw.githubusercontent.com/{RepoSlug}/main/CHANGELOG.md";
    public const string ReleasesPageUrl = $"https://github.com/{RepoSlug}/releases";

    private static readonly string[] BuiltinMirrorPrefixes =
    [
        "https://ghfast.top/",
        "https://gh-proxy.com/"
    ];

    private static readonly Regex ReleaseTagUrlRegex = new(
        @"releases/tag/(?<tag>v?\d+\.\d+\.\d+[A-Za-z0-9.\-]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AtomEntryIdTagRegex = new(
        @"Repository/\d+/(?<tag>v?\d+\.\d+\.\d+[A-Za-z0-9.\-]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChangelogHeadingRegex = new(
        @"^##\s*\[(?<ver>v?\d+\.\d+\.\d+[A-Za-z0-9.\-]*)\][^\r\n]*\r?\n?",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateHttpClient(allowAutoRedirect: true);
    private static readonly HttpClient NoRedirectHttp = CreateHttpClient(allowAutoRedirect: false);

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

    public static string ExtractRawGitHubUrl(string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl))
            return string.Empty;

        string trimmed = targetUrl.Trim();
        string[] githubHosts =
        [
            "https://github.com/",
            "https://objects.githubusercontent.com/",
            "https://raw.githubusercontent.com/"
        ];

        foreach (string host in githubHosts)
        {
            int idx = trimmed.IndexOf(host, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return trimmed[idx..];
        }

        return trimmed;
    }

    public static string ApplyMirror(string targetUrl, bool useMirror, string? mirrorPrefix)
    {
        string rawUrl = ExtractRawGitHubUrl(targetUrl);
        if (!useMirror || string.IsNullOrWhiteSpace(mirrorPrefix))
            return rawUrl;
        if (!rawUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
            !rawUrl.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) &&
            !rawUrl.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
        {
            return rawUrl;
        }
        string prefix = mirrorPrefix.Trim();
        if (!prefix.EndsWith('/')) prefix += "/";
        return prefix + rawUrl;
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

    public static bool TryExtractTagFromReleaseUrl(string? urlOrLocation, out string tagName)
    {
        tagName = string.Empty;
        if (string.IsNullOrWhiteSpace(urlOrLocation))
            return false;

        var match = ReleaseTagUrlRegex.Match(urlOrLocation);
        if (!match.Success)
            return false;

        tagName = match.Groups["tag"].Value.Trim();
        return tagName.Length > 0;
    }

    public static bool TryParseLatestFromAtomXml(string? atomXml, out string tagName, out string releaseNotes)
    {
        tagName = string.Empty;
        releaseNotes = string.Empty;
        if (string.IsNullOrWhiteSpace(atomXml))
            return false;

        int entryStart = atomXml.IndexOf("<entry>", StringComparison.OrdinalIgnoreCase);
        if (entryStart < 0)
            return false;

        int entryEnd = atomXml.IndexOf("</entry>", entryStart, StringComparison.OrdinalIgnoreCase);
        string entryBlock = entryEnd > entryStart ? atomXml[entryStart..(entryEnd + 8)] : atomXml[entryStart..];

        if (!TryExtractTagFromReleaseUrl(entryBlock, out tagName))
        {
            var idMatch = AtomEntryIdTagRegex.Match(entryBlock);
            if (!idMatch.Success)
                return false;
            tagName = idMatch.Groups["tag"].Value.Trim();
        }

        var contentMatch = Regex.Match(entryBlock, @"<content[^>]*>(?<c>[\s\S]*?)</content>", RegexOptions.IgnoreCase);
        if (contentMatch.Success)
        {
            string decoded = WebUtility.HtmlDecode(contentMatch.Groups["c"].Value);
            string plain = Regex.Replace(decoded, @"<[^>]+>", " ");
            releaseNotes = Regex.Replace(plain, @"\s+", " ").Trim();
        }

        return tagName.Length > 0;
    }

    public static bool TryParseLatestFromChangelog(string? changelogMarkdown, out string version, out string releaseNotes)
    {
        version = string.Empty;
        releaseNotes = string.Empty;
        if (string.IsNullOrWhiteSpace(changelogMarkdown))
            return false;

        var match = ChangelogHeadingRegex.Match(changelogMarkdown);
        if (!match.Success)
            return false;

        version = match.Groups["ver"].Value.Trim().TrimStart('v', 'V');
        int bodyStart = match.Index + match.Length;
        string rest = changelogMarkdown[bodyStart..];

        var nextHeading = ChangelogHeadingRegex.Match(rest);
        string section = nextHeading.Success ? rest[..nextHeading.Index] : rest;
        int hrIdx = section.IndexOf("\n---", StringComparison.Ordinal);
        if (hrIdx >= 0)
            section = section[..hrIdx];

        releaseNotes = section.Trim();
        return version.Length > 0;
    }

    public static UpdateCheckResult BuildReleaseResult(
        string currentVersion,
        string latestTagOrVersion,
        string? releaseNotes,
        bool useMirror,
        string? mirrorUrl,
        string? explicitAssetName = null,
        string? explicitRawDownloadUrl = null,
        string? explicitHtmlUrl = null)
    {
        string latestVer = latestTagOrVersion.Trim().TrimStart('v', 'V');
        string tagName = "v" + latestVer;
        string assetName = !string.IsNullOrWhiteSpace(explicitAssetName)
            ? explicitAssetName
            : $"PinDock-Setup-{latestVer}.exe";
        string rawDownloadUrl = !string.IsNullOrWhiteSpace(explicitRawDownloadUrl)
            ? explicitRawDownloadUrl
            : $"https://github.com/{RepoSlug}/releases/download/{tagName}/{assetName}";
        string htmlUrl = !string.IsNullOrWhiteSpace(explicitHtmlUrl)
            ? explicitHtmlUrl
            : $"https://github.com/{RepoSlug}/releases/tag/{tagName}";
        string finalDownloadUrl = ApplyMirror(rawDownloadUrl, useMirror, mirrorUrl);
        bool hasUpdate = IsNewerVersion(currentVersion, latestVer);

        return new UpdateCheckResult(
            hasUpdate,
            currentVersion,
            latestVer,
            releaseNotes ?? string.Empty,
            htmlUrl,
            assetName,
            finalDownloadUrl);
    }

    public static IReadOnlyList<string> BuildDownloadCandidateUrls(string downloadUrl, bool useMirror = true, string? mirrorUrl = null)
    {
        var list = new List<string>();
        void AddUnique(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            string trimmed = u.Trim();
            if (!list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                list.Add(trimmed);
        }

        string rawUrl = ExtractRawGitHubUrl(downloadUrl);
        if (useMirror && !string.IsNullOrWhiteSpace(mirrorUrl))
            AddUnique(ApplyMirror(rawUrl, true, mirrorUrl));

        AddUnique(downloadUrl);

        if (useMirror)
        {
            foreach (string builtin in BuiltinMirrorPrefixes)
                AddUnique(ApplyMirror(rawUrl, true, builtin));
        }

        AddUnique(rawUrl);
        return list;
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(bool useMirror, string? mirrorUrl, CancellationToken cancellationToken = default)
    {
        string current = GetCurrentVersion();
        Exception? lastError = null;

        // 1. 优先通过 GitHub releases/latest 302 Location 重定向检查（不占用 api.github.com 60次/小时限额，且支持镜像加速）
        var redirectCandidates = new List<string>();
        if (useMirror && !string.IsNullOrWhiteSpace(mirrorUrl))
            redirectCandidates.Add(ApplyMirror(LatestReleaseWebUrl, true, mirrorUrl));
        redirectCandidates.Add(LatestReleaseWebUrl);
        if (useMirror)
        {
            foreach (string builtin in BuiltinMirrorPrefixes)
            {
                string candidate = ApplyMirror(LatestReleaseWebUrl, true, builtin);
                if (!redirectCandidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    redirectCandidates.Add(candidate);
            }
        }

        foreach (string redirectUrl in redirectCandidates)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var response = await NoRedirectHttp.GetAsync(redirectUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                string? location = response.Headers.Location?.ToString();
                if (!string.IsNullOrWhiteSpace(location))
                {
                    if (TryExtractTagFromReleaseUrl(location, out string tag))
                    {
                        string notes = await TryFetchChangelogNotesAsync(useMirror, mirrorUrl, cancellationToken);
                        return BuildReleaseResult(current, tag, notes, useMirror, mirrorUrl);
                    }
                    if (location.TrimEnd('/').EndsWith("/releases", StringComparison.OrdinalIgnoreCase))
                    {
                        return new UpdateCheckResult(false, current, current, "暂无已发布的 GitHub Release 版本", ReleasesPageUrl, null, null);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }
        }

        // 2. 备选通道：通过 GitHub Releases Atom 订阅源解析最新版本（同样不受 REST API 60次/小时限流限制）
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var atomResp = await Http.GetAsync(ReleasesAtomUrl, cts.Token);
            if (atomResp.IsSuccessStatusCode)
            {
                string atomXml = await atomResp.Content.ReadAsStringAsync(cts.Token);
                if (TryParseLatestFromAtomXml(atomXml, out string atomTag, out string atomNotes))
                {
                    return BuildReleaseResult(current, atomTag, atomNotes, useMirror, mirrorUrl);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            lastError = ex;
        }

        // 3. 备选通道：通过镜像或直连读取仓库 CHANGELOG.md 最新版本段落（适用于 github.com 主站受阻但镜像可用的国内网络环境）
        var changelogCandidates = BuildDownloadCandidateUrls(RawChangelogUrl, useMirror, mirrorUrl);
        foreach (string changelogUrl in changelogCandidates)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var clResp = await Http.GetAsync(changelogUrl, cts.Token);
                if (clResp.IsSuccessStatusCode)
                {
                    string md = await clResp.Content.ReadAsStringAsync(cts.Token);
                    if (TryParseLatestFromChangelog(md, out string clVer, out string clNotes))
                    {
                        return BuildReleaseResult(current, clVer, clNotes, useMirror, mirrorUrl);
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
            }
        }

        // 4. 兜底通道：GitHub REST API
        try
        {
            using var response = await Http.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(false, current, current, "暂无已发布的 GitHub Release 版本", ReleasesPageUrl, null, null);
            }
            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? current) : current;
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

            return BuildReleaseResult(current, tagName, notes, useMirror, mirrorUrl, assetName, rawDownloadUrl, htmlUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            lastError = ex;
        }

        throw lastError ?? new HttpRequestException("无法连接 GitHub 或镜像源检查更新，请检查网络连接。");
    }

    private static async Task<string> TryFetchChangelogNotesAsync(bool useMirror, string? mirrorUrl, CancellationToken cancellationToken)
    {
        var candidates = BuildDownloadCandidateUrls(RawChangelogUrl, useMirror, mirrorUrl);
        foreach (string url in candidates)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var resp = await Http.GetAsync(url, cts.Token);
                if (!resp.IsSuccessStatusCode) continue;
                string md = await resp.Content.ReadAsStringAsync(cts.Token);
                if (TryParseLatestFromChangelog(md, out _, out string notes))
                    return notes;
            }
            catch (Exception)
            {
            }
        }
        return string.Empty;
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

        var candidates = BuildDownloadCandidateUrls(downloadUrl, useMirror: true, mirrorUrl: null);
        Exception? lastError = null;

        foreach (string candidateUrl in candidates)
        {
            try
            {
                using var response = await Http.GetAsync(candidateUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

                if (safeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsValidPeExecutable(tempPath))
                {
                    throw new IOException("下载的文件不是有效的 Windows 安装程序（镜像源可能返回了错误页）。");
                }

                File.Move(tempPath, targetPath, overwrite: true);
                onProgress?.Invoke(100);
                return targetPath;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                lastError = ex;
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        throw lastError ?? new HttpRequestException("下载升级安装包失败。");
    }

    private static bool IsValidPeExecutable(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length < 1024)
                return false;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int b0 = fs.ReadByte();
            int b1 = fs.ReadByte();
            return b0 == 'M' && b1 == 'Z';
        }
        catch
        {
            return false;
        }
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

    private static HttpClient CreateHttpClient(bool allowAutoRedirect)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PinDock-Updater/0.1.7");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, application/atom+xml, text/html, */*");
        return client;
    }
}