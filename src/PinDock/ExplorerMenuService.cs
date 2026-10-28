using Microsoft.Win32;

namespace PinDock;

public static class ExplorerMenuService
{
    private const string DirectoryShellKey = @"Software\Classes\Directory\shell\PinDock";
    private const string DirectoryBgShellKey = @"Software\Classes\Directory\Background\shell\PinDock";
    private const string MenuText = "添加到 PinDock";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DirectoryShellKey + @"\command");
        return key?.GetValue(null) is string cmd && cmd.Contains("--add", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetRegistered(bool enabled, string? executablePath = null)
    {
        if (enabled)
            Register(executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。"));
        else
            Unregister();
    }

    public static void Register(string executablePath)
    {
        WriteVerb(DirectoryShellKey, executablePath, "%1");
        WriteVerb(DirectoryBgShellKey, executablePath, "%V");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(DirectoryShellKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(DirectoryBgShellKey, throwOnMissingSubKey: false);
    }

    public static string BuildCommand(string executablePath, string token = "%1") =>
        $"\"{executablePath}\" --add \"{token}\"";

    public static string? TryExtractAddPath(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--add", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1].Trim().Trim('"');
            }
        }
        return null;
    }

    private static void WriteVerb(string subKeyPath, string executablePath, string token)
    {
        using var verbKey = Registry.CurrentUser.CreateSubKey(subKeyPath);
        verbKey.SetValue(null, MenuText);
        verbKey.SetValue("Icon", $"\"{executablePath}\",0");
        using var commandKey = verbKey.CreateSubKey("command");
        commandKey.SetValue(null, BuildCommand(executablePath, token));
    }
}
