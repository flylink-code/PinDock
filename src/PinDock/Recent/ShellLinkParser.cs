using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PinDock;

internal static class ShellLinkParser
{
    public sealed class ParsedShortcutLink
    {
        public string TargetPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string Arguments { get; set; } = "";
        public string IconLocation { get; set; } = "";
        public string AppUserModelId { get; set; } = "";
    }

    private static readonly byte[] ShellLinkHeaderSig =
    [
        0x4C, 0x00, 0x00, 0x00,
        0x01, 0x14, 0x02, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0xC0, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x46
    ];

    private static readonly byte[] AppUserModelIdFmtId =
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3").ToByteArray();

    public static List<ParsedShortcutLink> ParseEmbeddedLinks(byte[] data)
    {
        var list = new List<ParsedShortcutLink>();
        if (data is null || data.Length < 76) return list;

        for (int i = 0; i <= data.Length - 76; i++)
        {
            bool match = true;
            for (int j = 0; j < ShellLinkHeaderSig.Length; j++)
            {
                if (data[i + j] != ShellLinkHeaderSig[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;
            if (TryParseLnkAt(data, i, out var parsed, out int nextPos))
            {
                list.Add(parsed);
                if (nextPos > i + 76)
                    i = nextPos - 1;
            }
        }
        return list;
    }

    private static bool TryParseLnkAt(byte[] data, int offset, out ParsedShortcutLink result, out int endOffset)
    {
        result = new ParsedShortcutLink();
        endOffset = offset + 76;
        try
        {
            uint flags = BitConverter.ToUInt32(data, offset + 20);
            int pos = offset + 76;
            bool isUnicode = (flags & 0x80) != 0;
            string pidlPath = "";

            if ((flags & 0x01) != 0) // HasLinkTargetIDList
            {
                if (pos + 2 > data.Length) return false;
                ushort idListSize = BitConverter.ToUInt16(data, pos);
                pos += 2;
                if (pos + idListSize > data.Length) return false;
                pidlPath = DecodePidlPath(data, pos);
                pos += idListSize;
            }

            if ((flags & 0x02) != 0) // HasLinkInfo
            {
                if (pos + 12 > data.Length) return false;
                uint infoSize = BitConverter.ToUInt32(data, pos);
                if (infoSize < 12 || pos + infoSize > data.Length) return false;
                uint infoHeaderSize = BitConverter.ToUInt32(data, pos + 4);
                uint infoFlags = BitConverter.ToUInt32(data, pos + 8);
                if ((infoFlags & 0x01) != 0) // VolumeIDAndLocalBasePath
                {
                    if (infoHeaderSize >= 0x24 && pos + 32 <= data.Length)
                    {
                        uint uniOffset = BitConverter.ToUInt32(data, pos + 28);
                        if (uniOffset > 0 && pos + uniOffset < pos + infoSize)
                            result.TargetPath = ReadNullTerminatedUnicode(data, (int)(pos + uniOffset));
                    }
                    if (string.IsNullOrWhiteSpace(result.TargetPath) && pos + 20 <= data.Length)
                    {
                        uint ansiOffset = BitConverter.ToUInt32(data, pos + 16);
                        if (ansiOffset > 0 && pos + ansiOffset < pos + infoSize)
                            result.TargetPath = ReadNullTerminatedAnsi(data, (int)(pos + ansiOffset));
                    }
                }
                pos += (int)infoSize;
            }

            if (!string.IsNullOrWhiteSpace(pidlPath) &&
                (string.IsNullOrWhiteSpace(result.TargetPath) || (!File.Exists(result.TargetPath) && !Directory.Exists(result.TargetPath))))
            {
                result.TargetPath = pidlPath;
            }

            if ((flags & 0x04) != 0) result.Name = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x08) != 0) result.RelativePath = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x10) != 0) result.WorkingDirectory = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x20) != 0) result.Arguments = ReadCountedString(data, ref pos, isUnicode);
            if ((flags & 0x40) != 0) result.IconLocation = ReadCountedString(data, ref pos, isUnicode);

            // Parse ExtraData blocks (EnvironmentVariableDataBlock & PropertyStoreDataBlock)
            while (pos + 4 <= data.Length)
            {
                uint blockSize = BitConverter.ToUInt32(data, pos);
                if (blockSize < 4 || pos + blockSize > data.Length)
                    break;
                if (pos + 8 > data.Length)
                    break;
                uint blockSig = BitConverter.ToUInt32(data, pos + 4);
                if (blockSig == 0xA0000001 && blockSize >= 788 && string.IsNullOrWhiteSpace(result.TargetPath))
                {
                    string envPath = ReadNullTerminatedUnicode(data, pos + 268);
                    if (!string.IsNullOrWhiteSpace(envPath))
                        result.TargetPath = Environment.ExpandEnvironmentVariables(envPath);
                }
                else if (blockSig == 0xA0000009)
                {
                    string appId = ExtractAppUserModelIdFromBuffer(data, pos + 8, (int)(blockSize - 8));
                    if (!string.IsNullOrWhiteSpace(appId))
                        result.AppUserModelId = appId;
                }
                pos += (int)blockSize;
            }

            endOffset = pos;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ExtractAppUserModelIdFromBuffer(byte[] data, int start, int length)
    {
        int end = Math.Min(data.Length, start + length);
        for (int i = start; i <= end - 28; i++)
        {
            bool match = true;
            for (int j = 0; j < 16; j++)
            {
                if (data[i + j] != AppUserModelIdFmtId[j])
                {
                    match = false;
                    break;
                }
            }
            if (!match) continue;

            int pos = i + 16;
            while (pos + 13 < end)
            {
                uint valSize = BitConverter.ToUInt32(data, pos);
                if (valSize < 13 || pos + valSize > end) break;
                uint propId = BitConverter.ToUInt32(data, pos + 4);
                ushort vt = BitConverter.ToUInt16(data, pos + 9);
                if (propId == 5 && vt == 31 && pos + 17 <= end)
                {
                    uint cch = BitConverter.ToUInt32(data, pos + 13);
                    int byteLen = (int)Math.Min(cch * 2, valSize - 17);
                    if (byteLen > 0 && pos + 17 + byteLen <= end)
                        return Encoding.Unicode.GetString(data, pos + 17, byteLen).TrimEnd('\0');
                }
                pos += (int)valSize;
            }
        }
        return "";
    }

    private static string DecodePidlPath(byte[] data, int offset)
    {
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr ptr = IntPtr.Add(handle.AddrOfPinnedObject(), offset);
            var sb = new StringBuilder(1024);
            return SHGetPathFromIDListW(ptr, sb) ? sb.ToString() : "";
        }
        finally
        {
            handle.Free();
        }
    }

    private static string ReadCountedString(byte[] data, ref int pos, bool isUnicode)
    {
        if (pos + 2 > data.Length) return "";
        ushort count = BitConverter.ToUInt16(data, pos);
        pos += 2;
        int byteCount = isUnicode ? count * 2 : count;
        if (pos + byteCount > data.Length)
        {
            pos = data.Length;
            return "";
        }
        string s = isUnicode
            ? Encoding.Unicode.GetString(data, pos, byteCount)
            : Encoding.Default.GetString(data, pos, byteCount);
        pos += byteCount;
        return s.TrimEnd('\0');
    }

    private static string ReadNullTerminatedUnicode(byte[] data, int pos)
    {
        int end = pos;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0))
            end += 2;
        return end > pos ? Encoding.Unicode.GetString(data, pos, end - pos) : "";
    }

    private static string ReadNullTerminatedAnsi(byte[] data, int pos)
    {
        int end = pos;
        while (end < data.Length && data[end] != 0)
            end++;
        return end > pos ? Encoding.Default.GetString(data, pos, end - pos) : "";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);
}
