using System.Runtime.InteropServices;

namespace AgentNotch;

/// <summary>Win32 interop shared by the overlay windows: click-through styling,
/// a global low-level mouse hook (the indicator window is click-through, so it
/// never receives its own clicks), a matching keyboard hook (Esc collapses the
/// non-activating panel), and foreground-window-change notification
/// (to detect terminal activation, the Mac build's "revisiting the terminal
/// acknowledges finished agents" behavior).</summary>
internal static class NativeMethods
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x8000000;

    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public static nint GetWindowLongPtr(nint hWnd, int nIndex) =>
        nint.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    public static nint SetWindowLongPtr(nint hWnd, int nIndex, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, value) : SetWindowLong32(hWnd, nIndex, (int)value);

    /// <summary>No-activate + no-alt-tab overlay chrome. Does not set
    /// WS_EX_LAYERED (that bit is only for WPF AllowsTransparency windows).</summary>
    public static void MakeOverlay(nint hwnd) => SetClickThrough(hwnd, clickThrough: false);

    /// <summary>Toggle <see cref="WS_EX_TRANSPARENT"/> without dropping the
    /// overlay bits. The collapsed tab must not be click-through (or the page
    /// behind it eats the click); the expanded tab sits above the panel and
    /// must be, so session rows keep receiving input.</summary>
    public static void SetClickThrough(nint hwnd, bool clickThrough)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var next = (ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_TRANSPARENT;
        if (clickThrough) next |= WS_EX_TRANSPARENT;
        if (next == ex) return;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, next);
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    public static void MakeToolWindow(nint hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    public static void MakeTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [DllImport("gdi32.dll")]
    public static extern nint CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
        int nWidthEllipse, int nHeightEllipse);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(nint hWnd, nint hRgn, bool bRedraw);

    /// <summary>Round only the bottom corners so the overlay reads as a tab
    /// hanging off the top edge. Avoids WPF AllowsTransparency, which was
    /// producing a layered HWND that never composited onto the desktop.</summary>
    public static void ApplyHangingTabRegion(nint hwnd, int radiusPx)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;
        var region = CreateRoundRectRgn(0, -radiusPx, w + 1, h + 1, radiusPx * 2, radiusPx * 2);
        SetWindowRgn(hwnd, region, true);
    }

    /// <summary>Fully rounded pill — used when the indicator is dragged away
    /// from the top edge so it no longer reads as a hanging tab.</summary>
    public static void ApplyRoundedPillRegion(nint hwnd, int radiusPx)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;
        var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, radiusPx * 2, radiusPx * 2);
        SetWindowRgn(hwnd, region, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // --- global low-level mouse / keyboard hooks ---

    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public int mouseData;
        public int flags;
        public int time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public nint dwExtraInfo;
    }

    public delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);
    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, int dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, int dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    public static extern nint GetModuleHandle(string? lpModuleName);

    // --- foreground-window-change notification (EVENT_SYSTEM_FOREGROUND) ---

    public const uint EVENT_SYSTEM_FOREGROUND = 3;
    public const uint WINEVENT_OUTOFCONTEXT = 0;

    public delegate void WinEventDelegate(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(nint hWnd, out int lpdwProcessId);

    // --- top-level window enumeration (used to find the real GUI window of a
    // terminal/IDE that hosts an agent process via several layers of pty
    // helpers, e.g. Termius's winpty-agent chain) ---

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(nint hWnd);

    // --- popup menus (settings gear / right-click) ---

    public const int WM_COMMAND = 0x0111;
    public const uint MF_STRING = 0x0;
    public const uint MF_CHECKED = 0x8;
    public const uint MF_POPUP = 0x10;
    public const uint MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x0002;
    public const uint TPM_RETURNCMD = 0x0100;

    [DllImport("user32.dll")]
    public static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool AppendMenu(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    public static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
}
