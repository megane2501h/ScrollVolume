using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace ScrollVolume
{
    /// <summary>表示言語。T("日本語", "English") の形で書き、実行時にどちらかを返す。</summary>
    static class L
    {
        static bool? ja;

        public static bool IsJapanese
        {
            get
            {
                if (ja == null) ja = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
                return ja.Value;
            }
        }

        public static void Set(Language lang)
        {
            if (lang == Language.Japanese) ja = true;
            else if (lang == Language.English) ja = false;
            else ja = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";
        }

        public static string T(string japanese, string english) { return IsJapanese ? japanese : english; }
    }

    static class VolumeMath
    {
        /// <summary>1 目盛りの変化量として選べる値（どれも 100 を割り切れるので区切りが崩れない）。</summary>
        public static readonly int[] Steps = { 1, 2, 5, 10 };

        /// <summary>前の目盛りからこの間隔(ms)未満なら「速く回した」とみなす。</summary>
        public const int FastMs = 90;
        const int FasterMs = 40;

        public static int NormalizeStep(int v)
        {
            int best = Steps[0];
            foreach (int s in Steps) if (Math.Abs(s - v) < Math.Abs(best - v)) best = s;
            return best;
        }

        /// <summary>「区切りで進む」の区切り。それ以外の設定なら 0。</summary>
        public static int SnapGrid(FastSpin f)
        {
            return f == FastSpin.Snap5 ? 5 : f == FastSpin.Snap10 ? 10 : 0;
        }

        /// <summary>
        /// その 1 目盛りの変化量で選べる「速く回したとき」の動作か。
        /// 区切りは変化量より大きいときだけ、加速は 10% 未満のときだけ意味がある（10% の加速は 1 目盛りで 20〜60% 飛ぶ）。
        /// </summary>
        public static bool IsAvailable(FastSpin f, int step)
        {
            if (f == FastSpin.Faster) return step < 10;
            int g = SnapGrid(f);
            return g == 0 || g > step;
        }

        /// <summary>選べない組み合わせなら、同じ種類の近い動作（区切り → より大きい区切り）に、無ければ「通常どおり」に置き換える。</summary>
        public static FastSpin Fix(FastSpin f, int step)
        {
            if (IsAvailable(f, step)) return f;
            if (SnapGrid(f) > 0 && IsAvailable(FastSpin.Snap10, step)) return FastSpin.Snap10;
            return FastSpin.None;
        }

        public static int AccelMultiplier(int strength, int dtMs)
        {
            if (dtMs < 0) return 1;
            if (dtMs < FasterMs) return 1 + strength;
            if (dtMs < FastMs) return 1 + (strength + 1) / 2;
            return 1;
        }

        /// <summary>grid の倍数の上を notches 個ぶん進める（途中の値からなら、まず最寄りの区切りへ）。</summary>
        public static int Move(int cur, int notches, int grid)
        {
            if (grid < 1) grid = 1;
            cur = Math.Max(0, Math.Min(100, cur));
            int next;
            if (notches > 0) next = (cur / grid + notches) * grid;
            else if (notches < 0) next = ((cur + grid - 1) / grid + notches) * grid;
            else next = cur;
            return Math.Max(0, Math.Min(100, next));
        }

        /// <summary>
        /// ホイール（またはホットキー）を notches 目盛り動かした後の音量。
        /// dtMs は前の目盛りからの間隔（初回や方向転換は -1）。
        /// </summary>
        public static int Next(int cur, int notches, Settings s, int dtMs)
        {
            int step = NormalizeStep(s.Step);
            bool fast = dtMs >= 0 && dtMs < FastMs;
            if (fast)
            {
                int g = SnapGrid(s.FastSpin);
                if (g > step) return Move(cur, notches, g);
                if (s.FastSpin == FastSpin.Faster) return Move(cur, notches * AccelMultiplier(s.AccelStrength, dtMs), step);
            }
            return Move(cur, notches, step);
        }
    }

    /// <summary>ホイールの回転量を目盛りにまとめ、目盛り同士の間隔を測る（高解像度ホイール・タッチパッド対応）。</summary>
    sealed class WheelAccumulator
    {
        const int ResetMs = 400;
        int accum, lastEvent, lastNotch, lastSign;
        bool hasEvent, hasNotch;

        public int Feed(int delta, int now, out int dtMs)
        {
            if (!hasEvent || now - lastEvent > ResetMs) { accum = 0; hasNotch = false; }
            hasEvent = true;
            lastEvent = now;
            int sign = Math.Sign(delta);
            if (sign != 0 && lastSign != 0 && sign != lastSign) { accum = 0; hasNotch = false; } // 方向転換は仕切り直し
            if (sign != 0) lastSign = sign;
            accum += delta;
            int n = accum / 120;
            accum -= n * 120;
            dtMs = -1;
            if (n != 0)
            {
                if (hasNotch) dtMs = now - lastNotch;
                lastNotch = now;
                hasNotch = true;
            }
            return n;
        }
    }

    static class MuteRules
    {
        /// <summary>
        /// 音量を next に変えた後のミュート状態。ホイール・スライダー・ホットキーで共通。
        /// 「0% でミュート」がオンなら、上げたときの解除も必ず行う（ミュートのまま戻れなくなるのを防ぐ）。
        /// </summary>
        public static bool After(bool muted, int next, bool raising, Settings s)
        {
            if (muted && raising && next > 0 && (s.UnmuteOnChange || s.MuteAtZero)) muted = false;
            if (!muted && next == 0 && s.MuteAtZero) muted = true;
            return muted;
        }
    }

    /// <summary>ホットキーの値。(修飾キー &lt;&lt; 16) | 仮想キー。0 は未設定。</summary>
    static class Hotkey
    {
        public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

        public static int Make(int mods, Keys key) { return key == Keys.None ? 0 : ((mods & 0xF) << 16) | ((int)key & 0xFFFF); }
        public static int Mods(int hk) { return (hk >> 16) & 0xF; }
        public static Keys Key(int hk) { return (Keys)(hk & 0xFFFF); }

        public static bool IsModifierKey(Keys k)
        {
            return k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin
                || k == Keys.LControlKey || k == Keys.RControlKey || k == Keys.LShiftKey || k == Keys.RShiftKey
                || k == Keys.LMenu || k == Keys.RMenu;
        }

        /// <summary>文字を入力するキー（英数字・記号・スペース）。</summary>
        public static bool IsCharacterKey(Keys k)
        {
            return (k >= Keys.A && k <= Keys.Z) || (k >= Keys.D0 && k <= Keys.D9) || k == Keys.Space
                || (k >= Keys.Oem1 && k <= Keys.Oem7) || k == Keys.Oem8 || k == Keys.Oem102
                || k == Keys.Oemplus || k == Keys.Oemcomma || k == Keys.OemMinus || k == Keys.OemPeriod;
        }

        /// <summary>
        /// 登録してよい組み合わせか。文字入力やよく使う操作を奪わないようにする:
        /// - 修飾キーなしは F13〜F24・Pause・ScrollLock だけ
        /// - 文字キーは Shift か Win を含む組み合わせだけ（Ctrl+C・Alt+文字・Ctrl+Alt+文字＝AltGr を避ける）
        /// - Alt+F4 は不可
        /// </summary>
        public static bool IsValid(int hk)
        {
            if (hk == 0) return true;
            Keys k = Key(hk);
            if (k == Keys.None || IsModifierKey(k)) return false;
            int m = Mods(hk);
            if ((m & (Alt | Ctrl | Win)) == 0) return (k >= Keys.F13 && k <= Keys.F24) || k == Keys.Pause || k == Keys.Scroll;
            if (IsCharacterKey(k) && (m & (Shift | Win)) == 0) return false;
            if (k == Keys.F4 && m == Alt) return false;
            return true;
        }

        public static string Format(int hk)
        {
            if (hk == 0) return L.T("なし", "None");
            StringBuilder sb = new StringBuilder();
            int m = Mods(hk);
            if ((m & Ctrl) != 0) sb.Append("Ctrl + ");
            if ((m & Alt) != 0) sb.Append("Alt + ");
            if ((m & Shift) != 0) sb.Append("Shift + ");
            if ((m & Win) != 0) sb.Append("Win + ");
            sb.Append(KeyName(Key(hk)));
            return sb.ToString();
        }

        public static string KeyName(Keys k)
        {
            switch (k)
            {
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.Prior: return "PageUp";
                case Keys.Next: return "PageDown";
                case Keys.Oemplus: return "+";
                case Keys.OemMinus: return "-";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Multiply: return "Num *";
                case Keys.Divide: return "Num /";
                case Keys.Scroll: return "ScrollLock";
                case Keys.Back: return "Backspace";
                case Keys.Return: return "Enter";
                case Keys.Escape: return "Esc";
            }
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + (k - Keys.NumPad0);
            if (IsCharacterKey(k) && !(k >= Keys.A && k <= Keys.Z))
            {
                // 記号キーはキーボード配列ごとに違うので、Windows に名前を聞く（Oem1 などと出さない）
                try
                {
                    uint scan = Native.MapVirtualKey((uint)k, 0);
                    StringBuilder sb = new StringBuilder(32);
                    if (scan != 0 && Native.GetKeyNameText((int)(scan << 16), sb, sb.Capacity) > 0) return sb.ToString();
                }
                catch { }
            }
            return k.ToString();
        }
    }

    /// <summary>メーターの動き。上がるのは即座に、下がるのは時間で（描画の間隔に左右されない）。</summary>
    static class PeakMeter
    {
        /// <summary>0.1 秒で約 1/3 に下がる（再生を止めると 0.5 秒ほどで消える）。</summary>
        public const float DecayMs = 90f;

        public static float Next(float shown, float raw, int dtMs)
        {
            raw = Math.Max(0f, Math.Min(1f, raw));
            if (raw >= shown) return raw;
            float fallen = shown * (float)Math.Exp(-Math.Max(0, dtMs) / DecayMs);
            if (fallen < 0.005f) fallen = 0f;
            return Math.Max(raw, fallen);
        }

        /// <summary>
        /// 表示する大きさ（0〜1、スライダーの全長に対する割合）= 信号 × その行の音量。
        /// アプリの行にデバイスの音量は掛けない（掛けるとデバイスを下げているとき動きが小さすぎて見えない）。
        /// デバイスがミュートなら音は出ないのでアプリも 0。
        /// </summary>
        public static float Level(float peak, int rowVolume, bool rowMuted, int deviceVolume, bool deviceMuted, bool isApp)
        {
            if (rowMuted || (isApp && deviceMuted)) return 0f;
            return Math.Max(0f, Math.Min(1f, peak)) * Math.Max(0, Math.Min(100, rowVolume)) / 100f;
        }
    }

    /// <summary>トレイから開くメニューの向き。カーソルのある画面からはみ出さない向きを選ぶ（タスクバーが上下左右どこでも）。</summary>
    static class MenuPlacement
    {
        public static ToolStripDropDownDirection Direction(Point c, Rectangle workArea)
        {
            bool up = c.Y >= workArea.Bottom || (c.Y >= workArea.Top && c.Y > workArea.Top + workArea.Height / 2);
            bool left = c.X >= workArea.Right || (c.X >= workArea.Left && c.X > workArea.Left + workArea.Width / 2);
            return up ? (left ? ToolStripDropDownDirection.AboveLeft : ToolStripDropDownDirection.AboveRight)
                      : (left ? ToolStripDropDownDirection.BelowLeft : ToolStripDropDownDirection.BelowRight);
        }

        /// <summary>その向きで開いたときにメニューが占める範囲。</summary>
        public static Rectangle Bounds(Point c, Size size, ToolStripDropDownDirection d)
        {
            bool up = d == ToolStripDropDownDirection.AboveLeft || d == ToolStripDropDownDirection.AboveRight;
            bool left = d == ToolStripDropDownDirection.AboveLeft || d == ToolStripDropDownDirection.BelowLeft;
            return new Rectangle(left ? c.X - size.Width : c.X, up ? c.Y - size.Height : c.Y, size.Width, size.Height);
        }
    }

    /// <summary>ホイールクリックを横取りしたら、対になるボタンを離す操作も渡さない。取りこぼしで残らないよう押すたびに決め直す。</summary>
    sealed class MiddleClickGate
    {
        bool swallowUp;

        /// <summary>押したとき。mute なら横取りする（true を返す）。</summary>
        public bool Down(bool mute) { swallowUp = mute; return mute; }

        /// <summary>離したとき。横取りした押下の対なら true。</summary>
        public bool Up()
        {
            if (!swallowUp) return false;
            swallowUp = false;
            return true;
        }
    }

    /// <summary>「ゲーム中」かどうかの判定（純粋な部分。実際の情報収集は GameDetect）。</summary>
    static class GameRules
    {
        /// <summary>全画面で使っていてもマウス操作を止めないアプリ（ブラウザーや動画プレーヤーでの全画面表示）。</summary>
        static readonly string[] MediaApps =
        {
            "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "iexplore", "waterfox", "librewolf", "floorp", "zen", "thorium", "yandex", "whale",
            "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "mpv", "potplayer", "potplayer64", "potplayermini", "potplayermini64",
            "wmplayer", "video.ui", "microsoft.media.player", "kodi", "plex", "smplayer", "gom", "mpcqt", "applicationframehost",
        };

        public static bool IsMediaApp(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            string p = processName.ToLowerInvariant();
            if (p.EndsWith(".exe")) p = p.Substring(0, p.Length - 4);
            return Array.IndexOf(MediaApps, p) >= 0;
        }

        /// <summary>タイトルバーの無いウィンドウがモニター全体を覆っていれば全画面（最大化しただけのウィンドウは含めない）。</summary>
        public static bool IsFullscreen(Rectangle window, Rectangle monitor, bool hasCaption, bool maximized, string className)
        {
            if (hasCaption || maximized || monitor.IsEmpty) return false;
            if (className == "Progman" || className == "WorkerW" || className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd") return false;
            return window.Contains(monitor);
        }

        public static bool IsGame(bool d3dExclusive, bool fullscreen, string processName)
        {
            return d3dExclusive || (fullscreen && !IsMediaApp(processName));
        }
    }
}
