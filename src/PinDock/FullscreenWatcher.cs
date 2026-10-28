using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PinDock;

public sealed class FullscreenWatcher : IDisposable
{
    private readonly DispatcherTimer timer;
    private bool enabled = true;
    private bool busy;

    public event Action<bool>? Changed;

    public bool IsFullscreen { get; private set; }

    public FullscreenWatcher()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Poll();
        timer.Start();
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

    public void Dispose() => timer.Stop();

    private void Poll()
    {
        if (!enabled || busy) return;
        busy = true;
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return;
            bool fullscreen = state is 2 or 3 or 4;
            if (fullscreen == IsFullscreen) return;
            IsFullscreen = fullscreen;
            Changed?.Invoke(fullscreen);
        }
        finally { busy = false; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
