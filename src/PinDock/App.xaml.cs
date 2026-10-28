using System.Threading;
using System.Windows;

namespace PinDock;

public partial class App : Application
{
    private Mutex? mutex;
    private AppController? controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        mutex = new Mutex(true, @"Local\PinDock.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("PinDock 已在运行。");
            Shutdown();
            return;
        }

        base.OnStartup(e);
        controller = new AppController();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        controller?.Dispose();
        mutex?.ReleaseMutex();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
