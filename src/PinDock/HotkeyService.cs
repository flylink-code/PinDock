using System.Runtime.InteropServices;
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
        EnsureWindow();
        Unregister();
        uint mod = ParseModifiers(modifiers);
        uint vk = ParseKey(key);
        if (!RegisterHotKey(source!.Handle, HotkeyId, mod, vk))
            throw new InvalidOperationException($"无法注册全局快捷键 {modifiers}+{key}，可能已被其他程序占用。");
        registered = true;
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

    private void Unregister()
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

    private static uint ParseModifiers(string modifiers)
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
        return value;
    }

    private static uint ParseKey(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        throw new InvalidOperationException($"不支持的快捷键：{key}");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
