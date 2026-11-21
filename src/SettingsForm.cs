using System;
using System.Drawing;
using System.Windows.Forms;

namespace ScrollVolume
{
    sealed class SettingsForm : Form
    {
        readonly Action<Settings, bool> apply;
        readonly Func<int> currentVolume;
        readonly Action<int> previewOsd;
        readonly Settings original;

        internal TabControl Tabs;
        // 一般
        internal CheckBox autoStart, runElevated;
        // 操作
        internal NumericUpDown step;
        internal CheckBox accel, roundStep, unmute, muteZero, invert;
        internal TrackBar accelStrength;
        internal ComboBox leftClick, middleClick;
        // 場所
        internal CheckBox corners, cTL, cTR, cBL, cBR, outerCorners;
        internal NumericUpDown cornerSize;
        internal CheckBox edges, eTop, eBottom, eLeft, eRight, outerEdges;
        internal NumericUpDown edgeSize;
        internal CheckBox taskbar, tray;
        internal ComboBox taskbarMod;
        // 表示
        internal RadioButton[] StyleRadios;
        internal PictureBox[] Previews;
        internal ComboBox textColor;
        internal CheckBox showOsd;
        internal NumericUpDown osdSeconds;
        internal Button osdTry;

        static readonly string[] ClickNames = { "何もしない", "音量スライダーを表示", "ミュートの切り替え", "音量ミキサーを開く" };
        static readonly string[] ModNames = { "常に", "Ctrl キーを押している間のみ", "Shift キーを押している間のみ", "Alt キーを押している間のみ" };
        static readonly string[] ColorNames = { "自動（タスクバーの色に合わせる）", "白", "黒" };
        static readonly string[] StyleNames = { "数字", "数字（背景あり）", "バー" };

        public SettingsForm(Settings s, bool startupEnabled, Action<Settings, bool> onApply, Func<int> getVolume, Action<int> onPreviewOsd = null)
        {
            apply = onApply;
            currentVolume = getVolume;
            previewOsd = onPreviewOsd;
            original = s.Clone();
            Text = "ScrollVolume の設定";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.None;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            float k = DeviceDpi / 96f;
            ClientSize = new Size((int)(500 * k), (int)(560 * k));

            Tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point((int)(12 * k), (int)(4 * k)) };
            Tabs.TabPages.Add(BuildPlaces(s));
            Tabs.TabPages.Add(BuildOperation(s));
            Tabs.TabPages.Add(BuildDisplay(s));
            Tabs.TabPages.Add(BuildGeneral(s, startupEnabled));

            FlowLayoutPanel buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, Padding = new Padding((int)(8 * k))
            };
            Button cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
            Button applyBtn = new Button { Text = "適用", AutoSize = true };
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

