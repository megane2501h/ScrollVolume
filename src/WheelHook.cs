using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ScrollVolume
{
    /// <summary>
    /// 専用スレッドで低レベルマウスフックを保持する。
    /// - UI スレッドが詰まってもフックがタイムアウトで外されないよう、フック内は位置判定と PostMessage だけ。
    /// - Windows に黙って外されても戻るよう、30 秒ごと＋復帰時に張り直す。
    /// </summary>
    sealed class WheelHook
    {
        // ゾーン番号（UI へ渡す）
        public const int TL = 1, TR = 2, BL = 3, BR = 4, TASKBAR = 5, TRAY = 6, TOP = 7, BOTTOM = 8, LEFT = 9, RIGHT = 10;
        const uint WM_REINSTALL = Native.WM_APP + 10;

        readonly IntPtr target;
        readonly uint targetMsg;
        readonly LowLevelMouseProc proc; // GC に回収されないようフィールドで保持
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        readonly StringBuilder classBuf = new StringBuilder(64);
        volatile Settings settings;
        volatile int[] trayRect; // {left, top, right, bottom}
        IntPtr hook = IntPtr.Zero;
        uint threadId;
        Thread thread;

        public WheelHook(IntPtr targetHwnd, uint targetMessage, Settings s)
        {
            target = targetHwnd;
            targetMsg = targetMessage;
            settings = s;
            proc = HookProc;
        }

        public void UpdateSettings(Settings s) { settings = s; }
        public bool IsInstalled { get { return hook != IntPtr.Zero; } }
        public bool IsThreadAlive { get { return thread != null && thread.IsAlive; } }
        public void SetTrayRect(int[] rect) { trayRect = rect; }

        public void Start()
        {
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "WheelHook";
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
            ready.WaitOne(5000);
        }

        public void Reinstall()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, WM_REINSTALL, IntPtr.Zero, IntPtr.Zero);
        }

        public void Stop()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (thread != null) thread.Join(2000);
        }

        void Run()
        {
            MSG msg;
            threadId = Native.GetCurrentThreadId();
            Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0); // メッセージキューを作らせる
            ready.Set();
            Install();
            Native.SetTimer(IntPtr.Zero, IntPtr.Zero, 30000, IntPtr.Zero);
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && (msg.message == Native.WM_TIMER || msg.message == WM_REINSTALL))
                {
                    Install();
                    continue;
                }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }

        void Install()
        {
            // 先に新しいフックを張ってから古いのを外す（取りこぼしの隙間を作らない）
            IntPtr h = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            if (h == IntPtr.Zero)
            {
                Log.Write("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
                return;
            }
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = h;
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && wParam.ToInt32() == Native.WM_MOUSEWHEEL)
                {
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Settings s = settings;
                    int zone = HitZone(info.pt, s);
                    if (s.Debug) Log.Write("wheel pt=" + info.pt.X + "," + info.pt.Y + " zone=" + zone);
                    if (zone != 0)
                    {
                        short delta = (short)(info.mouseData >> 16);
                        Native.PostMessage(target, targetMsg, new IntPtr(delta), new IntPtr(zone));
                        return new IntPtr(1); // 下のウィンドウには渡さない
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        static readonly IMonitorLayout RealMonitors = new RealMonitorLayout();

        int HitZone(POINT pt, Settings s)
        {
            int z = HitScreen(pt, s, RealMonitors);
            if (z != 0) return z;
            if (s.TrayIcon)
            {
                int[] r = trayRect;
                if (r != null && pt.X >= r[0] && pt.X < r[2] && pt.Y >= r[1] && pt.Y < r[3]) return TRAY;
            }
            if (s.Taskbar && ModifierDown(s.TaskbarModifier) && IsTaskbar(pt)) return TASKBAR;
            return 0;
        }

        static bool ModifierDown(ModifierKey k)
        {
            switch (k)
            {
                case ModifierKey.Ctrl: return Native.GetAsyncKeyState(0x11) < 0;
                case ModifierKey.Shift: return Native.GetAsyncKeyState(0x10) < 0;
                case ModifierKey.Alt: return Native.GetAsyncKeyState(0x12) < 0;
            }
            return true;
        }

        bool IsTaskbar(POINT pt)
        {
            IntPtr w = Native.WindowFromPoint(pt);
            if (w == IntPtr.Zero) return false;
            IntPtr root = Native.GetAncestor(w, Native.GA_ROOT);
            classBuf.Length = 0;
            Native.GetClassName(root == IntPtr.Zero ? w : root, classBuf, classBuf.Capacity);
            string c = classBuf.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        }

        public static bool IsCorner(int zone) { return zone >= TL && zone <= BR; }
        public static bool IsEdge(int zone) { return zone >= TOP && zone <= RIGHT; }

        /// <summary>
        /// 画面の隅・辺の判定。隅を優先し、隅に当たらなければ辺を見る。
        /// 「つなぎ目を無視」がオンなら、その先に別のモニターが続いている隅・辺は除外する
        /// （カーソルが止まらず通り抜けてしまう場所なので）。
        /// </summary>
        public static int HitScreen(POINT pt, Settings s, IMonitorLayout layout)
        {
            Rectangle r;
            if (!layout.TryGetMonitor(pt.X, pt.Y, out r)) return 0;

            if (s.Corners)
            {
                int n = s.CornerSize;
                bool left = pt.X < r.Left + n, right = pt.X >= r.Right - n;
                bool top = pt.Y < r.Top + n, bottom = pt.Y >= r.Bottom - n;
                int c = 0;
                if (top && left) c = TL;
                else if (top && right) c = TR;
                else if (bottom && left) c = BL;
                else if (bottom && right) c = BR;
                if (c != 0 && s.IsCornerEnabled(c))
                {
                    bool junction = false;
                    if (s.OuterCornersOnly)
                    {
                        int cx = left ? r.Left : r.Right - 1;
                        int cy = top ? r.Top : r.Bottom - 1;
                        int dx = left ? -1 : 1, dy = top ? -1 : 1;
                        junction = layout.OnMonitor(cx + dx, cy) || layout.OnMonitor(cx, cy + dy);
                    }
                    if (!junction) return c;
                }
            }

            if (s.Edges)
            {
                int e = s.EdgeSize;
                bool outer = s.OuterEdgesOnly;
                if (s.EdgeTop && pt.Y < r.Top + e && !(outer && layout.OnMonitor(pt.X, r.Top - 1))) return TOP;
                if (s.EdgeBottom && pt.Y >= r.Bottom - e && !(outer && layout.OnMonitor(pt.X, r.Bottom))) return BOTTOM;
                if (s.EdgeLeft && pt.X < r.Left + e && !(outer && layout.OnMonitor(r.Left - 1, pt.Y))) return LEFT;
                if (s.EdgeRight && pt.X >= r.Right - e && !(outer && layout.OnMonitor(r.Right, pt.Y))) return RIGHT;
            }
            return 0;
        }
    }

    interface IMonitorLayout
    {
        bool TryGetMonitor(int x, int y, out Rectangle bounds);
        bool OnMonitor(int x, int y);
    }

    sealed class RealMonitorLayout : IMonitorLayout
    {
        public bool TryGetMonitor(int x, int y, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            IntPtr mon = Native.MonitorFromPoint(new POINT(x, y), Native.MONITOR_DEFAULTTONULL);
            if (mon == IntPtr.Zero) return false;
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;
            bounds = Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
            return true;
        }

        public bool OnMonitor(int x, int y)
        {
            return Native.MonitorFromPoint(new POINT(x, y), Native.MONITOR_DEFAULTTONULL) != IntPtr.Zero;
        }
    }

    /// <summary>テスト用の架空のモニター配置。</summary>
    sealed class FakeMonitorLayout : IMonitorLayout
    {
        readonly Rectangle[] screens;
        public FakeMonitorLayout(params Rectangle[] s) { screens = s; }

        public bool TryGetMonitor(int x, int y, out Rectangle bounds)
        {
            foreach (Rectangle r in screens) if (r.Contains(x, y)) { bounds = r; return true; }
            bounds = Rectangle.Empty;
            return false;
        }

        public bool OnMonitor(int x, int y)
        {
            Rectangle r;
            return TryGetMonitor(x, y, out r);
        }
    }
}
