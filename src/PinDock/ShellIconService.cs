using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinDock;

/// <summary>
/// SHGetFileInfo 依赖已初始化的 COM，并且在新进程里并发首次调用会失败。
/// 所有提取都在同一个 STA 后台线程上串行执行。
/// </summary>
public sealed class ShellIconService
{
    private static readonly HashSet<string> PerFileIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".ocx", ".lnk", ".ico", ".cur", ".ani",
        ".url", ".scr", ".cpl", ".msc", ".appref-ms",
        ".png", ".jpg", ".jpeg", ".bmp"
    };

    private readonly ConcurrentDictionary<string, Task<ImageSource>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> dataUriCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Path, TaskCompletionSource<ImageSource> Result)> queue = [];

    public ShellIconService()
    {
        var worker = new Thread(Run) { IsBackground = true, Name = "PinDock.ShellIcons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public ImageSource? TryGetCached(string path)
    {
        if (cache.TryGetValue(path, out var task) && task.IsCompletedSuccessfully)
            return task.Result;
        return null;
    }

    public ImageSource? TryGetCachedFileIcon(string filePath)
    {
        string key = GetFileCacheKey(filePath);
        if (cache.TryGetValue(key, out var task) && task.IsCompletedSuccessfully)
            return task.Result;
        if (cache.TryGetValue(filePath, out var directTask) && directTask.IsCompletedSuccessfully)
            return directTask.Result;
        return null;
    }

    public async Task<ImageSource> GetFileIconAsync(string filePath)
    {
        string key = GetFileCacheKey(filePath);
        var task = cache.GetOrAdd(key, _ =>
        {
            var result = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add((filePath, result));
            return result.Task;
        });
        try { return await task; }
        catch
        {
            cache.TryRemove(new KeyValuePair<string, Task<ImageSource>>(key, task));
            throw;
        }
    }

    private static string GetFileCacheKey(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(ext) && !PerFileIconExtensions.Contains(ext))
            return "ext:" + ext;
        return filePath;
    }

    public async Task<ImageSource> GetAsync(string path)
    {
        var task = cache.GetOrAdd(path, key =>
        {
            var result = new TaskCompletionSource<ImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add((key, result));
            return result.Task;
        });
        try { return await task; }
        catch
        {
            cache.TryRemove(new KeyValuePair<string, Task<ImageSource>>(path, task));
            throw;
        }
    }

    public string? TryGetCachedDataUri(string path) =>
        dataUriCache.TryGetValue(path, out string? uri) ? uri : null;

    public async Task<string?> GetDataUriAsync(string path)
    {
        if (dataUriCache.TryGetValue(path, out string? cachedUri))
            return cachedUri;
        try
        {
            var source = (BitmapSource)await GetAsync(path);
            string uri = await Task.Run(() =>
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            });
            dataUriCache[path] = uri;
            return uri;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private void Run()
    {
        foreach (var (path, result) in queue.GetConsumingEnumerable())
        {
            try { result.SetResult(Extract(path)); }
            catch (Win32Exception ex) { result.SetException(ex); }
        }
    }

    private static readonly Guid IidImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    private static ImageSource Extract(string path)
    {
        string ext = Path.GetExtension(path);
        if (File.Exists(path) && (
            string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".ico", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
                bmp.DecodePixelWidth = 64;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
            {
                // 回退到系统图标提取
            }
        }

        var shResult = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x4000 | 0x100);
        if (shResult == IntPtr.Zero)
            throw new Win32Exception($"无法提取图标：{path}");

        IntPtr hIcon = IntPtr.Zero;
        try
        {
            var iid = IidImageList;
            if (SHGetImageList(2 /* SHIL_EXTRALARGE 48x48 */, ref iid, out IntPtr imageList) == 0 && imageList != IntPtr.Zero)
            {
                hIcon = ImageList_GetIcon(imageList, info.IconIndex, 0x0001 /* ILD_TRANSPARENT */);
            }
            if (hIcon == IntPtr.Zero)
            {
                hIcon = info.Icon;
                info.Icon = IntPtr.Zero;
            }
            if (hIcon == IntPtr.Zero)
                throw new Win32Exception($"无法提取图标：{path}");

            var image = Imaging.CreateBitmapSourceFromHIcon(hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            if (info.Icon != IntPtr.Zero) DestroyIcon(info.Icon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
