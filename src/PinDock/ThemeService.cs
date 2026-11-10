using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PinDock;

public sealed class ThemeService
{
    private ThemeMode mode = ThemeMode.Light;
    private Palette palette = Palette.Forest;

    public event Action? Changed;

    public PaletteColors Current { get; private set; } = Palettes.Resolve(Palette.Forest, false);

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Apply(ThemeMode themeMode, Palette paletteChoice)
    {
        mode = themeMode;
        palette = paletteChoice;
        Refresh();
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (mode == ThemeMode.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
            Application.Current?.Dispatcher.Invoke(Refresh);
    }

    private void Refresh()
    {
        bool dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && IsSystemDark());
        Current = Palettes.Resolve(palette, dark);
        var app = Application.Current;
        if (app is not null)
        {
            SetBrush(app, "InkBrush", Current.Ink);
            SetBrush(app, "MutedBrush", Current.Muted);
            SetBrush(app, "PaperBrush", Current.Paper);
            SetBrush(app, "LineBrush", Current.Line);
            SetBrush(app, "AccentBrush", Current.Accent);
            SetBrush(app, "SoftBrush", Current.Soft);
            SetBrush(app, "SelectedBrush", Current.Selected);
            SetGlassBrush(app, Current.Glass);
            SetBrush(app, "GlassBorderBrush", Current.GlassBorder);
            SetBrush(app, "InputBackgroundBrush", Current.InputBackground);
            SetBrush(app, "SwitchOffBrush", Current.SwitchOff);
            SetBrush(app, "FolderBgBrush", Current.FolderBg);
            SetBrush(app, "FolderFgBrush", Current.FolderFg);
            SetBrush(app, "TooltipBgBrush", Current.TooltipBg);
        }
        Changed?.Invoke();
    }

    private static void SetBrush(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    /// <summary>
    /// Dock 玻璃底色使用纵向渐变，模拟 macOS 半透明材质的顶部高光与底部沉淀。
    /// </summary>
    private static void SetGlassBrush(Application app, Color color)
    {
        bool dark = color.R * 0.299 + color.G * 0.587 + color.B * 0.114 < 128;
        var top = Blend(color, Colors.White, dark ? 0.10 : 0.45);
        var bottom = Blend(color, Colors.Black, dark ? 0.18 : 0.03);
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        app.Resources["GlassBrush"] = brush;
    }

    private static Color Blend(Color color, Color target, double amount) => Color.FromArgb(
        color.A,
        (byte)Math.Round(color.R + (target.R - color.R) * amount),
        (byte)Math.Round(color.G + (target.G - color.G) * amount),
        (byte)Math.Round(color.B + (target.B - color.B) * amount));

    public static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        object? value = key?.GetValue("AppsUseLightTheme");
        return value is int i && i == 0;
    }
}
