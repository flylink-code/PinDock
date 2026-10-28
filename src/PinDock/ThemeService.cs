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
            SetBrush(app, "GlassBrush", Current.Glass);
        }
        Changed?.Invoke();
    }

    private static void SetBrush(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    public static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        object? value = key?.GetValue("AppsUseLightTheme");
        return value is int i && i == 0;
    }
}
