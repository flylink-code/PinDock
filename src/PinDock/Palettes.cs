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

        // macOS 系统强调色：中性灰底 + 系统圆角风格强调色
        (Palette.MacBlue, false) => C("#1D1D1F", "#6E6E73", "#F5F5F7", "#D8D8DE", "#007AFF", "#ECECF1", "#E3EFFF", 0.90, "#F5F5F7", dark: false, mac: true),
        (Palette.MacBlue, true) => C("#F5F5F7", "#A1A1A6", "#1C1C1E", "#38383A", "#0A84FF", "#2C2C2E", "#17293F", 0.93, "#232325", dark: true, mac: true),
        (Palette.MacPurple, false) => C("#1D1D1F", "#6E6E73", "#F5F5F7", "#D8D8DE", "#AF52DE", "#ECECF1", "#F3E9FB", 0.90, "#F5F5F7", dark: false, mac: true),
        (Palette.MacPurple, true) => C("#F5F5F7", "#A1A1A6", "#1C1C1E", "#38383A", "#BF5AF2", "#2C2C2E", "#281C33", 0.93, "#232325", dark: true, mac: true),
        (Palette.MacPink, false) => C("#1D1D1F", "#6E6E73", "#F5F5F7", "#D8D8DE", "#FF2D55", "#ECECF1", "#FFE7EC", 0.90, "#F5F5F7", dark: false, mac: true),
        (Palette.MacPink, true) => C("#F5F5F7", "#A1A1A6", "#1C1C1E", "#38383A", "#FF375F", "#2C2C2E", "#331A22", 0.93, "#232325", dark: true, mac: true),
        (Palette.MacOrange, false) => C("#1D1D1F", "#6E6E73", "#F5F5F7", "#D8D8DE", "#FF9500", "#ECECF1", "#FFF0DC", 0.90, "#F5F5F7", dark: false, mac: true),
        (Palette.MacOrange, true) => C("#F5F5F7", "#A1A1A6", "#1C1C1E", "#38383A", "#FF9F0A", "#2C2C2E", "#332416", 0.93, "#232325", dark: true, mac: true),
        (Palette.MacGreen, false) => C("#1D1D1F", "#6E6E73", "#F5F5F7", "#D8D8DE", "#34C759", "#ECECF1", "#E5F8EA", 0.90, "#F5F5F7", dark: false, mac: true),
        (Palette.MacGreen, true) => C("#F5F5F7", "#A1A1A6", "#1C1C1E", "#38383A", "#30D158", "#2C2C2E", "#162A1D", 0.93, "#232325", dark: true, mac: true),
        (Palette.MacGraphite, false) => C("#1D1D1F", "#6E6E73", "#F5F5F7", "#D8D8DE", "#8E8E93", "#ECECF1", "#ECECEF", 0.90, "#F5F5F7", dark: false, mac: true),
        (Palette.MacGraphite, true) => C("#F5F5F7", "#A1A1A6", "#1C1C1E", "#38383A", "#98989D", "#2C2C2E", "#2A2A2D", 0.93, "#232325", dark: true, mac: true),
        _ => throw new ArgumentOutOfRangeException(nameof(palette), palette, null)
    };

    private static PaletteColors C(string ink, string muted, string paper, string line, string accent, string soft, string selected, double glassAlpha, string glassRgb, bool dark, bool mac = false)
    {
        var paperColor = Parse(paper);
        var accentColor = Parse(accent);
        var baseGlass = Parse(glassRgb);
        baseGlass.A = (byte)(glassAlpha * 255);
        return new(
            Parse(ink),
            Parse(muted),
            paperColor,
            Parse(line),
            accentColor,
            Parse(soft),
            Parse(selected),
            baseGlass,
            Parse(dark ? "#2EFFFFFF" : "#C8FFFFFF"),
            Parse(dark ? "#13171B" : "#B8FFFFFF"),
            mac ? Parse(dark ? "#48484A" : "#C7C7CC") : Parse(dark ? "#38414A" : "#BCC5BB"),
            mac ? Mix(paperColor, accentColor, dark ? 0.26 : 0.16) : Parse(dark ? "#383020" : "#EEE2BD"),
            mac
                ? (dark ? Mix(accentColor, Colors.White, 0.30) : Mix(accentColor, Colors.Black, 0.25))
                : Parse(dark ? "#E0B86B" : "#A47C2F"),
            mac ? Parse("#1C1C1E") : Parse(dark ? "#10151A" : "#253E34"));
    }

    private static Color Mix(Color a, Color b, double t)
    {
        byte Blend(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(Blend(a.A, b.A), Blend(a.R, b.R), Blend(a.G, b.G), Blend(a.B, b.B));
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
}
