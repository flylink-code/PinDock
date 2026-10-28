using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PinDock;

public partial class SettingsWindow : Window
{
    private readonly AppController controller;
    private bool loading = true;

    public SettingsWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
    }

    public void ShowSettings()
    {
        LoadFromSettings();
        ShowPage("general");
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
        PaletteForest.IsChecked = s.Palette == Palette.Forest;
        PaletteOcean.IsChecked = s.Palette == Palette.Ocean;
        PaletteSand.IsChecked = s.Palette == Palette.Sand;
        PaletteClay.IsChecked = s.Palette == Palette.Clay;
        PaletteGraphite.IsChecked = s.Palette == Palette.Graphite;
        SelectCombo(ThemeBox, s.ThemeMode.ToString());
        SelectCombo(SizeBox, s.IconSize.ToString());
        OpacitySlider.Value = Math.Round(s.Opacity * 100);
        OpacityLabel.Text = $"透明度 {(int)OpacitySlider.Value}%";
        TopmostBox.IsChecked = s.Topmost;
        AvoidFullscreenBox.IsChecked = s.AvoidFullscreen;
        LockPositionBox.IsChecked = s.LockPosition;
        EdgeTop.IsChecked = s.DockEdge == DockEdge.Top;
        EdgeLeft.IsChecked = s.DockEdge == DockEdge.Left;
        EdgeRight.IsChecked = s.DockEdge == DockEdge.Right;
        EdgeBottom.IsChecked = s.DockEdge == DockEdge.Bottom;
        SelectCombo(MonitorPolicyBox, s.MonitorPolicy.ToString());
        MonitorBox.Items.Clear();
        foreach (var monitor in MonitorQuery.All())
        {
            MonitorBox.Items.Add(new ComboBoxItem { Content = monitor.DisplayName, Tag = monitor.DeviceName });
            if (string.Equals(monitor.DeviceName, s.MonitorDeviceName, StringComparison.OrdinalIgnoreCase) ||
                (s.MonitorDeviceName is null && monitor.IsPrimary))
                MonitorBox.SelectedItem = MonitorBox.Items[^1];
        }
        if (MonitorBox.SelectedItem is null && MonitorBox.Items.Count > 0) MonitorBox.SelectedIndex = 0;
        MonitorBox.IsEnabled = s.MonitorPolicy == MonitorPolicy.Fixed;
        loading = false;
    }

    private static void SelectCombo(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (Equals(item.Tag?.ToString(), tag)) { box.SelectedItem = item; return; }
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string page) ShowPage(page);
    }

    private void ShowPage(string page)
    {
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
        if (loading || OpacityLabel is null) return;
        OpacityLabel.Text = $"透明度 {(int)OpacitySlider.Value}%";
        ApplyToSettings();
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
        MonitorBox.IsEnabled = s.MonitorPolicy == MonitorPolicy.Fixed;
        if (MonitorBox.SelectedItem is ComboBoxItem monitor && monitor.Tag is string device)
            s.MonitorDeviceName = device;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
