using System.Windows;

namespace PinDock;

public partial class EdgeHandleWindow : Window
{
    public event Action? Hovered;

    public EdgeHandleWindow()
    {
        InitializeComponent();
        NativeWindow.HideFromAltTab(this);
    }

    public void ShowAt(ScreenRect rect, bool topmost)
    {
        Topmost = topmost;
        if (!IsVisible) Show();
        NativeWindow.SetBounds(this, rect);
        // 触发条只有 3px 高，一旦掉出顶层窗口带就完全不可见、不可悬停，Dock 将无法再唤出。
        NativeWindow.SetTopmost(this, topmost);
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Hovered?.Invoke();

    private void Window_DragEnter(object sender, DragEventArgs e) => Hovered?.Invoke();
}
