using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PinDock;

public sealed class ShellIconService
{
    private readonly ConcurrentDictionary<string, Task<ImageSource>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim workers = new(2);

    public async Task<ImageSource> GetAsync(string path)
    {
        var task = cache.GetOrAdd(path, key => Task.Run(async () =>
        {
            await workers.WaitAsync();
            try { return Extract(key); }
            finally { workers.Release(); }
        }));
        try { return await task; }
        catch
        {
            cache.TryRemove(new KeyValuePair<string, Task<ImageSource>>(path, task));
            throw;
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
