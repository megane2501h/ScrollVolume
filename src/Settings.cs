using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace ScrollVolume
{
    enum ClickAction { None, Slider, Mute, Mixer }
    enum IconStyle { Number, NumberBox, Bar }
    enum TextColorMode { Auto, White, Black }
    enum ModifierKey { None, Ctrl, Shift, Alt }

    /// <summary>設定。public フィールドがそのまま ini に保存される（編集は設定画面から）。</summary>
    sealed class Settings
    {
        // 操作
        public int Step = 1;
        public bool Accel = true;
        public int AccelStrength = 3;
        public bool RoundStep = false;
        public bool UnmuteOnChange = true;
        public bool MuteAtZero = false;
        public bool Invert = false;
        public bool ShowOsd = true;
        public int OsdDurationMs = 1200;

        // マウス：ホイールで音量を変える場所
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

        // マウス：トレイアイコンのクリック
        public ClickAction LeftClick = ClickAction.Slider;
        public ClickAction MiddleClick = ClickAction.Mute;

        // アイコン
        public IconStyle Style = IconStyle.Number;
        public TextColorMode TextColor = TextColorMode.Auto;

        // 一般
        public bool RunElevated = false;

        public bool Debug = false;

        public static readonly string AppDir = Path.GetDirectoryName(Application.ExecutablePath);
        public static string IniPath { get { return Path.Combine(AppDir, "ScrollVolume.ini"); } }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

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

        /// <summary>どこかでホイール操作を受け付ける設定になっているか（全部オフなら設定画面で警告する）。</summary>
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
            try
            {
                if (!File.Exists(iniPath)) { s.Save(iniPath); return s; }
                foreach (string raw in File.ReadAllLines(iniPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    int i = line.IndexOf('=');
                    if (line.StartsWith(";") || i <= 0) continue;
                    FieldInfo f = typeof(Settings).GetField(line.Substring(0, i).Trim(), BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (f == null) continue;
                    string v = line.Substring(i + 1).Trim();
                    try
                    {
                        if (f.FieldType == typeof(int)) { int n; if (int.TryParse(v, out n)) f.SetValue(s, n); }
                        else if (f.FieldType == typeof(bool)) f.SetValue(s, v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                        else if (f.FieldType.IsEnum) f.SetValue(s, Enum.Parse(f.FieldType, v, true));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Write("Settings.Load failed: " + ex.Message); }
            s.Normalize();
            return s;
        }

        public void Normalize()
        {
            Step = Math.Max(1, Math.Min(20, Step));
            AccelStrength = Math.Max(1, Math.Min(5, AccelStrength));
            CornerSize = Math.Max(2, Math.Min(200, CornerSize));
            EdgeSize = Math.Max(1, Math.Min(50, EdgeSize));
            OsdDurationMs = Math.Max(300, Math.Min(10000, OsdDurationMs));
        }

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
                sb.AppendLine("; ScrollVolume の設定（トレイの右クリック →「設定」から変更できます）");
                foreach (FieldInfo f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v = f.GetValue(this);
                    sb.Append(f.Name).Append('=').AppendLine(v is bool ? ((bool)v ? "1" : "0") : v.ToString());
                }
                File.WriteAllText(iniPath, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Write("Settings.Save failed: " + ex.Message); }
        }
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
