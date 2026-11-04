using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PinDock;

public sealed class AutoHideController : IDisposable
{
    private readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private Func<bool> popupOpen = () => false;
    private Func<bool> dragging = () => false;
    private Func<bool> dialogOpen = () => false;
    private Func<ScreenRect> hitArea = () => default;
    private Func<ScreenRect?> popupBounds = () => null;
    private bool enabled;
    private bool hideDelayElapsed = true;
    private bool holdUntilHover;

    public AutoHideState State { get; private set; } = AutoHideState.Visible;
    public event Action<AutoHideState>? StateChanged;

    public AutoHideController()
    {
        pollTimer.Tick += (_, _) => Evaluate();
        hideTimer.Tick += (_, _) =>
        {
            hideTimer.Stop();
            hideDelayElapsed = true;
            Evaluate();
        };
    }

    public void Configure(Func<bool> popupOpen, Func<bool> dragging, Func<bool> dialogOpen, Func<ScreenRect> hitArea, Func<ScreenRect?> popupBounds)
    {
        this.popupOpen = popupOpen;
        this.dragging = dragging;
        this.dialogOpen = dialogOpen;
        this.hitArea = hitArea;
        this.popupBounds = popupBounds;
    }

    public void SetEnabled(bool value)
    {
        if (enabled == value) return;
        enabled = value;
        hideTimer.Stop();
        holdUntilHover = false;
        if (!value)
        {
            pollTimer.Stop();
            SetState(AutoHideState.Visible);
            return;
        }
        hideDelayElapsed = true;
        pollTimer.Start();
        SetState(AutoHideState.Hidden);
    }

    /// <summary>展开 Dock。holdUntilHover 为 true 时保持展开，直到鼠标进入并再次离开。</summary>
    public void Reveal(bool holdUntilHover)
    {
        if (!enabled) return;
        this.holdUntilHover = holdUntilHover;
        hideDelayElapsed = false;
        hideTimer.Stop();
        SetState(AutoHideState.Visible);
    }

    public void HideNow()
    {
        if (!enabled) return;
        holdUntilHover = false;
        hideTimer.Stop();
        hideDelayElapsed = true;
        SetState(AutoHideState.Hidden);
    }

    public void Dispose()
    {
        pollTimer.Stop();
        hideTimer.Stop();
    }

    private void Evaluate()
    {
        if (!enabled || State == AutoHideState.Hidden) return;
        // 锁屏、UAC 安全桌面、切换会话时输入桌面不属于本进程，GetCursorPos 会失败。
        // 那时无法判断光标位置，跳过本次轮询（40ms 后重试），不要把异常抛到 DispatcherTimer。
        if (!GetCursorPos(out var cursor)) return;
        bool over = IsCursorOver(cursor, hitArea(), popupBounds());
        if (over) holdUntilHover = false;
        bool stay = AutoHideLogic.ShouldStayVisible(popupOpen(), dragging(), dialogOpen(), over) || holdUntilHover;
        if (stay)
        {
            hideDelayElapsed = false;
            hideTimer.Stop();
            return;
        }
        if (!hideDelayElapsed && !hideTimer.IsEnabled) hideTimer.Start();
        SetState(AutoHideLogic.NextState(State, false, hideDelayElapsed));
    }

    private void SetState(AutoHideState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private static bool IsCursorOver(Point32 cursor, ScreenRect dock, ScreenRect? popup) =>
        dock.Contains(cursor.X, cursor.Y) || (popup is ScreenRect p && p.Contains(cursor.X, cursor.Y));

    public static bool TryGetCursorPoint(out (int X, int Y) point)
    {
        if (GetCursorPos(out var p))
        {
            point = (p.X, p.Y);
            return true;
        }
        point = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32 { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point32 point);
}
