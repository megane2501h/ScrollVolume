using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ScrollVolume
{
    /// <summary>キーを押して組み合わせを登録する入力欄。Backspace / Delete / Esc で解除。</summary>
    sealed class HotkeyBox : TextBox
    {
        int value;
        public event EventHandler ValueChanged;
        public event EventHandler Rejected;

        public HotkeyBox()
        {
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            Cursor = Cursors.Default;
        }

        public int Value
        {
            get { return value; }
            set { this.value = value; Text = Hotkey.Format(value); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            if (key == Keys.Tab && (mods & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref msg, keyData);
            CaptureKey(key, mods);
            return true;
        }

        internal void CaptureKey(Keys key, Keys mods)
        {
            int m = 0;
            if ((mods & Keys.Control) != 0) m |= Hotkey.Ctrl;
            if ((mods & Keys.Alt) != 0) m |= Hotkey.Alt;
            if ((mods & Keys.Shift) != 0) m |= Hotkey.Shift;
            if (Native.GetKeyState(0x5B) < 0 || Native.GetKeyState(0x5C) < 0) m |= Hotkey.Win;
            if (Hotkey.IsModifierKey(key))
            {
                Text = m == 0 ? "" : Hotkey.Format(Hotkey.Make(m, Keys.A)).Replace(" + A", " + …");
                return;
            }
            if (m == 0 && (key == Keys.Back || key == Keys.Delete || key == Keys.Escape)) { Set(0); return; }
            int hk = Hotkey.Make(m, key);
            if (!Hotkey.IsValid(hk)) { Text = Hotkey.Format(value); if (Rejected != null) Rejected(this, EventArgs.Empty); return; }
            Set(hk);
        }

        void Set(int hk)
        {
            Value = hk;
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (Hotkey.IsModifierKey(e.KeyCode) && !e.Control && !e.Alt && !e.Shift) Text = Hotkey.Format(value);
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            Text = Hotkey.Format(value);
        }
    }

    sealed class SettingsForm : Form
    {
        readonly Action<Settings, bool> apply;
        readonly Func<int> currentVolume;
        readonly Action<int> previewOsd;
        readonly Settings original;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000, InitialDelay = 400 };
        float k = 1f;
        internal static float ScaleOverride; // テスト用（0 なら画面の拡大率）

        int P(int v) { return (int)Math.Round(v * k); }

        /// <summary>ホットキーの入力欄にフォーカスがある間 true（その間は登録済みのホットキーを外して、押したキーがここに届くようにする）。</summary>
        public event Action<bool> HotkeyCapture;

        internal TabControl Tabs;
        // 場所
        internal Label zoneWarning;
        internal CheckBox corners, cTL, cTR, cBL, cBR, outerCorners;
        internal NumericUpDown cornerSize;
        internal CheckBox edges, eTop, eBottom, eLeft, eRight, outerEdges;
        internal NumericUpDown edgeSize;
        internal CheckBox taskbar, tray;
        internal ComboBox taskbarMod;
        internal FlowLayoutPanel trayRow;
        internal static string TrayCoveredTip { get { return L.T("タスクバーで常に反応する設定なので、アイコンの上も含まれます。", "Already covered: the taskbar always reacts."); } }
        // 操作
        internal ComboBox step, fastSpin, leftClick, middleClick;
        internal TrackBar accelStrength;
        internal FlowLayoutPanel accelRow;
        internal CheckBox invert, middleMute, middleTaskbar, unmute, muteZero;
        readonly List<FastSpin> fastItems = new List<FastSpin>();
        FastSpin fastUser;
        bool rebuildingFast, unmuteUser;
        // ホットキー
        internal HotkeyBox[] hotkeys;
        internal Label[] hotkeyStatus;
        readonly bool[] hotkeyFailed = new bool[5];
        int[] hotkeyRegistered = new int[5]; // 登録を試した値（「使用中」はこの値のときだけ出す）
        int rejectedIndex = -1;
        // 表示
        internal RadioButton[] StyleRadios;
        internal PictureBox[] Previews;
        internal ComboBox textColor;
        internal CheckBox showOsd, osdHideGames;
        internal NumericUpDown osdSeconds;
        internal Button osdTry;
        // 全般
        internal CheckBox autoStart, runElevated, gameGuard;
        internal ComboBox language;

        static string[] ClickNames() { return new[] { L.T("何もしない", "Do nothing"), Terms.OpenMixer, Terms.ToggleMute, Terms.OpenWindowsMixer }; }
        static string[] ModNames() { return new[] { L.T("常に", "Always"), L.T("Ctrl を押している間", "While holding Ctrl"), L.T("Shift を押している間", "While holding Shift"), L.T("Alt を押している間", "While holding Alt") }; }
        static string[] ColorNames() { return new[] { L.T("自動", "Auto"), L.T("白", "White"), L.T("黒", "Black") }; }
        static string[] StyleNames() { return new[] { L.T("数字", "Number"), L.T("数字（背景あり）", "Number on tile"), L.T("バー", "Bar") }; }
        static string[] HotkeyNames() { return new[] { L.T("音量を上げる", "Volume up"), L.T("音量を下げる", "Volume down"), Terms.ToggleMute, Terms.OpenMixer, Terms.NextDevice }; }

        internal static string FastName(FastSpin f)
        {
            switch (f)
            {
                case FastSpin.None: return L.T("通常どおり", "Normal");
                case FastSpin.Faster: return L.T("加速する", "Accelerate");
                case FastSpin.Snap5: return L.T("5% 区切りで進む", "Snap to 5% steps");
                default: return L.T("10% 区切りで進む", "Snap to 10% steps");
            }
        }

        public SettingsForm(Settings s, bool startupEnabled, Action<Settings, bool> onApply, Func<int> getVolume, Action<int> onPreviewOsd = null)
        {
            apply = onApply;
            currentVolume = getVolume;
            previewOsd = onPreviewOsd;
            original = s.Clone();
            Text = Terms.Settings;
            k = DeviceDpi / 96f;
            Font = SystemFonts.MessageBoxFont;
            if (ScaleOverride > 0) // テスト用: 別の拡大率の画面を再現する
            {
                Font = new Font(Font.FontFamily, Font.SizeInPoints * ScaleOverride / k, Font.Style);
                k = ScaleOverride;
            }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.None;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size((int)(500 * k), (int)(540 * k));

            Tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point((int)(10 * k), (int)(4 * k)) };
            Tabs.TabPages.Add(BuildPlaces(s));
            Tabs.TabPages.Add(BuildOperation(s));
            Tabs.TabPages.Add(BuildHotkeys(s));
            Tabs.TabPages.Add(BuildDisplay(s));
            Tabs.TabPages.Add(BuildGeneral(s, startupEnabled));

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, Padding = new Padding((int)(8 * k))
            };
            Button cancel = new Button { Text = L.T("キャンセル", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
            Button applyBtn = new Button { Text = L.T("適用", "Apply"), AutoSize = true, Name = "apply" };
            Button ok = new Button { Text = "OK", AutoSize = true };
            ok.Click += delegate { DoApply(); Close(); };
            applyBtn.Click += delegate { DoApply(); };
            cancel.Click += delegate { Close(); };
            buttons.Controls.Add(cancel); buttons.Controls.Add(applyBtn); buttons.Controls.Add(ok);
            AcceptButton = ok; CancelButton = cancel;

            Controls.Add(Tabs);
            Controls.Add(buttons);
            UpdateEnabled();
        }

        // ---- 部品 ----

        FlowLayoutPanel Page(TabPage tp)
        {
            FlowLayoutPanel p = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(P(12), P(10), P(12), P(10))
            };
            tp.Controls.Add(p);
            tp.UseVisualStyleBackColor = true;
            return p;
        }

        FlowLayoutPanel Group(Control parent, string title)
        {
            GroupBox g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(P(10), P(4), P(10), P(6)), Margin = new Padding(0, 0, 0, P(8)), MinimumSize = new Size(P(440), 0) };
            FlowLayoutPanel inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            g.Controls.Add(inner);
            parent.Controls.Add(g);
            return inner;
        }

        CheckBox Check(Control parent, string text, bool value, int indent = 0, string tip = null)
        {
            CheckBox c = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3 + P(indent), 2, 3, 2) };
            c.CheckedChanged += delegate { UpdateEnabled(); };
            parent.Controls.Add(c);
            if (tip != null) tips.SetToolTip(c, tip);
            return c;
        }

        FlowLayoutPanel HRow(Control parent, int indent)
        {
            FlowLayoutPanel r = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(P(indent), 0, 0, 0) };
            parent.Controls.Add(r);
            return r;
        }

        Label RowLabel(string text, int width = 0)
        {
            Label l = new Label { Text = text, AutoSize = width == 0, Margin = new Padding(3, 6, 3, 3) };
            if (width > 0) { l.Width = P(width); l.AutoEllipsis = true; }
            return l;
        }

        FlowLayoutPanel Row(Control parent, string label, Control ctl, int indent = 0, string suffix = null, int labelWidth = 0)
        {
            FlowLayoutPanel r = HRow(parent, indent);
            if (label != null) r.Controls.Add(RowLabel(label, labelWidth));
            r.Controls.Add(ctl);
            if (suffix != null) r.Controls.Add(RowLabel(suffix));
            return r;
        }

        ComboBox Combo(string[] items, int index, int width)
        {
            ComboBox c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = P(width) };
            c.Items.AddRange(items);
            c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, index));
            c.SelectedIndexChanged += delegate { UpdateEnabled(); };
            return c;
        }

        NumericUpDown Num(int min, int max, int value)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = P(60) };
        }

        // ---- ページ ----

        TabPage BuildPlaces(Settings s)
        {
            TabPage tp = new TabPage(L.T("場所", "Areas"));
            FlowLayoutPanel p = Page(tp);
            zoneWarning = new Label
            {
                Text = L.T("音量を操作できる場所もホットキーもありません。", "No area or hotkey is set to change the volume."),
                AutoSize = true, ForeColor = Color.FromArgb(196, 43, 28), Margin = new Padding(3, 0, 3, 8), Visible = false
            };
            p.Controls.Add(zoneWarning);

            FlowLayoutPanel g = Group(p, L.T("画面の四隅", "Screen corners"));
            corners = Check(g, L.T("有効にする", "Enabled"), s.Corners);
            FlowLayoutPanel r = HRow(g, 20);
            cTL = Check(r, L.T("左上", "Top left"), s.CornerTL); cTR = Check(r, L.T("右上", "Top right"), s.CornerTR);
            cBL = Check(r, L.T("左下", "Bottom left"), s.CornerBL); cBR = Check(r, L.T("右下", "Bottom right"), s.CornerBR);
            cornerSize = Num(2, 200, s.CornerSize);
            Row(g, L.T("反応範囲:", "Size:"), cornerSize, 20, L.T("ピクセル", "px"));
            outerCorners = Check(g, L.T("ディスプレイ同士が接する隅では反応しない", "Ignore corners shared with another display"), s.OuterCornersOnly, 20);

            g = Group(p, L.T("画面の端", "Screen edges"));
            edges = Check(g, L.T("有効にする", "Enabled"), s.Edges);
            r = HRow(g, 20);
            eTop = Check(r, L.T("上", "Top"), s.EdgeTop); eBottom = Check(r, L.T("下", "Bottom"), s.EdgeBottom);
            eLeft = Check(r, L.T("左", "Left"), s.EdgeLeft); eRight = Check(r, L.T("右", "Right"), s.EdgeRight);
            edgeSize = Num(1, 50, s.EdgeSize);
            Row(g, L.T("反応範囲:", "Size:"), edgeSize, 20, L.T("ピクセル", "px"));
            tips.SetToolTip(edgeSize, L.T("広くすると、ウィンドウ端のスクロールバーの上などでも音量が変わります。", "A wider edge may catch the wheel over scroll bars at the screen edge."));
            outerEdges = Check(g, L.T("ディスプレイ同士が接する端では反応しない", "Ignore edges shared with another display"), s.OuterEdgesOnly, 20);

            g = Group(p, L.T("タスクバー", "Taskbar"));
            FlowLayoutPanel tr = HRow(g, 0);
            taskbar = Check(tr, L.T("タスクバーの上", "Over the taskbar"), s.Taskbar, 0, L.T("タスクバーと重なる四隅・端もこの設定に従います（タスクバーを上・左・右に置いた場合も）。", "Corners and edges covered by the taskbar follow this setting, wherever the taskbar is."));
            taskbarMod = Combo(ModNames(), (int)s.TaskbarModifier, 190);
            tr.Controls.Add(taskbarMod);
            trayRow = HRow(g, 0); // 灰色のチェックボックスにはツールチップが出ないので、理由は外側の行に付ける
            tray = Check(trayRow, L.T("ScrollVolume のアイコンの上", "Over the ScrollVolume icon"), s.TrayIcon, 0, L.T("タスクバーで修飾キーが必要な設定でも、アイコンの上ではキーなしで使えます。", "Works without the key even when the taskbar needs one."));
            return tp;
        }

        TabPage BuildOperation(Settings s)
        {
            TabPage tp = new TabPage(L.T("操作", "Controls"));
            FlowLayoutPanel p = Page(tp);

            FlowLayoutPanel g = Group(p, L.T("ホイール", "Wheel"));
            List<string> stepNames = new List<string>();
            foreach (int v in VolumeMath.Steps) stepNames.Add(v + "%");
            step = Combo(stepNames.ToArray(), Array.IndexOf(VolumeMath.Steps, VolumeMath.NormalizeStep(s.Step)), 80);
            Row(g, L.T("1 目盛り:", "Per notch:"), step, 0, null, 150);
            fastSpin = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = P(200) };
            fastUser = s.FastSpin;
            RebuildFast();
            fastSpin.SelectedIndexChanged += delegate { if (!rebuildingFast) fastUser = SelectedFast(); UpdateEnabled(); };
            step.SelectedIndexChanged += delegate { RebuildFast(); UpdateEnabled(); };
            Row(g, L.T("速く回したとき:", "When spun fast:"), fastSpin, 0, null, 150);
            tips.SetToolTip(fastSpin, L.T("例: 「5% 区切りで進む」なら、ゆっくり回すと 1 目盛りずつ、速く回すと 37 → 40 → 45 → 50%。", "e.g. \"Snap to 5% steps\": turn slowly for single notches, spin fast for 37 → 40 → 45 → 50%."));
            accelStrength = new TrackBar { Minimum = 1, Maximum = 5, Value = Math.Max(1, Math.Min(5, s.AccelStrength)), Width = P(150), TickStyle = TickStyle.BottomRight, AutoSize = false, Height = P(32) };
            accelRow = Row(g, L.T("加速の強さ:", "Speed-up:"), accelStrength, 0, null, 150);
            FlowLayoutPanel ar = accelRow;
            ar.Controls.Add(RowLabel(L.T("弱 ～ 強", "low – high")));
            invert = Check(g, L.T("回転方向を逆にする", "Reverse direction"), s.Invert);

            g = Group(p, L.T("クリック", "Click"));
            FlowLayoutPanel mr = HRow(g, 0);
            mr.Controls.Add(RowLabel(L.T("ホイールクリックでミュート:", "Middle-click to mute:"), 150));
            middleMute = Check(mr, L.T("四隅・端", "Corners, edges"), s.MiddleClickMute);
            middleTaskbar = Check(mr, L.T("タスクバー", "Taskbar"), s.MiddleClickTaskbar, 0,
                L.T("タスクバーのボタンをホイールクリックして新しいウィンドウを開く Windows の操作は使えなくなります。「場所」の修飾キーの設定に従います。",
                    "Replaces Windows' middle-click on taskbar buttons (open a new window). Follows the taskbar key setting on the Areas tab."));
            leftClick = Combo(ClickNames(), (int)s.LeftClick, 230);
            middleClick = Combo(ClickNames(), (int)s.MiddleClick, 230);
            Row(g, L.T("アイコンを左クリック:", "Icon left-click:"), leftClick, 0, null, 150);
            Row(g, L.T("アイコンをホイールクリック:", "Icon middle-click:"), middleClick, 0, null, 150);

            g = Group(p, L.T("ミュート", "Mute"));
            unmuteUser = s.UnmuteOnChange;
            unmute = Check(g, L.T("音量を上げたらミュートを解除する", "Unmute when turning the volume up"), s.UnmuteOnChange || s.MuteAtZero);
            unmute.CheckedChanged += delegate { if (unmute.Enabled) unmuteUser = unmute.Checked; };
            muteZero = Check(g, L.T("0% になったらミュートする", "Mute at 0%"), s.MuteAtZero, 0, L.T("オンの間は、音量を上げると必ずミュートを解除します。", "While on, turning the volume up always unmutes."));
            return tp;
        }

        /// <summary>
        /// 1 目盛りの変化量で選べるものだけを並べる。利用者が選んだもの（fastUser）は覚えておき、
        /// 一時的に選べない変化量を経由しても、選べる変化量に戻せば元に戻る。
        /// </summary>
        void RebuildFast()
        {
            int stepValue = VolumeMath.Steps[Math.Max(0, step.SelectedIndex)];
            rebuildingFast = true;
            try
            {
                fastItems.Clear();
                fastSpin.Items.Clear();
                foreach (FastSpin f in Enum.GetValues(typeof(FastSpin)))
                {
                    if (!VolumeMath.IsAvailable(f, stepValue)) continue;
                    fastItems.Add(f);
                    fastSpin.Items.Add(FastName(f));
                }
                fastSpin.SelectedIndex = Math.Max(0, fastItems.IndexOf(VolumeMath.Fix(fastUser, stepValue)));
            }
            finally { rebuildingFast = false; }
        }

        FastSpin SelectedFast()
        {
            int i = fastSpin.SelectedIndex;
            return i >= 0 && i < fastItems.Count ? fastItems[i] : FastSpin.None;
        }

        TabPage BuildHotkeys(Settings s)
        {
            TabPage tp = new TabPage(L.T("ホットキー", "Hotkeys"));
            FlowLayoutPanel p = Page(tp);
            FlowLayoutPanel g = Group(p, L.T("ホットキー", "Hotkeys"));
            string[] names = HotkeyNames();
            int[] values = s.Hotkeys();
            hotkeys = new HotkeyBox[names.Length];
            hotkeyStatus = new Label[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                HotkeyBox box = new HotkeyBox { Width = P(170) };
                box.Value = values[i];
                box.ValueChanged += delegate
                {
                    rejectedIndex = -1;
                    // 同じキーをほかの操作に使っていたら、そちらを外す（重複したまま保存されないように）
                    if (box.Value != 0)
                        for (int j = 0; j < hotkeys.Length; j++)
                            if (j != index && hotkeys[j].Value == box.Value) hotkeys[j].Value = 0;
                    UpdateEnabled();
                };
                box.Rejected += delegate { rejectedIndex = index; UpdateEnabled(); };
                box.Enter += delegate { if (HotkeyCapture != null) HotkeyCapture(true); };
                box.Leave += delegate { if (HotkeyCapture != null) HotkeyCapture(false); };
                hotkeys[i] = box;
                FlowLayoutPanel r = Row(g, names[i], box, 0, null, 150);
                hotkeyStatus[i] = new Label { AutoSize = true, ForeColor = Color.FromArgb(196, 43, 28), Margin = new Padding(3, 6, 3, 3) };
                r.Controls.Add(hotkeyStatus[i]);
            }
            Label hint = new Label
            {
                Text = L.T("欄を選んでキーを押します。Backspace で解除。", "Click a box and press the keys. Backspace clears."),
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3)
            };
            g.Controls.Add(hint);
            return tp;
        }

        /// <summary>登録に失敗した（ほかのアプリが使っている）ホットキーを表示する。registered は登録を試した値。</summary>
        public void SetHotkeyStatus(bool[] failed, int[] registered)
        {
            for (int i = 0; i < hotkeyFailed.Length && i < failed.Length && i < registered.Length; i++)
            {
                hotkeyFailed[i] = failed[i];
                hotkeyRegistered[i] = registered[i];
            }
            UpdateEnabled();
        }

        // 入力欄にフォーカスがあるまま別のウィンドウへ移ったら、ホットキーを使えるように戻す
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (ActiveControl is HotkeyBox && HotkeyCapture != null) HotkeyCapture(true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (ActiveControl is HotkeyBox && HotkeyCapture != null) HotkeyCapture(false);
        }

        internal void SimulateActivation(bool active) { if (active) OnActivated(EventArgs.Empty); else OnDeactivate(EventArgs.Empty); }

        TabPage BuildDisplay(Settings s)
        {
            TabPage tp = new TabPage(L.T("表示", "Display"));
            FlowLayoutPanel p = Page(tp);

            FlowLayoutPanel g = Group(p, L.T("タスクトレイのアイコン", "Tray icon"));
            string[] styles = StyleNames();
            StyleRadios = new RadioButton[styles.Length];
            Previews = new PictureBox[styles.Length];
            for (int i = 0; i < styles.Length; i++)
            {
                FlowLayoutPanel r = HRow(g, 0);
                // ラジオボタンは別々のパネルに入るので自動のグループ化が効かない → 排他は自前で行う
                RadioButton rb = new RadioButton { Text = styles[i], AutoSize = false, AutoCheck = false, Checked = (int)s.Style == i, Width = P(170), Height = P(36), Margin = new Padding(3, 0, 3, 0) };
                rb.Click += (o, e) => SelectStyle((RadioButton)o);
                StyleRadios[i] = rb;
                Previews[i] = new PictureBox { Width = P(84), Height = P(36), SizeMode = PictureBoxSizeMode.CenterImage };
                Previews[i].Click += (o, e) => SelectStyle(StyleRadios[Array.IndexOf(Previews, o)]);
                tips.SetToolTip(Previews[i], L.T("右はミュート中の表示", "Right: while muted"));
                r.Controls.Add(rb);
                r.Controls.Add(Previews[i]);
            }
            textColor = Combo(ColorNames(), (int)s.TextColor, 150);
            textColor.SelectedIndexChanged += delegate { RefreshPreviews(); };
            Row(g, L.T("文字色:", "Text color:"), textColor);

            g = Group(p, Terms.VolumeBar);
            showOsd = Check(g, L.T("音量を変えたときに表示する", "Show when the volume changes"), s.ShowOsd);
            osdSeconds = new NumericUpDown { Minimum = 0.3m, Maximum = 10m, DecimalPlaces = 1, Increment = 0.1m, Width = P(60), Value = Math.Max(0.3m, Math.Min(10m, s.OsdDurationMs / 1000m)) };
            FlowLayoutPanel row = Row(g, L.T("表示時間:", "Duration:"), osdSeconds, 20, L.T("秒", "s"));
            osdTry = new Button { Text = L.T("プレビュー", "Preview"), AutoSize = true, Margin = new Padding(12, 2, 3, 3) };
            osdTry.Click += delegate { if (previewOsd != null) previewOsd((int)(osdSeconds.Value * 1000)); };
            row.Controls.Add(osdTry);
            osdHideGames = Check(g, L.T("ゲーム中は表示しない", "Hide while gaming"), s.OsdHideInGames, 20,
                L.T("全画面のゲームの上には表示しません（ブラウザーや動画プレーヤーの全画面表示では表示します）。", "Not shown over full-screen games (still shown over full-screen browsers and video players)."));

            RefreshPreviews();
            return tp;
        }

        internal void SelectStyle(RadioButton target)
        {
            foreach (RadioButton rb in StyleRadios) rb.Checked = rb == target;
        }

        TabPage BuildGeneral(Settings s, bool startupEnabled)
        {
            TabPage tp = new TabPage(L.T("全般", "General"));
            FlowLayoutPanel p = Page(tp);
            FlowLayoutPanel g = Group(p, L.T("起動", "Startup"));
            autoStart = Check(g, L.T("Windows の起動時に開始する", "Start with Windows"), startupEnabled);
            runElevated = Check(g, L.T("管理者として実行する", "Run as administrator"), s.RunElevated, 0,
                L.T("管理者として実行中のアプリ（タスク マネージャーや一部のゲーム）の上でも操作できるようにします。変更時に確認画面が表示されます。",
                    "Lets ScrollVolume work while an elevated app (Task Manager, some games) is active. Windows asks for confirmation when changed.")
                + "\n" + (ElevatedTask.IsElevated() ? L.T("現在: 管理者として実行中", "Now: running as administrator") : L.T("現在: 通常の権限で実行中", "Now: running without elevation")));

            g = Group(p, L.T("ゲーム", "Games"));
            gameGuard = Check(g, L.T("ゲーム中はマウスで操作しない", "Ignore the mouse while gaming"), s.GameGuard, 0,
                L.T("全画面のゲームの使用中は、四隅・端・タスクバーで反応しません。ブラウザーや動画プレーヤーの全画面表示では使えます。ホットキーは使えます。",
                    "While a full-screen game is in front, corners, edges and the taskbar are ignored. Full-screen browsers and video players still work. Hotkeys always work."));

            g = Group(p, L.T("言語", "Language"));
            language = Combo(new[] { L.T("自動", "Auto"), "日本語", "English" }, (int)s.Language, 150);
            Row(g, null, language); tips.SetToolTip(language, L.T("適用すると切り替わります。", "Applies when you click Apply."));

            LinkLabel open = new LinkLabel { Text = L.T("設定ファイルの場所を開く", "Open settings folder"), AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            open.LinkClicked += delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + Settings.IniPath + "\""); } catch { }
            };
            p.Controls.Add(open);
            return tp;
        }

        internal string TipOf(Control c) { return tips.GetToolTip(c); }

        internal void SetRunElevated(bool value)
        {
            runElevated.Checked = value;
        }

        // ---- 動作 ----

        void RefreshPreviews()
        {
            if (Previews == null || textColor == null) return;
            int vol = currentVolume();
            bool light = Theme.IsLightTaskbar();
            TextColorMode mode = (TextColorMode)textColor.SelectedIndex;
            bool lightBg = mode == TextColorMode.Black || (mode == TextColorMode.Auto && light);
            for (int i = 0; i < Previews.Length; i++)
            {
                Bitmap canvas = new Bitmap(84, 36);
                using (Graphics gr = Graphics.FromImage(canvas))
                {
                    gr.Clear(lightBg ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32));
                    using (Bitmap a = IconRenderer.RenderBitmap(vol, false, (IconStyle)i, mode, light, 24))
                    using (Bitmap b = IconRenderer.RenderBitmap(vol, true, (IconStyle)i, mode, light, 24))
                    {
                        gr.DrawImage(a, 10, 6, 24, 24);
                        gr.DrawImage(b, 50, 6, 24, 24);
                    }
                }
                if (Previews[i].Image != null) Previews[i].Image.Dispose();
                Previews[i].Image = canvas;
            }
        }

        internal void UpdateEnabled()
        {
            if (cTL == null || eTop == null || accelStrength == null || taskbarMod == null || trayRow == null || osdSeconds == null || muteZero == null || hotkeys == null || gameGuard == null || language == null) return;
            cTL.Enabled = cTR.Enabled = cBL.Enabled = cBR.Enabled = cornerSize.Enabled = outerCorners.Enabled = corners.Checked;
            eTop.Enabled = eBottom.Enabled = eLeft.Enabled = eRight.Enabled = edgeSize.Enabled = outerEdges.Enabled = edges.Checked;
            taskbarMod.Enabled = taskbar.Checked;
            accelStrength.Enabled = accelRow.Visible = SelectedFast() == FastSpin.Faster; // 使わないときは出さない
            fastSpin.Enabled = fastSpin.Items.Count > 1;
            // タスクバーで常に操作できるなら、アイコンの上もそこに含まれる（別に選ぶ意味がない）
            tray.Enabled = !(taskbar.Checked && taskbarMod.SelectedIndex == (int)ModifierKey.None);
            tips.SetToolTip(trayRow, tray.Enabled ? null : TrayCoveredTip);
            osdSeconds.Enabled = osdTry.Enabled = osdHideGames.Enabled = showOsd.Checked;
            middleMute.Enabled = (corners.Checked || edges.Checked);
            middleTaskbar.Enabled = taskbar.Checked; // タスクバーで操作しない設定なら、ホイールクリックも反応しない

            // 0% でミュートするなら、上げたときの解除は必ず行う（戻れなくなるのを防ぐ）
            if (muteZero.Checked) { unmute.Enabled = false; unmute.Checked = true; }
            else if (!unmute.Enabled) { unmute.Enabled = true; unmute.Checked = unmuteUser; }

            // ホットキー: 重複・登録失敗・不正な組み合わせ
            for (int i = 0; i < hotkeys.Length; i++)
            {
                string status = "";
                string tip = null;
                if (i == rejectedIndex)
                {
                    status = L.T("使えません", "Not allowed");
                    tip = L.T("文字入力やよく使う操作とぶつかる組み合わせです。Ctrl・Alt・Win と、文字キーなら Shift か Win も組み合わせてください。",
                        "This would clash with typing or common shortcuts. Use Ctrl, Alt or Win, and add Shift or Win for letter keys.");
                }
                else if (hotkeys[i].Value != 0 && hotkeyFailed[i] && hotkeyRegistered[i] == hotkeys[i].Value)
                {
                    status = L.T("使用中", "In use");
                    tip = L.T("ほかのアプリがこのキーを使っています。", "Another app is using these keys.");
                }
                hotkeyStatus[i].Text = status;
                tips.SetToolTip(hotkeyStatus[i], tip);
            }

            Settings now = Collect();
            zoneWarning.Visible = !now.AnyZoneEnabled() && !now.AnyHotkey();
        }

        /// <summary>画面の状態から設定を組み立てる。</summary>
        internal Settings Collect()
        {
            Settings s = original.Clone(); // 画面に無い項目（Debug やミキサーの状態など）は元の値を引き継ぐ
            s.Step = VolumeMath.Steps[Math.Max(0, step.SelectedIndex)];
            s.FastSpin = SelectedFast();
            s.AccelStrength = accelStrength.Value;
            s.Invert = invert.Checked;
            s.MiddleClickMute = middleMute.Checked;
            s.MiddleClickTaskbar = middleTaskbar.Checked;
            s.MuteAtZero = muteZero.Checked;
            s.UnmuteOnChange = muteZero.Checked ? unmuteUser : unmute.Checked;
            s.ShowOsd = showOsd.Checked;
            s.OsdDurationMs = (int)Math.Round(osdSeconds.Value * 1000);
            s.OsdHideInGames = osdHideGames.Checked;
            s.RunElevated = runElevated.Checked;
            s.GameGuard = gameGuard.Checked;
            s.Language = (Language)language.SelectedIndex;
            s.Corners = corners.Checked;
            s.CornerTL = cTL.Checked; s.CornerTR = cTR.Checked; s.CornerBL = cBL.Checked; s.CornerBR = cBR.Checked;
            s.CornerSize = (int)cornerSize.Value;
            s.OuterCornersOnly = outerCorners.Checked;
            s.Edges = edges.Checked;
            s.EdgeTop = eTop.Checked; s.EdgeBottom = eBottom.Checked; s.EdgeLeft = eLeft.Checked; s.EdgeRight = eRight.Checked;
            s.EdgeSize = (int)edgeSize.Value;
            s.OuterEdgesOnly = outerEdges.Checked;
            s.Taskbar = taskbar.Checked;
            s.TaskbarModifier = (ModifierKey)taskbarMod.SelectedIndex;
            s.TrayIcon = tray.Checked;
            s.LeftClick = (ClickAction)leftClick.SelectedIndex;
            s.MiddleClick = (ClickAction)middleClick.SelectedIndex;
            s.TextColor = (TextColorMode)textColor.SelectedIndex;
            for (int i = 0; i < StyleRadios.Length; i++) if (StyleRadios[i].Checked) s.Style = (IconStyle)i;
            int[] hk = new int[hotkeys.Length];
            for (int i = 0; i < hk.Length; i++) hk[i] = hotkeys[i].Value;
            s.SetHotkeys(hk);
            s.Normalize();
            return s;
        }

        void DoApply()
        {
            apply(Collect(), autoStart.Checked);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                tips.Dispose();
                if (Previews != null) foreach (PictureBox pb in Previews) if (pb.Image != null) pb.Image.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
