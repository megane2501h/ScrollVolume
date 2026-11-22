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

        static void Near(string name, float expected, float actual)
        {
            Check(name, Math.Abs(expected - actual) < 0.001f, "expected " + expected + ", got " + actual);
        }

        static void Section(string title) { Console.WriteLine(); Console.WriteLine("== " + title); }

        public static int Run(string shotDir)
        {
            L.Set(Language.Japanese);
            Run("音量計算（刻み・速く回したとき）", TestVolumeMath);
            Run("音量計算の全組み合わせ（矛盾がないか）", TestVolumeMathExhaustive);
            Run("ホイールの目盛りの数え方", TestWheelAccumulator);
            Run("ミュートの規則", TestMuteRules);
            Run("ホットキー", TestHotkeys);
            Run("隅・辺の判定", TestHitScreen);
            Run("場所の判定（タスクバー・トレイ・優先順）", TestHitZone);
            Run("ゲーム中の判定", TestGameRules);
            Run("設定の保存と読み込み", TestSettingsIo);
            Run("設定画面（日本語）", () => TestSettingsForm(Language.Japanese));
            Run("設定画面（英語）", () => TestSettingsForm(Language.English));
            Run("設定画面の連動（矛盾する組み合わせを作れないか）", TestSettingsFormLinks);
            Run("ミキサー画面", TestFlyout);
            Run("ミキサー画面の配置とレイアウト", TestFlyoutLayout);
            Run("メーターの動き・描画の速さ", TestMeters);
            Run("トレイのメニューの向き（タスクバーが上下左右・隣の画面に出ない）", TestMenuPlacement);
            Run("設定画面の拡大率ごとのレイアウト", TestSettingsFormScales);
            Run("トレイアイコン描画", TestIconRendering);
            Run("OSD の位置", TestPlacement);
            Run("音声デバイス", TestAudio);
            Run("ScrollVolume ミキサーの音声データ（実機）", TestMixerSource);
            Run("トレイ登録", TestTrayIcon);
            Run("メニューから開く Windows の画面", TestLaunchTargets);
            Run("用語の統一", TestTerms);
            Run("マウスフック", TestHook);
            Run("管理者権限での起動", TestElevation);
            Run("多重起動防止", TestSingleInstance);
            if (shotDir != null) Run("画面キャプチャ", () => CaptureScreens(shotDir));
            L.Set(Language.Auto);

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

        static Settings Wheel(int step, FastSpin f, int strength = 3)
        {
            return new Settings { Step = step, FastSpin = f, AccelStrength = strength };
        }

        static void TestVolumeMath()
        {
            Settings s = Wheel(1, FastSpin.Snap5);
            Eq("1% + 5%区切り: ゆっくり 37 → 38", 38, VolumeMath.Next(37, 1, s, -1));
            Eq("1% + 5%区切り: ゆっくり(200ms) 37 → 38", 38, VolumeMath.Next(37, 1, s, 200));
            Eq("1% + 5%区切り: 速く 37 → 40", 40, VolumeMath.Next(37, 1, s, 30));
            Eq("1% + 5%区切り: 速く 40 → 45", 45, VolumeMath.Next(40, 1, s, 30));
            Eq("1% + 5%区切り: 速く下げる 37 → 35", 35, VolumeMath.Next(37, -1, s, 30));
            Eq("1% + 5%区切り: 速く下げる 35 → 30", 30, VolumeMath.Next(35, -1, s, 30));
            Eq("1% + 5%区切り: 速く 2目盛り 37 → 45", 45, VolumeMath.Next(37, 2, s, 30));
            Eq("1% + 5%区切り: 速く 98 → 100", 100, VolumeMath.Next(98, 1, s, 30));
            Eq("1% + 5%区切り: 速く 2 → 0", 0, VolumeMath.Next(2, -1, s, 30));
            s = Wheel(1, FastSpin.Snap10);
            Eq("1% + 10%区切り: 速く 37 → 40", 40, VolumeMath.Next(37, 1, s, 50));
            Eq("1% + 10%区切り: 速く 40 → 50", 50, VolumeMath.Next(40, 1, s, 50));
            s = Wheel(2, FastSpin.None);
            Eq("2%: 37 → 38（区切りにそろう）", 38, VolumeMath.Next(37, 1, s, -1));
            Eq("2%: 37 → 36", 36, VolumeMath.Next(37, -1, s, -1));
            Eq("2%: 38 → 40", 40, VolumeMath.Next(38, 1, s, -1));
            s = Wheel(5, FastSpin.Faster);
            Eq("5% + 加速: 速い 35 → 55（5の倍数のまま）", 55, VolumeMath.Next(35, 1, s, 10));
            Eq("5% + 加速: 37 → 40", 40, VolumeMath.Next(37, 1, s, -1));
            s = Wheel(1, FastSpin.Faster);
            Eq("1% + 加速: 速い 50 → 54", 54, VolumeMath.Next(50, 1, s, 10));
            Eq("1% + 加速: 中 50 → 53", 53, VolumeMath.Next(50, 1, s, 60));
            Eq("1% + 加速: 遅い 50 → 51", 51, VolumeMath.Next(50, 1, s, 200));
            s = Wheel(1, FastSpin.None);
            Eq("1% 加速なし: 速くても 50 → 51", 51, VolumeMath.Next(50, 1, s, 10));
            Eq("100 + は上限", 100, VolumeMath.Next(100, 1, s, -1));
            Eq("0 - は下限", 0, VolumeMath.Next(0, -1, s, -1));
            Eq("範囲外 150 → 100", 100, VolumeMath.Next(150, 0, s, -1));
            Eq("範囲外 -5 → 0", 0, VolumeMath.Next(-5, 0, s, -1));
            Eq("時刻が戻った(-5ms)は遅い扱い", 51, VolumeMath.Next(50, 1, Wheel(1, FastSpin.Snap5), -5));

            Eq("刻み 7 → 5 に丸める", 5, VolumeMath.NormalizeStep(7));
            Eq("刻み 3 → 2 に丸める", 2, VolumeMath.NormalizeStep(3));
            Eq("刻み 999 → 10 に丸める", 10, VolumeMath.NormalizeStep(999));
            Eq("刻み 0 → 1 に丸める", 1, VolumeMath.NormalizeStep(0));
            Check("5%区切りは 1% と 2% でだけ選べる", VolumeMath.IsAvailable(FastSpin.Snap5, 1) && VolumeMath.IsAvailable(FastSpin.Snap5, 2) && !VolumeMath.IsAvailable(FastSpin.Snap5, 5) && !VolumeMath.IsAvailable(FastSpin.Snap5, 10));
            Check("10%区切りは 10% では選べない", VolumeMath.IsAvailable(FastSpin.Snap10, 5) && !VolumeMath.IsAvailable(FastSpin.Snap10, 10));
            Eq("5% で 5%区切り → 10%区切りに置き換え", FastSpin.Snap10, VolumeMath.Fix(FastSpin.Snap5, 5));
            Eq("10% で 5%区切り → 通常どおり（加速に変えない）", FastSpin.None, VolumeMath.Fix(FastSpin.Snap5, 10));
            Eq("10% で 10%区切り → 通常どおり", FastSpin.None, VolumeMath.Fix(FastSpin.Snap10, 10));
            Check("10% では加速は選べない（1 目盛りで 20〜60% 飛ぶため）", !VolumeMath.IsAvailable(FastSpin.Faster, 10) && VolumeMath.IsAvailable(FastSpin.Faster, 5));
            Eq("10% で加速 → 通常どおり", FastSpin.None, VolumeMath.Fix(FastSpin.Faster, 10));
        }

        /// <summary>刻み × 速く回したとき × 強さ × 音量 × 目盛り数 × 間隔 の全組み合わせで、矛盾がないこと。</summary>
        static void TestVolumeMathExhaustive()
        {
            int total = 0, notMono = 0, stuck = 0, range = 0, offGrid = 0;
            string first = null;
            int[] dts = { -1, 10, 60, 89, 90, 200 };
            foreach (int step in VolumeMath.Steps)
                foreach (FastSpin f0 in Enum.GetValues(typeof(FastSpin)))
                {
                    FastSpin f = VolumeMath.Fix(f0, step);
                    for (int strength = 1; strength <= 5; strength++)
                    {
                        Settings s = Wheel(step, f, strength);
                        for (int cur = 0; cur <= 100; cur++)
                            foreach (int n in new[] { -3, -1, 1, 3 })
                                foreach (int dt in dts)
                                {
                                    total++;
                                    int next = VolumeMath.Next(cur, n, s, dt);
                                    string ctx = "step=" + step + " fast=" + f + " str=" + strength + " cur=" + cur + " n=" + n + " dt=" + dt + " -> " + next;
                                    if (next < 0 || next > 100) { range++; if (first == null) first = ctx; }
                                    if ((n > 0 && next < cur) || (n < 0 && next > cur)) { notMono++; if (first == null) first = ctx; }
                                    if ((n > 0 && cur < 100 && next <= cur) || (n < 0 && cur > 0 && next >= cur)) { stuck++; if (first == null) first = ctx; }
                                    bool fast = dt >= 0 && dt < VolumeMath.FastMs;
                                    int grid = fast && VolumeMath.SnapGrid(f) > step ? VolumeMath.SnapGrid(f) : step;
                                    if (next % grid != 0) { offGrid++; if (first == null) first = ctx + " grid=" + grid; }
                                }
                    }
                }
            Console.WriteLine("      (" + total + " 通り)");
            Eq("0〜100 の範囲に収まる", 0, range);
            Eq("上げて下がる・下げて上がることがない", 0, notMono);
            Eq("端でなければ必ず動く（止まらない）", 0, stuck);
            Eq("結果は常に区切り（ゆっくり=1目盛り、速い=区切り）の倍数", 0, offGrid);
            if (first != null) Console.WriteLine("      最初の反例: " + first);

            // どこからでも 0% と 100% に届く
            int unreachable = 0;
            foreach (int step in VolumeMath.Steps)
                foreach (FastSpin f0 in Enum.GetValues(typeof(FastSpin)))
                    foreach (int dt in new[] { -1, 10 })
                        for (int cur = 0; cur <= 100; cur++)
                        {
                            Settings s = Wheel(step, VolumeMath.Fix(f0, step));
                            int up = cur, down = cur;
                            for (int i = 0; i < 101; i++) { up = VolumeMath.Next(up, 1, s, dt); down = VolumeMath.Next(down, -1, s, dt); }
                            if (up != 100 || down != 0) unreachable++;
                        }
            Eq("どの設定・どこからでも 0% と 100% に届く", 0, unreachable);
        }

        static void TestWheelAccumulator()
        {
            WheelAccumulator w = new WheelAccumulator();
            int dt;
            Eq("1目盛り(120)", 1, w.Feed(120, 1000, out dt));
            Eq("最初の目盛りは間隔不明(-1)", -1, dt);
            Eq("30ms後の次の目盛り", 1, w.Feed(120, 1030, out dt));
            Eq("間隔 30ms", 30, dt);
            w = new WheelAccumulator();
            int notches = 0;
            for (int i = 0; i < 8; i++) notches += w.Feed(30, 2000 + i * 10, out dt);
            Eq("高解像度ホイール: 30×8 = 2目盛り", 2, notches);
            Eq("目盛りの間隔は細かい入力ではなく目盛り同士で測る(40ms)", 40, dt);
            w.Feed(120, 3000, out dt);
            Check("400ms 空いたら仕切り直し", dt == -1, "dt=" + dt);
            w.Feed(120, 3050, out dt);
            w.Feed(-120, 3080, out dt);
            Check("方向転換は仕切り直し（速い扱いにしない）", dt == -1, "dt=" + dt);
            w = new WheelAccumulator();
            Eq("半端な回転は持ち越す", 0, w.Feed(60, 5000, out dt));
            Eq("持ち越しと合わせて1目盛り", 1, w.Feed(60, 5010, out dt));
            Eq("1回で3目盛り", 3, new WheelAccumulator().Feed(360, 0, out dt));
            w = new WheelAccumulator();
            w.Feed(120, int.MaxValue - 10, out dt);
            w.Feed(120, unchecked(int.MaxValue + 21), out dt); // 折り返して負の値へ
            Eq("Environment.TickCount が折り返しても間隔を正しく測る(31ms)", 31, dt);
        }

        static void TestMuteRules()
        {
            int bad = 0; string first = null;
            foreach (bool unmute in new[] { false, true })
                foreach (bool zero in new[] { false, true })
                    foreach (bool muted in new[] { false, true })
                        foreach (bool raising in new[] { false, true })
                            foreach (int next in new[] { 0, 1, 50, 100 })
                            {
                                Settings s = new Settings { UnmuteOnChange = unmute, MuteAtZero = zero };
                                bool r = MuteRules.After(muted, next, raising, s);
                                string ctx = "unmute=" + unmute + " zero=" + zero + " muted=" + muted + " raising=" + raising + " next=" + next + " -> " + r;
                                bool ok = true;
                                if (zero && raising && next > 0 && r) ok = false;            // 0% でミュート → 上げれば必ず戻る
                                if (zero && next == 0 && !r) ok = false;                     // 0% ならミュート
                                if (!unmute && !zero && r != muted) ok = false;              // 両方オフなら変えない
                                if (!raising && muted && !r) ok = false;                     // 下げてミュート解除はしない
                                if (unmute && raising && next > 0 && r) ok = false;
                                if (!ok) { bad++; if (first == null) first = ctx; }
                            }
            Eq("全組み合わせで矛盾なし（0%でミュート後に戻れない状態を作らない）", 0, bad);
            if (first != null) Console.WriteLine("      反例: " + first);
        }

        static void TestHotkeys()
        {
            int ctrlAltUp = Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Up);
            Check("Ctrl+Alt+↑ は有効", Hotkey.IsValid(ctrlAltUp));
            Eq("修飾キーの取り出し", Hotkey.Ctrl | Hotkey.Alt, Hotkey.Mods(ctrlAltUp));
            Eq("キーの取り出し", Keys.Up, Hotkey.Key(ctrlAltUp));
            Eq("表示", "Ctrl + Alt + ↑", Hotkey.Format(ctrlAltUp));
            Check("未設定(0)は有効", Hotkey.IsValid(0));
            Check("修飾なしの A は無効（文字入力を奪う）", !Hotkey.IsValid(Hotkey.Make(0, Keys.A)));
            Check("Shift+A は無効（大文字入力を奪う）", !Hotkey.IsValid(Hotkey.Make(Hotkey.Shift, Keys.A)));
            Check("修飾なしの F1 は無効", !Hotkey.IsValid(Hotkey.Make(0, Keys.F1)));
            Check("F13 単独は有効", Hotkey.IsValid(Hotkey.Make(0, Keys.F13)));
            Check("Pause 単独は有効", Hotkey.IsValid(Hotkey.Make(0, Keys.Pause)));
            Check("Win+F5 は有効", Hotkey.IsValid(Hotkey.Make(Hotkey.Win, Keys.F5)));
            Check("修飾キーだけは無効", !Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl, Keys.ControlKey)));
            Check("Ctrl+C は無効（コピーを奪う）", !Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl, Keys.C)));
            Check("Alt+文字 は無効（メニューのアクセスキーを奪う）", !Hotkey.IsValid(Hotkey.Make(Hotkey.Alt, Keys.F)));
            Check("Ctrl+Alt+文字 は無効（欧州配列の AltGr で € などが打てなくなる）", !Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.E)) && !Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.D2)) && !Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Oem4)));
            Check("Ctrl+Space は無効（IME の切り替え）", !Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl, Keys.Space)));
            Check("Alt+F4 は無効", !Hotkey.IsValid(Hotkey.Make(Hotkey.Alt, Keys.F4)));
            Check("Ctrl+Shift+文字・Win+文字・Ctrl+Alt+Shift+文字 は有効", Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Shift, Keys.M)) && Hotkey.IsValid(Hotkey.Make(Hotkey.Win, Keys.M)) && Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt | Hotkey.Shift, Keys.M)));
            Check("Ctrl+Alt+矢印・F キー・PageUp は有効", Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Down)) && Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.F9)) && Hotkey.IsValid(Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.PageUp)));
            string oem = Hotkey.KeyName(Keys.Oem2);
            Check("記号キーは Oem2 のような内部名で表示しない", oem.Length > 0 && !oem.StartsWith("Oem"), oem);
            Eq("数字キーの表示", "Ctrl + Shift + 5", Hotkey.Format(Hotkey.Make(Hotkey.Ctrl | Hotkey.Shift, Keys.D5)));
            L.Set(Language.English);
            Eq("未設定の表示（英語）", "None", Hotkey.Format(0));
            L.Set(Language.Japanese);
            Eq("未設定の表示", "なし", Hotkey.Format(0));
            Settings s = new Settings { HotkeyMute = Hotkey.Make(0, Keys.A), HotkeyMixer = ctrlAltUp };
            s.Normalize();
            Eq("読み込み時に無効な組み合わせは外す", 0, s.HotkeyMute);
            Eq("有効な組み合わせは残す", ctrlAltUp, s.HotkeyMixer);
        }

        static void TestHitScreen()
        {
            FakeMonitorLayout h3 = new FakeMonitorLayout(
                new Rectangle(-1920, 0, 1920, 1080), new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1080));
            Settings d = new Settings();
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

            FakeMonitorLayout v2 = new FakeMonitorLayout(new Rectangle(0, 0, 1920, 1080), new Rectangle(0, 1080, 1920, 1080));
            Hit("縦2枚 上モニターの上辺", v2, d, 960, 0, WheelHook.TOP);
            Hit("縦2枚 つなぎ目(上モニター下辺)は無視", v2, d, 960, 1079, 0);
            Hit("縦2枚 つなぎ目(下モニター上辺)は無視", v2, d, 960, 1080, 0);
            Hit("縦2枚 下モニターの下辺", v2, d, 960, 2159, WheelHook.BOTTOM);
            Hit("縦2枚 上モニターの左辺", v2, d, 0, 500, WheelHook.LEFT);

            FakeMonitorLayout mix = new FakeMonitorLayout(new Rectangle(0, 0, 2560, 1440), new Rectangle(2560, 0, 1920, 1080));
            Hit("サイズ違い 大モニター右辺の上側(つなぎ目)は無視", mix, d, 2559, 500, 0);
            Hit("サイズ違い 大モニター右辺の下側(露出部分)は反応", mix, d, 2559, 1200, WheelHook.RIGHT);
            Hit("サイズ違い 小モニター左下", mix, d, 2560, 1079, WheelHook.BL);
            Hit("サイズ違い 小モニター下辺", mix, d, 3000, 1079, WheelHook.BOTTOM);

            FakeMonitorLayout neg = new FakeMonitorLayout(new Rectangle(0, 0, 1920, 1080), new Rectangle(0, -1080, 1920, 1080));
            Hit("上に置いたモニターの上辺", neg, d, 500, -1080, WheelHook.TOP);
            Hit("上に置いたモニターとのつなぎ目は無視", neg, d, 500, 0, 0);
        }

        static void Hit(string name, IMonitorLayout layout, Settings s, int x, int y, int expected)
        {
            Eq(name + " (" + x + "," + y + ")", expected, WheelHook.HitScreen(new POINT(x, y), s, layout));
        }

        static void TestHitZone()
        {
            // 1 枚、下に高さ 48 のタスクバー、右下にトレイのアイコン
            FakeMonitorLayout one = new FakeMonitorLayout(new Rectangle(0, 0, 1920, 1080));
            int taskbarCalls = 0;
            Func<POINT, bool> onTaskbar = p => { taskbarCalls++; return p.Y >= 1032; };
            int[] trayR = { 1700, 1040, 1724, 1072 };
            bool ctrl = false;
            Func<ModifierKey, bool> mod = k => k == ModifierKey.None || (k == ModifierKey.Ctrl && ctrl);

            Settings s = new Settings { TaskbarModifier = ModifierKey.Ctrl };
            Eq("Ctrl 必須: タスクバー中央・Ctrl なし → 反応しない", 0, WheelHook.HitZone(new POINT(960, 1050), s, one, onTaskbar, trayR, mod));
            Eq("Ctrl 必須: タスクバーの下端(=画面の下辺)・Ctrl なし → すり抜けない", 0, WheelHook.HitZone(new POINT(960, 1079), s, one, onTaskbar, trayR, mod));
            ctrl = true;
            Eq("Ctrl 必須: タスクバーの下端・Ctrl あり → タスクバー", WheelHook.TASKBAR, WheelHook.HitZone(new POINT(960, 1079), s, one, onTaskbar, trayR, mod));
            ctrl = false;
            Eq("タスクバーと重なる右下の隅も Ctrl なしでは反応しない（タスクバーの設定が優先）", 0, WheelHook.HitZone(new POINT(1915, 1075), s, one, onTaskbar, trayR, mod));
            ctrl = true;
            Eq("タスクバーと重なる右下の隅・Ctrl あり → タスクバー", WheelHook.TASKBAR, WheelHook.HitZone(new POINT(1915, 1075), s, one, onTaskbar, trayR, mod));
            ctrl = false;
            Eq("タスクバーと重ならない右上の隅は隅", WheelHook.TR, WheelHook.HitZone(new POINT(1915, 3), s, one, onTaskbar, trayR, mod));
            Eq("トレイのアイコンは修飾キーなしでも反応", WheelHook.TRAY, WheelHook.HitZone(new POINT(1710, 1050), s, one, onTaskbar, trayR, mod));
            Eq("上辺はタスクバーと無関係に反応", WheelHook.TOP, WheelHook.HitZone(new POINT(960, 0), s, one, onTaskbar, trayR, mod));

            s = new Settings { Taskbar = false };
            Eq("タスクバーオフ: タスクバーの下端は反応しない", 0, WheelHook.HitZone(new POINT(960, 1079), s, one, onTaskbar, trayR, mod));
            Eq("タスクバーオフ: トレイのアイコンは別設定なので反応", WheelHook.TRAY, WheelHook.HitZone(new POINT(1710, 1050), s, one, onTaskbar, trayR, mod));
            s.TrayIcon = false;
            Eq("トレイもオフ: 反応しない", 0, WheelHook.HitZone(new POINT(1710, 1050), s, one, onTaskbar, trayR, mod));
            taskbarCalls = 0;
            WheelHook.HitZone(new POINT(960, 540), s, one, onTaskbar, trayR, mod);
            Eq("タスクバーオフで画面中央なら、ウィンドウの問い合わせをしない", 0, taskbarCalls);
            Eq("トレイ未取得(null)でも例外なし", 0, WheelHook.HitZone(new POINT(1710, 1050), new Settings { TrayIcon = true, Taskbar = false }, one, onTaskbar, null, mod));

            // 自動で隠れるタスクバー: 下端 2px がタスクバー
            Func<POINT, bool> autoHide = p => p.Y >= 1078;
            s = new Settings { TaskbarModifier = ModifierKey.Ctrl };
            Eq("自動で隠すタスクバー: 下端は Ctrl なしで反応しない", 0, WheelHook.HitZone(new POINT(960, 1079), s, one, autoHide, null, mod));
            s.Taskbar = false;
            Eq("自動で隠すタスクバー・タスクバーオフ: 下端は反応しない", 0, WheelHook.HitZone(new POINT(960, 1079), s, one, autoHide, null, mod));
            s.Taskbar = true; s.TaskbarModifier = ModifierKey.None;
            Eq("自動で隠すタスクバー・常に: 下端はタスクバー", WheelHook.TASKBAR, WheelHook.HitZone(new POINT(960, 1079), s, one, autoHide, null, mod));

            // タスクバーを上・左・右に置いた場合（端と重なる）
            foreach (string side in new[] { "上", "左", "右" })
            {
                Func<POINT, bool> tb = side == "上" ? (Func<POINT, bool>)(p => p.Y < 48) : side == "左" ? (Func<POINT, bool>)(p => p.X < 64) : (p => p.X >= 1856);
                POINT edgeOnTaskbar = side == "上" ? new POINT(960, 0) : side == "左" ? new POINT(0, 540) : new POINT(1919, 540);
                POINT otherEdge = side == "上" ? new POINT(960, 1079) : side == "左" ? new POINT(1919, 540) : new POINT(0, 540);
                int otherZone = side == "上" ? WheelHook.BOTTOM : side == "左" ? WheelHook.RIGHT : WheelHook.LEFT;
                s = new Settings { TaskbarModifier = ModifierKey.Ctrl };
                ctrl = false;
                Eq("タスクバーが" + side + ": 重なる端は Ctrl なしで反応しない", 0, WheelHook.HitZone(edgeOnTaskbar, s, one, tb, null, mod));
                ctrl = true;
                Eq("タスクバーが" + side + ": 重なる端は Ctrl ありでタスクバー", WheelHook.TASKBAR, WheelHook.HitZone(edgeOnTaskbar, s, one, tb, null, mod));
                ctrl = false;
                Eq("タスクバーが" + side + ": 反対側の端はふつうに反応", otherZone, WheelHook.HitZone(otherEdge, s, one, tb, null, mod));
                POINT freeCorner = side == "上" ? new POINT(2, 1077) : side == "左" ? new POINT(1917, 2) : new POINT(2, 2);
                int freeZone = side == "上" ? WheelHook.BL : side == "左" ? WheelHook.TR : WheelHook.TL;
                POINT coveredCorner = side == "右" ? new POINT(1917, 2) : new POINT(2, 2);
                Eq("タスクバーが" + side + ": 重ならない隅は隅として反応", freeZone, WheelHook.HitZone(freeCorner, s, one, tb, null, mod));
                Eq("タスクバーが" + side + ": 重なる隅は Ctrl なしで反応しない", 0, WheelHook.HitZone(coveredCorner, s, one, tb, null, mod));
                s.Taskbar = false;
                Eq("タスクバーが" + side + "・タスクバーオフ: 重なる端は反応しない", 0, WheelHook.HitZone(edgeOnTaskbar, s, one, tb, null, mod));
            }

            // ホイールクリックでミュート
            Settings mc = new Settings { MiddleClickMute = true, MiddleClickTaskbar = false };
            Check("四隅・端のホイールクリック → ミュート", WheelHook.MiddleClickMutes(WheelHook.TL, mc) && WheelHook.MiddleClickMutes(WheelHook.BOTTOM, mc));
            Check("タスクバーは既定ではミュートしない（Windows のホイールクリックを残す）", !WheelHook.MiddleClickMutes(WheelHook.TASKBAR, mc));
            mc.MiddleClickTaskbar = true;
            Check("タスクバーをオンにするとタスクバーでもミュート", WheelHook.MiddleClickMutes(WheelHook.TASKBAR, mc));
            mc.MiddleClickMute = false;
            Check("四隅・端をオフ → 四隅・端ではミュートしない", !WheelHook.MiddleClickMutes(WheelHook.TR, mc) && !WheelHook.MiddleClickMutes(WheelHook.LEFT, mc));
            Check("トレイのアイコンは常に対象外（アイコンのクリック設定に任せる）", !WheelHook.MiddleClickMutes(WheelHook.TRAY, new Settings { MiddleClickMute = true, MiddleClickTaskbar = true }));
            Check("場所の外では何もしない", !WheelHook.MiddleClickMutes(0, new Settings { MiddleClickMute = true, MiddleClickTaskbar = true }));
            // 端と重なるタスクバーで、ホイールクリックはタスクバーの設定に従う
            s = new Settings { MiddleClickMute = true, MiddleClickTaskbar = false };
            int z = WheelHook.HitZone(new POINT(960, 1079), s, one, onTaskbar, trayR, mod);
            Check("タスクバーと重なる下端: タスクバーのミュートがオフならミュートしない（端の設定ですり抜けない）", !WheelHook.MiddleClickMutes(z, s), "zone=" + z);
            s.TaskbarModifier = ModifierKey.Ctrl; s.MiddleClickTaskbar = true; ctrl = false;
            z = WheelHook.HitZone(new POINT(960, 1050), s, one, onTaskbar, trayR, mod);
            Check("タスクバーの修飾キーはホイールクリックにも効く（Ctrl なし → ミュートしない）", !WheelHook.MiddleClickMutes(z, s), "zone=" + z);
            ctrl = true;
            z = WheelHook.HitZone(new POINT(960, 1050), s, one, onTaskbar, trayR, mod);
            Check("Ctrl あり → ミュート", WheelHook.MiddleClickMutes(z, s), "zone=" + z);
            ctrl = false;

            // ホイールクリックの判定（フックが使う関数そのもの）
            s = new Settings { TrayIcon = false, MiddleClickMute = true, MiddleClickTaskbar = true };
            Eq("アイコンの上のホイールクリックは、ホイールの「アイコンの上」がオフでも横取りしない（アイコンのクリック設定を優先）", 0, WheelHook.MiddleClickZone(new POINT(1710, 1050), s, one, onTaskbar, trayR, mod));
            s = new Settings { MiddleClickMute = true, MiddleClickTaskbar = false };
            Eq("タスクバーと重なる隅: タスクバーのミュートがオフなら横取りしない（スタートボタン・デスクトップの表示を残す）", 0, WheelHook.MiddleClickZone(new POINT(1915, 1075), s, one, onTaskbar, trayR, mod));
            Eq("タスクバーと重ならない隅は横取りする", WheelHook.TR, WheelHook.MiddleClickZone(new POINT(1915, 3), s, one, onTaskbar, trayR, mod));
            Eq("画面中央は横取りしない", 0, WheelHook.MiddleClickZone(new POINT(960, 540), s, one, onTaskbar, trayR, mod));

            MiddleClickGate gate = new MiddleClickGate();
            Check("横取りした押下 → 離す操作も横取り", gate.Down(true) && gate.Up());
            Check("2 回目の離す操作は通す", !gate.Up());
            Check("横取りしない押下 → 離す操作も通す", !gate.Down(false) && !gate.Up());
            gate.Down(true); // 離す操作を取りこぼした
            gate.Down(false); // 次はふつうのクリック
            Check("離す操作を取りこぼしても、次のふつうのクリックの離す操作は通す（ボタンが押されたままにならない）", !gate.Up());
        }

        static void TestGameRules()
        {
            Rectangle mon = new Rectangle(0, 0, 1920, 1080);
            Check("モニターぴったりの枠なしウィンドウ → 全画面", GameRules.IsFullscreen(mon, mon, false, false, "UnityWndClass"));
            Check("モニターより大きい枠なしウィンドウ → 全画面", GameRules.IsFullscreen(new Rectangle(-8, -8, 1936, 1096), mon, false, false, "X"));
            Check("最大化（タイトルバーあり）→ 全画面ではない", !GameRules.IsFullscreen(new Rectangle(-8, -8, 1936, 1096), mon, true, true, "Chrome_WidgetWin_1"));
            Check("小さいウィンドウ → 全画面ではない", !GameRules.IsFullscreen(new Rectangle(0, 0, 1280, 720), mon, false, false, "X"));
            Check("デスクトップ → 全画面ではない", !GameRules.IsFullscreen(mon, mon, false, false, "Progman") && !GameRules.IsFullscreen(mon, mon, false, false, "WorkerW"));
            Check("最大化したタイトルバーなしのアプリ（自動で隠すタスクバー）→ 全画面ではない", !GameRules.IsFullscreen(new Rectangle(-8, -8, 1936, 1096), mon, false, true, "Electron"));
            Check("ブラウザーは動画扱い", GameRules.IsMediaApp("chrome") && GameRules.IsMediaApp("MSEDGE") && GameRules.IsMediaApp("firefox.exe"));
            Check("動画プレーヤーは動画扱い", GameRules.IsMediaApp("vlc") && GameRules.IsMediaApp("mpc-hc64"));
            Check("ゲームは動画扱いしない", !GameRules.IsMediaApp("eldenring") && !GameRules.IsMediaApp(null) && !GameRules.IsMediaApp(""));
            Check("全画面のゲーム → ゲーム中", GameRules.IsGame(false, true, "game"));
            Check("全画面のブラウザー → ゲーム中ではない（動画の全画面では使える）", !GameRules.IsGame(false, true, "chrome"));
            Check("排他的フルスクリーン → 何であってもゲーム中", GameRules.IsGame(true, false, "chrome"));
            Check("全画面でない → ゲーム中ではない", !GameRules.IsGame(false, false, "game"));
            GameDetect.Update();
            Check("実機での判定が例外なく終わる", true);
            GameDetect.Active = false;
        }

        static Settings NonDefaultSettings()
        {
            Settings d = new Settings();
            return new Settings
            {
                Step = 2, FastSpin = FastSpin.Snap10, AccelStrength = 5, Invert = !d.Invert,
                UnmuteOnChange = !d.UnmuteOnChange, MuteAtZero = !d.MuteAtZero, MiddleClickMute = !d.MiddleClickMute, MiddleClickTaskbar = !d.MiddleClickTaskbar,
                ShowOsd = !d.ShowOsd, OsdDurationMs = 2500, OsdHideInGames = !d.OsdHideInGames,
                Corners = !d.Corners, CornerTL = false, CornerTR = true, CornerBL = false, CornerBR = true,
                CornerSize = 33, OuterCornersOnly = !d.OuterCornersOnly,
                Edges = !d.Edges, EdgeTop = false, EdgeBottom = true, EdgeLeft = false, EdgeRight = true,
                EdgeSize = 9, OuterEdgesOnly = !d.OuterEdgesOnly,
                Taskbar = !d.Taskbar, TaskbarModifier = ModifierKey.Alt, TrayIcon = !d.TrayIcon, GameGuard = !d.GameGuard,
                LeftClick = ClickAction.Mixer, MiddleClick = ClickAction.None,
                HotkeyVolumeUp = Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Up), HotkeyVolumeDown = Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Down),
                HotkeyMute = Hotkey.Make(Hotkey.Win, Keys.F5), HotkeyMixer = Hotkey.Make(0, Keys.F13), HotkeyNextDevice = Hotkey.Make(Hotkey.Ctrl | Hotkey.Shift, Keys.D5),
                Style = IconStyle.Bar, TextColor = TextColorMode.Black,
                MixerWindowed = true, MixerTopmost = true, MixerPosition = "-1500,200", CollapsedDevices = "{0.0.0.00000000}.{abc}|{0.0.0.00000000}.{def}",
                RunElevated = !d.RunElevated, Language = Language.English, Debug = true,
            };
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

                File.WriteAllText(tmp, "Step=999\nCornerSize=-4\nEdgeSize=abc\nStyle=Nonexistent\nUnknownKey=1\nnot a line\n=\nCorners=0\nStyle=99\n");
                Settings bad = Settings.Load(tmp);
                Eq("壊れた値: Step は 10 に丸める", 10, bad.Step);
                Eq("壊れた値: CornerSize は下限2に丸める", 2, bad.CornerSize);
                Eq("壊れた値: EdgeSize は既定のまま", new Settings().EdgeSize, bad.EdgeSize);
                Eq("壊れた値: 範囲外の列挙値は既定のまま", new Settings().Style, bad.Style);
                Eq("正しい値は読める: Corners=0", false, bad.Corners);
                Eq("Step=10 なら「速く回したとき」は選べる値（通常どおり）に直す", FastSpin.None, bad.FastSpin);
                File.WriteAllText(tmp, "OsdDurationMs=5\n");
                Eq("表示時間が短すぎる値は 300ms に丸める", 300, Settings.Load(tmp).OsdDurationMs);

                // 以前の版からの引き継ぎ
                File.WriteAllText(tmp, "Step=1\nAccel=0\nRoundStep=1\n");
                Eq("旧設定 Accel=0 → 変えない", FastSpin.None, Settings.Load(tmp).FastSpin);
                File.WriteAllText(tmp, "Step=7\nAccel=1\nRoundStep=1\n");
                Settings old = Settings.Load(tmp);
                Eq("旧設定 Accel=1・Step=7 → 5% と 10%区切り", FastSpin.Snap10, old.FastSpin);
                Eq("旧設定 Step=7 → 5%", 5, old.Step);
                File.WriteAllText(tmp, "Step=1\nAccel=1\n");
                Eq("旧設定 Accel=1（以前の既定）・Step=1 → 5%区切り", FastSpin.Snap5, Settings.Load(tmp).FastSpin);
                File.WriteAllText(tmp, "Step=1\nAccel=0\nFastSpin=Snap10\n");
                Eq("FastSpin があれば旧設定 Accel は無視", FastSpin.Snap10, Settings.Load(tmp).FastSpin);
                File.WriteAllText(tmp, "Step=1\n");
                Eq("旧設定に Accel が無ければ既定(5%区切り)", FastSpin.Snap5, Settings.Load(tmp).FastSpin);
                File.WriteAllText(tmp, "Step=5\nFastSpin=Snap5\n");
                Eq("5% と 5%区切りの組み合わせは 10%区切りに直す", FastSpin.Snap10, Settings.Load(tmp).FastSpin);
                File.WriteAllText(tmp, "HotkeyMute=65\n");
                Eq("修飾なしのホットキーは読み込み時に外す", 0, Settings.Load(tmp).HotkeyMute);
                File.WriteAllText(tmp, "MixerPosition=12,34\nCollapsedDevices=a|b||c\n");
                Settings m = Settings.Load(tmp);
                Point mp;
                Check("ミキサーの位置を読める", m.TryGetMixerPosition(out mp) && mp == new Point(12, 34));
                Eq("折りたたみの一覧（空要素は無視）", 3, m.CollapsedSet().Count);
                File.WriteAllText(tmp, "MixerPosition=abc\n");
                Check("壊れた位置は使わない", !Settings.Load(tmp).TryGetMixerPosition(out mp));
                File.WriteAllText(tmp, "MixerSize=640,480\n");
                Size msz;
                Check("ミキサーの大きさを読める", Settings.Load(tmp).TryGetMixerSize(out msz) && msz == new Size(640, 480));
                foreach (string badSize in new[] { "abc", "640", "0,0", "-5,300", "99999,300", "640,480,1" })
                {
                    File.WriteAllText(tmp, "MixerSize=" + badSize + "\n");
                    Settings sb = Settings.Load(tmp);
                    Check("壊れた・極端な大きさは使わず空に戻す: " + badSize, !sb.TryGetMixerSize(out msz) && sb.MixerSize == "");
                }
                Settings ss = new Settings();
                ss.SetMixerSize(new Size(500, 300));
                Eq("大きさを保存用の文字列に", "500,300", ss.MixerSize);
                ss.SetMixerSize(Size.Empty);
                Eq("覚えていなければ空", "", ss.MixerSize);

                File.Delete(tmp);
                Settings created = Settings.Load(tmp);
                Check("ファイルが無ければ既定値で作る", File.Exists(tmp) && new Settings().DiffFrom(created) == null);

                Settings cur = new Settings { MixerWindowed = true, MixerTopmost = true, MixerPosition = "1,2", MixerSize = "600,400", CollapsedDevices = "x" };
                Settings fromForm = new Settings();
                TrayApp.KeepMixerState(cur, fromForm);
                Check("設定画面の適用でミキサーの状態（ウィンドウ表示・最前面・位置・大きさ・折りたたみ）を上書きしない", fromForm.MixerWindowed && fromForm.MixerTopmost && fromForm.MixerPosition == "1,2" && fromForm.MixerSize == "600,400" && fromForm.CollapsedDevices == "x");
                File.WriteAllText(tmp, "MixerPinned=1\n");
                Settings oldPin = Settings.Load(tmp);
                Check("旧設定のピン留めは「ウィンドウ表示・最前面」に引き継ぐ", oldPin.MixerWindowed && oldPin.MixerTopmost);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        static void TestSettingsForm(Language lang)
        {
            L.Set(lang);
            try
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
                    f.Location = new Point(-32000, -32000);
                    f.Show();
                    Application.DoEvents();

                    f.Tabs.SelectedIndex = 3;
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

                    f.osdSeconds.Value = 3.4m;
                    Eq("表示時間 3.4 秒 → 3400ms", 3400, f.Collect().OsdDurationMs);
                    f.osdTry.PerformClick();
                    Eq("プレビューで選んだ時間が渡される", 3400, previewMs);
                    f.showOsd.Checked = false;
                    Check("音量バーを表示しない → 時間・プレビュー・ゲーム中の設定が無効", !f.osdSeconds.Enabled && !f.osdTry.Enabled && !f.osdHideGames.Enabled);
                    f.showOsd.Checked = true;

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
                    f.step.SelectedIndex = 2; // 5%
                    f.Tabs.SelectedIndex = 0; Application.DoEvents();
                    f.eLeft.Checked = false;
                    f.Tabs.SelectedIndex = 4; Application.DoEvents();
                    f.autoStart.Checked = true;
                    Button applyBtn = FindButton(f, "apply");
                    Check("適用ボタンがある", applyBtn != null);
                    if (applyBtn != null) applyBtn.PerformClick();
                    Check("適用で設定が渡される", applied != null);
                    if (applied != null)
                    {
                        Eq("適用: 変化量", 5, applied.Step);
                        Eq("適用: 左辺", false, applied.EdgeLeft);
                        Eq("適用: 自動起動", true, appliedAuto);
                    }

                    string ln = lang == Language.English ? "[英語] " : "";
                    for (int t = 0; t < f.Tabs.TabPages.Count; t++)
                    {
                        f.Tabs.SelectedIndex = t; Application.DoEvents();
                        TabPage page = f.Tabs.TabPages[t];
                        FlowLayoutPanel panel = (FlowLayoutPanel)page.Controls[0];
                        Check(ln + "「" + page.Text + "」タブ: 縦スクロールが出ない", !panel.VerticalScroll.Visible);
                        Check(ln + "「" + page.Text + "」タブ: 横スクロールが出ない", !panel.HorizontalScroll.Visible);
                        string overflow = FindOverflow(page, page);
                        Check(ln + "「" + page.Text + "」タブ: 右端からはみ出す部品がない", overflow == null, overflow);
                        string cut = FindTruncatedLabel(page);
                        Check(ln + "「" + page.Text + "」タブ: 幅を固定したラベルの文字が切れていない", cut == null, cut);
                    }
                    Check(ln + "タブの見出しが 1 行に収まる", f.Tabs.RowCount <= 1, "rows=" + f.Tabs.RowCount);
                    f.Close();
                }
            }
            finally { L.Set(Language.Japanese); }
        }

        static void TestSettingsFormScales()
        {
            foreach (float scale in new[] { 1.25f, 1.5f, 2f })
                foreach (Language lang in new[] { Language.Japanese, Language.English })
                {
                    L.Set(lang);
                    SettingsForm.ScaleOverride = scale;
                    try
                    {
                        using (SettingsForm f = new SettingsForm(NonDefaultSettings(), false, (a, b) => { }, () => 45))
                        {
                            f.StartPosition = FormStartPosition.Manual;
                            f.Location = new Point(-32000, -32000);
                            f.Show();
                            string ctx = " (x" + scale + ", " + lang + ")";
                            for (int t = 0; t < f.Tabs.TabPages.Count; t++)
                            {
                                f.Tabs.SelectedIndex = t; Application.DoEvents();
                                TabPage page = f.Tabs.TabPages[t];
                                FlowLayoutPanel panel = (FlowLayoutPanel)page.Controls[0];
                                string overflow = FindOverflow(page, page);
                                string cut = FindTruncatedLabel(page);
                                Check("「" + page.Text + "」" + ctx + ": スクロールが出ない・はみ出さない・文字が切れない",
                                    !panel.VerticalScroll.Visible && !panel.HorizontalScroll.Visible && overflow == null && cut == null,
                                    (panel.VerticalScroll.Visible ? "縦スクロール " : "") + (overflow ?? "") + " " + (cut ?? ""));
                            }
                            f.Close();
                        }
                    }
                    finally { SettingsForm.ScaleOverride = 0; }
                }
            L.Set(Language.Japanese);
        }

        static void TestSettingsFormLinks()
        {
            using (SettingsForm f = new SettingsForm(new Settings(), false, (a, b) => { }, () => 45))
            {
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-32000, -32000);
                f.Show();
                Application.DoEvents();

                // 刻みと「速く回したとき」
                f.Tabs.SelectedIndex = 1; Application.DoEvents(); // 表示中のタブでないと Visible が取れない
                f.step.SelectedIndex = 0; // 1%
                Eq("1% のとき「速く回したとき」は 4 択", 4, f.fastSpin.Items.Count);
                f.fastSpin.SelectedIndex = 2;
                Eq("1% + 5%区切り を選べる（1%派と区切り派の両立）", FastSpin.Snap5, f.Collect().FastSpin);
                Eq("1% + 5%区切り: 1 目盛りは 1%", 1, f.Collect().Step);
                f.step.SelectedIndex = 2; // 5%
                Eq("5% にすると 5%区切りは選択肢から消える", 3, f.fastSpin.Items.Count);
                Eq("選んでいた 5%区切りは 10%区切りに置き換わる", FastSpin.Snap10, f.Collect().FastSpin);
                f.step.SelectedIndex = 3; // 10%
                Eq("10% では区切りも加速も選べない（通常どおりだけ）", 1, f.fastSpin.Items.Count);
                Eq("10% では「通常どおり」になる", FastSpin.None, f.Collect().FastSpin);
                Check("選択肢が 1 つなら選べない状態にする", !f.fastSpin.Enabled);
                f.step.SelectedIndex = 0;
                Eq("1% に戻すと選んでいた 5%区切りに戻る（10% を経由しても消えない）", FastSpin.Snap5, f.Collect().FastSpin);
                Check("戻したら選べる", f.fastSpin.Enabled);
                f.fastSpin.SelectedIndex = 1;
                Check("「加速する」のときだけ加速の強さを変えられる・表示する", f.accelStrength.Enabled && f.accelRow.Visible);
                f.fastSpin.SelectedIndex = 0;
                Check("「通常どおり」なら加速の強さは無効・非表示", !f.accelStrength.Enabled && !f.accelRow.Visible);
                f.fastSpin.SelectedIndex = 2;
                int bad = 0;
                for (int si = 0; si < f.step.Items.Count; si++)
                {
                    f.step.SelectedIndex = si;
                    for (int fi = 0; fi < f.fastSpin.Items.Count; fi++)
                    {
                        f.fastSpin.SelectedIndex = fi;
                        Settings c = f.Collect();
                        if (!VolumeMath.IsAvailable(c.FastSpin, c.Step)) bad++;
                    }
                }
                Eq("画面上のどの組み合わせを選んでも意味のある設定になる", 0, bad);

                // ミュート
                f.muteZero.Checked = false;
                f.unmute.Checked = false;
                f.muteZero.Checked = true;
                Check("0% でミュート オン → 解除は自動でオン・変更不可", f.unmute.Checked && !f.unmute.Enabled);
                Settings c2 = f.Collect();
                Check("0% でミュート オン → 上げれば必ず戻る", !MuteRules.After(true, 5, true, c2));
                f.muteZero.Checked = false;
                Check("0% でミュート オフ → 解除は元の選択に戻る", !f.unmute.Checked && f.unmute.Enabled);
                f.unmute.Checked = true;

                // 場所がない警告
                f.Tabs.SelectedIndex = 0; Application.DoEvents();
                f.corners.Checked = false; f.edges.Checked = false; f.taskbar.Checked = false; f.tray.Checked = false;
                Check("場所もホットキーも無い → 警告を表示", f.zoneWarning.Visible);
                Check("四隅も端もオフ → ホイールクリックのミュート（四隅・端）は無効", !f.middleMute.Enabled);
                Check("タスクバーオフ → タスクバーのホイールクリックも無効", !f.middleTaskbar.Enabled);
                f.hotkeys[0].CaptureKey(Keys.Up, Keys.Control | Keys.Alt);
                Check("ホットキーがあれば警告を消す", !f.zoneWarning.Visible);
                f.hotkeys[0].CaptureKey(Keys.Back, Keys.None);
                Check("ホットキーを消すと警告に戻る", f.zoneWarning.Visible);
                f.tray.Checked = true;
                Check("トレイのアイコンだけでも警告は消える", !f.zoneWarning.Visible);
                f.corners.Checked = true; f.edges.Checked = true; f.taskbar.Checked = true;

                // ホットキー
                f.hotkeys[0].CaptureKey(Keys.Up, Keys.Control | Keys.Alt);
                Eq("Ctrl+Alt+↑ を登録", Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Up), f.Collect().HotkeyVolumeUp);
                f.hotkeys[1].CaptureKey(Keys.Up, Keys.Control | Keys.Alt);
                Eq("同じキーを別の操作に設定 → 後から設定した方に移る", Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Up), f.Collect().HotkeyVolumeDown);
                Eq("前の操作からは外れる（重複したまま保存されない）", 0, f.Collect().HotkeyVolumeUp);
                f.hotkeys[0].CaptureKey(Keys.Up, Keys.Control | Keys.Alt);
                f.hotkeys[1].CaptureKey(Keys.Down, Keys.Control | Keys.Alt);
                f.hotkeys[2].CaptureKey(Keys.A, Keys.Shift);
                Eq("Shift+A は登録しない", 0, f.Collect().HotkeyMute);
                Check("登録できない組み合わせは理由を表示", f.hotkeyStatus[2].Text.Length > 0);
                f.hotkeys[2].CaptureKey(Keys.M, Keys.Control | Keys.Alt);
                Check("Ctrl+Alt+文字 も理由を表示して登録しない", f.hotkeyStatus[2].Text.Length > 0 && f.Collect().HotkeyMute == 0);
                f.hotkeys[2].CaptureKey(Keys.M, Keys.Control | Keys.Shift);
                Check("正しく入れ直すと表示が消える", f.hotkeyStatus[2].Text.Length == 0);
                int ctrlShiftM = Hotkey.Make(Hotkey.Ctrl | Hotkey.Shift, Keys.M);
                f.hotkeys[2].CaptureKey(Keys.ControlKey, Keys.Control);
                Eq("Ctrl を押しただけでは値は変わらない", ctrlShiftM, f.Collect().HotkeyMute);
                f.SetHotkeyStatus(new[] { false, false, true, false, false }, new[] { 0, 0, ctrlShiftM, 0, 0 });
                Check("ほかのアプリが使用中 → 表示", f.hotkeyStatus[2].Text.Length > 0);
                f.hotkeys[2].CaptureKey(Keys.N, Keys.Control | Keys.Shift);
                Check("キーを変えたら使用中の表示を消す", f.hotkeyStatus[2].Text.Length == 0);
                f.SetHotkeyStatus(new[] { false, false, true, false, false }, new[] { 0, 0, ctrlShiftM, 0, 0 });
                Check("まだ適用していないキーには「使用中」を出さない（登録を試した値だけ）", f.hotkeyStatus[2].Text.Length == 0);

                // 入力欄にフォーカスがある間だけホットキーを止める
                List<bool> capture = new List<bool>();
                f.HotkeyCapture += c => capture.Add(c);
                f.Tabs.SelectedIndex = 2; Application.DoEvents();
                f.ActiveControl = f.hotkeys[0];
                f.SimulateActivation(false);
                Check("入力中に別のウィンドウへ移ったら、ホットキーを使えるように戻す", capture.Count > 0 && !capture[capture.Count - 1]);
                f.SimulateActivation(true);
                Check("戻ってきたら、また止める", capture.Count > 0 && capture[capture.Count - 1]);
                f.ActiveControl = f.Tabs;
                Check("入力欄から離れたら、ホットキーを使えるように戻す", capture.Count > 0 && !capture[capture.Count - 1]);

                // アイコンの上
                f.Tabs.SelectedIndex = 0; Application.DoEvents();
                f.taskbar.Checked = true; f.taskbarMod.SelectedIndex = 0;
                Check("タスクバーで常に操作できるなら「アイコンの上」は選べない（意味がない）", !f.tray.Enabled);
                Eq("選べない理由をツールチップで出す（灰色の項目には出ないので外側の行に）", SettingsForm.TrayCoveredTip, f.TipOf(f.trayRow));
                Check("選べない項目は理由の行の中にある", f.tray.Parent == f.trayRow);
                f.taskbarMod.SelectedIndex = 1;
                Check("タスクバーに修飾キーが必要なら「アイコンの上」を選べる", f.tray.Enabled);
                Check("選べるときは理由を出さない", string.IsNullOrEmpty(f.TipOf(f.trayRow)));
                f.taskbar.Checked = false;
                Check("タスクバーオフなら「アイコンの上」を選べる", f.tray.Enabled);
                f.taskbar.Checked = true; f.taskbarMod.SelectedIndex = 0;
                f.hotkeys[2].CaptureKey(Keys.Delete, Keys.None);
                Eq("Delete で解除", 0, f.Collect().HotkeyMute);
                f.Close();
            }
        }

        static Button FindButton(Control root, string name)
        {
            foreach (Control c in root.Controls)
            {
                Button b = c as Button;
                if (b != null && b.Name == name) return b;
                Button inner = FindButton(c, name);
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

        static string FindTruncatedLabel(Control c)
        {
            foreach (Control child in c.Controls)
            {
                Label l = child as Label;
                if (l != null && l.Visible && !l.AutoSize && l.Text.Length > 0)
                {
                    int w = TextRenderer.MeasureText(l.Text, l.Font).Width;
                    if (w > l.Width) return "'" + l.Text + "' " + w + " > " + l.Width;
                }
                string inner = FindTruncatedLabel(child);
                if (inner != null) return inner;
            }
            return null;
        }

        // ---------- ミキサー画面 ----------

        sealed class FakeMixer : IMixerSource
        {
            public List<MixerDevice> List = new List<MixerDevice>();
            public string LastDefault;
            public int Refreshes;
            public List<MixerDevice> Devices { get { return List; } }
            public void Refresh(bool full) { Refreshes++; }
            MixerDevice D(string id) { return List.Find(x => x.Id == id); }
            MixerApp A(string id, string key) { MixerDevice d = D(id); return d == null ? null : d.Apps.Find(x => x.Key == key); }
            public void SetDeviceVolume(string id, int v) { D(id).Volume = v; }
            public void SetDeviceMute(string id, bool m) { D(id).Muted = m; }
            public void SetAppVolume(string id, string key, int v) { A(id, key).Volume = v; }
            public void SetAppMute(string id, string key, bool m) { A(id, key).Muted = m; }
            public void SetDefault(string id) { LastDefault = id; foreach (MixerDevice d in List) d.IsDefault = d.Id == id; }
        }

        static Bitmap FakeIcon(Color c)
        {
            Bitmap b = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush br = new SolidBrush(c)) g.FillEllipse(br, 2, 2, 28, 28);
            }
            return b;
        }

        static FakeMixer SampleMixer(int extraApps = 0)
        {
            FakeMixer m = new FakeMixer();
            MixerDevice spk = new MixerDevice { Id = "spk", Name = L.T("スピーカー (Realtek(R) Audio)", "Speakers (Realtek(R) Audio)"), IsDefault = true, Volume = 42, Peak = 0.6f };
            spk.Apps.Add(new MixerApp { Key = "chrome", Name = "Google Chrome", Icon = FakeIcon(Color.FromArgb(66, 133, 244)), Volume = 100, Peak = 0.8f });
            spk.Apps.Add(new MixerApp { Key = "spotify", Name = "Spotify", Icon = FakeIcon(Color.FromArgb(30, 215, 96)), Volume = 64, Peak = 0.5f });
            spk.Apps.Add(new MixerApp { Key = "game", Name = "とても長い名前のゲーム Ultimate Edition Remastered", Icon = FakeIcon(Color.FromArgb(230, 120, 40)), Volume = 80, Muted = true });
            for (int i = 0; i < extraApps; i++) spk.Apps.Add(new MixerApp { Key = "app" + i, Name = "App " + i, Volume = i % 101 });
            spk.Apps.Add(new MixerApp { Key = "system", Name = Terms.SystemSounds, IsSystem = true, Volume = 100 });
            MixerDevice hp = new MixerDevice { Id = "hp", Name = L.T("ヘッドホン (USB Audio Device With A Very Long Name)", "Headphones (USB Audio Device With A Very Long Name)"), Volume = 25 };
            hp.Apps.Add(new MixerApp { Key = "discord", Name = "Discord", Icon = FakeIcon(Color.FromArgb(88, 101, 242)), Volume = 70, Peak = 0.3f });
            MixerDevice hdmi = new MixerDevice { Id = "hdmi", Name = "LG ULTRAGEAR (NVIDIA High Definition Audio)", Volume = 100, Muted = true };
            m.List.Add(spk); m.List.Add(hp); m.List.Add(hdmi);
            return m;
        }

        static MixerFlyout NewFlyout(FakeMixer m, Settings s)
        {
            MixerFlyout f = new MixerFlyout(m, () => s);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Prepare(new Rectangle(wa.Right - 100, wa.Bottom + 4, 24, 24), 1f, Palette.Create(false, Theme.Accent));
            return f;
        }

        static MixerFlyout.Row RowOf(MixerFlyout f, string dev, string app)
        {
            foreach (MixerFlyout.Row r in f.Rows)
                if (r.Dev != null && r.Dev.Id == dev && (app == null ? r.App == null : r.App != null && r.App.Key == app)) return r;
            return null;
        }

        static Point CenterOf(Rectangle r) { return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2); }

        static void TestFlyout()
        {
            Settings s = new Settings();
            FakeMixer m = SampleMixer();
            using (MixerFlyout f = NewFlyout(m, s))
            {
                Eq("行数 = デバイス3 + アプリ5", 8, f.Rows.Count);
                MixerFlyout.Row chrome = RowOf(f, "spk", "chrome");
                MixerFlyout.Row spotify = RowOf(f, "spk", "spotify");
                MixerFlyout.Row spk = RowOf(f, "spk", null);
                MixerFlyout.Row hp = RowOf(f, "hp", null);
                Check("デバイスとアプリの行がある", chrome != null && spotify != null && spk != null && hp != null);
                Eq("システム音は最後", "system", m.List[0].Apps[m.List[0].Apps.Count - 1].Key);

                MixerFlyout.Row hit;
                Eq("スライダーの中央 → スライダー", MixerFlyout.Hit.Slider, f.HitTest(CenterOf(f.SliderRectOf(spotify)), out hit));
                Check("当たった行が正しい", hit == spotify);
                Eq("ミュートボタン → ミュート", MixerFlyout.Hit.Mute, f.HitTest(CenterOf(f.MuteRectOf(chrome)), out hit));
                Eq("アプリ名 → 行", MixerFlyout.Hit.Row, f.HitTest(CenterOf(f.NameRectOf(chrome)), out hit));
                Eq("デバイス名 → 折りたたみ", MixerFlyout.Hit.Chevron, f.HitTest(CenterOf(f.NameRectOf(spk)), out hit));
                Eq("既定でないデバイスの「既定にする」", MixerFlyout.Hit.SetDefault, f.HitTest(new Point(f.TagRectOf(hp).Left + 5, CenterOf(f.TagRectOf(hp)).Y), out hit));
                Check("既定のデバイスには「既定にする」が無い", f.HitTest(new Point(f.TagRectOf(spk).Left + 5, CenterOf(f.TagRectOf(spk)).Y), out hit) != MixerFlyout.Hit.SetDefault);
                Eq("上部 → 見出し", MixerFlyout.Hit.Header, f.HitTest(new Point(30, 10), out hit));
                MixerFlyout.Row hdmi = RowOf(f, "hdmi", null);
                Check("アプリの無いデバイスの名前は折りたたみにならない（矢印を描いていない）", f.HitTest(CenterOf(f.NameRectOf(hdmi)), out hit) == MixerFlyout.Hit.Row);

                // ホイール
                Point onSpotify = CenterOf(f.NameRectOf(spotify));
                f.HandleWheel(onSpotify, 120, 10000);
                Eq("アプリの行でホイール → そのアプリだけ +1", 65, m.List[0].Apps[1].Volume);
                Eq("デバイスの音量は変わらない", 42, m.List[0].Volume);
                f.HandleWheel(onSpotify, 120, 10030);
                Eq("速く回す → 5% 区切りへ (65 → 70)", 70, m.List[0].Apps[1].Volume);
                f.HandleWheel(CenterOf(f.NameRectOf(spk)), -120, 20000);
                Eq("デバイスの行でホイール → デバイス -1", 41, m.List[0].Volume);
                s.Invert = true;
                f.HandleWheel(onSpotify, 120, 30000);
                Eq("回転方向の反転もミキサーに効く", 69, m.List[0].Apps[1].Volume);
                s.Invert = false;

                // ミュート
                MixerFlyout.Row game = RowOf(f, "spk", "game");
                f.HandleWheel(CenterOf(f.NameRectOf(game)), 120, 40000);
                Check("ミュート中のアプリを上げる → 解除", !m.List[0].Apps[2].Muted);
                f.HandleClick(CenterOf(f.NameRectOf(chrome)), MouseButtons.Middle);
                Check("ホイールクリック → そのアプリだけミュート", m.List[0].Apps[0].Muted && !m.List[0].Muted && !m.List[0].Apps[1].Muted);
                f.HandleClick(CenterOf(f.MuteRectOf(chrome)), MouseButtons.Left);
                Check("ミュートボタン → 解除", !m.List[0].Apps[0].Muted);
                s.MuteAtZero = true;
                m.List[0].Apps[1].Volume = 1;
                f.HandleWheel(onSpotify, -120, 50000);
                Check("0% でミュート はミキサーでも同じ", m.List[0].Apps[1].Muted && m.List[0].Apps[1].Volume == 0);
                f.HandleWheel(onSpotify, 120, 51000);
                Check("上げると戻る", !m.List[0].Apps[1].Muted);
                s.MuteAtZero = false;

                // スライダーのクリック
                Rectangle sl = f.SliderRectOf(chrome);
                f.HandleClick(new Point(sl.Left + 8, sl.Top + sl.Height / 2), MouseButtons.Left);
                Eq("スライダーの左端クリック → 0", 0, m.List[0].Apps[0].Volume);
                f.HandleClick(new Point(sl.Right - 8, sl.Top + sl.Height / 2), MouseButtons.Left);
                Eq("スライダーの右端クリック → 100", 100, m.List[0].Apps[0].Volume);

                // 既定にする
                f.HandleClick(new Point(f.TagRectOf(hp).Left + 5, CenterOf(f.TagRectOf(hp)).Y), MouseButtons.Left);
                Eq("「既定にする」で既定のデバイスを切り替える", "hp", m.LastDefault);

                // 折りたたみ
                int before = f.Height, rowsBefore = f.Rows.Count;
                spk = RowOf(f, "spk", null);
                f.HandleClick(CenterOf(f.NameRectOf(spk)), MouseButtons.Left);
                Check("折りたたむと行が減る", f.Rows.Count == rowsBefore - m.List[0].Apps.Count, rowsBefore + " → " + f.Rows.Count);
                Check("折りたたむと低くなる", f.Height < before, before + " → " + f.Height);
                Check("折りたたみ状態を保存用に返す", new List<string>(f.CollapsedIds).Contains("spk"));
                spk = RowOf(f, "spk", null);
                Eq("折りたたむと隠れたアプリの数を出す", m.List[0].Apps.Count.ToString(), f.CollapsedCount(spk));
                Check("開いているデバイスには数を出さない", f.CollapsedCount(RowOf(f, "hp", null)) == null);
                Check("アプリの無いデバイスには数を出さない", f.CollapsedCount(RowOf(f, "hdmi", null)) == null);
                f.HandleClick(CenterOf(f.NameRectOf(spk)), MouseButtons.Left);
                Eq("もう一度で元に戻る", rowsBefore, f.Rows.Count);

                // アプリが増えたら並べ直す
                m.List[1].Apps.Add(new MixerApp { Key = "new", Name = "New App", Volume = 50 });
                f.SimulateFrame();
                Eq("アプリが増えたら行が増える", rowsBefore + 1, f.Rows.Count);

                // ポップアップ ⇔ ウィンドウ
                f.Location = new Point(-32000, -32000);
                f.Show(); Application.DoEvents();
                Check("ポップアップはタスクバーに出さない・枠なし・最前面", !f.Windowed && !f.ShowInTaskbar && f.FormBorderStyle == FormBorderStyle.None && f.TopMost);
                f.SimulateDeactivate();
                Check("ポップアップは、ほかをクリックすると閉じる", !f.Visible);
                f.Show(); Application.DoEvents();
                MixerFlyout.Row hb;
                Eq("ポップアップの右上はウィンドウ化ボタン", MixerFlyout.Hit.Window, f.HitTest(CenterOf(HeaderButton(f, 0)), out hb));
                Eq("ポップアップには最前面ボタンが無い（常に最前面）", MixerFlyout.Hit.Gear, f.HitTest(CenterOf(HeaderButton(f, 1)), out hb));
                int rowsInPopup = f.Rows.Count;
                f.HandleClick(CenterOf(HeaderButton(f, 0)), MouseButtons.Left);
                Application.DoEvents();
                Check("ウィンドウ化 → タスクバーに出る・タイトルバーあり・大きさを変えられる（Alt+Tab・タスクバーから選べる）", f.Windowed && f.ShowInTaskbar && f.FormBorderStyle == FormBorderStyle.Sizable && !f.MaximizeBox && f.Visible);
                Check("ウィンドウ化しても中身の幅は同じ", f.ClientSize.Width == (int)Math.Round(400 * f.UiScale), f.ClientSize.Width.ToString());
                Eq("ウィンドウ化しても行はそのまま", rowsInPopup, f.Rows.Count);
                Check("ウィンドウは最前面にしない（既定）", !f.TopMost);
                f.SimulateDeactivate();
                Check("ウィンドウは、ほかをクリックしても閉じない", f.Visible);
                Eq("ウィンドウでは最前面ボタンが出る", MixerFlyout.Hit.Pin, f.HitTest(CenterOf(HeaderButton(f, 1)), out hb));
                f.HandleClick(CenterOf(HeaderButton(f, 1)), MouseButtons.Left);
                Check("最前面ボタン → 最前面", f.Topmost && f.TopMost);
                f.HandleClick(CenterOf(HeaderButton(f, 1)), MouseButtons.Left);
                Check("もう一度で解除", !f.Topmost && !f.TopMost);
                MixerFlyout.Row spRow = RowOf(f, "spk", "spotify");
                int vol0 = m.List[0].Apps[1].Volume;
                f.HandleWheel(CenterOf(f.NameRectOf(spRow)), 120, 90000);
                Check("ウィンドウでも行の上のホイールで音量が変わる（当たり判定が枠の分ずれない）", m.List[0].Apps[1].Volume == vol0 + 1, vol0 + " → " + m.List[0].Apps[1].Volume);

                // 大きさを変える
                float k = f.UiScale;
                int head = (int)Math.Round(40 * k), pad = (int)Math.Round(6 * k);
                Check("大きさを変えるまでは中身に合わせる（覚えていない）", f.UserClientSize.IsEmpty);
                int slider0 = f.SliderWidthOf(), w0 = f.ClientSize.Width;
                f.SimulateUserResize(new Size(w0 + 300, head + pad + (int)Math.Round(150 * k)));
                Check("幅を広げるとスライダーが伸びる（細かく合わせやすい）", f.SliderWidthOf() >= slider0 + 190, slider0 + " → " + f.SliderWidthOf());
                Check("枠をドラッグした大きさを覚える", f.UserClientSize.Width == (int)Math.Round(f.ClientSize.Width / k) && f.UserClientSize.Height == (int)Math.Round(f.ClientSize.Height / k), f.UserClientSize.ToString());
                Eq("表示範囲は窓の高さに合わせる", f.ClientSize.Height - head - pad, f.ViewHeight);
                Check("低くすると入りきらない分はスクロール", f.ContentHeight > f.ViewHeight);
                int miss = 0;
                foreach (MixerFlyout.Row r in f.Rows)
                {
                    if (r.Dev == null) continue;
                    Point c = CenterOf(f.SliderRectOf(r));
                    if (c.Y < head || c.Y >= head + f.ViewHeight) continue; // 見えていない行
                    MixerFlyout.Row h;
                    if (f.HitTest(c, out h) != MixerFlyout.Hit.Slider || h != r) miss++;
                    if (f.MuteRectOf(r).Right > f.SliderRectOf(r).Left || f.SliderRectOf(r).Right > f.ClientSize.Width) miss++;
                }
                Eq("広げても当たり判定と描画が一致・はみ出さない", 0, miss);
                int hBefore = f.Height;
                m.List[1].Apps.Add(new MixerApp { Key = "new2", Name = "New App 2", Volume = 50 });
                f.SimulateFrame();
                Eq("大きさを決めた後はアプリが増えても窓の大きさは変わらない", hBefore, f.Height);
                f.SimulateUserResize(new Size(50, 20));
                Check("既定の幅・1 行分より小さくはできない", f.ClientSize.Width >= (int)Math.Round(400 * k) && f.ClientSize.Height >= head + pad + (int)Math.Round(40 * k), f.ClientSize.ToString());
                Check("枠のドラッグにも下限がある（MinimumSize）", f.MinimumSize.Width >= (int)Math.Round(400 * k) && f.MinimumSize.Height > head + pad, f.MinimumSize.ToString());
                Check("覚える大きさも下限より小さくならない", f.UserClientSize.Width >= 400 && f.UserClientSize.Height >= 86, f.UserClientSize.ToString());
                f.SimulateUserResize(new Size(w0 + 300, head + pad + (int)Math.Round(150 * k)));
                f.Close();
                Application.DoEvents();
                Check("ウィンドウの × は隠すだけ（破棄しない）", !f.Visible && !f.IsDisposed);
                f.Show(); Application.DoEvents();
                f.HandleClick(CenterOf(HeaderButton(f, 0)), MouseButtons.Left);
                Application.DoEvents();
                Check("ポップアップに戻す → 元の見た目", !f.Windowed && !f.ShowInTaskbar && f.FormBorderStyle == FormBorderStyle.None && f.TopMost);
                Check("ポップアップは決めた大きさを使わない（既定の幅・中身に合わせた高さ）", f.ClientSize.Width == (int)Math.Round(400 * f.UiScale) && f.ClientSize.Height == (int)Math.Round(40 * f.UiScale) + f.ViewHeight + (int)Math.Round(6 * f.UiScale) && f.ViewHeight <= f.ContentHeight && !f.UserClientSize.IsEmpty, f.ClientSize.ToString());
                f.HandleClick(CenterOf(HeaderButton(f, 0)), MouseButtons.Left);
                Application.DoEvents();
                Check("もう一度ウィンドウにすると決めた大きさに戻る", f.ClientSize.Width == (int)Math.Round(f.UserClientSize.Width * f.UiScale), f.ClientSize.ToString());
                f.HandleClick(CenterOf(HeaderButton(f, 0)), MouseButtons.Left);
                f.HideFlyout();
            }

            using (MixerFlyout f = NewFlyout(SampleMixer(), new Settings { MixerWindowed = true, MixerSize = "700,300" }))
            {
                Check("保存した大きさで開く", f.ClientSize == new Size(700, 300), f.ClientSize.ToString());
                Eq("表示範囲は保存した高さから", 300 - 40 - 6, f.ViewHeight);
            }
            using (MixerFlyout f = NewFlyout(SampleMixer(), new Settings { MixerWindowed = false, MixerSize = "700,300" }))
                Check("ポップアップでは保存した大きさを使わない", f.ClientSize.Width == 400, f.ClientSize.ToString());
            using (MixerFlyout f = NewFlyout(SampleMixer(), new Settings { MixerWindowed = true, MixerSize = "9000,9000" }))
            {
                Rectangle wa = Screen.FromPoint(f.Location).WorkingArea;
                Check("画面より大きい保存値でも作業領域に収める", f.Width <= wa.Width && f.Height <= wa.Height, f.Size + " / " + wa.Size);
            }

            Settings pinnedSettings = new Settings { MixerWindowed = true, MixerTopmost = true, CollapsedDevices = "hp" };
            using (MixerFlyout f = NewFlyout(SampleMixer(), pinnedSettings))
            {
                Check("保存したウィンドウ表示・最前面の状態で開く", f.Windowed && f.Topmost && f.TopMost && f.ShowInTaskbar);
                Check("保存した折りたたみの状態で開く", RowOf(f, "hp", "discord") == null && RowOf(f, "spk", "chrome") != null);
            }

            using (MixerFlyout f = NewFlyout(new FakeMixer(), new Settings()))
            {
                Eq("デバイスが無いときはメッセージ 1 行", 1, f.Rows.Count);
                MixerFlyout.Row r;
                f.HitTest(new Point(100, 60), out r);
                f.HandleWheel(new Point(100, 60), 120, 0);
                f.HandleClick(new Point(100, 60), MouseButtons.Middle);
                Check("デバイスが無くても操作で例外が出ない", true);
            }

            // 長時間使ったときのハンドルリーク
            using (MixerFlyout f = NewFlyout(SampleMixer(), new Settings()))
            {
                for (int i = 0; i < 5; i++) { f.Show(); Application.DoEvents(); f.HideFlyout(); }
                uint g0 = Gdi(), u0 = User();
                for (int i = 0; i < 200; i++)
                {
                    f.Show();
                    f.SimulateFrame();
                    f.Refresh();
                    f.HideFlyout();
                }
                uint g1 = Gdi(), u1 = User();
                Check("ミキサーを200回開閉・描画してもGDIハンドルが増えない", g1 <= g0 + 10, g0 + " → " + g1);
                Check("ミキサーを200回開閉・描画してもUSERハンドルが増えない", u1 <= u0 + 10, u0 + " → " + u1);
            }
        }

        /// <summary>見出しのボタン（右から index 番目）。</summary>
        static Rectangle HeaderButton(MixerFlyout f, int index)
        {
            int size = (int)Math.Round(32 * f.UiScale), y = ((int)Math.Round(40 * f.UiScale) - size) / 2;
            return new Rectangle(f.ClientSize.Width - (int)Math.Round(6 * f.UiScale) - size * (index + 1), y, size, size);
        }

        static void TestFlyoutLayout()
        {
            foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
                foreach (Language lang in new[] { Language.Japanese, Language.English })
                foreach (string userSize in new[] { "", "700,500", "1100,900" }) // ポップアップ、ウィンドウで大きさを変えたとき
                {
                    L.Set(lang);
                    string ctx = " (x" + scale + ", " + lang + (userSize == "" ? "" : ", window " + userSize) + ")";
                    Settings ls = new Settings { MixerWindowed = userSize != "", MixerSize = userSize };
                    using (MixerFlyout f = new MixerFlyout(SampleMixer(), () => ls))
                    {
                        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                        f.Prepare(new Rectangle(wa.Right - 100, wa.Bottom + 4, 24, 24), scale, Palette.Create(true, Theme.Accent));
                        int bad = 0; string first = null;
                        foreach (MixerFlyout.Row r in f.Rows)
                        {
                            if (r.Dev == null) continue;
                            Rectangle name = f.NameRectOf(r), mute = f.MuteRectOf(r), slider = f.SliderRectOf(r);
                            string why = null;
                            if (name.Width < 40 * scale) why = "名前の幅が狭い " + name.Width;
                            else if (r.App != null && name.Right > mute.Left) why = "名前とミュートが重なる";
                            else if (mute.Right > slider.Left) why = "ミュートとスライダーが重なる";
                            else if (slider.Right > f.Width) why = "右にはみ出す";
                            else if (r.IsDevice && f.TagRectOf(r).Right > mute.Left) why = "既定の表示とミュートが重なる";
                            else if (r.IsDevice && TextRenderer.MeasureText(Terms.DefaultDevice, f.SmallFont).Width > f.TagRectOf(r).Width) why = "「既定のデバイス」が入らない";
                            else if (r.IsDevice && TextRenderer.MeasureText(Terms.SetDefault, f.SmallFont).Width > f.TagRectOf(r).Width) why = "「既定にする」が入らない";
                            if (why != null) { bad++; if (first == null) first = (r.App != null ? r.App.Name : r.Dev.Name) + ": " + why; }
                        }
                        // 描いた位置とクリックの当たり判定が一致するか
                        int miss = 0;
                        int head = (int)Math.Round(40 * scale);
                        foreach (MixerFlyout.Row r in f.Rows)
                        {
                            if (r.Dev == null) continue;
                            int cy = CenterOf(f.SliderRectOf(r)).Y;
                            if (cy - 16 * scale < head || cy + 16 * scale >= head + f.ViewHeight) continue; // スクロールで隠れている行
                            MixerFlyout.Row h;
                            if (f.HitTest(CenterOf(f.SliderRectOf(r)), out h) != MixerFlyout.Hit.Slider || h != r) miss++;
                            if (f.HitTest(CenterOf(f.MuteRectOf(r)), out h) != MixerFlyout.Hit.Mute || h != r) miss++;
                        }
                        Eq("スライダーとミュートの当たり判定が描画と一致" + ctx, 0, miss);
                        Eq("部品が重ならず収まる" + ctx, 0, bad);
                        if (userSize != "") Check("広げた分スライダーが長い" + ctx, f.SliderWidthOf() > (int)Math.Round(120 * scale) + 100, f.SliderWidthOf().ToString());
                        if (first != null) Console.WriteLine("      " + first);
                        Check("作業領域に収まる" + ctx, wa.Contains(f.Bounds), f.Bounds + " / " + wa);
                    }
                }
            L.Set(Language.Japanese);

            // 大量のアプリ → スクロール、画面に収まる
            using (MixerFlyout f = NewFlyout(SampleMixer(60), new Settings()))
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Check("アプリが多いと中身が表示範囲より長い（スクロール）", f.ContentHeight > f.ViewHeight);
                Check("アプリが多くても画面からはみ出さない", wa.Contains(f.Bounds), f.Bounds + " / " + wa);
                MixerFlyout.Row last = f.Rows[f.Rows.Count - 1];
                for (int i = 0; i < 200; i++) f.HandleWheel(new Point(30, 10), -120, i * 1000); // 見出しの上でホイール → スクロール
                Point lp = new Point(100, (int)Math.Round(40 * f.UiScale) + f.ViewHeight - 5);
                MixerFlyout.Row hit;
                f.HitTest(lp, out hit);
                Check("見出しの上でホイール → 最後までスクロールできる", hit == last, hit == null ? "null" : (hit.App != null ? hit.App.Name : "device"));
            }

            // 位置
            int bad2 = 0; string firstBad = null;
            List<Rectangle> areas = new List<Rectangle>();
            foreach (Screen sc in Screen.AllScreens) areas.Add(sc.WorkingArea);
            areas.Add(new Rectangle(-1920, 0, 1920, 1040));
            areas.Add(new Rectangle(0, 40, 1280, 680));
            areas.Add(new Rectangle(48, 0, 1872, 1080)); // 左にタスクバー
            areas.Add(new Rectangle(0, 0, 1024, 300));   // 極端に低い
            foreach (Rectangle wa in areas)
                foreach (Size size in new[] { new Size(400, 300), new Size(800, 1400), new Size(400, 1000) })
                    foreach (Point c in Probe(wa))
                    {
                        Point p = MixerFlyout.ComputeLocation(c, size, wa, 12);
                        Rectangle r = new Rectangle(p, size);
                        bool fits = size.Width <= wa.Width && size.Height <= wa.Height;
                        if (fits ? !wa.Contains(r) : (r.Left < wa.Left || r.Top < wa.Top)) { bad2++; if (firstBad == null) firstBad = "wa=" + wa + " anchor=" + c + " size=" + size + " -> " + p; }
                    }
            Check("ミキサーはタスクバーが上下左右どこでも作業領域内に開く", bad2 == 0, firstBad);
        }

        static void TestMeters()
        {
            Near("上がるのは即座", 0.8f, PeakMeter.Next(0.1f, 0.8f, 33));
            float v = 1f;
            for (int i = 0; i < 15; i++) v = PeakMeter.Next(v, 0f, 33); // 約 0.5 秒
            Check("音が止まると 0.5 秒ほどで消える", v == 0f, v.ToString());
            float slow = PeakMeter.Next(1f, 0f, 500), fast = 1f;
            for (int i = 0; i < 5; i++) fast = PeakMeter.Next(fast, 0f, 100);
            Check("下がり方は描画の間隔に左右されない（500ms 1 回 ≒ 100ms 5 回）", Math.Abs(slow - fast) < 0.01f, slow + " / " + fast);
            Check("途中で音が来たらそこで止まる", PeakMeter.Next(0.9f, 0.5f, 1000) == 0.5f);
            Near("アプリ: 信号1.0 × アプリ100% → 1.0（デバイスの音量は掛けない）", 1f, PeakMeter.Level(1f, 100, false, 11, false, true));
            Near("アプリ: 信号0.8 × アプリ50% → 0.4", 0.4f, PeakMeter.Level(0.8f, 50, false, 11, false, true));
            Near("デバイスがミュートならアプリのメーターも 0", 0f, PeakMeter.Level(1f, 100, false, 100, true, true));
            Near("ミュートしたアプリは 0", 0f, PeakMeter.Level(1f, 100, true, 100, false, true));
            Near("デバイス: 信号 × デバイスの音量", 0.3f, PeakMeter.Level(0.6f, 50, false, 50, false, false));

            FakeMixer m = SampleMixer(20);
            using (MixerFlyout f = NewFlyout(m, new Settings()))
            {
                m.List[0].Volume = 11; m.List[0].Apps[0].Volume = 100; m.List[0].Apps[0].Peak = 0.7f;
                f.SimulateFrame();
                MixerFlyout.Row chrome = RowOf(f, "spk", "chrome");
                Check("デバイスを 11% に下げていても、アプリのメーターは信号どおりに伸びる（小さくならない）", Math.Abs(f.MeterOf(chrome) - 0.7f) < 0.02f, f.MeterOf(chrome).ToString());
                m.List[0].Apps[0].Peak = 0f;
                for (int i = 0; i < 20; i++) { System.Threading.Thread.Sleep(30); f.SimulateFrame(); }
                Check("音が止まると実際の時間で 0.6 秒ほどで消える", f.MeterOf(chrome) == 0f, f.MeterOf(chrome).ToString());
                Palette dark = Palette.Create(false, Theme.Accent), lightP = Palette.Create(true, Theme.Accent);
                Check("メーターの色は音量の色と区別できる（ダーク・ライト）", ColorGap(dark.Meter, dark.Accent) > 150 && ColorGap(lightP.Meter, lightP.Accent) > 150);

                // 描画の速さ（30 fps に間に合うか）
                f.Prepare(new Rectangle(Screen.PrimaryScreen.WorkingArea.Right - 100, Screen.PrimaryScreen.WorkingArea.Bottom + 4, 24, 24), 1.5f, Palette.Create(false, Theme.Accent));
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    for (int i = 0; i < 5; i++) f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    Stopwatch sw = Stopwatch.StartNew();
                    for (int i = 0; i < 60; i++) { f.SimulateFrame(); f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); }
                    double ms = sw.ElapsedMilliseconds / 60.0;
                    Console.WriteLine("      (アプリ 24 個・150% で 1 フレーム " + ms.ToString("0.0") + "ms)");
                    Check("1 フレームの更新と描画が 20ms 未満（30 fps に間に合う）", ms < 20, ms.ToString("0.0") + "ms");
                }
            }
        }

        static int ColorGap(Color a, Color b) { return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); }

        static void TestMenuPlacement()
        {
            Size menu = new Size(260, 420);
            // 2 枚並び（左に隣の画面）、メイン画面のタスクバーを上下左右に置く
            Rectangle main = new Rectangle(0, 0, 1920, 1080);
            var cases = new[]
            {
                new { Name = "下", WA = new Rectangle(0, 0, 1920, 1032), Icon = new Point(1700, 1056) },
                new { Name = "上", WA = new Rectangle(0, 48, 1920, 1032), Icon = new Point(1700, 24) },
                new { Name = "左", WA = new Rectangle(64, 0, 1856, 1080), Icon = new Point(32, 1000) },
                new { Name = "右", WA = new Rectangle(0, 0, 1856, 1080), Icon = new Point(1888, 1000) },
                new { Name = "左（アイコンが上寄り）", WA = new Rectangle(64, 0, 1856, 1080), Icon = new Point(32, 60) },
                new { Name = "下（アイコンが左端）", WA = new Rectangle(0, 0, 1920, 1032), Icon = new Point(10, 1056) },
            };
            foreach (var c in cases)
            {
                ToolStripDropDownDirection d = MenuPlacement.Direction(c.Icon, c.WA);
                Rectangle r = MenuPlacement.Bounds(c.Icon, menu, d);
                Check("タスクバーが" + c.Name + ": メニューが同じ画面に収まる（隣の画面に出ない）", main.Contains(r), d + " " + r);
            }
            // 画面のどこから開いても、その画面に収まる
            int bad = 0; string first = null;
            foreach (Rectangle wa in new[] { new Rectangle(0, 0, 1920, 1032), new Rectangle(64, 0, 1856, 1080), new Rectangle(0, 48, 1920, 1032), new Rectangle(0, 0, 1856, 1080) })
                for (int x = 0; x < 1920; x += 40)
                    for (int y = 0; y < 1080; y += 40)
                    {
                        Point pt = new Point(x, y);
                        Rectangle r = MenuPlacement.Bounds(pt, menu, MenuPlacement.Direction(pt, wa));
                        if (!main.Contains(r)) { bad++; if (first == null) first = wa + " " + pt + " -> " + r; }
                    }
            Eq("画面のどこで開いてもはみ出さない", 0, bad);
            if (first != null) Console.WriteLine("      " + first);
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
            areas.Add(new Rectangle(0, 40, 1280, 680));
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

        static void TestMixerSource()
        {
            using (MixerSource m = new MixerSource())
            {
                m.Refresh(true);
                Check("再生デバイスを列挙できる", m.Devices.Count >= 1, "count=" + m.Devices.Count);
                int defaults = 0;
                foreach (MixerDevice d in m.Devices)
                {
                    if (d.IsDefault) defaults++;
                    Console.WriteLine("      " + (d.IsDefault ? "* " : "  ") + d.Name + "  " + d.Volume + "%" + (d.Muted ? " muted" : ""));
                    foreach (MixerApp a in d.Apps)
                        Console.WriteLine("          " + a.Name + "  " + a.Volume + "%" + (a.Muted ? " muted" : "") + (a.Icon != null ? "  [icon]" : ""));
                    Check("音量は 0〜100: " + d.Name, d.Volume >= 0 && d.Volume <= 100);
                    foreach (MixerApp a in d.Apps) Check("アプリ名が取れる: " + a.Name, !string.IsNullOrEmpty(a.Name));
                }
                if (m.Devices.Count > 0)
                {
                    Eq("既定デバイスはちょうど1つ", 1, defaults);
                    Check("既定のデバイスが先頭", m.Devices[0].IsDefault);
                }

                // 既存のアプリの音量を同じ値で書き戻す（音は変わらない）
                foreach (MixerDevice d in m.Devices)
                    foreach (MixerApp a in d.Apps) { int v = a.Volume; m.SetAppVolume(d.Id, a.Key, v); }
                m.Refresh(false);
                Check("アプリの音量を書き戻しても例外なし", true);

                for (int i = 0; i < 20; i++) m.Refresh(true);
                uint h0 = (uint)Process.GetCurrentProcess().HandleCount;
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < 300; i++) m.Refresh(i % 10 == 0);
                long ms = sw.ElapsedMilliseconds;
                GC.Collect(); GC.WaitForPendingFinalizers();
                uint h1 = (uint)Process.GetCurrentProcess().HandleCount;
                Check("一覧の取り直しを繰り返してもハンドルが増え続けない", h1 <= h0 + 30, h0 + " → " + h1);
                Console.WriteLine("      (1 フレームの更新: 平均 " + (ms / 300.0).ToString("0.00") + "ms)");
                Check("1 フレームの更新は軽い（平均 30ms 未満）", ms / 300.0 < 30, (ms / 300.0).ToString("0.00") + "ms");
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

        static void TestLaunchTargets()
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            Check("サウンド コントロール パネル（mmsys.cpl）がある", File.Exists(Path.Combine(sys, "mmsys.cpl")));
            Check("Windows の音量ミキサー（sndvol.exe）がある", File.Exists(Path.Combine(sys, "SndVol.exe")));
            Check("コントロール パネル（control.exe）がある", File.Exists(Path.Combine(sys, "control.exe")));
            Eq("サウンド コントロール パネルは「再生」タブで開く", "mmsys.cpl,,0", TrayApp.SoundControlPanelArgs);
        }

        /// <summary>用語の統一（TERMS.md と同じ表記か、同じものを指す箇所で同じ言葉を使っているか）。</summary>
        static void TestTerms()
        {
            foreach (Language lang in new[] { Language.Japanese, Language.English })
            {
                L.Set(lang);
                string ctx = " (" + lang + ")";
                bool ja = lang == Language.Japanese;
                Eq("ミキサーの名前" + ctx, ja ? "ScrollVolume ミキサー" : "ScrollVolume Mixer", Terms.Mixer);
                Check("ScrollVolume のミキサーは必ず「ScrollVolume」を付ける（Windows の音量ミキサーと区別）" + ctx, Terms.Mixer.StartsWith("ScrollVolume") && Terms.OpenMixer.Contains(Terms.Mixer));
                Check("Windows の音量ミキサーを開く項目は Windows の呼び名を含む" + ctx, Terms.OpenWindowsMixer.ToLowerInvariant().Contains(Terms.WindowsMixer.ToLowerInvariant()));
                Eq("音量の表示" + ctx, ja ? "音量: 42%" : "Volume: 42%", Terms.VolumeLine(42, false));
                Eq("ミュート中" + ctx, ja ? "音量: 42%（ミュート）" : "Volume: 42% (muted)", Terms.VolumeLine(42, true));
                Eq("デバイスなし" + ctx, ja ? "音量: 再生デバイスがありません" : "Volume: no playback device", Terms.VolumeLine(-1, false));

                using (SettingsForm f = new SettingsForm(new Settings(), false, (a, b) => { }, () => 50))
                {
                    Eq("クリック動作の「ミキサーを開く」" + ctx, Terms.OpenMixer, f.leftClick.Items[(int)ClickAction.Slider]);
                    Eq("クリック動作の「Windows の音量ミキサー」" + ctx, Terms.OpenWindowsMixer, f.leftClick.Items[(int)ClickAction.Mixer]);
                    Eq("クリック動作の「ミュートの切り替え」" + ctx, Terms.ToggleMute, f.leftClick.Items[(int)ClickAction.Mute]);
                    Eq("ホットキーの「ミキサーを開く」はクリック動作と同じ言葉" + ctx, Terms.OpenMixer, ((Label)f.hotkeys[3].Parent.Controls[0]).Text);
                    Eq("ホットキーの「ミュートの切り替え」はクリック動作と同じ言葉" + ctx, Terms.ToggleMute, ((Label)f.hotkeys[2].Parent.Controls[0]).Text);
                    Eq("ホットキーの「再生デバイスの切り替え」" + ctx, Terms.NextDevice, ((Label)f.hotkeys[4].Parent.Controls[0]).Text);
                    Eq("設定画面の題名" + ctx, Terms.Settings, f.Text);
                }
                using (MixerFlyout fl = NewFlyout(SampleMixer(), new Settings()))
                    Eq("ミキサーの題名" + ctx, Terms.Mixer, fl.Text);
            }
            L.Set(Language.Japanese);

            // トレイメニューの太字 = 左クリックと同じ動作
            using (ToolStripMenuItem mixer = new ToolStripMenuItem(), mute = new ToolStripMenuItem(), win = new ToolStripMenuItem())
            {
                Check("左クリック = ScrollVolume ミキサー → その項目が太字", TrayApp.DefaultItem(ClickAction.Slider, mixer, mute, win) == mixer);
                Check("左クリック = ミュート → ミュートが太字", TrayApp.DefaultItem(ClickAction.Mute, mixer, mute, win) == mute);
                Check("左クリック = Windows の音量ミキサー → その項目が太字", TrayApp.DefaultItem(ClickAction.Mixer, mixer, mute, win) == win);
                Check("左クリック = 何もしない → 太字なし", TrayApp.DefaultItem(ClickAction.None, mixer, mute, win) == null);
            }
        }

        static void TestHook()
        {
            NativeWindow w = new NativeWindow();
            w.CreateHandle(new CreateParams());
            try
            {
                WheelHook h = new WheelHook(w.Handle, Native.WM_APP + 51, Native.WM_APP + 52, new Settings());
                h.Start();
                System.Threading.Thread.Sleep(200);
                Check("フックを張れる", h.IsInstalled);
                for (int i = 0; i < 20; i++) h.Reinstall();
                System.Threading.Thread.Sleep(300);
                Check("張り直しを20回しても張られたまま", h.IsInstalled && h.IsThreadAlive);
                h.SetOwnRect(new Rectangle(10, 10, 100, 100));
                h.SetOwnRect(Rectangle.Empty);
                h.Stop();
                Check("停止するとスレッドが終わる", !h.IsThreadAlive);
            }
            finally { w.DestroyHandle(); }

            NativeWindow hk = new NativeWindow();
            hk.CreateHandle(new CreateParams());
            try
            {
                // ふだん誰も使わない組み合わせで登録と解除ができるか
                bool reg = Native.RegisterHotKey(hk.Handle, 1, (uint)(Hotkey.Ctrl | Hotkey.Alt | Hotkey.Shift) | Native.MOD_NOREPEAT, (uint)Keys.F24);
                Check("ホットキーを登録できる", reg);
                bool again = Native.RegisterHotKey(hk.Handle, 2, (uint)(Hotkey.Ctrl | Hotkey.Alt | Hotkey.Shift) | Native.MOD_NOREPEAT, (uint)Keys.F24);
                Check("同じ組み合わせの二重登録は失敗として検出できる", !again);
                Check("ホットキーを解除できる", Native.UnregisterHotKey(hk.Handle, 1));
            }
            finally { hk.DestroyHandle(); }
        }

        static void TestElevation()
        {
            bool elevated = ElevatedTask.IsElevated();
            Console.WriteLine("      (このテストの権限: " + (elevated ? "管理者" : "通常") + ")");
            Check("権限の判定で例外が出ない", true);

            string path = @"C:\Tools\テスト & 'フォルダ' (1)\ScrollVolume.exe";
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
            foreach (Language lang in new[] { Language.Japanese, Language.English })
            {
                L.Set(lang);
                string tag = lang == Language.English ? "en" : "ja";
                using (SettingsForm f = new SettingsForm(new Settings { HotkeyVolumeUp = Hotkey.Make(Hotkey.Ctrl | Hotkey.Alt, Keys.Up) }, false, (a, b) => { }, () => 45))
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-32000, -32000);
                    f.Show();
                    for (int i = 0; i < f.Tabs.TabPages.Count; i++)
                    {
                        f.Tabs.SelectedIndex = i;
                        Application.DoEvents();
                        Save(f, Path.Combine(dir, "settings_" + tag + "_" + i + ".png"));
                    }
                    f.Close();
                }
                foreach (bool light in new[] { false, true })
                    using (MixerFlyout fl = new MixerFlyout(SampleMixer(), () => new Settings()))
                    {
                        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                        fl.Prepare(new Rectangle(wa.Right - 100, wa.Bottom + 4, 24, 24), 1.5f, Palette.Create(light, Theme.SystemAccent()));
                        fl.Location = new Point(-32000, -32000);
                        fl.Show(); fl.SimulateFrame(); Application.DoEvents();
                        Save(fl, Path.Combine(dir, "mixer_" + tag + "_" + (light ? "light" : "dark") + ".png"));
                        fl.HideFlyout();
                    }
            }
            L.Set(Language.Japanese);
            using (MixerFlyout fl = new MixerFlyout(SampleMixer(), () => new Settings { MixerWindowed = true, MixerSize = "620,330" }))
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                fl.Prepare(new Rectangle(wa.Right - 100, wa.Bottom + 4, 24, 24), 1.5f, Palette.Create(false, Theme.SystemAccent()));
                fl.Location = new Point(-32000, -32000);
                fl.Show(); fl.SimulateFrame(); Application.DoEvents();
                Save(fl, Path.Combine(dir, "mixer_window_resized.png"));
                fl.HideFlyout();
            }
            using (MixerFlyout fl = new MixerFlyout(SampleMixer(), () => new Settings { CollapsedDevices = "spk|hp", MixerWindowed = true, MixerTopmost = true }))
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                fl.Prepare(new Rectangle(wa.Right - 100, wa.Bottom + 4, 24, 24), 1.5f, Palette.Create(false, Theme.SystemAccent()));
                fl.Location = new Point(-32000, -32000);
                fl.Show(); Application.DoEvents();
                Save(fl, Path.Combine(dir, "mixer_window_collapsed.png"));
                fl.HideFlyout();
            }
            L.Set(Language.Japanese);
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
