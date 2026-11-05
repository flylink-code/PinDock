using System.IO;
using System.Text;

namespace PinDock;

/// <summary>
/// 把未处理异常追加写入 %LOCALAPPDATA%\PinDock\logs\crash.log。
/// PinDock 常驻托盘，崩溃时没有控制台也没有窗口，此前只能靠系统事件日志里的 WER 记录排查。
/// </summary>
internal static class CrashLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PinDock", "logs", "crash.log");

    private static readonly object Gate = new();

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception);

        // UI 线程上的事件回调异常默认会终止整个进程；记录后保持常驻，避免 Dock 直接消失。
        Application.Current.DispatcherUnhandledException += (_, e) =>
        {
            Write("Dispatcher", e.Exception);
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Task", e.Exception);
            e.SetObserved();
        };
    }

    private static void Write(string source, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                FileInfo info = new(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                    info.Delete();

                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} | PinDock {typeof(App).Assembly.GetName().Version} | .NET {Environment.Version} | {Environment.OSVersion}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception writeEx) when (writeEx is IOException or UnauthorizedAccessException)
        {
            // 崩溃处理路径里写日志失败不能再抛，否则会覆盖原始异常。
        }
    }
}
