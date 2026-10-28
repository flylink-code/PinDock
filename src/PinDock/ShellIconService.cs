using System.Collections.Concurrent;
using System.ComponentModel;
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
    private readonly ConcurrentDictionary<string, Task<ImageSource>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Path, TaskCompletionSource<ImageSource> Result)> queue = [];

    public ShellIconService()
    {
        var worker = new Thread(Run) { IsBackground = true, Name = "PinDock.ShellIcons" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
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

    private void Run()
    {
        foreach (var (path, result) in queue.GetConsumingEnumerable())
        {
            try { result.SetResult(Extract(path)); }
            catch (Win32Exception ex) { result.SetException(ex); }
        }
    }

    private static ImageSource Extract(string path)
    {
        var result = SHGetFileInfo(path, 0, out var info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 | 0x1);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero)
            throw new Win32Exception($"无法提取图标：{path}");
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { DestroyIcon(info.Icon); }
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
