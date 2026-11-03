using System.IO;
using System.Text;

namespace PinDock;

internal static class RecentIo
{
    internal static byte[] ReadFileShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream((int)Math.Min(fs.Length, 16 * 1024 * 1024));
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    internal static string ReadTextShared(string path) =>
        Encoding.UTF8.GetString(ReadFileShared(path));

    internal static DateTime SafeGetLastWriteTime(string path, bool isDirectory)
    {
        try
        {
            return isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    internal static string? TryDecodeHexUtf8(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
            return null;
        for (int i = 0; i < hex.Length; i++)
        {
            if (!Uri.IsHexDigit(hex[i]))
                return null;
        }
        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
