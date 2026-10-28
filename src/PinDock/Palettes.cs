using System.Windows.Media;

namespace PinDock;

public readonly record struct PaletteColors(
    Color Ink,
    Color Muted,
    Color Paper,
    Color Line,
    Color Accent,
    Color Soft,
    Color Selected,
    Color Glass,
    Color GlassBorder,
    Color InputBackground,
    Color SwitchOff,
    Color FolderBg,
    Color FolderFg,
    Color TooltipBg);

public static class Palettes
{
    public static PaletteColors Resolve(Palette palette, bool dark) => (palette, dark) switch
    {
        (Palette.Forest, false) => C("#1F3530", "#6A7B74", "#F8F9F5", "#DDE4DC", "#236950", "#E6EFE3", "#EDF4EA", 0.92, "#FAFBF7", dark: false),
        (Palette.Forest, true) => C("#EDF2EE", "#94A39B", "#181E1C", "#2B3531", "#52B788", "#232D29", "#283831", 0.94, "#1B2220", dark: true),
        (Palette.Ocean, false) => C("#1F3447", "#667B8C", "#F6F9FC", "#DAE3EC", "#2B6CB0", "#E3EEF7", "#EBF3FA", 0.92, "#F8FBFE", dark: false),
        (Palette.Ocean, true) => C("#EDF2F7", "#93A4B4", "#171D24", "#2A3440", "#5FA8D3", "#222B35", "#263544", 0.94, "#1A2129", dark: true),
        (Palette.Sand, false) => C("#3D3326", "#827463", "#FBF8F2", "#E6DFD1", "#9C6B30", "#F1E8D8", "#F6EFE2", 0.92, "#FCF9F4", dark: false),
        (Palette.Sand, true) => C("#F2EFE9", "#A69E93", "#1C1B19", "#33302B", "#D4A359", "#292622", "#352F26", 0.94, "#201E1B", dark: true),
        (Palette.Clay, false) => C("#402C27", "#8A726B", "#FCF6F3", "#EBDCD5", "#B8563E", "#F4E4DC", "#F9ECE6", 0.92, "#FDF8F5", dark: false),
        (Palette.Clay, true) => C("#F4EEEB", "#A89A94", "#1D1A19", "#352E2C", "#E07A5F", "#2B2523", "#382C28", 0.94, "#211D1C", dark: true),
        (Palette.Graphite, false) => C("#262C32", "#6E7881", "#F7F8FA", "#DFE3E7", "#475B6D", "#E7EBEF", "#EFF2F5", 0.92, "#F9FAFB", dark: false),
        (Palette.Graphite, true) => C("#EEF0F2", "#98A1A8", "#181A1D", "#2C3137", "#7C98B3", "#23272C", "#2B323A", 0.94, "#1B1E22", dark: true),
        _ => throw new ArgumentOutOfRangeException(nameof(palette), palette, null)
    };

    private static PaletteColors C(string ink, string muted, string paper, string line, string accent, string soft, string selected, double glassAlpha, string glassRgb, bool dark)
    {
        var baseGlass = Parse(glassRgb);
        baseGlass.A = (byte)(glassAlpha * 255);
        return new(
            Parse(ink),
            Parse(muted),
            Parse(paper),
            Parse(line),
            Parse(accent),
            Parse(soft),
            Parse(selected),
            baseGlass,
            Parse(dark ? "#2EFFFFFF" : "#C8FFFFFF"),
            Parse(dark ? "#13171B" : "#B8FFFFFF"),
            Parse(dark ? "#38414A" : "#BCC5BB"),
            Parse(dark ? "#383020" : "#EEE2BD"),
            Parse(dark ? "#E0B86B" : "#A47C2F"),
            Parse(dark ? "#10151A" : "#253E34"));
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
