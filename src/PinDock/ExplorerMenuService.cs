using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PinDock;

public static class ExplorerMenuService
{
    private const string DirectoryShellKey = @"Software\Classes\Directory\shell\PinDock";
    private const string DirectoryBgShellKey = @"Software\Classes\Directory\Background\shell\PinDock";
    private const string DriveShellKey = @"Software\Classes\Drive\shell\PinDock";
    private const string FileShellKey = @"Software\Classes\*\shell\PinDock";
    private const string Win11ClassicClsidKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string Win11ClassicInprocKey = Win11ClassicClsidKey + @"\InprocServer32";
    private const string MenuTextZh = "添加到 PinDock";
    private const string MenuTextEn = "Add to PinDock";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DirectoryShellKey + @"\command");
        return key?.GetValue(null) is string cmd && cmd.Contains("--add", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetRegistered(bool enabled, string? executablePath = null, bool english = false)
    {
        if (enabled)
            Register(executablePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径。"), english);
        else
            Unregister();
    }

    public static void Register(string executablePath, bool english = false)
    {
        string text = english ? MenuTextEn : MenuTextZh;
        WriteVerb(DirectoryShellKey, executablePath, "%1", text);
        WriteVerb(DirectoryBgShellKey, executablePath, "%V", text);
        WriteVerb(DriveShellKey, executablePath, "%1", text);
        WriteVerb(FileShellKey, executablePath, "%1", text);
        CreateSendToShortcut(executablePath);
        NotifyShellAssociationChanged();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(DirectoryShellKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(DirectoryBgShellKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(DriveShellKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(FileShellKey, throwOnMissingSubKey: false);
        RemoveSendToShortcut();
        NotifyShellAssociationChanged();
    }

    public static bool IsWin11ClassicMenuEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Win11ClassicInprocKey);
        return key is not null && key.GetValue(null) is string val && val.Length == 0;
    }

    public static void SetWin11ClassicMenuEnabled(bool enabled, bool restartExplorer = false)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Win11ClassicInprocKey);
            key.SetValue(null, "");
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(Win11ClassicClsidKey, throwOnMissingSubKey: false);
        }
        NotifyShellAssociationChanged();
        if (restartExplorer)
        {
            RestartExplorerProcess();
        }
    }

    public static string BuildCommand(string executablePath, string token = "%1") =>
        $"\"{executablePath}\" --add \"{token} \"";

    public static string? TryExtractAddPath(IReadOnlyList<string> args)
    {
        var all = ExtractAddPaths(args);
        return all.Count > 0 ? all[0] : null;
    }

    public static IReadOnlyList<string> ExtractAddPaths(IReadOnlyList<string> args)
    {
        var results = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "--add", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    string? cleaned = CleanPathArg(args[++i]);
                    if (cleaned is not null) results.Add(cleaned);
                }
                continue;
            }
            if (arg.StartsWith("--", StringComparison.Ordinal))
                continue;
            string? pos = CleanPathArg(arg);
            if (pos is not null && (File.Exists(pos) || Directory.Exists(pos)))
                results.Add(pos);
        }
        return results;
    }

    private static string? CleanPathArg(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().Trim('"').Trim();
        if (s.Length == 0) return null;
        if (s.Length == 2 && char.IsLetter(s[0]) && s[1] == ':')
            return s + @"\";
        return s;
    }

    private static void WriteVerb(string subKeyPath, string executablePath, string token, string menuText)
    {
        using var verbKey = Registry.CurrentUser.CreateSubKey(subKeyPath);
        verbKey.SetValue(null, menuText);
        verbKey.SetValue("MUIVerb", menuText);
        verbKey.SetValue("Icon", $"\"{executablePath}\",0");
        verbKey.SetValue("Position", "Top");
        using var commandKey = verbKey.CreateSubKey("command");
        commandKey.SetValue(null, BuildCommand(executablePath, token));
    }

    private static void CreateSendToShortcut(string executablePath)
    {
        try
        {
            string sendToDir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            if (string.IsNullOrWhiteSpace(sendToDir) || !Directory.Exists(sendToDir)) return;
            string shortcutPath = Path.Combine(sendToDir, "PinDock.lnk");
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = executablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? "";
            shortcut.Description = "添加到 PinDock / Add to PinDock";
            shortcut.IconLocation = $"{executablePath},0";
            shortcut.Save();
        }
        catch (Exception)
        {
        }
    }

    private static void RemoveSendToShortcut()
    {
        try
        {
            string sendToDir = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            if (string.IsNullOrWhiteSpace(sendToDir)) return;
            string shortcutPath = Path.Combine(sendToDir, "PinDock.lnk");
            if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
        }
        catch (Exception)
        {
        }
    }

    private static void NotifyShellAssociationChanged()
    {
        try
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception)
        {
        }
    }

    private static void RestartExplorerProcess()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("explorer"))
            {
                try { proc.Kill(); proc.WaitForExit(2000); }
                catch (Exception) { }
                finally { proc.Dispose(); }
            }
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
