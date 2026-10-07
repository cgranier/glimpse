using System.Runtime.InteropServices;

namespace Glimpse.App;

public sealed record TrayMenuItem(string Text, Action Invoke, bool Checked = false, bool IsDefault = false)
{
    public static readonly TrayMenuItem Separator = new("-", () => { });
}

/// <summary>
/// Notification-area icon via Shell_NotifyIcon, using the main window's message loop
/// (WinUI has no tray API). Left click runs <see cref="Clicked"/>; right click shows a native menu
/// built fresh each time from <see cref="BuildMenu"/>, so checkmarks are always current.
/// </summary>
public sealed partial class TrayIcon : IDisposable
{
    const uint WM_APP_TRAY = 0x8000 + 0x47; // WM_APP + n
    const uint WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_NULL = 0x0000;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800, MF_CHECKED = 0x8;
    const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;
    const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    const int SM_CXSMICON = 49, SM_CYSMICON = 50;

    // Explorer broadcasts this after it restarts; the icon has to be re-added.
    static readonly uint WM_TASKBARCREATED = RegisterWindowMessageW("TaskbarCreated");

    readonly nint _hwnd;
    readonly nint _hicon;
    readonly SubclassProc _proc;
    string _tooltip;

    public event Action? Clicked;
    public Func<IReadOnlyList<TrayMenuItem>>? BuildMenu { get; set; }

    /// <summary>Extra window messages to route (e.g. a "summon" message from a second instance).</summary>
    public event Action<uint>? Message;

    public TrayIcon(nint hwnd, string iconPath, string tooltip)
    {
        _hwnd = hwnd;
        _tooltip = tooltip;
        _hicon = LoadImageW(0, iconPath, IMAGE_ICON, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_LOADFROMFILE);
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, 2, 0);
        Notify(NIM_ADD);
    }

    public string Tooltip
    {
        get => _tooltip;
        set { _tooltip = value; Notify(NIM_MODIFY); }
    }

    void Notify(uint action)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _hicon,
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        };
        Shell_NotifyIconW(action, ref data);
    }

    nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (msg == WM_APP_TRAY)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case WM_LBUTTONUP: Clicked?.Invoke(); break;
                case WM_RBUTTONUP: ShowMenu(); break;
            }
            return 0;
        }
        if (msg == WM_TASKBARCREATED) Notify(NIM_ADD);
        else if (msg >= 0xC000) Message?.Invoke(msg); // registered (app-defined) messages
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    void ShowMenu()
    {
        var items = BuildMenu?.Invoke() ?? [];
        var menu = CreatePopupMenu();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (ReferenceEquals(item, TrayMenuItem.Separator)) AppendMenuW(menu, MF_SEPARATOR, 0, null);
                else AppendMenuW(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), (nuint)(i + 1), item.Text);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)(i + 1), 0);
            }

            // Required so the menu closes when you click elsewhere (documented Shell_NotifyIcon quirk).
            SetForegroundWindow(_hwnd);
            GetCursorPos(out var pt);
            var cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            if (cmd > 0) items[cmd - 1].Invoke();
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        Notify(NIM_DELETE);
        RemoveWindowSubclass(_hwnd, _proc, 2);
        if (_hicon != 0) DestroyIcon(_hicon);
    }

    // ---- interop --------------------------------------------------------------------------

    delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint id, nuint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("comctl32.dll")]
    static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);

    [DllImport("comctl32.dll")]
    static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImageW(nint hinst, string name, uint type, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hicon);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuDefaultItem(nint menu, uint item, uint byPos);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT pt);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
