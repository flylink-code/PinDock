using System.Runtime.InteropServices;
using System.Windows;

namespace PinDock;

public sealed class FullscreenWatcher : IDisposable
{
    private readonly Timer timer;
    private volatile bool enabled = true;
    private int busy;

    public event Action<bool>? Changed;

    public bool IsFullscreen { get; private set; }

    public FullscreenWatcher()
    {
        timer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(800));
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
            bool fullscreen = state is 2 or 3 or 4;
            if (fullscreen == IsFullscreen) return;
            IsFullscreen = fullscreen;
            Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(fullscreen));
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
