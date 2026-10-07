using System.Runtime.InteropServices;

namespace Glimpse.App;

/// <summary>
/// System-wide hotkey via RegisterHotKey. WM_HOTKEY arrives on the window's message loop,
/// which we tap with SetWindowSubclass (WinUI doesn't expose WndProc).
/// </summary>
public sealed partial class GlobalHotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    readonly nint _hwnd;
    readonly int _id = 0x6C1;
    readonly SubclassProc _proc; // keep the delegate alive for the lifetime of the subclass
    public event Action? Pressed;
    public bool IsRegistered { get; }

    public GlobalHotkey(nint hwnd, string gesture)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, 1, 0);

        var (mods, vk) = Parse(gesture);
        IsRegistered = vk != 0 && RegisterHotKey(hwnd, _id, mods | MOD_NOREPEAT, vk);
    }

    /// <summary>Parses "Win+Shift+F", "Ctrl+Alt+Space", etc.</summary>
    static (uint Mods, uint Vk) Parse(string gesture)
    {
        uint mods = 0, vk = 0;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "win": mods |= MOD_WIN; break;
                case "ctrl" or "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "space": vk = 0x20; break;
                case var k when k.Length == 1 && char.IsLetterOrDigit(k[0]): vk = char.ToUpperInvariant(k[0]); break;
                case var k when k.StartsWith('f') && int.TryParse(k[1..], out var n) && n is >= 1 and <= 24: vk = (uint)(0x6F + n); break;
            }
        }
        return (mods, vk);
    }

    nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WM_HOTKEY && wParam == _id) Pressed?.Invoke();
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, _id);
        RemoveWindowSubclass(_hwnd, _proc, 1);
    }

    delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint mods, uint vk);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("comctl32.dll")]
    static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
}
