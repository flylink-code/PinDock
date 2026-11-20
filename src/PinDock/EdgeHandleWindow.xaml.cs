using System.Windows;

namespace PinDock;

public partial class EdgeHandleWindow : Window
{
    private IntPtr lastObservedForeground;

    public event Action? Hovered;

    public EdgeHandleWindow()
    {
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
    }

    public void ShowAt(ScreenRect rect, bool topmost)
    {
        Topmost = topmost;
        NativeWindow.SetBounds(this, rect);
        if (!IsVisible) Show();
        NativeWindow.SetBounds(this, rect);
        // 触发条只有 3px 高，一旦掉出顶层窗口带就完全不可见、不可悬停，Dock 将无法再唤出。
        NativeWindow.SetTopmost(this, topmost);
    }

    public void EnsureVisibleAndTopmost(ScreenRect rect, bool topmost, bool allowZOrderRefresh)
    {
        if (!IsVisible || !NativeWindow.IsWindowVisible(this))
        {
            ShowAt(rect, topmost);
            return;
        }

        var current = NativeWindow.GetPixelBounds(this);
        if (Math.Abs(current.X - rect.X) > 1 ||
            Math.Abs(current.Y - rect.Y) > 1 ||
            Math.Abs(current.Width - rect.Width) > 1 ||
            Math.Abs(current.Height - rect.Height) > 1)
        {
            NativeWindow.SetBounds(this, rect);
        }

        if (Topmost != topmost)
            Topmost = topmost;

        if (topmost && allowZOrderRefresh &&
            (!NativeWindow.IsTopmost(this) || NativeWindow.ShouldRefreshEdgeHandleTopmost(this, ref lastObservedForeground)))
        {
            NativeWindow.SetTopmost(this, true);
        }
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Hovered?.Invoke();

    private void Window_DragEnter(object sender, DragEventArgs e) => Hovered?.Invoke();
}
