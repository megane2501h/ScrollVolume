using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace ScrollVolume
{
    enum ClickAction { None, Slider, Mute, Mixer } // Slider = ScrollVolume のミキサーを表示（ini の互換のため名前はそのまま）
    enum IconStyle { Number, NumberBox, Bar }
    enum TextColorMode { Auto, White, Black }
    enum ModifierKey { None, Ctrl, Shift, Alt }
    enum FastSpin { None, Faster, Snap5, Snap10 }
    enum Language { Auto, Japanese, English }

    /// <summary>設定。public フィールドがそのまま ini に保存される（編集は設定画面から）。</summary>
    sealed class Settings
    {
        // ホイール
        public int Step = 1;
        public FastSpin FastSpin = FastSpin.Snap5;
        public int AccelStrength = 3;
        public bool Invert = false;
        public bool UnmuteOnChange = true;
        public bool MuteAtZero = false;
        public bool MiddleClickMute = true;      // 四隅・端
        public bool MiddleClickTaskbar = false;  // タスクバー（Windows の「ホイールクリックで新しいウィンドウ」と競合するので既定はオフ）

        // 音量バー
        public bool ShowOsd = true;
        public int OsdDurationMs = 1200;
        public bool OsdHideInGames = true;

        // 場所
        public bool Corners = true;
        public bool CornerTL = true, CornerTR = true, CornerBL = true, CornerBR = true;
        public int CornerSize = 16;
        public bool OuterCornersOnly = false;
        public bool Edges = true;
        public bool EdgeTop = true, EdgeBottom = true, EdgeLeft = true, EdgeRight = true;
        public int EdgeSize = 2;
        public bool OuterEdgesOnly = true;
        public bool Taskbar = true;
        public ModifierKey TaskbarModifier = ModifierKey.None;
        public bool TrayIcon = true;
        public bool GameGuard = true;

        // トレイアイコンのクリック
        public ClickAction LeftClick = ClickAction.Slider;
        public ClickAction MiddleClick = ClickAction.Mute;

        // ホットキー（Hotkey.Make の値、0 は未設定）
        public int HotkeyVolumeUp = 0;
        public int HotkeyVolumeDown = 0;
        public int HotkeyMute = 0;
        public int HotkeyMixer = 0;
        public int HotkeyNextDevice = 0;

        // アイコン
        public IconStyle Style = IconStyle.Number;
        public TextColorMode TextColor = TextColorMode.Auto;

        // ミキサー画面
        public bool MixerWindowed = false;   // ふつうのウィンドウとして開く（タスクバーに出る・Alt+Tab で選べる）
        public bool MixerTopmost = false;    // ウィンドウ表示のとき最前面に置く
        public string MixerPosition = "";    // "x,y"（ウィンドウ表示の位置）。空ならトレイの近く
        public string MixerSize = "";        // "幅,高さ"（ウィンドウ表示で枠をドラッグして決めた中身の大きさ、96 dpi 換算）。空なら中身に合わせる
        public string CollapsedDevices = ""; // 折りたたんだデバイスの ID を | 区切りで

        // 一般
        public bool RunElevated = false;
        public Language Language = Language.Auto;

        public bool Debug = false;

        public static readonly string AppDir = Path.GetDirectoryName(Application.ExecutablePath);
        public static string IniPath { get { return Path.Combine(AppDir, "ScrollVolume.ini"); } }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        public int[] Hotkeys() { return new[] { HotkeyVolumeUp, HotkeyVolumeDown, HotkeyMute, HotkeyMixer, HotkeyNextDevice }; }

        public void SetHotkeys(int[] v)
        {
            HotkeyVolumeUp = v[0]; HotkeyVolumeDown = v[1]; HotkeyMute = v[2]; HotkeyMixer = v[3]; HotkeyNextDevice = v[4];
        }

        public bool AnyHotkey()
        {
            foreach (int h in Hotkeys()) if (h != 0) return true;
            return false;
        }

        public bool IsCornerEnabled(int corner)
        {
            switch (corner)
            {
                case WheelHook.TL: return CornerTL;
                case WheelHook.TR: return CornerTR;
                case WheelHook.BL: return CornerBL;
                case WheelHook.BR: return CornerBR;
            }
            return false;
        }

        /// <summary>どこかでホイール操作を受け付ける設定になっているか。</summary>
        public bool AnyZoneEnabled()
        {
            return (Corners && (CornerTL || CornerTR || CornerBL || CornerBR))
                || (Edges && (EdgeTop || EdgeBottom || EdgeLeft || EdgeRight))
                || Taskbar || TrayIcon;
        }

        public static Settings Load() { return Load(IniPath); }

        public static Settings Load(string iniPath)
        {
            Settings s = new Settings();
            Dictionary<string, string> raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(iniPath)) { s.Save(iniPath); return s; }
                foreach (string line0 in File.ReadAllLines(iniPath, Encoding.UTF8))
                {
                    string line = line0.Trim();
                    int i = line.IndexOf('=');
                    if (line.StartsWith(";") || i <= 0) continue;
                    string key = line.Substring(0, i).Trim();
                    string v = line.Substring(i + 1).Trim();
                    raw[key] = v;
                    FieldInfo f = typeof(Settings).GetField(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (f == null) continue;
                    try
                    {
                        if (f.FieldType == typeof(int)) { int n; if (int.TryParse(v, out n)) f.SetValue(s, n); }
                        else if (f.FieldType == typeof(bool)) f.SetValue(s, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                        else if (f.FieldType == typeof(string)) f.SetValue(s, v);
                        else if (f.FieldType.IsEnum)
                        {
                            object e = Enum.Parse(f.FieldType, v, true);
                            if (Enum.IsDefined(f.FieldType, e)) f.SetValue(s, e);
                        }
                    }
                    catch { }
                }
                Migrate(s, raw);
            }
            catch (Exception ex) { Log.Write("Settings.Load failed: " + ex.Message); }
            s.Normalize();
            return s;
        }

        /// <summary>以前の版の ini（Accel / RoundStep）からの引き継ぎ。</summary>
        static void Migrate(Settings s, Dictionary<string, string> raw)
        {
            string pinned;
            if (!raw.ContainsKey("MixerWindowed") && raw.TryGetValue("MixerPinned", out pinned) && pinned == "1") { s.MixerWindowed = true; s.MixerTopmost = true; }
            if (raw.ContainsKey("FastSpin")) return;
            string accel;
            // 以前の版は Accel=1 が既定で、利用者が選んだのか区別できない → 新しい既定（区切りで進む）にする。オフにしていた人はオフのまま。
            if (raw.TryGetValue("Accel", out accel)) s.FastSpin = (accel == "1" || accel.Equals("true", StringComparison.OrdinalIgnoreCase)) ? FastSpin.Snap5 : FastSpin.None;
        }

        public void Normalize()
        {
            Step = VolumeMath.NormalizeStep(Step);
            FastSpin = VolumeMath.Fix(FastSpin, Step);
            AccelStrength = Math.Max(1, Math.Min(5, AccelStrength));
            CornerSize = Math.Max(2, Math.Min(200, CornerSize));
            EdgeSize = Math.Max(1, Math.Min(50, EdgeSize));
            OsdDurationMs = Math.Max(300, Math.Min(10000, OsdDurationMs));
            int[] hk = Hotkeys();
            for (int i = 0; i < hk.Length; i++) if (!Hotkey.IsValid(hk[i])) hk[i] = 0;
            SetHotkeys(hk);
            MixerPosition = Clean(MixerPosition);
            MixerSize = Clean(MixerSize);
            System.Drawing.Size ms;
            if (MixerSize.Length > 0 && !TryGetMixerSize(out ms)) MixerSize = "";
            CollapsedDevices = Clean(CollapsedDevices);
        }

        static string Clean(string v) { return (v ?? "").Replace("\r", "").Replace("\n", "").Trim(); }

        /// <summary>全フィールドが同じか（テスト用）。違うフィールド名を返す。null なら一致。</summary>
        public string DiffFrom(Settings o)
        {
            foreach (FieldInfo f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!Equals(f.GetValue(this), f.GetValue(o))) return f.Name + ": " + f.GetValue(this) + " != " + f.GetValue(o);
            return null;
        }

        public void Save() { Save(IniPath); }

        public void Save(string iniPath)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("; ScrollVolume settings (edit from the tray menu > Settings)");
                foreach (FieldInfo f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v = f.GetValue(this);
                    sb.Append(f.Name).Append('=').AppendLine(v is bool ? ((bool)v ? "1" : "0") : Convert.ToString(v));
                }
                File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Write("Settings.Save failed: " + ex.Message); }
        }

        // ---- ミキサー画面の状態 ----

        public HashSet<string> CollapsedSet()
        {
            HashSet<string> set = new HashSet<string>();
            foreach (string id in (CollapsedDevices ?? "").Split('|')) if (id.Length > 0) set.Add(id);
            return set;
        }

        public void SetCollapsed(IEnumerable<string> ids) { CollapsedDevices = string.Join("|", ids); }

        public bool TryGetMixerPosition(out System.Drawing.Point p)
        {
            p = System.Drawing.Point.Empty;
            string[] a = (MixerPosition ?? "").Split(',');
            int x, y;
            if (a.Length != 2 || !int.TryParse(a[0], out x) || !int.TryParse(a[1], out y)) return false;
            p = new System.Drawing.Point(x, y);
            return true;
        }

        /// <summary>ウィンドウ表示の中身の大きさ（96 dpi 換算）。壊れた値や極端な値は「無し」として扱う。</summary>
        public bool TryGetMixerSize(out System.Drawing.Size size)
        {
            size = System.Drawing.Size.Empty;
            string[] a = (MixerSize ?? "").Split(',');
            int w, h;
            if (a.Length != 2 || !int.TryParse(a[0], out w) || !int.TryParse(a[1], out h)) return false;
            if (w < 100 || h < 50 || w > 10000 || h > 10000) return false;
            size = new System.Drawing.Size(w, h);
            return true;
        }

        public void SetMixerSize(System.Drawing.Size size) { MixerSize = size.IsEmpty ? "" : size.Width + "," + size.Height; }
    }

    static class Log
    {
        static readonly object gate = new object();
        static string path;

        public static void Init()
        {
            path = Path.Combine(Settings.AppDir, "ScrollVolume.log");
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            }
            catch { }
        }

        public static void Write(string text)
        {
            if (path == null) return;
            lock (gate)
            {
                try { File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + text + Environment.NewLine, Encoding.UTF8); }
                catch { }
            }
        }
    }
}
