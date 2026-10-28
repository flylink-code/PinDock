using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PinDock;

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x5044;
    private HwndSource? source;
    private bool registered;

    public event Action? Pressed;

    public void Register(string modifiers, string key)
    {
        Unregister();
        if (string.IsNullOrWhiteSpace(modifiers) || string.IsNullOrWhiteSpace(key))
            return;

        EnsureWindow();
        uint mod = ParseModifiers(modifiers);
        uint vk = ParseKey(key);
        if (!RegisterHotKey(source!.Handle, HotkeyId, mod, vk))
            throw new InvalidOperationException($"无法注册全局快捷键 {FormatDisplay(modifiers, key)}，可能已被其他程序占用。");
        registered = true;
    }

    public static string FormatDisplay(string modifiers, string key)
    {
        if (string.IsNullOrWhiteSpace(modifiers) || string.IsNullOrWhiteSpace(key))
            return "不使用";
        return $"{modifiers.Replace("+", " + ")} + {key}";
    }

    public static bool TryFormatKey(Key wpfKey, out string keyText)
    {
        keyText = "";
        if (wpfKey is >= Key.A and <= Key.Z)
        {
            keyText = wpfKey.ToString();
            return true;
        }
        if (wpfKey is >= Key.D0 and <= Key.D9)
        {
            keyText = ((char)('0' + (wpfKey - Key.D0))).ToString();
            return true;
        }
        if (wpfKey is >= Key.F1 and <= Key.F12)
        {
            keyText = wpfKey.ToString();
            return true;
        }
        if (wpfKey == Key.Space)
        {
            keyText = "Space";
            return true;
        }
        if (wpfKey == Key.Oem3)
        {
            keyText = "`";
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        Unregister();
        source?.RemoveHook(Hook);
        source?.Dispose();
        source = null;
    }

    private void EnsureWindow()
    {
        if (source is not null) return;
        var parameters = new HwndSourceParameters("PinDockHotkey")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = unchecked((int)0x80000000)
        };
        source = new HwndSource(parameters);
        source.AddHook(Hook);
    }

    public void Unregister()
    {
        if (!registered || source is null) return;
        UnregisterHotKey(source.Handle, HotkeyId);
        registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static uint ParseModifiers(string modifiers)
    {
        uint value = 0;
        foreach (string part in modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            uint flag = part.ToUpperInvariant() switch
            {
                "ALT" => 0x0001u,
                "CTRL" or "CONTROL" => 0x0002u,
                "SHIFT" => 0x0004u,
                "WIN" or "WINDOWS" => 0x0008u,
                _ => throw new InvalidOperationException($"未知快捷键修饰键：{part}")
            };
            value |= flag;
        }
        if (value == 0)
            throw new InvalidOperationException("快捷键需要至少包含一个修饰键（Ctrl / Alt / Shift / Win）。");
        return value;
    }

    public static uint ParseKey(string key)
    {
        string normalized = key.Trim().ToUpperInvariant();
        if (normalized.Length == 1)
        {
            char c = normalized[0];
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
            if (c == '`') return 0xC0u;
        }
        if (normalized == "SPACE") return 0x20u;
        if (normalized.StartsWith('F') && int.TryParse(normalized[1..], out int fn) && fn is >= 1 and <= 12)
            return (uint)(0x70 + fn - 1);
        throw new InvalidOperationException($"不支持的快捷键：{key}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
