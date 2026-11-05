using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;

namespace PinDock;

public partial class App : Application
{
    private const string PipeName = "PinDock.SingleInstance.Pipe";
    private Mutex? mutex;
    private bool ownsMutex;
    private CancellationTokenSource? pipeCts;
    private AppController? controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        Environment.SetEnvironmentVariable("CHROME_CRASHPAD_PIPE_NAME", null);
        Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", null);
        CrashLog.Install();

        var addPaths = ExplorerMenuService.ExtractAddPaths(e.Args);
        string? firstAddPath = addPaths.Count > 0 ? addPaths[0] : null;

        mutex = new Mutex(true, @"Local\PinDock.SingleInstance", out bool created);
        ownsMutex = created;
        if (!created)
        {
            if (addPaths.Count > 0)
            {
                foreach (string p in addPaths)
                    SendToRunningInstance(p);
            }
            else
            {
                SendToRunningInstance("--show");
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        controller = new AppController(firstAddPath);
        if (addPaths.Count > 1)
        {
            controller.AddPaths(addPaths.Skip(1));
        }
        pipeCts = new CancellationTokenSource();
        _ = ListenForCommandsAsync(pipeCts.Token);
    }

    private static void SendToRunningInstance(string payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(payload);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
        }
    }

    private async Task ListenForCommandsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server);
                string? line = await reader.ReadLineAsync(token);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (line == "--show")
                        {
                            controller?.RevealDock();
                            return;
                        }
                        controller?.AddPaths([line]);
                        controller?.RevealDock();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        pipeCts?.Cancel();
        pipeCts?.Dispose();
        controller?.Dispose();
        // 只有创建者持有互斥体；第二实例走 Shutdown() 时若也 ReleaseMutex 会抛
        // ApplicationException（Object synchronization method was called from an unsynchronized block of code）。
        if (ownsMutex) mutex?.ReleaseMutex();
        mutex?.Dispose();
        base.OnExit(e);
    }
}
