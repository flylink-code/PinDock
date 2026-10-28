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
    public const int MaxPinnedVisible = 9;
    public const int UtilitySlots = 5;
    public const double HandleThickness = 3;
    public const double DockGap = 9;
    public const double DockMargin = 18;

    public static int IconPixels(IconSize size) => (int)size;

    public static int ComputeCapacity(double availableAlongEdge, double iconSize)
    {
        double unit = iconSize + DockGap;
        int capacity = (int)Math.Floor((availableAlongEdge - DockMargin * 2) / unit) - UtilitySlots;
        return Math.Clamp(capacity, 1, MaxPinnedVisible);
    }

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
