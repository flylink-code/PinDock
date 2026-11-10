namespace PinDock;

public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    public ScreenRect Union(ScreenRect other)
    {
        double left = Math.Min(X, other.X);
        double top = Math.Min(Y, other.Y);
        double right = Math.Max(X + Width, other.X + other.Width);
        double bottom = Math.Max(Y + Height, other.Y + other.Height);
        return new(left, top, right - left, bottom - top);
    }
}

public readonly record struct DockPlacement(ScreenRect Bounds, int Capacity, ScreenRect EdgeHandle);

public static class DockLayout
{
    public const int MinIconPixels = 32;
    public const double HandleThickness = 3;
    public const double DockGap = 9;
    public const double DockMargin = 18;

    private const double ChromePadding = 24;
    private const double LabelExtra = 16;
    private const double LabelGap = 10;
    private const double VerticalLabelExtra = 20;
    private const double SeparatorExtent = 10;
    private const double CustomSeparatorExtent = 19;

    public static int IconPixels(IconSize size) => (int)size;

    /// <summary>
    /// 按屏幕可用空间计算能完整显示的固定项数量。与 macOS Dock 一样优先展示全部固定项，
    /// 仅在图标缩到下限仍放不下时才把末尾项目移入「更多」。
    /// </summary>
    public static int FitPinnedCount(
        int pinnedCount,
        int groupCount,
        int customSeparatorCount,
        bool utilityCollapsed,
        bool showLabels,
        bool vertical,
        double availablePixels,
        double iconPixels,
        double dpiScale)
    {
        if (pinnedCount <= 0) return 0;

        double item = ItemExtent(iconPixels, showLabels, vertical);
        double drawer = Math.Max(28, iconPixels * 0.58) + DockGap;
        double customSeparators = customSeparatorCount * CustomSeparatorExtent;

        for (int visible = pinnedCount; visible >= 1; visible--)
        {
            int overflow = pinnedCount - visible;
            int utilities = (utilityCollapsed ? 0 : 3) + (overflow > 0 ? 1 : 0);
            bool hasMain = visible + groupCount + customSeparatorCount > 0;

            int separators = 0;
            if (hasMain && utilities > 0) separators++;
            if (hasMain && utilityCollapsed) separators++;
            if (customSeparatorCount == 0 && visible > 0 && groupCount > 0) separators++;

            // 最后一个元素的外边距会被清零，这里扣除对应间隙。
            double content = ChromePadding
                + visible * item
                + groupCount * item
                + utilities * item
                + drawer
                + customSeparators
                + separators * SeparatorExtent
                - DockGap;

            if (content * dpiScale + DockMargin * 2 <= availablePixels)
                return visible;
        }

        return 1;
    }

    private static double ItemExtent(double iconPixels, bool showLabels, bool vertical) =>
        vertical
            ? iconPixels + (showLabels ? VerticalLabelExtra + DockGap : DockGap)
            : iconPixels + (showLabels ? LabelExtra + LabelGap : DockGap);

    public static ScreenRect Fit(ScreenRect workArea, DockEdge edge, double width, double height, double edgeOffset, bool lockPosition)
    {
        bool vertical = edge is DockEdge.Left or DockEdge.Right;
        double along = vertical ? height : width;
        double offset = lockPosition ? 0 : ClampEdgeOffset(workArea, edge, along, edgeOffset);
        double x, y;
        switch (edge)
        {
            case DockEdge.Bottom:
                x = workArea.X + (workArea.Width - width) / 2 + offset;
                y = workArea.Y + workArea.Height - height - DockMargin;
                break;
            case DockEdge.Top:
                x = workArea.X + (workArea.Width - width) / 2 + offset;
                y = workArea.Y + DockMargin;
                break;
            case DockEdge.Left:
                x = workArea.X + DockMargin;
                y = workArea.Y + (workArea.Height - height) / 2 + offset;
                break;
            case DockEdge.Right:
                x = workArea.X + workArea.Width - width - DockMargin;
                y = workArea.Y + (workArea.Height - height) / 2 + offset;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(edge), edge, null);
        }
        return new ScreenRect(x, y, width, height);
    }

    public static ScreenRect EdgeHandleFor(ScreenRect workArea, DockEdge edge, ScreenRect dockBounds)
    {
        switch (edge)
        {
            case DockEdge.Bottom:
                return new(dockBounds.X, workArea.Y + workArea.Height - HandleThickness, dockBounds.Width, HandleThickness);
            case DockEdge.Top:
                return new(dockBounds.X, workArea.Y, dockBounds.Width, HandleThickness);
            case DockEdge.Left:
                return new(workArea.X, dockBounds.Y, HandleThickness, dockBounds.Height);
            case DockEdge.Right:
                return new(workArea.X + workArea.Width - HandleThickness, dockBounds.Y, HandleThickness, dockBounds.Height);
            default:
                throw new ArgumentOutOfRangeException(nameof(edge), edge, null);
        }
    }

    public static double ClampEdgeOffset(ScreenRect workArea, DockEdge edge, double dockAlong, double offset)
    {
        bool vertical = edge is DockEdge.Left or DockEdge.Right;
        double available = vertical ? workArea.Height : workArea.Width;
        double maxOffset = Math.Max(0, available - dockAlong - DockMargin * 2);
        return Math.Clamp(offset, -maxOffset / 2, maxOffset / 2);
    }
}
