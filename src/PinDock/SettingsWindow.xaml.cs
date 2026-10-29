using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PinDock;

public partial class SettingsWindow : CardWindowBase
{
    private readonly AppController controller;
    private bool loading = true;
    private bool recordingHotkey;

    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
    }

    public void ShowSettings()
    {
        recordingHotkey = false;
        LoadFromSettings();
        NavGeneral.IsChecked = true;
        Show();
        Activate();
    }

    private void LoadFromSettings()
    {
        loading = true;
        var s = controller.Data.Settings;
        ModePersistent.IsChecked = s.DisplayMode == DisplayMode.Persistent;
        ModeHotkey.IsChecked = s.DisplayMode == DisplayMode.Hotkey;
        ModeAutoHide.IsChecked = s.DisplayMode == DisplayMode.EdgeAutoHide;
        CollapseAfterLaunch.IsChecked = s.CollapseAfterLaunch;
        RefreshHotkeyUi(s);
        PaletteForest.IsChecked = s.Palette == Palette.Forest;
        PaletteOcean.IsChecked = s.Palette == Palette.Ocean;
        PaletteSand.IsChecked = s.Palette == Palette.Sand;
        PaletteClay.IsChecked = s.Palette == Palette.Clay;
        PaletteGraphite.IsChecked = s.Palette == Palette.Graphite;
        SelectCombo(ThemeBox, s.ThemeMode.ToString());
        SelectCombo(SizeBox, s.IconSize.ToString());
        OpacitySlider.Value = Math.Round(s.Opacity * 100);
        OpacityValue.Text = $"{(int)OpacitySlider.Value}%";
        TopmostBox.IsChecked = s.Topmost;
        AvoidFullscreenBox.IsChecked = s.AvoidFullscreen;
        LockPositionBox.IsChecked = s.LockPosition;
        ExplorerMenuBox.IsChecked = s.ExplorerContextMenu || ExplorerMenuService.IsRegistered();
        EdgeTop.IsChecked = s.DockEdge == DockEdge.Top;
        EdgeLeft.IsChecked = s.DockEdge == DockEdge.Left;
        EdgeRight.IsChecked = s.DockEdge == DockEdge.Right;
        EdgeBottom.IsChecked = s.DockEdge == DockEdge.Bottom;
        SelectCombo(MonitorPolicyBox, s.MonitorPolicy.ToString());
        BuildMonitorCards(s);
        loading = false;
    }

    private void RefreshHotkeyUi(AppSettings s)
    {
        HotkeyText.Text = recordingHotkey
            ? "请按下组合键… (Esc 取消)"
            : HotkeyService.FormatDisplay(s.HotkeyModifiers, s.HotkeyKey);
        ClearHotkeyButton.IsEnabled = s.HasHotkey || recordingHotkey;
        HotkeyHintText.Text = !s.HasHotkey && s.DisplayMode == DisplayMode.Hotkey
            ? "当前未设置快捷键；在“快捷键呼出”模式下请通过系统托盘图标呼出 Dock"
            : "点击快捷键可重新录制，或点击“清除”设为不使用";
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        recordingHotkey = !recordingHotkey;
        RefreshHotkeyUi(controller.Data.Settings);
    }

    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        recordingHotkey = false;
        var s = controller.Data.Settings;
        s.HotkeyModifiers = "";
        s.HotkeyKey = "";
        RefreshHotkeyUi(s);
        controller.UpdateHotkey();
    }

    private void ResetHotkey_Click(object sender, RoutedEventArgs e)
    {
        recordingHotkey = false;
        var s = controller.Data.Settings;
        s.HotkeyModifiers = "Alt+Shift";
        s.HotkeyKey = "P";
        RefreshHotkeyUi(s);
        controller.UpdateHotkey();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!recordingHotkey) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            recordingHotkey = false;
            RefreshHotkeyUi(controller.Data.Settings);
            return;
        }
        if (key is Key.Back or Key.Delete)
        {
            ClearHotkey_Click(sender, e);
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var mods = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods.Add("Win");

        if (mods.Count == 0)
        {
            HotkeyHintText.Text = "请至少搭配一个修饰键（Ctrl / Alt / Shift / Win）";
            return;
        }
        if (!HotkeyService.TryFormatKey(key, out string keyName))
        {
            HotkeyHintText.Text = "仅支持字母 A-Z、数字 0-9、F1-F12、Space 或 ` 键";
            return;
        }

        recordingHotkey = false;
        var s = controller.Data.Settings;
        s.HotkeyModifiers = string.Join("+", mods);
        s.HotkeyKey = keyName;
        RefreshHotkeyUi(s);
        controller.UpdateHotkey();
    }

    private void ExplorerMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        bool wantEnabled = ExplorerMenuBox.IsChecked == true;
        try
        {
            ExplorerMenuService.SetRegistered(wantEnabled);
            controller.Data.Settings.ExplorerContextMenu = wantEnabled;
            controller.ApplySettings();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"无法更新资源管理器右键菜单：{ex.Message}");
            loading = true;
            ExplorerMenuBox.IsChecked = ExplorerMenuService.IsRegistered();
            loading = false;
        }
    }

    private void BuildMonitorCards(AppSettings s)
    {
        MonitorList.Items.Clear();
        var monitors = MonitorQuery.All();
        string? selected = s.MonitorDeviceName ?? monitors.First(m => m.IsPrimary).DeviceName;
        for (int i = 0; i < monitors.Count; i++)
        {
            var monitor = monitors[i];
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = $"{i + 1} · {(monitor.IsPrimary ? "主显示器" : "外接显示器")}",
                FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{monitor.Bounds.Width:0} × {monitor.Bounds.Height:0}",
                FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
            });
            var card = new RadioButton
            {
                GroupName = "Monitor",
                Content = content,
                Tag = monitor.DeviceName,
                Style = (Style)FindResource("SelectCard"),
                Margin = new Thickness(0, 0, i == monitors.Count - 1 ? 0 : 12, 0),
                IsChecked = string.Equals(monitor.DeviceName, selected, StringComparison.OrdinalIgnoreCase),
                IsEnabled = s.MonitorPolicy == MonitorPolicy.Fixed
            };
            card.Checked += SettingsChanged;
            MonitorList.Items.Add(card);
        }
        MonitorHint.Text = s.MonitorPolicy == MonitorPolicy.Fixed
            ? "Dock 固定显示在所选显示器上；该显示器被拔掉时回到主显示器。"
            : s.MonitorPolicy == MonitorPolicy.Primary
                ? "Dock 始终显示在系统主显示器上。"
                : "每次呼出时，Dock 出现在鼠标所在的显示器上。";
        ScreenLabel.Text = monitors.Count > 1 ? "显示器" : "主显示器";
    }

    private static void SelectCombo(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag?.ToString(), tag)) { box.SelectedItem = item; return; }
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGeneral is null || sender is not RadioButton { Tag: string page }) return;
        PanelGeneral.Visibility = page == "general" ? Visibility.Visible : Visibility.Collapsed;
        PanelAppearance.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        PanelBehavior.Visibility = page == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = page == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SettingsChanged(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        ApplyToSettings();
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (loading) return;
        OpacityValue.Text = $"{(int)OpacitySlider.Value}%";
        controller.Data.Settings.Opacity = OpacitySlider.Value / 100.0;
        controller.UpdateOpacityOnly();
    }

    private void ApplyToSettings()
    {
        var s = controller.Data.Settings;
        s.DisplayMode = ModeAutoHide.IsChecked == true ? DisplayMode.EdgeAutoHide
            : ModeHotkey.IsChecked == true ? DisplayMode.Hotkey
            : DisplayMode.Persistent;
        s.CollapseAfterLaunch = CollapseAfterLaunch.IsChecked == true;
        s.Palette = PaletteOcean.IsChecked == true ? Palette.Ocean
            : PaletteSand.IsChecked == true ? Palette.Sand
            : PaletteClay.IsChecked == true ? Palette.Clay
            : PaletteGraphite.IsChecked == true ? Palette.Graphite
            : Palette.Forest;
        if (ThemeBox.SelectedItem is ComboBoxItem theme && theme.Tag is string themeTag)
            s.ThemeMode = Enum.Parse<ThemeMode>(themeTag);
        if (SizeBox.SelectedItem is ComboBoxItem size && size.Tag is string sizeTag)
            s.IconSize = Enum.Parse<IconSize>(sizeTag);
        s.Opacity = OpacitySlider.Value / 100.0;
        s.Topmost = TopmostBox.IsChecked == true;
        s.AvoidFullscreen = AvoidFullscreenBox.IsChecked == true;
        s.LockPosition = LockPositionBox.IsChecked == true;
        s.DockEdge = EdgeTop.IsChecked == true ? DockEdge.Top
            : EdgeLeft.IsChecked == true ? DockEdge.Left
            : EdgeRight.IsChecked == true ? DockEdge.Right
            : DockEdge.Bottom;
        if (MonitorPolicyBox.SelectedItem is ComboBoxItem policy && policy.Tag is string policyTag)
            s.MonitorPolicy = Enum.Parse<MonitorPolicy>(policyTag);
        foreach (RadioButton card in MonitorList.Items)
        {
            if (card.IsChecked == true && card.Tag is string device) s.MonitorDeviceName = device;
            card.IsEnabled = s.MonitorPolicy == MonitorPolicy.Fixed;
        }
        MonitorHint.Text = s.MonitorPolicy == MonitorPolicy.Fixed
            ? "Dock 固定显示在所选显示器上；该显示器被拔掉时回到主显示器。"
            : s.MonitorPolicy == MonitorPolicy.Primary
                ? "Dock 始终显示在系统主显示器上。"
                : "每次呼出时，Dock 出现在鼠标所在的显示器上。";
        RefreshHotkeyUi(s);
        controller.ApplySettings();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PinDock 收藏|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var (added, skipped) = controller.Import(dialog.FileName);
            MessageBox.Show(this, $"导入完成：新增 {added} 项，跳过重复 {skipped} 项。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or UnsupportedLibraryVersionException)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}");
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "PinDock 收藏|*.json", FileName = "pindock-library.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            controller.Export(dialog.FileName);
            MessageBox.Show(this, "导出完成。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
