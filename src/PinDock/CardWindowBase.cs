using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PinDock;

/// <summary>
/// 套用 Theme.xaml 中 CardWindow 样式的窗口基类：圆角卡片、Logo 标题栏、可拖动、×关闭。
/// </summary>
public class CardWindowBase : Window
{
    public CardWindowBase()
    {
        Style = (Style)Application.Current.FindResource("CardWindow");
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        var close = (Button)GetTemplateChild("PART_Close");
        close.Click += (_, _) => Close();
        var titleBar = (UIElement)GetTemplateChild("PART_TitleBar");
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (!IsInsideButton((DependencyObject)e.OriginalSource, titleBar)) DragMove();
        };
    }

    private static bool IsInsideButton(DependencyObject source, UIElement boundary)
    {
        for (DependencyObject? node = source; node is not null && !ReferenceEquals(node, boundary); node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button) return true;
        }
        return false;
    }
}
