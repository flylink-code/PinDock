using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PinDock;

public static class NativeWindow
{
    private const int GwlExstyle = -20;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;

    public static void HideFromAltTab(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        int style = GetWindowLong(handle, GwlExstyle);
        style = (style | WsExToolwindow) & ~WsExAppwindow;
        SetWindowLong(handle, GwlExstyle, style);
    }

    public static ScreenRect GetPixelBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!GetWindowRect(handle, out var rect))
            throw new InvalidOperationException($"GetWindowRect 失败：{window.GetType().Name}");
        return new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public static void MoveTo(Window window, double x, double y)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, SwpNosize | SwpNozorder | SwpNoactivate))
            throw new InvalidOperationException($"SetWindowPos 失败：{window.GetType().Name}");
    }

    public static void SetBounds(Window window, ScreenRect bounds)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y), (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), SwpNozorder | SwpNoactivate))
            throw new InvalidOperationException($"SetWindowPos 失败：{window.GetType().Name}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
