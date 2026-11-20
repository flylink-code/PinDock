using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace PinDock;

public sealed class FullscreenWatcher : IDisposable
{
    private const int DwmwaCloaked = 14;

    private readonly Timer timer;
    private volatile bool enabled = true;
    private Func<ScreenRect?>? targetMonitorBounds;
    private int busy;

    public event Action<bool>? Changed;

    public bool IsFullscreen { get; private set; }

    public FullscreenWatcher()
    {
        timer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800));
    }

    public void Configure(Func<ScreenRect?> targetMonitorBounds)
    {
        this.targetMonitorBounds = targetMonitorBounds;
    }

    public void SetEnabled(bool value)
    {
        enabled = value;
        if (!enabled && IsFullscreen)
        {
            IsFullscreen = false;
            Changed?.Invoke(false);
        }
    }

    public void Dispose() => timer.Dispose();

    private void Poll()
    {
        if (!enabled || Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return;
            bool fullscreen = EvaluateState(state);
            if (fullscreen == IsFullscreen) return;
            IsFullscreen = fullscreen;
            Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(fullscreen));
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    private bool EvaluateState(int qunsState)
    {
        if (qunsState is not (2 or 3 or 4))
            return false;

        ScreenRect? monitorBounds = targetMonitorBounds?.Invoke();
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
            return false;

        bool visible = IsWindowVisible(fg);
        bool minimized = IsIconic(fg);
        bool cloaked = DwmGetWindowAttribute(fg, DwmwaCloaked, out int cloakedVal, sizeof(int)) == 0 && cloakedVal != 0;
        _ = GetWindowThreadProcessId(fg, out uint pid);
        bool ownProcess = pid == (uint)Environment.ProcessId;
        string className = GetWindowClassName(fg);
        ScreenRect? fgRect = GetWindowRect(fg, out var rect)
            ? new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : null;

        return EvaluateFullscreenState(
            qunsState,
            hasForeground: true,
            fgRect,
            className,
            ownProcess,
            visible,
            minimized,
            cloaked,
            monitorBounds);
    }

    /// <summary>
    /// 仅当系统处于全屏/演示通知态，且当前前台非壳层/非本进程可见窗口确实覆盖 Dock 所在显示器全屏范围时才避让，
    /// 避免副屏全屏、桌面假阳性 QUNS_BUSY 或无前台窗口时误隐藏自动隐藏触发条。
    /// </summary>
    public static bool EvaluateFullscreenState(
        int qunsState,
        bool hasForeground,
        ScreenRect? foregroundRect,
        string? foregroundClassName,
        bool isForegroundOwnProcess,
        bool isForegroundVisible,
        bool isForegroundMinimized,
        bool isForegroundCloaked,
        ScreenRect? monitorBounds)
    {
        if (qunsState is not (2 or 3 or 4))
            return false;

        if (monitorBounds is not ScreenRect mon || mon.Width <= 0 || mon.Height <= 0)
            return false;

        if (!hasForeground || !isForegroundVisible || isForegroundMinimized || isForegroundCloaked || isForegroundOwnProcess)
            return false;

        if (string.Equals(foregroundClassName, "Progman", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(foregroundClassName, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(foregroundClassName, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(foregroundClassName, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
            return false;

        if (foregroundRect is not ScreenRect fg)
            return false;

        const double tolerance = 2;
        return fg.X <= mon.X + tolerance &&
               fg.Y <= mon.Y + tolerance &&
               fg.X + fg.Width >= mon.X + mon.Width - tolerance &&
               fg.Y + fg.Height >= mon.Y + mon.Height - tolerance;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);
}
