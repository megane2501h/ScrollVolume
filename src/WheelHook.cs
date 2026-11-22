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
        volatile int[] ownRect;  // 表示中のミキサー画面（その上では何もしない）
        readonly MiddleClickGate middle = new MiddleClickGate();
        IntPtr hook = IntPtr.Zero;
        uint threadId;
        Thread thread;

        readonly uint clickMsg;
        readonly Func<POINT, bool> isTaskbarFn;
        readonly Func<ModifierKey, bool> modifierFn = ModifierDown;

        /// <param name="targetMessage">ホイール（wParam=回転量, lParam=場所）</param>
        /// <param name="clickMessage">ホイールクリック（lParam=場所）</param>
        public WheelHook(IntPtr targetHwnd, uint targetMessage, uint clickMessage, Settings s)
        {
            target = targetHwnd;
            targetMsg = targetMessage;
            clickMsg = clickMessage;
            settings = s;
            proc = HookProc;
            isTaskbarFn = IsTaskbar;
        }

        public void UpdateSettings(Settings s) { settings = s; }
        public bool IsInstalled { get { return hook != IntPtr.Zero; } }
        public bool IsThreadAlive { get { return thread != null && thread.IsAlive; } }
        public void SetTrayRect(int[] rect) { trayRect = rect; }
        public void SetOwnRect(Rectangle r) { ownRect = r.IsEmpty ? null : new[] { r.Left, r.Top, r.Right, r.Bottom }; }

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
                int msg = wParam.ToInt32();
                if (nCode >= 0 && msg == Native.WM_MBUTTONUP && middle.Up())
                    return new IntPtr(1); // 押したときに横取りしたボタンは、離したときも渡さない
                if (nCode >= 0 && (msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MBUTTONDOWN))
                {
                    MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    Settings s = settings;
                    bool active = !(s.GameGuard && GameDetect.Active) && !InRect(ownRect, info.pt);
                    if (msg == Native.WM_MOUSEWHEEL)
                    {
                        int zone = active ? HitZone(info.pt, s, RealMonitors, isTaskbarFn, trayRect, modifierFn) : 0;
                        if (s.Debug) Log.Write("wheel pt=" + info.pt.X + "," + info.pt.Y + " zone=" + zone);
                        if (zone != 0)
                        {
                            short delta = (short)(info.mouseData >> 16);
                            Native.PostMessage(target, targetMsg, new IntPtr(delta), new IntPtr(zone));
                            return new IntPtr(1); // 下のウィンドウには渡さない
                        }
                    }
                    else
                    {
                        int zone = active ? MiddleClickZone(info.pt, s, RealMonitors, isTaskbarFn, trayRect, modifierFn) : 0;
                        if (s.Debug) Log.Write("middle pt=" + info.pt.X + "," + info.pt.Y + " zone=" + zone);
                        if (middle.Down(zone != 0))
                        {
                            Native.PostMessage(target, clickMsg, IntPtr.Zero, new IntPtr(zone));
                            return new IntPtr(1);
                        }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        static bool InRect(int[] r, POINT pt)
        {
            return r != null && pt.X >= r[0] && pt.X < r[2] && pt.Y >= r[1] && pt.Y < r[3];
        }

        static readonly IMonitorLayout RealMonitors = new RealMonitorLayout();

        /// <summary>
        /// ホイールを受け付ける場所の判定。
        /// 1. ScrollVolume のアイコンの上（「アイコンの上」がオンなら）
        /// 2. タスクバーの上なら、四隅・端と重なっていても「タスクバー」の設定（修飾キーを含む）だけで決める
        ///    （タスクバーを上・左・右に移動していても同じ。位置ではなくその場所のウィンドウで判定する）
        /// 3. 四隅 → 端
        /// </summary>
        public static int HitZone(POINT pt, Settings s, IMonitorLayout layout, Func<POINT, bool> onTaskbar, int[] tray, Func<ModifierKey, bool> modifierDown)
        {
            if (s.TrayIcon && InRect(tray, pt)) return TRAY;
            int c = HitCorner(pt, s, layout);
            int e = c != 0 ? 0 : HitEdge(pt, s, layout);
            if ((c != 0 || e != 0 || s.Taskbar) && onTaskbar(pt)) // 画面の中ほどではウィンドウを調べない（軽くするため）
                return s.Taskbar && modifierDown(s.TaskbarModifier) ? TASKBAR : 0;
            return c != 0 ? c : e;
        }

        /// <summary>ホイールクリックでミュートする場所（0 ならそのまま下のウィンドウへ）。アイコンの上は、アイコン自体のクリック設定に任せる。</summary>
        public static int MiddleClickZone(POINT pt, Settings s, IMonitorLayout layout, Func<POINT, bool> onTaskbar, int[] tray, Func<ModifierKey, bool> modifierDown)
        {
            if (InRect(tray, pt)) return 0;
            int zone = HitZone(pt, s, layout, onTaskbar, tray, modifierDown);
            return MiddleClickMutes(zone, s) ? zone : 0;
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

        /// <summary>
        /// その場所でのホイールクリックをミュートに使うか。トレイのアイコンは対象外（アイコン自体のクリック設定に任せる）。
        /// タスクバーの上かどうか・修飾キーは HitZone が決めるので、端と重なっていてもタスクバーの設定が優先される。
        /// </summary>
        public static bool MiddleClickMutes(int zone, Settings s)
        {
            if (IsCorner(zone) || IsEdge(zone)) return s.MiddleClickMute;
            if (zone == TASKBAR) return s.MiddleClickTaskbar;
            return false;
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
            int c = HitCorner(pt, s, layout);
            return c != 0 ? c : HitEdge(pt, s, layout);
        }

        public static int HitCorner(POINT pt, Settings s, IMonitorLayout layout)
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
            return 0;
        }

        public static int HitEdge(POINT pt, Settings s, IMonitorLayout layout)
        {
            Rectangle r;
            if (!layout.TryGetMonitor(pt.X, pt.Y, out r)) return 0;
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

    /// <summary>
    /// 前面のアプリがゲーム（全画面・タイトルバーなし、ブラウザーや動画プレーヤー以外）かどうか。
    /// UI スレッドから定期的に Update し、フックは Active を読むだけ。
    /// </summary>
    static class GameDetect
    {
        public static volatile bool Active;
        static readonly StringBuilder cls = new StringBuilder(64);
        static uint lastPid;
        static string lastName;
        static readonly uint OwnPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        public static void Update()
        {
            try { Active = Evaluate(); }
            catch { Active = false; }
        }

        static bool Evaluate()
        {
            int state;
            bool d3d = Native.SHQueryUserNotificationState(out state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN;
            IntPtr w = Native.GetForegroundWindow();
            if (w == IntPtr.Zero) return d3d;
            RECT wr;
            if (!Native.GetWindowRect(w, out wr)) return d3d;
            IntPtr mon = Native.MonitorFromWindow(w, Native.MONITOR_DEFAULTTONEAREST);
            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!Native.GetMonitorInfo(mon, ref mi)) return d3d;
            cls.Length = 0;
            Native.GetClassName(w, cls, cls.Capacity);
            bool caption = (Native.GetWindowLong(w, Native.GWL_STYLE) & Native.WS_CAPTION) == Native.WS_CAPTION;
            bool full = GameRules.IsFullscreen(
                Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom),
                Rectangle.FromLTRB(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                caption, Native.IsZoomed(w), cls.ToString());
            if (!full && !d3d) return false;
            uint pid;
            Native.GetWindowThreadProcessId(w, out pid);
            if (pid != lastPid)
            {
                lastPid = pid;
                string path = Native.ProcessPath(pid);
                lastName = path == null ? null : System.IO.Path.GetFileNameWithoutExtension(path);
            }
            if (pid == OwnPid) return false;
            return GameRules.IsGame(d3d, full, lastName);
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
