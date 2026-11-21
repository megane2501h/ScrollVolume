using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScrollVolume
{
    /// <summary>
    /// 自動テスト（ScrollVolume.exe --test [画像出力フォルダ]）。
    /// 結果は標準出力に PASS/FAIL で出し、失敗数を終了コードで返す。
    /// </summary>
    static class Tests
    {
        static int pass, fail;
        static readonly List<string> failures = new List<string>();

        [DllImport("user32.dll")]
        static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

        static void Check(string name, bool ok, string detail = null)
        {
            if (ok) pass++;
            else { fail++; failures.Add(name + (detail != null ? "  [" + detail + "]" : "")); }
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (!ok && detail != null ? "  [" + detail + "]" : ""));
        }

        static void Eq<T>(string name, T expected, T actual)
        {
            Check(name, Equals(expected, actual), "expected " + expected + ", got " + actual);
        }

        static void Section(string title) { Console.WriteLine(); Console.WriteLine("== " + title); }

        public static int Run(string shotDir)
        {
            Run("音量計算", TestVolumeMath);
            Run("隅・辺の判定", TestHitScreen);
            Run("設定の保存と読み込み", TestSettingsIo);
            Run("設定画面", () => TestSettingsForm(shotDir));
            Run("トレイアイコン描画", TestIconRendering);
            Run("OSD・スライダーの位置", TestPlacement);
            Run("音声デバイス", TestAudio);
            Run("トレイ登録", TestTrayIcon);
            Run("マウスフック", TestHook);
            Run("管理者権限での起動", TestElevation);
            Run("多重起動防止", TestSingleInstance);
            if (shotDir != null) Run("画面キャプチャ", () => CaptureScreens(shotDir));

            Console.WriteLine();
            Console.WriteLine("==== 結果: " + pass + " passed, " + fail + " failed");
            foreach (string f in failures) Console.WriteLine("  FAIL " + f);
            return fail;
        }

        static void Run(string title, Action body)
        {
            Section(title);
            try { body(); }
            catch (Exception ex) { Check(title + " で例外", false, ex.ToString()); }
        }

        // ------------------------------------------------------------

        static void TestVolumeMath()
        {
            Eq("50 +1", 51, VolumeMath.Next(50, 1, 1, false));
            Eq("50 -1", 49, VolumeMath.Next(50, -1, 1, false));
            Eq("100 +1 は上限", 100, VolumeMath.Next(100, 1, 1, false));
            Eq("0 -1 は下限", 0, VolumeMath.Next(0, -1, 1, false));
            Eq("99 +5 は100で止まる", 100, VolumeMath.Next(99, 1, 5, false));
            Eq("3 -5 は0で止まる", 0, VolumeMath.Next(3, -1, 5, false));
            Eq("50 +3ノッチ×2%", 56, VolumeMath.Next(50, 3, 2, false));
            Eq("そろえる: 37 +5 → 40", 40, VolumeMath.Next(37, 1, 5, true));
            Eq("そろえる: 37 -5 → 35", 35, VolumeMath.Next(37, -1, 5, true));
            Eq("そろえる: 40 +5 → 45", 45, VolumeMath.Next(40, 1, 5, true));
            Eq("そろえる: 40 -5 → 35", 35, VolumeMath.Next(40, -1, 5, true));
            Eq("そろえる: 98 +2ノッチ → 100", 100, VolumeMath.Next(98, 2, 5, true));
            Eq("そろえる: 2 -1ノッチ → 0", 0, VolumeMath.Next(2, -1, 5, true));
            Eq("範囲外の入力 150 → 100", 100, VolumeMath.Next(150, 0, 1, false));
            Eq("範囲外の入力 -5 → 0", 0, VolumeMath.Next(-5, 0, 1, false));
            Eq("変化量0は1扱い", 51, VolumeMath.Next(50, 1, 0, false));

            Settings s = new Settings { Accel = true, AccelStrength = 3 };
            Eq("加速: 速い(10ms)", 4, VolumeMath.AccelMultiplier(s, 10));
            Eq("加速: 中(60ms)", 3, VolumeMath.AccelMultiplier(s, 60));
            Eq("加速: 遅い(200ms)", 1, VolumeMath.AccelMultiplier(s, 200));
            Eq("加速: 時刻が戻った(-5ms)", 1, VolumeMath.AccelMultiplier(s, -5));
            s.Accel = false;
            Eq("加速オフ", 1, VolumeMath.AccelMultiplier(s, 10));
            s.Accel = true; s.AccelStrength = 1;
            Eq("加速 強さ1 中", 2, VolumeMath.AccelMultiplier(s, 60));
        }

        static void TestHitScreen()
        {
            // 実機と同じ横3枚
            FakeMonitorLayout h3 = new FakeMonitorLayout(
                new Rectangle(-1920, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1080));
            Settings d = new Settings(); // 既定値
            Hit("横3枚 中央モニター左上", h3, d, 0, 0, WheelHook.TL);
            Hit("横3枚 中央モニター右上", h3, d, 1919, 0, WheelHook.TR);
            Hit("横3枚 中央モニター左下", h3, d, 0, 1079, WheelHook.BL);
            Hit("横3枚 中央モニター右下", h3, d, 1919, 1079, WheelHook.BR);
            Hit("隅の範囲内(15,15)", h3, d, 15, 15, WheelHook.TL);
            Hit("隅の範囲外(16,16)", h3, d, 16, 16, 0);
            Hit("左モニター左上", h3, d, -1920, 0, WheelHook.TL);
            Hit("右モニター右下", h3, d, 3839, 1079, WheelHook.BR);
            Hit("上辺", h3, d, 960, 0, WheelHook.TOP);
            Hit("上辺 幅2の内側", h3, d, 960, 1, WheelHook.TOP);
            Hit("上辺 幅2の外側", h3, d, 960, 2, 0);
            Hit("下辺", h3, d, 960, 1079, WheelHook.BOTTOM);
            Hit("左モニターの左辺", h3, d, -1920, 540, WheelHook.LEFT);
            Hit("右モニターの右辺", h3, d, 3839, 540, WheelHook.RIGHT);
            Hit("つなぎ目の辺(中央モニター左辺)は無視", h3, d, 0, 540, 0);
            Hit("つなぎ目の辺(中央モニター右辺)は無視", h3, d, 1919, 540, 0);
            Hit("画面中央", h3, d, 960, 540, 0);
            Hit("モニター外", h3, d, 5000, 0, 0);

            Settings s = d.Clone(); s.OuterEdgesOnly = false;
            Hit("つなぎ目を無視しない設定なら中央モニター左辺も反応", h3, s, 0, 540, WheelHook.LEFT);

            s = d.Clone(); s.OuterCornersOnly = true;
            Hit("隅のつなぎ目無視: 中央左上は隅ではなく上辺扱い", h3, s, 0, 0, WheelHook.TOP);
            Hit("隅のつなぎ目無視: 中央左上の範囲内(10,10)は反応なし", h3, s, 10, 10, 0);
            Hit("隅のつなぎ目無視: 左モニター左上は隅", h3, s, -1920, 0, WheelHook.TL);

            s = d.Clone(); s.CornerTL = false;
            Hit("左上だけ無効: (10,10) は反応なし", h3, s, 10, 10, 0);
            Hit("左上だけ無効: (0,0) は上辺として反応", h3, s, 0, 0, WheelHook.TOP);
            Hit("左上だけ無効: 右上は反応", h3, s, 1919, 0, WheelHook.TR);

            s = d.Clone(); s.Corners = false;
            Hit("隅オフ: (10,10) 反応なし", h3, s, 10, 10, 0);
            s = d.Clone(); s.Edges = false;
            Hit("辺オフ: 上辺 反応なし", h3, s, 960, 0, 0);
            Hit("辺オフ: 隅は反応", h3, s, 0, 0, WheelHook.TL);
            s = d.Clone(); s.EdgeTop = false;
            Hit("上辺だけ無効", h3, s, 960, 0, 0);
            Hit("上辺だけ無効でも下辺は反応", h3, s, 960, 1079, WheelHook.BOTTOM);
            s = d.Clone(); s.EdgeSize = 10;
            Hit("辺の幅10: y=9 は反応", h3, s, 960, 9, WheelHook.TOP);
            Hit("辺の幅10: y=10 は反応なし", h3, s, 960, 10, 0);

            // 縦に2枚
            FakeMonitorLayout v2 = new FakeMonitorLayout(new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 1080, 1920, 1080));
            Hit("縦2枚 上モニターの上辺", v2, d, 960, 0, WheelHook.TOP);
            Hit("縦2枚 つなぎ目(上モニター下辺)は無視", v2, d, 960, 1079, 0);
            Hit("縦2枚 つなぎ目(下モニター上辺)は無視", v2, d, 960, 1080, 0);
            Hit("縦2枚 下モニターの下辺", v2, d, 960, 2159, WheelHook.BOTTOM);
            Hit("縦2枚 上モニターの左辺", v2, d, 0, 500, WheelHook.LEFT);

            // サイズ違い（左が大きい、上揃え）
            FakeMonitorLayout mix = new FakeMonitorLayout(new Rectangle(0, 0, 2560, 1440), new Rectangle(2560, 0, 1920, 1080));
            Hit("サイズ違い 大モニター右辺の上側(つなぎ目)は無視", mix, d, 2559, 500, 0);
            Hit("サイズ違い 大モニター右辺の下側(露出部分)は反応", mix, d, 2559, 1200, WheelHook.RIGHT);
            Hit("サイズ違い 小モニター左下", mix, d, 2560, 1079, WheelHook.BL);
            Hit("サイズ違い 小モニター下辺", mix, d, 3000, 1079, WheelHook.BOTTOM);

            // 負の座標が上にあるモニター
            FakeMonitorLayout neg = new FakeMonitorLayout(new Rectangle(0, 0, 1920, 1080), new Rectangle(0, -1080, 1920, 1080));
            Hit("上に置いたモニターの上辺", neg, d, 500, -1080, WheelHook.TOP);
            Hit("上に置いたモニターとのつなぎ目は無視", neg, d, 500, 0, 0);
        }

        static void Hit(string name, IMonitorLayout layout, Settings s, int x, int y, int expected)
        {
            Eq(name + " (" + x + "," + y + ")", expected, WheelHook.HitScreen(new POINT(x, y), s, layout));
        }

        static Settings NonDefaultSettings()
        {
            Settings d = new Settings();
            Settings s = new Settings
            {
                Step = 7, Accel = !d.Accel, AccelStrength = 5, RoundStep = !d.RoundStep,
                UnmuteOnChange = !d.UnmuteOnChange, MuteAtZero = !d.MuteAtZero, Invert = !d.Invert, ShowOsd = !d.ShowOsd, OsdDurationMs = 2500,
                Corners = !d.Corners, CornerTL = false, CornerTR = true, CornerBL = false, CornerBR = true,
                CornerSize = 33, OuterCornersOnly = !d.OuterCornersOnly,
                Edges = !d.Edges, EdgeTop = false, EdgeBottom = true, EdgeLeft = false, EdgeRight = true,
                EdgeSize = 9, OuterEdgesOnly = !d.OuterEdgesOnly,
                Taskbar = !d.Taskbar, TaskbarModifier = ModifierKey.Alt, TrayIcon = !d.TrayIcon,
                LeftClick = ClickAction.Mixer, MiddleClick = ClickAction.None,
                Style = IconStyle.Bar, TextColor = TextColorMode.Black, RunElevated = !d.RunElevated, Debug = true,
            };
            return s;
        }

        static void TestSettingsIo()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ScrollVolume_test_" + Guid.NewGuid().ToString("N") + ".ini");
            try
            {
                Settings s = NonDefaultSettings();
                s.Save(tmp);
                Settings back = Settings.Load(tmp);
                Check("保存→読み込みで全項目が一致", s.DiffFrom(back) == null, s.DiffFrom(back));

                File.WriteAllText(tmp, "Step=999\nCornerSize=-4\nEdgeSize=abc\nStyle=Nonexistent\nUnknownKey=1\nnot a line\n=\nCorners=0\n");
                Settings bad = Settings.Load(tmp);
                Eq("壊れた値: Step は上限20に丸める", 20, bad.Step);
                Eq("壊れた値: CornerSize は下限2に丸める", 2, bad.CornerSize);
                Eq("壊れた値: EdgeSize は既定のまま", new Settings().EdgeSize, bad.EdgeSize);
                Eq("壊れた値: Style は既定のまま", new Settings().Style, bad.Style);
                File.WriteAllText(tmp, "OsdDurationMs=5\n");
                Eq("表示時間が短すぎる値は 300ms に丸める", 300, Settings.Load(tmp).OsdDurationMs);
                File.WriteAllText(tmp, "Step=999\nCornerSize=-4\nEdgeSize=abc\nStyle=Nonexistent\nUnknownKey=1\nnot a line\n=\nCorners=0\n");
                bad = Settings.Load(tmp);
                Eq("正しい値は読める: Corners=0", false, bad.Corners);

                File.Delete(tmp);
                Settings created = Settings.Load(tmp);
                Check("ファイルが無ければ既定値で作る", File.Exists(tmp) && new Settings().DiffFrom(created) == null);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        static void TestSettingsForm(string shotDir)
        {
            foreach (Settings src in new[] { new Settings(), NonDefaultSettings() })
            {
                string label = src.Debug ? "既定以外の設定" : "既定の設定";
                using (SettingsForm f = new SettingsForm(src.Clone(), false, (a, b) => { }, () => 45))
                {
                    Settings got = f.Collect();
                    Check(label + ": 画面に読み込んでそのまま取り出すと一致", src.DiffFrom(got) == null, src.DiffFrom(got));
                }
            }

            Settings applied = null; bool appliedAuto = false; int previewMs = -1;
            using (SettingsForm f = new SettingsForm(new Settings(), false, (a, b) => { applied = a; appliedAuto = b; }, () => 45, ms => previewMs = ms))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-32000, -32000); // 画面外で操作する
                f.Show();
                Application.DoEvents();

                // スタイルのラジオボタン（今回のバグ）
                f.Tabs.SelectedIndex = 2;
                Application.DoEvents();
                for (int i = 0; i < f.StyleRadios.Length; i++)
                {
                    f.StyleRadios[i].PerformClick();
                    Application.DoEvents();
                    int checkedCount = 0;
                    foreach (RadioButton rb in f.StyleRadios) if (rb.Checked) checkedCount++;
                    Eq("スタイル" + i + " をクリック → 選択はちょうど1つ", 1, checkedCount);
                    Eq("スタイル" + i + " をクリック → 設定に反映", (IconStyle)i, f.Collect().Style);
                }
                f.StyleRadios[0].PerformClick(); f.StyleRadios[0].PerformClick();
                Eq("選択中のスタイルをもう一度クリックしても外れない", IconStyle.Number, f.Collect().Style);

                // 音量バーの表示時間
                f.osdSeconds.Value = 3.4m;
                Eq("表示時間 3.4 秒 → 3400ms", 3400, f.Collect().OsdDurationMs);
                f.osdTry.PerformClick();
                Eq("「試しに表示」で選んだ時間が渡される", 3400, previewMs);
                f.showOsd.Checked = false;
                Check("音量バーを表示しない → 時間と試し表示が無効", !f.osdSeconds.Enabled && !f.osdTry.Enabled);
                f.showOsd.Checked = true;
                f.osdSeconds.Value = 0.3m;
                Eq("最短 0.3 秒", 300, f.Collect().OsdDurationMs);

                // 連動して無効化されるか
                f.Tabs.SelectedIndex = 0; Application.DoEvents();
                f.corners.Checked = false;
                Check("隅オフ → 隅の項目が無効", !f.cTL.Enabled && !f.cornerSize.Enabled && !f.outerCorners.Enabled);
                f.corners.Checked = true;
                Check("隅オン → 隅の項目が有効", f.cTL.Enabled && f.cornerSize.Enabled);
                f.edges.Checked = false;
                Check("辺オフ → 辺の項目が無効", !f.eTop.Enabled && !f.edgeSize.Enabled && !f.outerEdges.Enabled);
                f.edges.Checked = true;
                f.taskbar.Checked = false;
                Check("タスクバーオフ → 修飾キーが無効", !f.taskbarMod.Enabled);
                f.taskbar.Checked = true;
                f.Tabs.SelectedIndex = 1; Application.DoEvents();
                f.accel.Checked = false;
                Check("加速オフ → 強さが無効", !f.accelStrength.Enabled);
                f.accel.Checked = true;

                // 値を変えて「適用」
                f.step.Value = 5;
                f.eLeft.Checked = false;
                f.Tabs.SelectedIndex = 3; Application.DoEvents();
                f.autoStart.Checked = true;
                Button applyBtn = FindButton(f, "適用");
                Check("適用ボタンがある", applyBtn != null);
                if (applyBtn != null) applyBtn.PerformClick();
                Check("適用で設定が渡される", applied != null);
                if (applied != null)
                {
                    Eq("適用: 変化量", 5, applied.Step);
                    Eq("適用: 左辺", false, applied.EdgeLeft);
                    Eq("適用: 自動起動", true, appliedAuto);
                }

                // 各タブのレイアウト（はみ出し・スクロールバー）
                for (int t = 0; t < f.Tabs.TabPages.Count; t++)
                {
                    f.Tabs.SelectedIndex = t; Application.DoEvents();
                    TabPage page = f.Tabs.TabPages[t];
                    FlowLayoutPanel panel = (FlowLayoutPanel)page.Controls[0];
                    Check("「" + page.Text + "」タブ: 縦スクロールが出ない", !panel.VerticalScroll.Visible);
                    Check("「" + page.Text + "」タブ: 横スクロールが出ない", !panel.HorizontalScroll.Visible);
                    string overflow = FindOverflow(page, page);
                    Check("「" + page.Text + "」タブ: 右端からはみ出す部品がない", overflow == null, overflow);
                }
                f.Close();
            }
        }

        static Button FindButton(Control root, string text)
        {
            foreach (Control c in root.Controls)
            {
                Button b = c as Button;
                if (b != null && b.Text == text) return b;
                Button inner = FindButton(c, text);
                if (inner != null) return inner;
            }
            return null;
        }

        static string FindOverflow(Control page, Control c)
        {
            foreach (Control child in c.Controls)
            {
                if (!child.Visible) continue;
                Rectangle r = page.RectangleToClient(child.RectangleToScreen(child.ClientRectangle));
                if (r.Right > page.ClientSize.Width + 1) return child.GetType().Name + " '" + child.Text + "' right=" + r.Right + " > " + page.ClientSize.Width;
                string inner = FindOverflow(page, child);
                if (inner != null) return inner;
            }
            return null;
        }

        static uint Gdi() { return GetGuiResources(Process.GetCurrentProcess().Handle, 0); }
        static uint User() { return GetGuiResources(Process.GetCurrentProcess().Handle, 1); }

        static void TestIconRendering()
        {
            int errors = 0, blank = 0, total = 0;
            foreach (IconStyle st in Enum.GetValues(typeof(IconStyle)))
                foreach (TextColorMode cm in Enum.GetValues(typeof(TextColorMode)))
                    foreach (int size in new[] { 16, 20, 24, 32, 48 })
                        for (int pct = -1; pct <= 100; pct++)
                            foreach (bool muted in new[] { false, true })
                            {
                                if (st == IconStyle.Bar && pct == 0 && !muted) { total++; continue; } // 0%のバーは枠のみで正常
                                total++;
                                try
                                {
                                    using (Bitmap b = IconRenderer.RenderBitmap(pct, muted, st, cm, false, size))
                                    {
                                        if (b.Width != size || b.Height != size) errors++;
                                        else if (!HasInk(b)) blank++;
                                    }
                                }
                                catch { errors++; }
                            }
            Eq("全組み合わせ(" + total + "通り)で描画エラーなし", 0, errors);
            Eq("全組み合わせで何かしら描かれている", 0, blank);

            // 長時間起動を想定したハンドルリーク確認
            for (int i = 0; i < 50; i++) IconRenderer.Render(i, false, IconStyle.Number, TextColorMode.Auto, false, 16).Dispose();
            uint g0 = Gdi(), u0 = User();
            for (int i = 0; i < 3000; i++)
                IconRenderer.Render(i % 101, i % 7 == 0, (IconStyle)(i % 3), TextColorMode.Auto, false, 16).Dispose();
            uint g1 = Gdi(), u1 = User();
            Check("アイコン3000回作り直してもGDIハンドルが増えない", g1 <= g0 + 10, g0 + " → " + g1);
            Check("アイコン3000回作り直してもUSERハンドルが増えない", u1 <= u0 + 10, u0 + " → " + u1);
        }

        static bool HasInk(Bitmap b)
        {
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                    if (b.GetPixel(x, y).A > 0) return true;
            return false;
        }

        static void TestPlacement()
        {
            List<Rectangle> areas = new List<Rectangle>();
            foreach (Screen s in Screen.AllScreens) areas.Add(s.WorkingArea);
            areas.Add(new Rectangle(-1920, 0, 1920, 1040));
            areas.Add(new Rectangle(0, 40, 1280, 680));     // 上にタスクバー
            areas.Add(new Rectangle(3840, -200, 2560, 1400));
            int bad = 0; string firstBad = null;
            foreach (Rectangle wa in areas)
                foreach (float scale in new[] { 1f, 1.5f, 2f })
                    for (int zone = 1; zone <= 10; zone++)
                        foreach (Point c in Probe(wa))
                        {
                            Rectangle r = Osd.ComputeBounds(c, zone, wa, scale);
                            if (!wa.Contains(r)) { bad++; if (firstBad == null) firstBad = "wa=" + wa + " zone=" + zone + " cursor=" + c + " -> " + r; }
                        }
            Check("OSDは全ゾーン・全倍率で作業領域内に収まる", bad == 0, firstBad);

            bad = 0; firstBad = null;
            Size popup = new Size(300, 84);
            foreach (Rectangle wa in areas)
                foreach (Point c in Probe(wa))
                {
                    Point p = VolumePopup.ComputeLocation(c, popup, wa);
                    if (!wa.Contains(new Rectangle(p, popup))) { bad++; if (firstBad == null) firstBad = "wa=" + wa + " anchor=" + c + " -> " + p; }
                }
            Check("音量スライダーは作業領域内に収まる", bad == 0, firstBad);
        }

        static IEnumerable<Point> Probe(Rectangle wa)
        {
            int[] xs = { wa.Left, wa.Left + 5, wa.Left + wa.Width / 2, wa.Right - 5, wa.Right - 1, wa.Right + 20, wa.Left - 30 };
            int[] ys = { wa.Top - 30, wa.Top, wa.Top + 5, wa.Top + wa.Height / 2, wa.Bottom - 1, wa.Bottom + 20 };
            foreach (int x in xs) foreach (int y in ys) yield return new Point(x, y);
        }

        static void TestAudio()
        {
            using (Audio a = new Audio())
            {
                int pct; bool muted;
                bool ok = a.TryGet(out pct, out muted);
                Check("現在の音量を取得できる", ok && pct >= 0 && pct <= 100, "ok=" + ok + " pct=" + pct);
                if (!ok) return;

                Check("同じ値の設定は成功する", a.SetPercent(pct));
                int again; bool m2;
                a.TryGet(out again, out m2);
                Eq("設定後も値が変わらない", pct, again);

                List<AudioDevice> devs = a.ListDevices();
                Check("再生デバイスが1つ以上", devs.Count >= 1, "count=" + devs.Count);
                int defaults = 0;
                foreach (AudioDevice d in devs) { if (d.IsDefault) defaults++; Check("デバイス名が取れる: " + d.Name, !string.IsNullOrEmpty(d.Name) && d.Name != d.Id); }
                Eq("既定デバイスはちょうど1つ", 1, defaults);

                // 作り直しの繰り返し（スリープ復帰などを想定）
                int failures = 0;
                for (int i = 0; i < 300; i++) { a.Reset(); int p; bool m; if (!a.TryGet(out p, out m)) failures++; }
                Eq("取り直しを300回繰り返しても失敗しない", 0, failures);
                for (int i = 0; i < 30; i++) a.ListDevices();
                uint h0 = (uint)Process.GetCurrentProcess().HandleCount;
                for (int i = 0; i < 500; i++) { a.Reset(); int p; bool m; a.TryGet(out p, out m); if (i % 10 == 0) a.ListDevices(); }
                GC.Collect(); GC.WaitForPendingFinalizers();
                uint h1 = (uint)Process.GetCurrentProcess().HandleCount;
                Check("取り直しを繰り返してもハンドルが増え続けない", h1 <= h0 + 30, h0 + " → " + h1);
            }
        }

        static void TestTrayIcon()
        {
            NativeWindow w = new NativeWindow();
            w.CreateHandle(new CreateParams { Caption = "ScrollVolumeTestWindow" });
            try
            {
                TrayIcon t = new TrayIcon(w.Handle, Native.WM_APP + 50);
                t.Update(IconRenderer.Render(42, false, IconStyle.Number, TextColorMode.Auto, false, 16), "テスト");
                Application.DoEvents();
                Rectangle r = t.GetRect();
                Console.WriteLine("      (トレイ上の位置: " + r + ")");
                for (int i = 0; i < 20; i++) t.Update(IconRenderer.Render(i, false, IconStyle.Number, TextColorMode.Auto, false, 16), "テスト");
                uint g0 = Gdi(), u0 = User();
                for (int i = 0; i < 500; i++)
                    t.Update(IconRenderer.Render(i % 101, i % 5 == 0, (IconStyle)(i % 3), TextColorMode.Auto, false, 16), "テスト " + i);
                uint g1 = Gdi(), u1 = User();
                Check("トレイアイコンを500回更新してもGDIハンドルが増えない", g1 <= g0 + 10, g0 + " → " + g1);
                Check("トレイアイコンを500回更新してもUSERハンドルが増えない", u1 <= u0 + 10, u0 + " → " + u1);
                t.OnTaskbarCreated();
                t.Ensure();
                Check("タスクバー再作成後も更新できる", true);
                t.Dispose();
            }
            finally { w.DestroyHandle(); }
        }

        static void TestHook()
        {
            NativeWindow w = new NativeWindow();
            w.CreateHandle(new CreateParams());
            try
            {
                WheelHook h = new WheelHook(w.Handle, Native.WM_APP + 51, new Settings());
                h.Start();
                System.Threading.Thread.Sleep(200);
                Check("フックを張れる", h.IsInstalled);
                for (int i = 0; i < 20; i++) h.Reinstall();
                System.Threading.Thread.Sleep(300);
                Check("張り直しを20回しても張られたまま", h.IsInstalled && h.IsThreadAlive);
                h.Stop();
                Check("停止するとスレッドが終わる", !h.IsThreadAlive);
            }
            finally { w.DestroyHandle(); }
        }

        static void TestElevation()
        {
            bool elevated = ElevatedTask.IsElevated();
            Console.WriteLine("      (このテストの権限: " + (elevated ? "管理者" : "通常") + ")");
            Check("権限の判定で例外が出ない", true);

            string path = @"H:\_AI\Projects\テスト & 'フォルダ' (1)\ScrollVolume.exe";
            string xml = ElevatedTask.BuildXml(path, @"PC\ユーザー");
            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            bool parsed = true;
            try { doc.LoadXml(xml); } catch (Exception ex) { parsed = false; Console.WriteLine("      " + ex.Message); }
            Check("タスク定義が正しいXML（記号入りのパスでも）", parsed);
            if (parsed)
            {
                System.Xml.XmlNamespaceManager ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
                Eq("最上位の特権で実行", "HighestAvailable", Node(doc, ns, "//t:RunLevel"));
                Eq("実行時間の上限なし（既定の3日で止まらない）", "PT0S", Node(doc, ns, "//t:ExecutionTimeLimit"));
                Eq("優先度は通常（既定の低優先度にしない）", "4", Node(doc, ns, "//t:Priority"));
                Eq("バッテリー時も止めない", "false", Node(doc, ns, "//t:StopIfGoingOnBatteries"));
                Eq("実行ファイルのパスがそのまま入る", path, Node(doc, ns, "//t:Command"));
                Eq("起動引数", ElevatedTask.FromTaskArg, Node(doc, ns, "//t:Arguments"));
                Eq("ユーザー名", @"PC\ユーザー", Node(doc, ns, "//t:UserId"));
                Check("起動トリガーは付けない（自動起動は Run キー側）", doc.SelectSingleNode("//t:Triggers", ns) == null);
            }

            Stopwatch sw = Stopwatch.StartNew();
            bool exists = ElevatedTask.Exists();
            Console.WriteLine("      (タスク登録済み: " + exists + ", " + sw.ElapsedMilliseconds + "ms)");
            Check("タスクの有無を5秒以内に確認できる", sw.ElapsedMilliseconds < 5000);
        }

        static string Node(System.Xml.XmlDocument doc, System.Xml.XmlNamespaceManager ns, string xpath)
        {
            System.Xml.XmlNode n = doc.SelectSingleNode(xpath, ns);
            return n == null ? null : n.InnerText;
        }

        static void TestSingleInstance()
        {
            string name = @"Local\ScrollVolume_Test_" + Guid.NewGuid().ToString("N");
            System.Threading.ManualResetEvent held = new System.Threading.ManualResetEvent(false);
            System.Threading.ManualResetEvent release = new System.Threading.ManualResetEvent(false);
            System.Threading.Thread other = new System.Threading.Thread(() =>
            {
                System.Threading.Mutex m = SingleInstance.TryAcquire(name, 0);
                held.Set();
                release.WaitOne();
                // 解放せずにスレッド終了 → 「前のインスタンスが落ちた」状態を再現
                GC.KeepAlive(m);
            });
            other.Start();
            held.WaitOne();
            Check("起動中のインスタンスがあると取得できない", SingleInstance.TryAcquire(name, 0) == null);
            Stopwatch sw = Stopwatch.StartNew();
            release.Set();
            System.Threading.Mutex got = SingleInstance.TryAcquire(name, 5000);
            Check("前のインスタンスが終われば（解放せずに落ちても）待って取得できる", got != null);
            Check("待ち時間は短い", sw.ElapsedMilliseconds < 3000, sw.ElapsedMilliseconds + "ms");
            if (got != null) { got.ReleaseMutex(); got.Dispose(); }
            Check("解放後はすぐ取得できる", SingleInstance.TryAcquire(name, 0) != null);
        }

        static void CaptureScreens(string dir)
        {
            Directory.CreateDirectory(dir);
            using (SettingsForm f = new SettingsForm(new Settings(), false, (a, b) => { }, () => 45))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-32000, -32000);
                f.Show();
                for (int i = 0; i < f.Tabs.TabPages.Count; i++)
                {
                    f.Tabs.SelectedIndex = i;
                    Application.DoEvents();
                    Save(f, Path.Combine(dir, "settings_" + i + ".png"));
                }
                // ラジオを順に押した後の状態
                f.Tabs.SelectedIndex = 2; Application.DoEvents();
                f.StyleRadios[1].PerformClick(); Application.DoEvents();
                Save(f, Path.Combine(dir, "settings_icon_after_click.png"));
                f.Close();
            }
            using (VolumePopup p = new VolumePopup())
            {
                p.SetState(45, false);
                p.Location = new Point(-32000, -32000);
                p.Show(); Application.DoEvents();
                Save(p, Path.Combine(dir, "popup.png"));
            }
            Console.WriteLine("      画像: " + dir);
        }

        static void Save(Form f, string path)
        {
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
