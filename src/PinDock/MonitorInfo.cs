namespace PinDock;

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }
    public required string DisplayName { get; init; }
    public required ScreenRect Bounds { get; init; }
    public required ScreenRect WorkingArea { get; init; }
    public required bool IsPrimary { get; init; }
}

public static class MonitorQuery
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        return System.Windows.Forms.Screen.AllScreens.Select(Map).ToList();
    }

    private static MonitorInfo Map(System.Windows.Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var work = screen.WorkingArea;
        return new MonitorInfo
        {
            DeviceName = screen.DeviceName,
            DisplayName = screen.Primary ? $"主显示器 · {bounds.Width} × {bounds.Height}" : $"{screen.DeviceName} · {bounds.Width} × {bounds.Height}",
            Bounds = new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height),
            WorkingArea = new ScreenRect(work.X, work.Y, work.Width, work.Height),
            IsPrimary = screen.Primary
        };
    }

    public static MonitorInfo Resolve(AppSettings settings, System.Drawing.Point? cursor = null)
    {
        var monitors = All();
        MonitorInfo primary = monitors.First(m => m.IsPrimary);
        if (settings.MonitorPolicy == MonitorPolicy.Primary)
            return primary;
        if (settings.MonitorPolicy == MonitorPolicy.FollowCursor)
        {
            var point = cursor ?? System.Windows.Forms.Cursor.Position;
            return monitors.FirstOrDefault(m => m.Bounds.Contains(point.X, point.Y)) ?? primary;
        }
        if (settings.MonitorPolicy == MonitorPolicy.Fixed)
        {
            if (settings.MonitorDeviceName is string name)
            {
                var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            return primary;
        }
        throw new ArgumentOutOfRangeException(nameof(settings.MonitorPolicy), settings.MonitorPolicy, null);
    }
}
