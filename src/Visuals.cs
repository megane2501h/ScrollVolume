using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScrollVolume
{
    static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(0, 120, 212);
        public static readonly Color MuteRed = Color.FromArgb(232, 72, 72);

        public static bool IsLightTaskbar()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        public static float ScaleAt(Point p)
        {
            try
            {
                IntPtr mon = Native.MonitorFromPoint(new POINT(p.X, p.Y), Native.MONITOR_DEFAULTTONEAREST);
                uint dx, dy;
                if (Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0) return dx / 96f;
            }
            catch { }
            return 1f;
        }
    }

    static class IconRenderer
    {
        public static Bitmap RenderBitmap(int percent, bool muted, IconStyle style, TextColorMode colorMode, bool lightTaskbar, int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                bool light = colorMode == TextColorMode.Black || (colorMode == TextColorMode.Auto && lightTaskbar);
                Color fg = light ? Color.FromArgb(20, 20, 20) : Color.White;
                string text = percent >= 0 ? percent.ToString() : "--";

                switch (style)
                {
                    case IconStyle.NumberBox:
                    {
                        Color bg = muted ? Theme.MuteRed : Theme.Accent;
                        using (GraphicsPath rr = RoundRect(new RectangleF(0, 0, size, size), size * 0.22f))
                        using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, rr);
                        DrawNumber(g, text, Color.White, size, 0.64f, 0.92f);
                        break;
                    }
                    case IconStyle.Bar:
                    {
                        Color c = muted ? Theme.MuteRed : fg;
                        float w = size * 0.5f, x = (size - w) / 2f, pad = 1f;
                        using (Pen p = new Pen(c, Math.Max(1f, size / 16f)))
                            g.DrawRectangle(p, x, pad, w, size - pad * 2 - 1);
                        float inner = size - pad * 2 - 3;
                        float h = percent > 0 ? Math.Max(1f, inner * percent / 100f) : 0f;
                        using (SolidBrush b = new SolidBrush(c))
                            g.FillRectangle(b, x + 1.5f, pad + 1.5f + inner - h, w - 2.5f, h);
                        break;
                    }
                    default:
                        DrawNumber(g, text, muted ? Theme.MuteRed : fg, size, 0.80f, 1f);
                        break;
                }
            }
            return bmp;
        }

        static void DrawNumber(Graphics g, string text, Color color, int size, float heightRatio, float widthRatio)
        {
            using (GraphicsPath path = new GraphicsPath())
            using (GraphicsPath refPath = new GraphicsPath())
            using (FontFamily ff = new FontFamily("Segoe UI"))
            {
                path.AddString(text, ff, (int)FontStyle.Bold, 100f, Point.Empty, StringFormat.GenericTypographic);
                refPath.AddString("0", ff, (int)FontStyle.Bold, 100f, Point.Empty, StringFormat.GenericTypographic);
                RectangleF b = path.GetBounds();
                RectangleF rb = refPath.GetBounds(); // 数字の高さを基準にする（"--" が潰れないように）
                if (b.Width <= 0 || rb.Height <= 0) return;
                float maxW = size * widthRatio, maxH = size * heightRatio;
                float sy = maxH / rb.Height;
                float sx = Math.Min(sy, maxW / b.Width); // 3桁は横だけ詰める
                float w = b.Width * sx;
                using (Matrix m = new Matrix())
                {
                    m.Translate((size - w) / 2f, (size - maxH) / 2f);
                    m.Scale(sx, sy);
                    m.Translate(-b.X, -rb.Y);
                    path.Transform(m);
                }
                using (SolidBrush br = new SolidBrush(color)) g.FillPath(br, path);
            }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>HICON を自前で破棄した Icon を返す（ハンドルリーク防止）。</summary>
        public static Icon Render(int percent, bool muted, IconStyle style, TextColorMode colorMode, bool lightTaskbar, int size)
        {
            using (Bitmap bmp = RenderBitmap(percent, muted, style, colorMode, lightTaskbar, size))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }
    }

    /// <summary>音量変更時に出す小さな表示。フォーカスを奪わず、クリックも透過する。</summary>
    sealed class Osd : Form
    {
        int percent;
        bool muted;
        float scale = 1f;
        readonly Timer hideTimer = new Timer();

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 32, 32);
            DoubleBuffered = true;
            Opacity = 0.94;
            hideTimer.Interval = 1200;
            hideTimer.Tick += delegate { hideTimer.Stop(); Native.ShowWindow(Handle, Native.SW_HIDE); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000020 /*TRANSPARENT*/ | 0x00000008 /*TOPMOST*/;
                return cp;
            }
        }

        public void ShowAt(Point cursor, int zone, int pct, bool isMuted, int durationMs)
        {
            hideTimer.Interval = Math.Max(100, durationMs);
            percent = pct;
            muted = isMuted;
            scale = Theme.ScaleAt(cursor);
            Rectangle b = ComputeBounds(cursor, zone, Screen.FromPoint(cursor).WorkingArea, scale);
            IntPtr hwnd = Handle; // ハンドルを確実に作る
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Invalidate();
            hideTimer.Stop();
            hideTimer.Start();
        }

        /// <summary>OSD の位置。隅・辺ならその近く、それ以外はカーソル付近。必ず作業領域の内側に収める。</summary>
        public static Rectangle ComputeBounds(Point cursor, int zone, Rectangle wa, float scale)
        {
            int w = (int)(240 * scale), h = (int)(48 * scale), margin = (int)(20 * scale);
            int minX = wa.Left + margin, maxX = wa.Right - w - margin;
            int minY = wa.Top + margin, maxY = wa.Bottom - h - margin;
            int cx = cursor.X - w / 2, cy = cursor.Y - h / 2;
            int x, y;
            switch (zone)
            {
                case WheelHook.TL: x = minX; y = minY; break;
                case WheelHook.TR: x = maxX; y = minY; break;
                case WheelHook.BL: x = minX; y = maxY; break;
                case WheelHook.BR: x = maxX; y = maxY; break;
                case WheelHook.TOP: x = cx; y = minY; break;
                case WheelHook.BOTTOM: x = cx; y = maxY; break;
                case WheelHook.LEFT: x = minX; y = cy; break;
                case WheelHook.RIGHT: x = maxX; y = cy; break;
                default: // タスクバー・トレイ上
                    x = cx;
                    y = cursor.Y >= wa.Bottom ? maxY : (cursor.Y < wa.Top ? minY : cursor.Y - h - margin);
                    break;
            }
            // 画面が極端に小さくても左上を優先して収める
            x = Math.Max(wa.Left, Math.Min(Math.Max(minX, Math.Min(maxX, x)), wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(Math.Max(minY, Math.Min(maxY, y)), wa.Bottom - h));
            return new Rectangle(x, y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;
            using (Pen border = new Pen(Color.FromArgb(70, 70, 70))) g.DrawRectangle(border, 0, 0, w - 1, h - 1);

            Color accent = muted ? Theme.MuteRed : Color.FromArgb(76, 160, 255);
            float pad = 14 * scale, textW = 70 * scale;
            using (Font f = new Font("Segoe UI", 20 * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font fs = new Font("Yu Gothic UI", 13 * scale, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush tb = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(muted ? "ミュート" : percent.ToString(), muted ? fs : f, tb, new RectangleF(w - pad - textW, 0, textW, h), sf);
            }

            float barX = pad, barW = w - pad * 3 - textW, barH = 6 * scale, barY = (h - barH) / 2f;
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(80, 80, 80))) g.FillRectangle(bg, barX, barY, barW, barH);
            using (SolidBrush fg = new SolidBrush(accent)) g.FillRectangle(fg, barX, barY, barW * percent / 100f, barH);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hideTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>トレイアイコンをクリックすると出る音量スライダー。</summary>
    sealed class VolumePopup : Form
    {
        readonly TrackBar bar = new TrackBar();
        readonly Label pctLabel = new Label();
        readonly CheckBox muteBox = new CheckBox();
        bool updating;
        public int LastHiddenTick;

        public event Action<int> VolumeChanged;
        public event Action<bool> MuteChanged;
        public event Action OpenMixer;

        public VolumePopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Font = SystemFonts.MessageBoxFont;
            BackColor = Color.FromArgb(43, 43, 43);
            ForeColor = Color.White;
            Padding = new Padding(1);

            bar.Minimum = 0; bar.Maximum = 100;
            bar.TickFrequency = 10; bar.SmallChange = 1; bar.LargeChange = 10;
            bar.TickStyle = TickStyle.None;
            bar.AutoSize = false;
            bar.ValueChanged += delegate
            {
                pctLabel.Text = bar.Value.ToString();
                if (!updating && VolumeChanged != null) VolumeChanged(bar.Value);
            };

            pctLabel.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            pctLabel.TextAlign = ContentAlignment.MiddleRight;
            pctLabel.AutoSize = false;

            muteBox.Text = "ミュート";
            muteBox.AutoSize = true;
            muteBox.CheckedChanged += delegate { if (!updating && MuteChanged != null) MuteChanged(muteBox.Checked); };

            LinkLabel mixer = new LinkLabel();
            mixer.Text = "音量ミキサー";
            mixer.AutoSize = true;
            mixer.LinkColor = Color.FromArgb(120, 180, 255);
            mixer.ActiveLinkColor = mixer.LinkColor;
            mixer.LinkClicked += delegate { Hide(); if (OpenMixer != null) OpenMixer(); };

            Controls.Add(bar); Controls.Add(pctLabel); Controls.Add(muteBox); Controls.Add(mixer);

            float s = DeviceDpi / 96f;
            ClientSize = new Size((int)(300 * s), (int)(84 * s));
            bar.SetBounds((int)(10 * s), (int)(10 * s), (int)(220 * s), (int)(34 * s));
            pctLabel.SetBounds((int)(230 * s), (int)(6 * s), (int)(60 * s), (int)(36 * s));
            muteBox.Location = new Point((int)(14 * s), (int)(52 * s));
            mixer.Location = new Point((int)(200 * s), (int)(54 * s));

            Deactivate += delegate { HidePopup(); };
            KeyDown += (o, e) => { if (e.KeyCode == Keys.Escape) HidePopup(); };
        }

        void HidePopup()
        {
            if (!Visible) return;
            Hide();
            LastHiddenTick = Environment.TickCount;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Color.FromArgb(80, 80, 80))) e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        public void SetState(int pct, bool muted)
        {
            if (bar.Capture) return; // ドラッグ中は上書きしない
            updating = true;
            try
            {
                if (pct >= 0) bar.Value = Math.Max(0, Math.Min(100, pct));
                muteBox.Checked = muted;
            }
            finally { updating = false; }
        }

        /// <summary>アンカー（トレイアイコン）の近くで、作業領域の内側に収まる位置。</summary>
        public static Point ComputeLocation(Point c, Size size, Rectangle wa)
        {
            int x = c.X - size.Width / 2;
            int y = c.Y >= wa.Bottom ? wa.Bottom - size.Height - 8 : (c.Y < wa.Top ? wa.Top + 8 : c.Y - size.Height - 16);
            x = Math.Max(wa.Left, Math.Min(Math.Max(wa.Left + 8, Math.Min(wa.Right - size.Width - 8, x)), wa.Right - size.Width));
            y = Math.Max(wa.Top, Math.Min(Math.Max(wa.Top + 8, Math.Min(wa.Bottom - size.Height - 8, y)), wa.Bottom - size.Height));
            return new Point(x, y);
        }

        public void ShowNear(Rectangle anchor, int pct, bool muted)
        {
            SetState(pct, muted);
            Point c = anchor.IsEmpty ? Cursor.Position : new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2);
            Location = ComputeLocation(c, Size, Screen.FromPoint(c).WorkingArea);
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
            bar.Focus();
        }
    }
}
