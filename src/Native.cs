using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScrollVolume
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    static class Native
    {
        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_LBUTTONUP = 0x0202;
        public const int WM_LBUTTONDBLCLK = 0x0203;
        public const int WM_RBUTTONUP = 0x0205;
        public const int WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_CONTEXTMENU = 0x007B;
        public const int NIN_KEYSELECT = 0x0401;
        public const int WM_TIMER = 0x0113;
        public const int WM_QUIT = 0x0012;
        public const int WM_APP = 0x8000;
        public const int WM_POWERBROADCAST = 0x0218;
        public const int PBT_APMRESUMESUSPEND = 0x0007;
        public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        public const int WM_WTSSESSION_CHANGE = 0x02B1;
        public const int WTS_SESSION_UNLOCK = 0x8;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const uint MONITOR_DEFAULTTONULL = 0;
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;
        public const uint NOTIFYICON_VERSION_4 = 4;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")]
        public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessage(string lpString);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        public const uint GA_ROOT = 2;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
        public const int SM_CXSMICON = 49;

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SW_HIDE = 0;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
        [DllImport("shell32.dll")]
        public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

        // 管理者権限で動いているとき、通常権限のエクスプローラーからの通知（トレイのクリック等）を受け取れるようにする
        [DllImport("user32.dll")]
        public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);
        public const uint MSGFLT_ALLOW = 1;

        [DllImport("wtsapi32.dll")]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);
        [DllImport("wtsapi32.dll")]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        // ホットキー
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_NOREPEAT = 0x4000;
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ゲーム中の判定
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        public const int GWL_STYLE = -16;
        public const int WS_CAPTION = 0x00C00000;
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

        // プロセス
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint code);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        // 枠なしウィンドウの移動・角丸
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;
        public const int WM_EXITSIZEMOVE = 0x0232;
        public const int WM_SIZING = 0x0214;
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_MBUTTONDOWN = 0x0207;
        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        [DllImport("user32.dll")]
        public static extern short GetKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);
        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        /// <summary>プロセスの実行ファイルのフルパス。取れなければ null。</summary>
        public static string ProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        /// <summary>プロセスがまだ動いているか（開けないプロセスは動いているとみなす）。</summary>
        public static bool ProcessAlive(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5; // アクセス拒否 = 存在はする
            try
            {
                uint code;
                return !GetExitCodeProcess(h, out code) || code == 259; // STILL_ACTIVE
            }
            finally { CloseHandle(h); }
        }
    }
}