        static FlowLayoutPanel Page(TabPage tp)
        {
            FlowLayoutPanel p = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(12, 10, 12, 10)
            };
            tp.Controls.Add(p);
            tp.UseVisualStyleBackColor = true;
            return p;
        }

        static FlowLayoutPanel Group(Control parent, string title)
        {
            GroupBox g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 4, 10, 6), Margin = new Padding(0, 0, 0, 8), MinimumSize = new Size(440, 0) };
            FlowLayoutPanel inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill };
            g.Controls.Add(inner);
            parent.Controls.Add(g);
            return inner;
        }

        CheckBox Check(Control parent, string text, bool value, int indent = 0)
        {
            CheckBox c = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(3 + indent, 2, 3, 2) };
            c.CheckedChanged += delegate { UpdateEnabled(); };
            parent.Controls.Add(c);
            return c;
        }

        static FlowLayoutPanel HRow(Control parent, int indent)
        {
            FlowLayoutPanel r = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(indent, 0, 0, 0) };
            parent.Controls.Add(r);
            return r;
        }

        static FlowLayoutPanel Row(Control parent, string label, Control ctl, int indent = 0, string suffix = null)
        {
            FlowLayoutPanel r = HRow(parent, indent);
            if (label != null) r.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            r.Controls.Add(ctl);
            if (suffix != null) r.Controls.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
            return r;
        }

        static ComboBox Combo(string[] items, int index, int width)
        {
            ComboBox c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
            c.Items.AddRange(items);
            c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, index));
            return c;
        }

        static NumericUpDown Num(int min, int max, int value)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = 60 };
        }

        static Label Note(Control parent, string text, int indent = 0)
        {
            Label l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3 + indent, 2, 3, 4), MaximumSize = new Size(420, 0) };
            parent.Controls.Add(l);
            return l;
        }

        // ---- ページ ----

        TabPage BuildPlaces(Settings s)
        {
            TabPage tp = new TabPage("対象エリア");
            FlowLayoutPanel p = Page(tp);
            Note(p, "ここで選んだ場所でマウスホイールを回すと、スクロールの代わりに音量を調整します。");

            FlowLayoutPanel g = Group(p, "画面の四隅");
            corners = Check(g, "有効にする", s.Corners);
            FlowLayoutPanel r = HRow(g, 20);
            cTL = Check(r, "左上", s.CornerTL); cTR = Check(r, "右上", s.CornerTR);
            cBL = Check(r, "左下", s.CornerBL); cBR = Check(r, "右下", s.CornerBR);
            cornerSize = Num(2, 200, s.CornerSize);
            Row(g, "反応範囲: 隅から", cornerSize, 20, "ピクセル");
            outerCorners = Check(g, "ディスプレイ同士が接する隅では反応しない", s.OuterCornersOnly, 20);

            g = Group(p, "画面の端");
            edges = Check(g, "有効にする", s.Edges);
            r = HRow(g, 20);
            eTop = Check(r, "上", s.EdgeTop); eBottom = Check(r, "下", s.EdgeBottom);
            eLeft = Check(r, "左", s.EdgeLeft); eRight = Check(r, "右", s.EdgeRight);
            edgeSize = Num(1, 50, s.EdgeSize);
            Row(g, "反応範囲: 端から", edgeSize, 20, "ピクセル");
            outerEdges = Check(g, "ディスプレイ同士が接する端では反応しない", s.OuterEdgesOnly, 20);
            Note(g, "反応範囲を広くすると、ウィンドウ端のスクロールバーの上などでも音量が変わることがあります。", 20);

            g = Group(p, "その他");
            taskbar = Check(g, "タスクバーの上", s.Taskbar);
            taskbarMod = Combo(ModNames, (int)s.TaskbarModifier, 220);
            Row(g, null, taskbarMod, 20);
            tray = Check(g, "タスクトレイの ScrollVolume アイコンの上", s.TrayIcon);
            return tp;
        }

        TabPage BuildOperation(Settings s)
        {
            TabPage tp = new TabPage("操作");
            FlowLayoutPanel p = Page(tp);

            FlowLayoutPanel g = Group(p, "マウスホイール");
            step = Num(1, 20, s.Step);
            Row(g, "1 目盛りあたりの音量の変化:", step, 0, "%");
            accel = Check(g, "速く回したときは変化を大きくする（加速）", s.Accel);
            accelStrength = new TrackBar { Minimum = 1, Maximum = 5, Value = Math.Max(1, Math.Min(5, s.AccelStrength)), Width = 160, TickStyle = TickStyle.BottomRight, AutoSize = false, Height = 32 };
            Row(g, "加速の強さ:  弱", accelStrength, 20, "強");
            roundStep = Check(g, "変化量の倍数にそろえる（例: 5% 単位なら 37% → 40%）", s.RoundStep);
            invert = Check(g, "回転方向を逆にする（下に回すと音量が上がる）", s.Invert);

            g = Group(p, "ミュート");
            unmute = Check(g, "音量を上げたときにミュートを解除する", s.UnmuteOnChange);
            muteZero = Check(g, "音量が 0% になったらミュートする", s.MuteAtZero);

            g = Group(p, "タスクトレイのアイコンをクリックしたときの動作");
            leftClick = Combo(ClickNames, (int)s.LeftClick, 200);
            middleClick = Combo(ClickNames, (int)s.MiddleClick, 200);
            Row(g, "左クリック:", leftClick);
            Row(g, "ホイールクリック:", middleClick);
            return tp;
        }

        TabPage BuildDisplay(Settings s)
        {
            TabPage tp = new TabPage("表示");
            FlowLayoutPanel p = Page(tp);

            FlowLayoutPanel g = Group(p, "タスクトレイのアイコン");
            StyleRadios = new RadioButton[StyleNames.Length];
            Previews = new PictureBox[StyleNames.Length];
            for (int i = 0; i < StyleNames.Length; i++)
            {
                FlowLayoutPanel r = HRow(g, 0);
                // ラジオボタンは別々のパネルに入るので自動のグループ化が効かない → 排他は自前で行う
                RadioButton rb = new RadioButton { Text = StyleNames[i], AutoSize = false, AutoCheck = false, Checked = (int)s.Style == i, Width = 170, Height = 36, Margin = new Padding(3, 0, 3, 0) };
                rb.Click += (o, e) => SelectStyle((RadioButton)o);
                StyleRadios[i] = rb;
                Previews[i] = new PictureBox { Width = 84, Height = 36 };
                Previews[i].Click += (o, e) => SelectStyle(StyleRadios[Array.IndexOf(Previews, o)]);
                r.Controls.Add(rb);
                r.Controls.Add(Previews[i]);
            }
            textColor = Combo(ColorNames, (int)s.TextColor, 220);
            textColor.SelectedIndexChanged += delegate { RefreshPreviews(); };
            Row(g, "文字色:", textColor);
            Note(g, "各プレビューの右側は、ミュート時の表示です。");

            g = Group(p, "音量バー（音量を変えたときに画面に表示）");
            showOsd = Check(g, "表示する", s.ShowOsd);
            osdSeconds = new NumericUpDown { Minimum = 0.3m, Maximum = 10m, DecimalPlaces = 1, Increment = 0.1m, Width = 60, Value = Math.Max(0.3m, Math.Min(10m, s.OsdDurationMs / 1000m)) };
            FlowLayoutPanel row = Row(g, "表示時間:", osdSeconds, 20, "秒");
            osdTry = new Button { Text = "プレビュー", AutoSize = true, Margin = new Padding(12, 2, 3, 3) };
            osdTry.Click += delegate { if (previewOsd != null) previewOsd((int)(osdSeconds.Value * 1000)); };
            row.Controls.Add(osdTry);

            RefreshPreviews();
            return tp;
        }

        internal void SelectStyle(RadioButton target)
        {
            foreach (RadioButton rb in StyleRadios) rb.Checked = rb == target;
        }

        TabPage BuildGeneral(Settings s, bool startupEnabled)
        {
            TabPage tp = new TabPage("全般");
            FlowLayoutPanel p = Page(tp);
            FlowLayoutPanel g = Group(p, "起動");
            autoStart = Check(g, "Windows の起動時に自動的に開始する", startupEnabled);
            runElevated = Check(g, "管理者として実行する", s.RunElevated);
            Note(g, "タスク マネージャーなど、管理者として実行中のアプリを操作しているときでも音量を調整できるようになります。設定を変更するときに、ユーザー アカウント制御の確認画面が表示されます。", 20);
            Note(g, ElevatedTask.IsElevated() ? "現在の状態: 管理者として実行中" : "現在の状態: 通常の権限で実行中", 20);

            Note(p, "設定ファイル: " + Settings.IniPath);
            return tp;
        }

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

        void UpdateEnabled()
        {
            if (cTL == null || eTop == null || accelStrength == null || taskbarMod == null || osdSeconds == null) return;
            cTL.Enabled = cTR.Enabled = cBL.Enabled = cBR.Enabled = cornerSize.Enabled = outerCorners.Enabled = corners.Checked;
            eTop.Enabled = eBottom.Enabled = eLeft.Enabled = eRight.Enabled = edgeSize.Enabled = outerEdges.Enabled = edges.Checked;
            taskbarMod.Enabled = taskbar.Checked;
            accelStrength.Enabled = accel.Checked;
            osdSeconds.Enabled = osdTry.Enabled = showOsd.Checked;
        }

        /// <summary>画面の状態から設定を組み立てる。</summary>
        internal Settings Collect()
        {
            Settings s = original.Clone(); // 画面に無い項目（Debug など）は元の値を引き継ぐ
            s.Step = (int)step.Value;
            s.Accel = accel.Checked;
            s.AccelStrength = accelStrength.Value;
            s.RoundStep = roundStep.Checked;
            s.UnmuteOnChange = unmute.Checked;
            s.MuteAtZero = muteZero.Checked;
            s.Invert = invert.Checked;
            s.ShowOsd = showOsd.Checked;
            s.OsdDurationMs = (int)Math.Round(osdSeconds.Value * 1000);
            s.RunElevated = runElevated.Checked;
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
            s.Normalize();
            return s;
        }

        void DoApply()
        {
            apply(Collect(), autoStart.Checked);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Previews != null)
                foreach (PictureBox pb in Previews) if (pb.Image != null) pb.Image.Dispose();
            base.Dispose(disposing);
        }
    }
}
