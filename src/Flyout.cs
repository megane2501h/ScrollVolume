using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ScrollVolume
{
    sealed class Palette
    {
        public bool Light;
        public Color Back, Border, Text, SubText, Hover, Track, Accent, AccentDim, ThumbOuter, Separator, Mute, Meter;

        public static Palette Create(bool light, Color accent)
        {
            Palette p = new Palette { Light = light };
            if (light)
            {
                p.Back = Color.FromArgb(249, 249, 249); p.Border = Color.FromArgb(210, 210, 210);
                p.Text = Color.FromArgb(26, 26, 26); p.SubText = Color.FromArgb(96, 96, 96);
                p.Hover = Color.FromArgb(234, 234, 234); p.Track = Color.FromArgb(196, 196, 196);
                p.ThumbOuter = Color.White; p.Separator = Color.FromArgb(229, 229, 229);
                p.Mute = Color.FromArgb(196, 43, 28);
                p.Accent = Theme.Mix(accent, Color.Black, 0.1f);
            }
            else
            {
                p.Back = Color.FromArgb(36, 36, 36); p.Border = Color.FromArgb(66, 66, 66);
                p.Text = Color.White; p.SubText = Color.FromArgb(168, 168, 168);
                p.Hover = Color.FromArgb(50, 50, 50); p.Track = Color.FromArgb(96, 96, 96);
                p.ThumbOuter = Color.FromArgb(69, 69, 69); p.Separator = Color.FromArgb(56, 56, 56);
                p.Mute = Color.FromArgb(255, 110, 100);
                p.Accent = Theme.Mix(accent, Color.White, 0.35f);
            }
            p.AccentDim = Theme.Mix(p.Accent, p.Back, 0.55f);
            p.Meter = light ? Color.FromArgb(40, 40, 40) : Color.White; // 鳴っている部分は文字と同じ色で目立たせる
            return p;
        }

        public static Palette Current() { return Create(Theme.IsLightTaskbar(), Theme.SystemAccent()); } // タスクトレイの他のポップアップと同じく Windows の色に合わせる
    }

    /// <summary>
    /// ScrollVolume のミキサー。再生デバイスごとに、音を出しているアプリをアイコン付きで並べる。
    /// - デバイスは折りたためる（状態は保存）
    /// - ピンで固定すると、ほかをクリックしても閉じず最前面に残り、上部をドラッグして動かせる
    /// - どの行でも ホイール＝音量、ホイールクリック＝ミュート
    /// </summary>
    sealed class MixerFlyout : Form
    {
        internal enum Hit { None, Header, Pin, Window, Gear, Chevron, SetDefault, Mute, Slider, Row, Scroll }

        internal sealed class Row
        {
            public MixerDevice Dev;   // null ならメッセージ行
            public MixerApp App;      // null ならデバイスの行
            public int Y, H;          // 中身の先頭からの位置（拡大率込みのピクセル）
            public bool IsDevice { get { return Dev != null && App == null; } }
        }

        const int W = 400, HeaderH = 40, DevH = 64, AppH = 40, Pad = 12, PctW = 40, SliderW = 120, MuteW = 32, BottomPad = 6, Gap = 6, Margin12 = 12;
        const string GGear = "", GPin = "", GPinned = "", GToWindow = "", GToPopup = "", GDown = "", GRight = "",
            GMute = "", GVol0 = "", GVol1 = "", GVol2 = "", GVol3 = "", GSpeaker = "", GApp = "";

        readonly IMixerSource source;
        readonly Func<Settings> settings;
        readonly Timer timer = new Timer();
        readonly HashSet<string> collapsed;
        readonly Dictionary<string, float> shownPeak = new Dictionary<string, float>();
        readonly WheelAccumulator wheel = new WheelAccumulator();
        internal readonly List<Row> Rows = new List<Row>();
        Palette pal = Palette.Create(false, Theme.Accent);
        float k = 1f;
        Font fText, fBold, fSmall, fSmallUnderline, fGlyph, fGlyphSmall;
        int scrollY, contentH, viewH;
        string signature = "";
        bool windowed, topmost, rounded;
        int lastFrameTick;
        readonly Dictionary<Bitmap, Bitmap> scaledIcons = new Dictionary<Bitmap, Bitmap>(); // 表示する大きさに縮小済みのアイコン（毎回の拡大縮小を避ける）
        public int LastDeactivatedTick;
        Row dragRow;
        bool scrollDrag;
        int scrollDragY, scrollDragStart;
        Row hoverRow;
        Hit hoverHit;
        int frame;
        Rectangle anchor;
        public int LastHiddenTick;
        readonly ToolTip buttonTips = new ToolTip();
        string tipText;
        Size userClient;   // ウィンドウ表示で利用者が決めた中身の大きさ（96 dpi 換算）
        bool userSizing;   // 枠をドラッグして大きさを変えている途中

        public event Action VolumeChanged;
        public event Action OpenSettings;
        public event Action StateChanged;          // 表示方法・最前面・折りたたみ・位置が変わった
        public event Action<Rectangle> ShownBounds; // 表示中の位置（隠したら Empty）

        public MixerFlyout(IMixerSource src, Func<Settings> getSettings)
        {
            source = src;
            settings = getSettings;
            Settings s = getSettings();
            collapsed = s.CollapsedSet();
            windowed = s.MixerWindowed;
            topmost = s.MixerTopmost;
            Size us;
            if (s.TryGetMixerSize(out us)) userClient = us;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ApplyMode();
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            timer.Interval = 33;
            timer.Tick += delegate { OnFrame(); };
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) HideFlyout(); };
            MakeFonts();
        }

        public bool Windowed { get { return windowed; } }
        public bool Topmost { get { return topmost; } }
        int CW { get { return ClientSize.Width; } }

        /// <summary>
        /// ポップアップ: 枠なし・タスクバーに出ない・ほかをクリックすると閉じる。
        /// ウィンドウ: タイトルバーあり・タスクバーと Alt+Tab に出る・最前面は選べる。
        /// </summary>
        void ApplyMode()
        {
            FormBorderStyle = windowed ? FormBorderStyle.Sizable : FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = windowed;
            ShowInTaskbar = windowed;
            Text = Terms.Mixer;
            TopMost = windowed ? topmost : true;
            UpdateMinimumSize();
        }

        /// <summary>ウィンドウ表示で縮められる下限（幅は既定の幅、高さは 1 行分）。</summary>
        Size MinClient() { return new Size(S(W), S(HeaderH) + S(AppH) + S(BottomPad)); }

        void UpdateMinimumSize() { MinimumSize = windowed ? SizeFromClientSize(MinClient()) : Size.Empty; }

        /// <summary>ウィンドウ表示で利用者が大きさを決めたか（決めていなければ中身に合わせる）。</summary>
        bool UserSized { get { return windowed && !userClient.IsEmpty; } }

        /// <summary>利用者が決めた中身の大きさ（今の拡大率で、作業領域に収まるように）。</summary>
        Size UserClient()
        {
            Rectangle wa = CurrentWorkArea();
            Size frame = SizeFromClientSize(new Size(100, 100)) - new Size(100, 100);
            Size min = MinClient();
            int w = Math.Max(min.Width, Math.Min(S(userClient.Width), wa.Width - frame.Width));
            int h = Math.Max(min.Height, Math.Min(S(userClient.Height), wa.Height - frame.Height));
            return new Size(w, h);
        }

        Rectangle CurrentWorkArea() { return WorkAreaFor(IsHandleCreated && Visible ? Center(Bounds) : Center(anchor)); }

        /// <summary>ウィンドウ表示で利用者が決めた中身の大きさ（96 dpi 換算）。Empty なら中身に合わせる。</summary>
        public Size UserClientSize { get { return userClient; } }
        public IEnumerable<string> CollapsedIds { get { return collapsed; } }
        internal float UiScale { get { return k; } }
        internal Font SmallFont { get { return fSmall; } }
        internal int ContentHeight { get { return contentH; } }
        internal int ViewHeight { get { return viewH; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (!windowed)
                {
                    cp.ExStyle |= 0x00000080; // TOOLWINDOW（Alt+Tab に出さない）
                    cp.ClassStyle |= 0x00020000; // DROPSHADOW
                }
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int v = 2; // Windows 11 の角丸
                rounded = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4) == 0;
                int dark = pal.Light ? 0 : 1; // タイトルバーも中身の色に合わせる
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            }
            catch { rounded = false; }
        }

        int S(int v) { return (int)Math.Round(v * k); }

        void MakeFonts()
        {
            DisposeFonts();
            string ff;
            using (Font sys = SystemFonts.MessageBoxFont) ff = sys.Name; // SystemFonts は毎回新しい Font を返すので破棄する
            fText = new Font(ff, 12.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fBold = new Font(ff, 12.5f * k, FontStyle.Bold, GraphicsUnit.Pixel);
            fSmall = new Font(ff, 11.5f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fSmallUnderline = new Font(ff, 11.5f * k, FontStyle.Underline, GraphicsUnit.Pixel);
            string glyph = Theme.GlyphFontName();
            fGlyph = new Font(glyph, 15f * k, FontStyle.Regular, GraphicsUnit.Pixel);
            fGlyphSmall = new Font(glyph, 10f * k, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        void DisposeFonts()
        {
            foreach (Font f in new[] { fText, fBold, fSmall, fSmallUnderline, fGlyph, fGlyphSmall }) if (f != null) f.Dispose();
        }

        // ---------- 表示・位置 ----------

        /// <summary>トレイのアイコン（anchor）の近くに開く。固定中で位置を覚えていればそこに開く。</summary>
        public void ShowNear(Rectangle anchorRect)
        {
            Rectangle a = anchorRect.IsEmpty ? new Rectangle(Cursor.Position, new Size(1, 1)) : anchorRect;
            Point saved;
            Point at = windowed && settings().TryGetMixerPosition(out saved) && OnScreen(saved) ? saved : Center(a);
            Prepare(a, Theme.ScaleAt(at), Palette.Current());
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            frame = 0;
            timer.Start();
            if (ShownBounds != null) ShownBounds(Bounds);
        }

        /// <summary>表示せずに中身と位置を整える（テストからも使う）。</summary>
        internal void Prepare(Rectangle anchorRect, float scale, Palette palette)
        {
            anchor = anchorRect;
            k = scale;
            if (pal == null || pal.Light != palette.Light) { pal = palette; if (IsHandleCreated) RecreateHandle(); } // タイトルバーの色を合わせ直す
            pal = palette;
            MakeFonts();
            ClearIconCache();
            source.Refresh(true);
            Relayout();
            PlaceInitial();
        }

        internal void SimulateFrame() { OnFrame(); }
        internal void SimulateDeactivate() { OnDeactivate(EventArgs.Empty); }

        static Point Center(Rectangle r) { return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); }

        static bool OnScreen(Point p)
        {
            foreach (Screen s in Screen.AllScreens) if (s.WorkingArea.Contains(p)) return true;
            return false;
        }

        public void HideFlyout()
        {
            if (!Visible) return;
            timer.Stop();
            dragRow = null; scrollDrag = false; Capture = false;
            Hide();
            LastHiddenTick = Environment.TickCount;
            if (ShownBounds != null) ShownBounds(Rectangle.Empty);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            LastDeactivatedTick = Environment.TickCount;
            if (!windowed) HideFlyout();
        }

        /// <summary>アンカーの近くで、作業領域の内側に収まる位置（タスクバーが上下左右どこにあっても）。</summary>
        public static Point ComputeLocation(Point c, Size size, Rectangle wa, int margin)
        {
            int x, y;
            if (c.X < wa.Left) x = wa.Left + margin;
            else if (c.X >= wa.Right) x = wa.Right - size.Width - margin;
            else x = c.X - size.Width / 2;
            if (c.Y >= wa.Bottom) y = wa.Bottom - size.Height - margin;
            else if (c.Y < wa.Top) y = wa.Top + margin;
            else y = c.Y - size.Height - margin;
            x = Math.Max(wa.Left, Math.Min(Math.Max(wa.Left + margin, Math.Min(wa.Right - size.Width - margin, x)), wa.Right - size.Width));
            y = Math.Max(wa.Top, Math.Min(Math.Max(wa.Top + margin, Math.Min(wa.Bottom - size.Height - margin, y)), wa.Bottom - size.Height));
            return new Point(x, y);
        }

        Rectangle WorkAreaFor(Point p) { return Screen.FromPoint(p).WorkingArea; }

        void PlaceInitial()
        {
            Point saved;
            Size sz = OuterSize();
            Point p;
            if (windowed && settings().TryGetMixerPosition(out saved) && OnScreen(saved))
                p = Clamp(new Rectangle(saved, sz), WorkAreaFor(saved)).Location;
            else
                p = ComputeLocation(Center(anchor), sz, WorkAreaFor(Center(anchor)), S(Margin12));
            Bounds = new Rectangle(p, sz);
        }

        static Rectangle Clamp(Rectangle r, Rectangle wa)
        {
            int x = Math.Max(wa.Left, Math.Min(wa.Right - r.Width, r.X));
            int y = Math.Max(wa.Top, Math.Min(wa.Bottom - r.Height, r.Y));
            return new Rectangle(x, y, r.Width, r.Height);
        }

        /// <summary>中身の大きさ（ウィンドウ表示ならタイトルバーと枠を足した外側の大きさ）。</summary>
        Size OuterSize()
        {
            Size client = UserSized ? UserClient() : new Size(S(W), S(HeaderH) + viewH + S(BottomPad));
            return windowed ? SizeFromClientSize(client) : client;
        }

        int FrameHeight() { return windowed ? SizeFromClientSize(new Size(100, 100)).Height - 100 : 0; }

        /// <summary>行の増減で高さが変わったとき。画面の下半分にあれば下端を、上半分にあれば上端を動かさない。</summary>
        void ResizeToContent()
        {
            Size sz = OuterSize();
            if (sz == Size) return;
            Rectangle wa = WorkAreaFor(Center(Bounds));
            bool keepBottom = Center(Bounds).Y > wa.Top + wa.Height / 2;
            Rectangle r = new Rectangle(Left, keepBottom ? Bottom - sz.Height : Top, sz.Width, sz.Height);
            Bounds = Clamp(r, wa);
            if (ShownBounds != null && Visible) ShownBounds(Bounds);
        }

        // ---------- 行の組み立て ----------

        string Signature()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (MixerDevice d in source.Devices)
            {
                sb.Append(d.Id).Append(d.IsDefault ? "*" : "").Append(collapsed.Contains(d.Id) ? "-" : "+").Append('|');
                foreach (MixerApp a in d.Apps) sb.Append(a.Key).Append('|');
                sb.Append('\n');
            }
            return sb.ToString();
        }

        internal void Relayout()
        {
            Rows.Clear();
            int y = 0;
            List<MixerDevice> devs = source.Devices;
            for (int i = 0; i < devs.Count; i++)
            {
                MixerDevice d = devs[i];
                if (i > 0) y += S(Gap);
                Rows.Add(new Row { Dev = d, Y = y, H = S(DevH) });
                y += S(DevH);
                if (collapsed.Contains(d.Id)) continue;
                foreach (MixerApp a in d.Apps)
                {
                    Rows.Add(new Row { Dev = d, App = a, Y = y, H = S(AppH) });
                    y += S(AppH);
                }
            }
            if (devs.Count == 0) { Rows.Add(new Row { Y = 0, H = S(AppH) }); y = S(AppH); }
            contentH = y;
            Rectangle wa = CurrentWorkArea();
            int maxView = Math.Max(S(AppH) * 2, wa.Height - S(HeaderH) - S(BottomPad) - S(Margin12) * 2 - FrameHeight());
            viewH = UserSized ? UserClient().Height - S(HeaderH) - S(BottomPad) : Math.Min(contentH, maxView);
            scrollY = Math.Max(0, Math.Min(scrollY, contentH - viewH));
            signature = Signature();
        }

        void OnFrame()
        {
            frame++;
            if (WindowState == FormWindowState.Minimized) return; // 最小化中は何もしない
            source.Refresh(frame % 30 == 0); // 一覧は約 1 秒ごと、音量とメーターは毎フレーム
            if (Signature() != signature) { Relayout(); ResizeToContent(); }
            UpdatePeaks();
            Invalidate();
        }

        /// <summary>メーターは「実際に出ている大きさ」（信号 × 音量 × デバイスの音量）を、経過時間に応じて滑らかに。</summary>
        void UpdatePeaks()
        {
            int now = Environment.TickCount;
            int dt = lastFrameTick == 0 ? 33 : Math.Min(1000, Math.Max(0, now - lastFrameTick));
            lastFrameTick = now;
            foreach (MixerDevice d in source.Devices)
            {
                Smooth(d.Id, PeakMeter.Level(d.Peak, d.Volume, d.Muted, d.Volume, d.Muted, false), dt);
                foreach (MixerApp a in d.Apps) Smooth(d.Id + "|" + a.Key, PeakMeter.Level(a.Peak, a.Volume, a.Muted, d.Volume, d.Muted, true), dt);
            }
        }

        void Smooth(string key, float level, int dt)
        {
            float prev;
            shownPeak.TryGetValue(key, out prev);
            shownPeak[key] = PeakMeter.Next(prev, level, dt);
        }

        /// <summary>表示中のメーター（スライダー全長に対する割合）。</summary>
        internal float MeterOf(Row r)
        {
            float v;
            return shownPeak.TryGetValue(r.App == null ? r.Dev.Id : r.Dev.Id + "|" + r.App.Key, out v) ? Math.Max(0, Math.Min(1, v)) : 0f;
        }

        // ---------- 幾何 ----------

        int RowTop(Row r) { return S(HeaderH) + r.Y - scrollY; }
        int ControlsY(Row r) { return RowTop(r) + (r.IsDevice ? S(44) : S(20)); }

        Rectangle PctRect(int cy) { return new Rectangle(CW - S(Pad) - S(PctW), cy - S(12), S(PctW), S(24)); }
        /// <summary>幅を広げたら、増えた分の 2/3 をスライダーに回す（細かく合わせやすくなる）。残りは名前の欄。</summary>
        int SliderWidth() { return S(SliderW) + Math.Max(0, CW - S(W)) * 2 / 3; }
        Rectangle SliderRect(int cy) { Rectangle p = PctRect(cy); int w = SliderWidth(); return new Rectangle(p.Left - S(6) - w, cy - S(12), w, S(24)); }
        Rectangle MuteRect(int cy) { Rectangle s = SliderRect(cy); return new Rectangle(s.Left - S(4) - S(MuteW), cy - S(16), S(MuteW), S(32)); }
        Rectangle ChevronRect(Row r) { return new Rectangle(S(8), RowTop(r) + S(4), S(24), S(24)); }
        Rectangle NameRect(Row r)
        {
            if (r.IsDevice) return new Rectangle(S(36), RowTop(r) + S(4), CW - S(36) - S(Pad), S(24));
            int cy = ControlsY(r);
            return new Rectangle(S(66), cy - S(12), MuteRect(cy).Left - S(4) - S(66), S(24));
        }
        Rectangle TagRect(Row r)
        {
            int cy = ControlsY(r);
            return new Rectangle(S(36), cy - S(12), MuteRect(cy).Left - S(4) - S(36), S(24));
        }
        Rectangle IconRect(Row r) { int cy = ControlsY(r); return new Rectangle(S(36), cy - S(11), S(22), S(22)); }
        int TrackLeft(Rectangle slider) { return slider.Left + S(8); }
        int TrackWidth(Rectangle slider) { return slider.Width - S(16); }

        List<KeyValuePair<Hit, Rectangle>> HeaderButtons()
        {
            List<KeyValuePair<Hit, Rectangle>> list = new List<KeyValuePair<Hit, Rectangle>>();
            int size = S(32), y = (S(HeaderH) - size) / 2, x = CW - S(6) - size;
            // 右から: [ウィンドウ ⇔ ポップアップ] [最前面（ウィンドウ表示のときだけ）] [設定]
            list.Add(new KeyValuePair<Hit, Rectangle>(Hit.Window, new Rectangle(x, y, size, size))); x -= size;
            if (windowed) { list.Add(new KeyValuePair<Hit, Rectangle>(Hit.Pin, new Rectangle(x, y, size, size))); x -= size; }
            list.Add(new KeyValuePair<Hit, Rectangle>(Hit.Gear, new Rectangle(x, y, size, size)));
            return list;
        }

        bool HasScrollbar { get { return contentH > viewH; } }

        internal Hit HitTest(Point p, out Row row)
        {
            row = null;
            if (p.Y < S(HeaderH))
            {
                foreach (KeyValuePair<Hit, Rectangle> b in HeaderButtons()) if (b.Value.Contains(p)) return b.Key;
                return Hit.Header;
            }
            if (p.Y >= S(HeaderH) + viewH) return Hit.None;
            if (HasScrollbar && p.X >= CW - S(10)) return Hit.Scroll;
            int y = p.Y - S(HeaderH) + scrollY;
            foreach (Row r in Rows) if (y >= r.Y && y < r.Y + r.H) { row = r; break; }
            if (row == null || row.Dev == null) return Hit.None;
            int cy = ControlsY(row);
            if (MuteRect(cy).Contains(p)) return Hit.Mute;
            Rectangle sl = SliderRect(cy);
            if (p.X >= sl.Left && p.X < sl.Right && p.Y >= cy - S(18) && p.Y < cy + S(18)) return Hit.Slider;
            // デバイス名の行（矢印を描いているとき＝アプリがあるときだけ折りたためる）
            if (row.IsDevice && p.Y < RowTop(row) + S(28)) return row.Dev.Apps.Count > 0 ? Hit.Chevron : Hit.Row;
            if (row.IsDevice && !row.Dev.IsDefault && TagRect(row).Contains(p) && p.X < S(36) + TextRenderer.MeasureText(DefaultActionText, fSmall).Width + S(4)) return Hit.SetDefault;
            return Hit.Row;
        }

        internal Rectangle SliderRectOf(Row r) { return SliderRect(ControlsY(r)); }
        internal Rectangle MuteRectOf(Row r) { return MuteRect(ControlsY(r)); }
        internal Rectangle NameRectOf(Row r) { return NameRect(r); }
        internal Rectangle TagRectOf(Row r) { return TagRect(r); }
        internal int SliderWidthOf() { return SliderWidth(); }

        /// <summary>折りたたんだデバイスの行に出す、隠れているアプリの数（出さないなら null）。</summary>
        internal string CollapsedCount(Row r)
        {
            return r.IsDevice && collapsed.Contains(r.Dev.Id) && r.Dev.Apps.Count > 0 ? r.Dev.Apps.Count.ToString() : null;
        }

        static string DefaultActionText { get { return Terms.SetDefault; } }
        static string DefaultText { get { return Terms.DefaultDevice; } }

        // ---------- 値 ----------

        static int VolumeOf(Row r) { return r.App != null ? r.App.Volume : r.Dev.Volume; }
        static bool MutedOf(Row r) { return r.App != null ? r.App.Muted : r.Dev.Muted; }

        void SetVolume(Row r, int v)
        {
            if (r.App != null) source.SetAppVolume(r.Dev.Id, r.App.Key, v);
            else source.SetDeviceVolume(r.Dev.Id, v);
        }

        void SetMute(Row r, bool m)
        {
            if (r.App != null) source.SetAppMute(r.Dev.Id, r.App.Key, m);
            else source.SetDeviceMute(r.Dev.Id, m);
        }

        void Apply(Row r, int next, bool raising)
        {
            int cur = VolumeOf(r);
            bool muted = MutedOf(r);
            bool nm = MuteRules.After(muted, next, raising, settings());
            if (next != cur) SetVolume(r, next);
            if (nm != muted) SetMute(r, nm);
            Changed();
        }

        void Changed()
        {
            if (VolumeChanged != null) VolumeChanged();
            Invalidate();
        }

        internal void ToggleMute(Row r)
        {
            if (r == null || r.Dev == null) return;
            SetMute(r, !MutedOf(r));
            Changed();
        }

        void SetFromX(Row r, int x)
        {
            Rectangle sl = SliderRect(ControlsY(r));
            int tw = Math.Max(1, TrackWidth(sl));
            int v = (int)Math.Round((x - TrackLeft(sl)) * 100.0 / tw);
            v = Math.Max(0, Math.Min(100, v));
            int cur = VolumeOf(r);
            if (v != cur) Apply(r, v, v > cur);
        }

        void ToggleCollapsed(MixerDevice d)
        {
            if (!collapsed.Remove(d.Id)) collapsed.Add(d.Id);
            Relayout();
            ResizeToContent();
            Invalidate();
            if (StateChanged != null) StateChanged();
        }

        internal void SetTopmost(bool value)
        {
            topmost = value;
            if (windowed) TopMost = value;
            Invalidate();
            if (StateChanged != null) StateChanged();
        }

        /// <summary>ポップアップ ⇔ ウィンドウ。ウィンドウにするときは今の位置のまま、戻すときはトレイの近くへ。</summary>
        internal void SetWindowed(bool value)
        {
            if (windowed == value) return;
            Point clientTopLeft = IsHandleCreated ? PointToScreen(Point.Empty) : Location;
            windowed = value;
            ApplyMode();
            Relayout();
            if (windowed)
            {
                Size sz = OuterSize();
                Point outer = new Point(clientTopLeft.X - (sz.Width - S(W)) / 2, clientTopLeft.Y - FrameHeight() + (sz.Width - S(W)) / 2);
                Bounds = Clamp(new Rectangle(outer, sz), WorkAreaFor(clientTopLeft));
            }
            else PlaceInitial();
            Invalidate();
            if (Visible) { Activate(); if (ShownBounds != null) ShownBounds(Bounds); }
            if (StateChanged != null) StateChanged();
        }

        // ウィンドウの × は閉じる（隠す）だけ。次にトレイから開くと同じ状態で出る
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideFlyout(); return; }
            base.OnFormClosing(e);
        }

        // ---------- マウス ----------

        /// <summary>行の上でホイール → その行の音量。見出しやスクロールバーの上ならスクロール。</summary>
        internal void HandleWheel(Point p, int delta, int now)
        {
            Row r;
            Hit h = HitTest(p, out r);
            if (r == null || r.Dev == null || h == Hit.Scroll || (Control.ModifierKeys & Keys.Shift) != 0) // Shift+ホイールはどこでもスクロール
            {
                if (HasScrollbar) { scrollY = Math.Max(0, Math.Min(contentH - viewH, scrollY - delta * S(AppH) / 120)); Invalidate(); }
                return;
            }
            Settings s = settings();
            int dt;
            int n = wheel.Feed(s.Invert ? -delta : delta, now, out dt);
            if (n == 0) return;
            Apply(r, VolumeMath.Next(VolumeOf(r), n, s, dt), n > 0);
        }

        internal void HandleClick(Point p, MouseButtons button)
        {
            Row r;
            Hit h = HitTest(p, out r);
            if (button == MouseButtons.Middle) { if (r != null && r.Dev != null) ToggleMute(r); return; }
            if (button != MouseButtons.Left) return;
            switch (h)
            {
                case Hit.Pin: SetTopmost(!topmost); break;
                case Hit.Window: SetWindowed(!windowed); break;
                case Hit.Gear:
                    if (!windowed) HideFlyout();
                    if (OpenSettings != null) OpenSettings();
                    break;
                case Hit.Chevron: ToggleCollapsed(r.Dev); break;
                case Hit.SetDefault:
                    source.SetDefault(r.Dev.Id);
                    source.Refresh(true);
                    Relayout(); ResizeToContent();
                    Changed();
                    break;
                case Hit.Mute: ToggleMute(r); break;
                case Hit.Slider:
                    dragRow = r;
                    Capture = true;
                    SetFromX(r, p.X);
                    break;
                case Hit.Scroll:
                    scrollDrag = true; scrollDragY = p.Y; scrollDragStart = scrollY;
                    Capture = true;
                    break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); HandleClick(e.Location, e.Button); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragRow != null) { SetFromX(dragRow, e.X); return; }
            if (scrollDrag)
            {
                int track = Math.Max(1, viewH - ScrollThumbHeight());
                scrollY = Math.Max(0, Math.Min(contentH - viewH, scrollDragStart + (e.Y - scrollDragY) * (contentH - viewH) / track));
                Invalidate();
                return;
            }
            Row r;
            Hit h = HitTest(e.Location, out r);
            if (h != hoverHit || r != hoverRow) { hoverHit = h; hoverRow = r; Invalidate(); }
            Cursor c = Cursors.Default;
            if (h == Hit.Pin || h == Hit.Window || h == Hit.Gear || h == Hit.Chevron || h == Hit.SetDefault || h == Hit.Mute || h == Hit.Slider) c = Cursors.Hand;
            string tip = h == Hit.Pin ? (topmost ? L.T("最前面に表示: オン", "Always on top: on") : L.T("最前面に表示", "Always on top"))
                : h == Hit.Window ? (windowed ? L.T("ポップアップに戻す", "Back to pop-up") : L.T("ウィンドウで表示", "Open as a window"))
                : h == Hit.Gear ? Terms.Settings
                : h == Hit.Chevron && r != null ? (collapsed.Contains(r.Dev.Id) ? L.T("アプリを表示", "Show apps") : L.T("アプリを隠す", "Hide apps")) : null;
            if (tip != tipText) { tipText = tip; buttonTips.SetToolTip(this, tip); }
            if (Cursor != c) Cursor = c;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragRow = null; scrollDrag = false; Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverRow = null; hoverHit = Hit.None; Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            HandleWheel(e.Location, e.Delta, Environment.TickCount);
        }

        /// <summary>ウィンドウ表示で大きさが変わったら、見える範囲を合わせる。枠をドラッグしたときはその大きさを覚える。</summary>
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!windowed || WindowState != FormWindowState.Normal) return;
            if (userSizing)
            {
                Size min = MinClient();
                userClient = new Size((int)Math.Round(Math.Max(min.Width, ClientSize.Width) / k), (int)Math.Round(Math.Max(min.Height, ClientSize.Height) / k));
            }
            viewH = Math.Max(0, ClientSize.Height - S(HeaderH) - S(BottomPad));
            scrollY = Math.Max(0, Math.Min(scrollY, contentH - viewH));
            Invalidate();
        }

        /// <summary>テスト用: 枠をドラッグして中身をこの大きさにしたのと同じ。</summary>
        internal void SimulateUserResize(Size client)
        {
            userSizing = true;
            try
            {
                ClientSize = client;
                // 実際のドラッグでは MinimumSize で止まる。プログラムからの変更では効かないことがあるので、ここで同じ下限に合わせる
                Size c = ClientSize, min = MinClient();
                if (c.Width < min.Width || c.Height < min.Height) ClientSize = new Size(Math.Max(c.Width, min.Width), Math.Max(c.Height, min.Height));
            }
            finally { userSizing = false; }
            if (StateChanged != null) StateChanged();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_SIZING) userSizing = true;
            else if (m.Msg == Native.WM_EXITSIZEMOVE)
            {
                userSizing = false;
                if (StateChanged != null) StateChanged();
                if (ShownBounds != null) ShownBounds(Bounds);
            }
            else if (m.Msg == Native.WM_DPICHANGED)
            {
                k = ((m.WParam.ToInt32() >> 16) & 0xFFFF) / 96f;
                UpdateMinimumSize();
                MakeFonts();
                Relayout();
                ResizeToContent();
                Invalidate();
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // ---------- 描画 ----------

        int ScrollThumbHeight() { return Math.Max(S(24), viewH * viewH / Math.Max(1, contentH)); }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(pal.Back);
            DrawHeader(g);

            Rectangle content = new Rectangle(0, S(HeaderH), CW, viewH);
            g.SetClip(content);
            bool firstDevice = true;
            foreach (Row r in Rows)
            {
                int top = RowTop(r);
                if (top + r.H < content.Top || top > content.Bottom) { if (r.IsDevice) firstDevice = false; continue; }
                if (r.Dev == null) { DrawText(g, Terms.NoDevice, fText, new Rectangle(0, top, CW, r.H), pal.SubText, TextFormatFlags.HorizontalCenter); continue; }
                if (r.IsDevice)
                {
                    if (!firstDevice)
                        using (Pen sp = new Pen(pal.Separator)) g.DrawLine(sp, S(Pad), top - S(Gap) / 2, CW - S(Pad), top - S(Gap) / 2);
                    firstDevice = false;
                }
                if (r == hoverRow || r == dragRow) FillRound(g, new Rectangle(S(4), top, CW - S(8), r.H), S(6), pal.Hover);
                if (r.IsDevice) DrawDevice(g, r); else DrawApp(g, r);
            }
            g.ResetClip();

            if (HasScrollbar)
            {
                int th = ScrollThumbHeight();
                int ty = S(HeaderH) + (viewH - th) * scrollY / Math.Max(1, contentH - viewH);
                FillRound(g, new Rectangle(CW - S(6), ty, S(3), th), S(1), Color.FromArgb(140, pal.SubText));
            }
            if (!rounded && !windowed) using (Pen b = new Pen(pal.Border)) g.DrawRectangle(b, 0, 0, CW - 1, ClientSize.Height - 1);
        }

        void DrawHeader(Graphics g)
        {
            if (!windowed) DrawText(g, Terms.Mixer, fSmall, new Rectangle(S(Pad + 2), 0, CW / 2, S(HeaderH)), pal.SubText, TextFormatFlags.Left); // ウィンドウならタイトルバーに出ている
            foreach (KeyValuePair<Hit, Rectangle> b in HeaderButtons())
            {
                if (hoverHit == b.Key || (b.Key == Hit.Pin && topmost)) FillRound(g, b.Value, S(4), pal.Hover); // 最前面がオンなら押された状態に見せる
                string glyph = b.Key == Hit.Window ? (windowed ? GToPopup : GToWindow) : b.Key == Hit.Gear ? GGear : (topmost ? GPinned : GPin);
                Color c = b.Key == Hit.Pin && topmost ? pal.Accent : pal.SubText;
                DrawText(g, glyph, fGlyph, b.Value, c, TextFormatFlags.HorizontalCenter);
            }
            using (Pen sp = new Pen(pal.Separator)) g.DrawLine(sp, 0, S(HeaderH) - 1, CW, S(HeaderH) - 1);
        }

        void DrawDevice(Graphics g, Row r)
        {
            MixerDevice d = r.Dev;
            if (d.Apps.Count > 0)
                DrawText(g, collapsed.Contains(d.Id) ? GRight : GDown, fGlyphSmall, ChevronRect(r), pal.SubText, TextFormatFlags.HorizontalCenter);
            Rectangle nr = NameRect(r);
            string count = CollapsedCount(r);
            if (count != null)
            {
                // 折りたたみ中は、隠れているアプリの数を名前の後ろに出す
                const TextFormatFlags mf = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
                int pillW = TextRenderer.MeasureText(g, count, fSmall, Size.Empty, mf).Width + S(12);
                int nameW = Math.Min(nr.Width - pillW - S(6), TextRenderer.MeasureText(g, d.Name, fBold, Size.Empty, mf).Width + S(1));
                DrawText(g, d.Name, fBold, new Rectangle(nr.X, nr.Y, Math.Max(0, nameW), nr.Height), pal.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                Rectangle pill = new Rectangle(nr.X + Math.Max(0, nameW) + S(6), nr.Y + S(3), pillW, nr.Height - S(6));
                FillRound(g, pill, pill.Height / 2f, Theme.Mix(pal.Back, pal.Text, 0.12f));
                DrawText(g, count, fSmall, pill, pal.SubText, TextFormatFlags.HorizontalCenter);
            }
            else DrawText(g, d.Name, fBold, nr, pal.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Rectangle tag = TagRect(r);
            if (d.IsDefault) DrawText(g, DefaultText, fSmall, tag, pal.SubText, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            else
            {
                // 押せることが分かるように、普段からリンクの色で出す
                bool hot = hoverRow == r && hoverHit == Hit.SetDefault;
                DrawText(g, DefaultActionText, hot ? fSmallUnderline : fSmall, tag, pal.Accent, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            DrawControls(g, r);
        }

        void DrawApp(Graphics g, Row r)
        {
            MixerApp a = r.App;
            Rectangle ir = IconRect(r);
            if (a.Icon != null)
            {
                Bitmap icon = ScaledIcon(a.Icon, ir.Width);
                if (a.Muted)
                {
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ColorMatrix cm = new ColorMatrix { Matrix33 = 0.4f };
                        ia.SetColorMatrix(cm);
                        g.DrawImage(icon, new Rectangle(ir.Location, icon.Size), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                else g.DrawImageUnscaled(icon, ir.Location);
            }
            else DrawText(g, a.IsSystem ? GSpeaker : GApp, fGlyph, ir, a.Muted ? pal.Track : pal.SubText, TextFormatFlags.HorizontalCenter);
            DrawText(g, a.Name, fText, NameRect(r), a.Muted ? pal.SubText : pal.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            DrawControls(g, r);
        }

        void DrawControls(Graphics g, Row r)
        {
            int cy = ControlsY(r);
            int vol = VolumeOf(r);
            bool muted = MutedOf(r);

            Rectangle mr = MuteRect(cy);
            if (hoverRow == r && hoverHit == Hit.Mute) FillRound(g, mr, S(4), Theme.Mix(pal.Hover, pal.Text, 0.06f));
            string glyph = muted ? GMute : vol == 0 ? GVol0 : vol < 34 ? GVol1 : vol < 67 ? GVol2 : GVol3;
            DrawText(g, glyph, fGlyph, mr, muted ? pal.Mute : pal.Text, TextFormatFlags.HorizontalCenter);

            Rectangle sl = SliderRect(cy);
            int left = TrackLeft(sl), tw = TrackWidth(sl), th = Math.Max(2, S(4));
            float volW = tw * vol / 100f;
            FillRound(g, new RectangleF(left, cy - th / 2f, tw, th), th / 2f, pal.Track);
            if (volW > 0) FillRound(g, new RectangleF(left, cy - th / 2f, volW, th), th / 2f, muted ? pal.SubText : pal.AccentDim);
            float pk = Math.Min(volW, MeterOf(r) * tw); // 実際に出ている大きさ（音量のつまみより先には出ない）
            if (pk > 0.5f) FillRound(g, new RectangleF(left, cy - th / 2f, pk, th), th / 2f, pal.Meter); // 音量の色とはっきり分ける
            float tx = left + volW;
            float ro = S(8), ri = (dragRow == r || (hoverRow == r && hoverHit == Hit.Slider)) ? S(5) : S(4);
            using (SolidBrush b = new SolidBrush(pal.ThumbOuter)) g.FillEllipse(b, tx - ro, cy - ro, ro * 2, ro * 2);
            using (Pen p = new Pen(Color.FromArgb(40, pal.Text))) g.DrawEllipse(p, tx - ro, cy - ro, ro * 2, ro * 2);
            using (SolidBrush b = new SolidBrush(muted ? pal.SubText : pal.Accent)) g.FillEllipse(b, tx - ri, cy - ri, ri * 2, ri * 2);

            DrawText(g, vol.ToString(), fText, PctRect(cy), muted ? pal.SubText : pal.Text, TextFormatFlags.Right);
        }

        Bitmap ScaledIcon(Bitmap src, int size)
        {
            Bitmap b;
            if (scaledIcons.TryGetValue(src, out b) && b.Width == size) return b;
            if (b != null) b.Dispose();
            b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(src, 0, 0, size, size);
            }
            scaledIcons[src] = b;
            return b;
        }

        void ClearIconCache()
        {
            foreach (Bitmap b in scaledIcons.Values) b.Dispose();
            scaledIcons.Clear();
        }

        static void DrawText(Graphics g, string text, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, f, r, c, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
        }

        static void FillRound(Graphics g, RectangleF r, float radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            radius = Math.Max(0.5f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
            using (GraphicsPath p = IconRenderer.RoundRect(r, radius))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); buttonTips.Dispose(); DisposeFonts(); ClearIconCache(); }
            base.Dispose(disposing);
        }
    }
}
