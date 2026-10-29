using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace PinDock.Setup;

public sealed record PreviousInstallInfo(
    bool IsInstalled,
    string InstallDir,
    string? InstalledVersion,
    bool CreateDesktopShortcut,
    bool CreateStartMenuShortcut,
    bool LaunchAtStartup);

public static class Program
{
    private const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PinDock";
    private const string InnoUninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F96D13B-4C7E-4B18-9F53-0D726EA18C90}_is1";
    private const string RunRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ExplorerShellCommandKey = @"Software\Classes\Directory\shell\PinDock\command";

    [STAThread]
    public static int Main(string[] args)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version is Version v
            ? $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}"
            : "0.1.0";
        string fallbackInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "PinDock");

        var prev = DetectPreviousInstall(fallbackInstallDir);

        bool isSilent = args.Any(a =>
            string.Equals(a, "/SILENT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/VERYSILENT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase));
        bool isUninstall = args.Any(a =>
            string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/UNINSTALL", StringComparison.OrdinalIgnoreCase));

        if (isUninstall)
        {
            RunUninstall(prev.InstallDir, isSilent);
            return 0;
        }

        if (isSilent)
        {
            InstallCore(
                prev.InstallDir,
                version,
                createDesktopShortcut: prev.CreateDesktopShortcut,
                createStartMenuShortcut: prev.CreateStartMenuShortcut,
                launchAtStartup: prev.LaunchAtStartup,
                launchNow: true);
            return 0;
        }

        var app = new Application();
        var win = BuildSetupWindow(prev, version);
        app.Run(win);
        return 0;
    }

    private static PreviousInstallInfo DetectPreviousInstall(string fallbackInstallDir)
    {
        string? detectedDir = null;
        string? detectedVersion = null;
        int? savedDesktopOpt = null;
        int? savedStartMenuOpt = null;
        int? savedStartupOpt = null;

        // 1. 从注册表 Uninstall\PinDock 读取之前记录的安装目录、版本与勾选项
        foreach (string keyPath in new[] { UninstallRegKey, InnoUninstallRegKey })
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath) ?? Registry.LocalMachine.OpenSubKey(keyPath);
            if (key is null) continue;

            if (string.IsNullOrWhiteSpace(detectedDir) && key.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                detectedDir = loc.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(detectedVersion) && key.GetValue("DisplayVersion") is string ver && !string.IsNullOrWhiteSpace(ver))
                detectedVersion = ver.Trim();
            if (savedDesktopOpt is null && key.GetValue("CreateDesktopShortcut") is int dOpt)
                savedDesktopOpt = dOpt;
            if (savedStartMenuOpt is null && key.GetValue("CreateStartMenuShortcut") is int sOpt)
                savedStartMenuOpt = sOpt;
            if (savedStartupOpt is null && key.GetValue("LaunchAtStartup") is int rOpt)
                savedStartupOpt = rOpt;
        }

        // 2. 若注册表未记录或目录已移动，尝试从正在运行的 PinDock 进程定位安装目录
        if (string.IsNullOrWhiteSpace(detectedDir) || !Directory.Exists(detectedDir))
        {
            int selfPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("PinDock"))
            {
                try
                {
                    if (proc.Id == selfPid) continue;
                    string? exePath = proc.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    {
                        detectedDir = Path.GetDirectoryName(exePath);
                        detectedVersion ??= FileVersionInfo.GetVersionInfo(exePath).ProductVersion?.Split('+')[0];
                        break;
                    }
                }
                catch (Exception)
                {
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        // 3. 尝试从开机自启注册表项或右键菜单注册表项反推安装目录
        if (string.IsNullOrWhiteSpace(detectedDir))
        {
            detectedDir = TryExtractExeDirectoryFromRegistry(RunRegKey, "PinDock")
                       ?? TryExtractExeDirectoryFromRegistry(ExplorerShellCommandKey, null);
        }

        // 4. 尝试从桌面或开始菜单现有快捷方式读取目标路径
        string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PinDock.lnk");
        string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "PinDock.lnk");
        if (string.IsNullOrWhiteSpace(detectedDir))
        {
            detectedDir = TryReadShortcutTargetDir(desktopLnk) ?? TryReadShortcutTargetDir(startMenuLnk);
        }

        bool isInstalled = !string.IsNullOrWhiteSpace(detectedDir) && File.Exists(Path.Combine(detectedDir, "PinDock.exe"));
        if (isInstalled && string.IsNullOrWhiteSpace(detectedVersion))
        {
            string exe = Path.Combine(detectedDir!, "PinDock.exe");
            detectedVersion = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
        }

        bool hasRunReg = false;
        using (var runKey = Registry.CurrentUser.OpenSubKey(RunRegKey))
        {
            hasRunReg = runKey?.GetValue("PinDock") is string s && !string.IsNullOrWhiteSpace(s);
        }

        bool createDesktop = savedDesktopOpt.HasValue ? savedDesktopOpt.Value != 0 : (!isInstalled || File.Exists(desktopLnk));
        bool createStartMenu = savedStartMenuOpt.HasValue ? savedStartMenuOpt.Value != 0 : (!isInstalled || File.Exists(startMenuLnk));
        bool launchAtStartup = savedStartupOpt.HasValue ? savedStartupOpt.Value != 0 : hasRunReg;

        return new PreviousInstallInfo(
            IsInstalled: isInstalled,
            InstallDir: !string.IsNullOrWhiteSpace(detectedDir) ? detectedDir! : fallbackInstallDir,
            InstalledVersion: detectedVersion,
            CreateDesktopShortcut: createDesktop,
            CreateStartMenuShortcut: createStartMenu,
            LaunchAtStartup: launchAtStartup);
    }

    private static string? TryExtractExeDirectoryFromRegistry(string subKeyPath, string? valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        if (key?.GetValue(valueName) is not string raw || string.IsNullOrWhiteSpace(raw))
            return null;
        string cleaned = raw.Trim();
        if (cleaned.StartsWith('"'))
        {
            int endQuote = cleaned.IndexOf('"', 1);
            if (endQuote > 1) cleaned = cleaned[1..endQuote];
        }
        else
        {
            int space = cleaned.IndexOf(' ');
            if (space > 0) cleaned = cleaned[..space];
        }
        return File.Exists(cleaned) ? Path.GetDirectoryName(cleaned) : null;
    }

    private static string? TryReadShortcutTargetDir(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string? target = shortcut.TargetPath as string;
            if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                return Path.GetDirectoryName(target);
        }
        catch (Exception)
        {
        }
        return null;
    }

    private static Window BuildSetupWindow(PreviousInstallInfo prev, string version)
    {
        var ink = (Brush)new BrushConverter().ConvertFromString("#1F3530")!;
        var muted = (Brush)new BrushConverter().ConvertFromString("#6A7B74")!;
        var paper = (Brush)new BrushConverter().ConvertFromString("#F8F9F5")!;
        var line = (Brush)new BrushConverter().ConvertFromString("#DDE4DC")!;
        var accent = (Brush)new BrushConverter().ConvertFromString("#236950")!;
        var soft = (Brush)new BrushConverter().ConvertFromString("#E6EFE3")!;

        var win = new Window
        {
            Title = prev.IsInstalled ? $"PinDock v{version} 覆盖更新 / 安装向导" : $"PinDock v{version} 安装向导",
            Width = 515,
            Height = prev.IsInstalled ? 405 : 370,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Background = paper
        };

        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = prev.IsInstalled ? $"更新 / 重新安装 PinDock v{version}" : $"安装 PinDock v{version}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink
        });
        header.Children.Add(new TextBlock
        {
            Text = "Windows 独立悬浮启动栏 · 收藏留在本机，不替换系统任务栏",
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 5, 0, 0)
        });
        if (prev.IsInstalled)
        {
            var detectedBanner = new Border
            {
                Background = soft,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    Text = $"已自动读取历史安装信息（当前已安装 v{prev.InstalledVersion ?? version}），将直接覆盖更新并保留全部收藏数据。",
                    FontSize = 11.5,
                    Foreground = accent,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            header.Children.Add(detectedBanner);
        }
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var dirPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        dirPanel.Children.Add(new TextBlock
        {
            Text = "安装目标文件夹：",
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = ink,
            Margin = new Thickness(0, 0, 0, 6)
        });
        var dirRow = new DockPanel();
        var browseBtn = new Button
        {
            Content = "浏览…",
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.White,
            BorderBrush = line,
            Foreground = ink
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        var dirBox = new TextBox
        {
            Text = prev.InstallDir,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12,
            BorderBrush = line,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "选择安装目录" };
            if (Directory.Exists(dirBox.Text))
                dlg.InitialDirectory = dirBox.Text;
            if (dlg.ShowDialog(win) == true)
            {
                string chosen = dlg.FolderName.TrimEnd('\\', '/');
                bool alreadyPinDockFolder =
                    string.Equals(Path.GetFileName(chosen), "PinDock", StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(Path.Combine(chosen, "PinDock.exe"));
                dirBox.Text = alreadyPinDockFolder ? chosen : Path.Combine(chosen, "PinDock");
            }
        };
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        dirPanel.Children.Add(dirRow);
        Grid.SetRow(dirPanel, 1);
        root.Children.Add(dirPanel);

        var opts = new StackPanel();
        var cbDesktop = new CheckBox { Content = "创建桌面快捷方式", IsChecked = prev.CreateDesktopShortcut, Margin = new Thickness(0, 4, 0, 6), Foreground = ink };
        var cbStartMenu = new CheckBox { Content = "创建开始菜单快捷方式", IsChecked = prev.CreateStartMenuShortcut, Margin = new Thickness(0, 0, 0, 6), Foreground = ink };
        var cbStartup = new CheckBox { Content = "开机自动启动 PinDock", IsChecked = prev.LaunchAtStartup, Margin = new Thickness(0, 0, 0, 6), Foreground = ink };
        var cbLaunchNow = new CheckBox { Content = "安装完成后立即启动 PinDock", IsChecked = true, Margin = new Thickness(0, 0, 0, 6), Foreground = ink };
        var statusText = new TextBlock { Text = "", FontSize = 11.5, Foreground = accent, Margin = new Thickness(0, 8, 0, 0) };
        opts.Children.Add(cbDesktop);
        opts.Children.Add(cbStartMenu);
        opts.Children.Add(cbStartup);
        opts.Children.Add(cbLaunchNow);
        opts.Children.Add(statusText);
        Grid.SetRow(opts, 2);
        root.Children.Add(opts);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button
        {
            Content = "取消",
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = Brushes.White,
            BorderBrush = line,
            Foreground = ink
        };
        cancelBtn.Click += (_, _) => win.Close();

        var installBtn = new Button
        {
            Content = prev.IsInstalled ? "覆盖更新" : "立即安装",
            Padding = new Thickness(20, 7, 20, 7),
            Background = accent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold
        };
        installBtn.Click += async (_, _) =>
        {
            string targetDir = dirBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetDir)) return;
            installBtn.IsEnabled = false;
            cancelBtn.IsEnabled = false;
            statusText.Text = "正在释放文件并配置快捷方式…";
            bool desk = cbDesktop.IsChecked == true;
            bool menu = cbStartMenu.IsChecked == true;
            bool autoRun = cbStartup.IsChecked == true;
            bool runNow = cbLaunchNow.IsChecked == true;
            try
            {
                await Task.Run(() => InstallCore(targetDir, version, desk, menu, autoRun, runNow));
                win.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(win, $"安装失败：{ex.Message}", "PinDock 安装向导", MessageBoxButton.OK, MessageBoxImage.Error);
                installBtn.IsEnabled = true;
                cancelBtn.IsEnabled = true;
            }
        };
        footer.Children.Add(cancelBtn);
        footer.Children.Add(installBtn);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        win.Content = root;
        return win;
    }

    private static void InstallCore(
        string installDir,
        string version,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        bool launchAtStartup,
        bool launchNow)
    {
        StopRunningPinDockProcesses();
        Directory.CreateDirectory(installDir);

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PinDock.Payload.zip")
            ?? throw new InvalidOperationException("安装包未包含有效载荷 (PinDock.Payload.zip)。");
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            archive.ExtractToDirectory(installDir, overwriteFiles: true);
        }

        string mainExe = Path.Combine(installDir, "PinDock.exe");
        string uninstallerExe = Path.Combine(installDir, "Uninstall-PinDock.exe");
        if (Environment.ProcessPath is string selfPath && File.Exists(selfPath))
        {
            try { File.Copy(selfPath, uninstallerExe, overwrite: true); }
            catch (IOException) { }
        }

        if (createDesktopShortcut)
        {
            string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PinDock.lnk");
            CreateShortcut(desktopLnk, mainExe, installDir);
        }

        if (createStartMenuShortcut)
        {
            string programsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            Directory.CreateDirectory(programsDir);
            CreateShortcut(Path.Combine(programsDir, "PinDock.lnk"), mainExe, installDir);
        }

        using (var runKey = Registry.CurrentUser.CreateSubKey(RunRegKey))
        {
            if (launchAtStartup)
                runKey.SetValue("PinDock", $"\"{mainExe}\"");
            else
                runKey.DeleteValue("PinDock", throwOnMissingValue: false);
        }

        using (var uninstKey = Registry.CurrentUser.CreateSubKey(UninstallRegKey))
        {
            uninstKey.SetValue("DisplayName", "PinDock");
            uninstKey.SetValue("DisplayVersion", version);
            uninstKey.SetValue("Publisher", "PinDock");
            uninstKey.SetValue("InstallLocation", installDir);
            uninstKey.SetValue("DisplayIcon", mainExe);
            uninstKey.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall");
            uninstKey.SetValue("CreateDesktopShortcut", createDesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
            uninstKey.SetValue("CreateStartMenuShortcut", createStartMenuShortcut ? 1 : 0, RegistryValueKind.DWord);
            uninstKey.SetValue("LaunchAtStartup", launchAtStartup ? 1 : 0, RegistryValueKind.DWord);
            uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        if (launchNow && File.Exists(mainExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = mainExe,
                WorkingDirectory = installDir,
                UseShellExecute = true
            });
        }
    }

    private static void RunUninstall(string fallbackInstallDir, bool silent)
    {
        if (!silent && MessageBox.Show("确定要卸载 PinDock 吗？（不会删除你的本地收藏数据）", "卸载 PinDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        StopRunningPinDockProcesses();
        string installDir = fallbackInstallDir;
        using (var uninstKey = Registry.CurrentUser.OpenSubKey(UninstallRegKey))
        {
            if (uninstKey?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                installDir = loc;
        }

        using (var runKey = Registry.CurrentUser.CreateSubKey(RunRegKey))
        {
            runKey.DeleteValue("PinDock", throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, throwOnMissingSubKey: false);

        string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PinDock.lnk");
        string startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "PinDock.lnk");
        if (File.Exists(desktopLnk)) File.Delete(desktopLnk);
        if (File.Exists(startMenuLnk)) File.Delete(startMenuLnk);

        if (Directory.Exists(installDir))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/C ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{installDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
    }

    private static void StopRunningPinDockProcesses()
    {
        int selfPid = Environment.ProcessId;
        foreach (var proc in Process.GetProcessesByName("PinDock"))
        {
            try
            {
                if (proc.Id == selfPid) continue;
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch (Exception)
            {
            }
            finally
            {
                proc.Dispose();
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetExe;
        shortcut.WorkingDirectory = workingDir;
        shortcut.Description = "PinDock - Windows 独立悬浮启动栏";
        shortcut.Save();
    }
}
