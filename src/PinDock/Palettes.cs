using System.Windows.Media;

namespace PinDock;

public readonly record struct PaletteColors(
    Color Ink, Color Muted, Color Paper, Color Line, Color Accent, Color Soft, Color Selected, Color Glass);

public static class Palettes
{
    public static PaletteColors Resolve(Palette palette, bool dark) => (palette, dark) switch
    {
        (Palette.Forest, false) => C("#233B38", "#71817A", "#F7F8F2", "#DCE3DA", "#236950", "#E4EDDF", "#EDF3E7", 0.88, "#F9FBF5"),
        (Palette.Forest, true) => C("#E0E8DE", "#A4B3A7", "#28382F", "#435347", "#80B294", "#394F3D", "#354A3B", 0.96, "#26372D"),
        (Palette.Ocean, false) => C("#283E53", "#6C8191", "#F3F7FA", "#D6E1E9", "#306B94", "#DCEBF4", "#E8F2F9", 0.94, "#F2F8FC"),
        (Palette.Ocean, true) => C("#DEEBF5", "#A0B6C7", "#243440", "#415563", "#86B8D9", "#344C60", "#2E4557", 0.96, "#1F303D"),
        (Palette.Sand, false) => C("#514433", "#8A7A65", "#FAF6EE", "#E6DDCC", "#936A36", "#EFE3CE", "#F5EDDF", 0.94, "#FCF7EC"),
        (Palette.Sand, true) => C("#EEE5D4", "#BAAC95", "#393329", "#564C3C", "#CFAD75", "#514635", "#473E30", 0.96, "#332D23"),
        (Palette.Clay, false) => C("#563B35", "#947B72", "#FCF4EF", "#EAD8CE", "#AD5F48", "#F1DDD1", "#F7E7DE", 0.94, "#FCF2EC"),
        (Palette.Clay, true) => C("#F4E4DD", "#C3A69A", "#3E2E2A", "#61463E", "#DF9B80", "#583C33", "#4D352E", 0.96, "#382621"),
        (Palette.Graphite, false) => C("#353C42", "#77828A", "#F4F5F6", "#DCE0E3", "#546574", "#E0E5E9", "#EBEFF2", 0.94, "#F6F8F9"),
        (Palette.Graphite, true) => C("#E5E9ED", "#AAB4BC", "#2B3035", "#464E55", "#B0C5D5", "#414B54", "#364049", 0.96, "#242A2F"),
        _ => throw new ArgumentOutOfRangeException(nameof(palette), palette, null)
    };

    private static PaletteColors C(string ink, string muted, string paper, string line, string accent, string soft, string selected, double glassAlpha, string glassRgb)
    {
        var baseGlass = (Color)ColorConverter.ConvertFromString(glassRgb)!;
        baseGlass.A = (byte)(glassAlpha * 255);
        return new(
            (Color)ColorConverter.ConvertFromString(ink)!,
            (Color)ColorConverter.ConvertFromString(muted)!,
            (Color)ColorConverter.ConvertFromString(paper)!,
            (Color)ColorConverter.ConvertFromString(line)!,
            (Color)ColorConverter.ConvertFromString(accent)!,
            (Color)ColorConverter.ConvertFromString(soft)!,
            (Color)ColorConverter.ConvertFromString(selected)!,
            baseGlass);
    }
}
