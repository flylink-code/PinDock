namespace PinDock;

public static class I18n
{
    public static bool IsEn(AppSettings? settings) =>
        settings is not null && string.Equals(settings.Language, "en-US", StringComparison.OrdinalIgnoreCase);

    public static string T(AppSettings? settings, string zh, string en) =>
        IsEn(settings) ? en : zh;
}
