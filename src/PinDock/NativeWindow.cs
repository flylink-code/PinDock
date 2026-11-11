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
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

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

    /// <summary>
    /// 把窗口重新放回顶层窗口带。WPF 的 Topmost 属性赋值在值未变化时不会调用 SetWindowPos，
    /// 而窗口一旦因全屏应用或其它置顶窗口掉出顶层带，再 Show() 也不会自动恢复。
    /// </summary>
    public static void SetTopmost(Window window, bool topmost)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        if (!SetWindowPos(handle, topmost ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, SwpNosize | SwpNomove | SwpNoactivate))
            throw new InvalidOperationException($"SetWindowPos 失败：{window.GetType().Name}");
    }

    public static void EnableHardwareRoundedCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        int preference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

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
