using Microsoft.Win32;

namespace PinDock;

public static class StartupService
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "PinDock";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        return key?.GetValue(AppValueName) is string cmd && !string.IsNullOrWhiteSpace(cmd);
    }

    public static void SetRegistered(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath);
        if (enabled)
        {
            string exe = executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。");
            key.SetValue(AppValueName, BuildCommand(exe));
        }
        else
        {
            key.DeleteValue(AppValueName, throwOnMissingValue: false);
        }
    }

    public static string BuildCommand(string executablePath) => $"\"{executablePath}\"";
}