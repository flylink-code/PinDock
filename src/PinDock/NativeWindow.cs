using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PinDock;

public static class NativeWindow
{
    private const int GwlExstyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExAppwindow = 0x00040000;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint GwHwndPrev = 3;
    private const int DwmwaCloaked = 14;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    public static void HideFromAltTab(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        int style = GetWindowLong(handle, GwlExstyle);
        style = (style | WsExToolwindow) & ~WsExAppwindow;
        SetWindowLong(handle, GwlExstyle, style);
    }

    public static bool IsWindowVisible(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && IsWindowVisible(handle);
    }

    public static bool IsTopmost(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;
        return (GetWindowLong(handle, GwlExstyle) & WsExTopmost) != 0;
    }

    /// <summary>
    /// 判断自动隐藏触发条是否被任务栏压住，或刚发生过前台窗口切换需要重新刷新顶层 Z 序。
    /// 跳过开始菜单 / 右键菜单等瞬态壳层浮窗与本进程窗口，避免在用户操作菜单时争抢层级。
    /// </summary>
    public static bool ShouldRefreshEdgeHandleTopmost(Window handleWindow, ref IntPtr lastForegroundWindow)
    {
        var handle = new WindowInteropHelper(handleWindow).Handle;
        if (handle == IntPtr.Zero) return false;

        IntPtr fg = GetForegroundWindow();
        bool fgChanged = fg != IntPtr.Zero && fg != lastForegroundWindow;
        if (fg != IntPtr.Zero)
            lastForegroundWindow = fg;

        if (fg != IntPtr.Zero && IsTransientShellOrMenuWindow(fg))
            return false;

        uint selfPid = (uint)Environment.ProcessId;
        IntPtr cursor = handle;
        for (int step = 0; step < 64; step++)
        {
            cursor = GetWindow(cursor, GwHwndPrev);
            if (cursor == IntPtr.Zero) break;
            if (!IsWindowVisible(cursor) || IsCloaked(cursor)) continue;

            _ = GetWindowThreadProcessId(cursor, out uint pid);
            if (pid == selfPid) continue;

            string cls = GetWindowClassName(cursor);
            if (string.Equals(cls, "Shell_TrayWnd", StringComparison.Ordinal) ||
                string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (fgChanged && fg != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid != selfPid)
                return true;
        }

        return false;
    }

    private static bool IsTransientShellOrMenuWindow(IntPtr hwnd)
    {
        string cls = GetWindowClassName(hwnd);
        return string.Equals(cls, "#32768", StringComparison.Ordinal) ||
               string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.Ordinal) ||
               string.Equals(cls, "XamlExplorerHostIslandWindow", StringComparison.Ordinal);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(128);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
