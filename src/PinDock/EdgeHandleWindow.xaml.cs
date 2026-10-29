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
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Hovered?.Invoke();

    private void Window_DragEnter(object sender, DragEventArgs e) => Hovered?.Invoke();
}
