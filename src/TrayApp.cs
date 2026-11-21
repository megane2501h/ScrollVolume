using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScrollVolume
{
    static class VolumeMath
    {
        /// <summary>前回のホイールからの間隔(ms)に応じた倍率。</summary>
        public static int AccelMultiplier(Settings s, int dtMs)
        {
            if (!s.Accel || dtMs < 0) return 1;
            if (dtMs < 40) return 1 + s.AccelStrength;
            if (dtMs < 90) return 1 + (s.AccelStrength + 1) / 2;
            return 1;
        }

        /// <summary>現在値 cur から steps ノッチ（負なら下げる）動かした後の音量 0〜100。</summary>
        public static int Next(int cur, int steps, int amount, bool roundStep)
        {
            if (amount < 1) amount = 1;
            cur = Math.Max(0, Math.Min(100, cur));
            int next;
            if (roundStep)
                next = steps > 0 ? (cur / amount + steps) * amount : ((cur + amount - 1) / amount + steps) * amount;
            else
                next = cur + steps * amount;
            return Math.Max(0, Math.Min(100, next));
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        public const uint WM_WHEELZONE = Native.WM_APP + 1;
        public const uint WM_TRAYCALLBACK = Native.WM_APP + 2;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ScrollVolume";

        readonly MsgWindow win;
        readonly TrayIcon tray;
        readonly Audio audio = new Audio();
        readonly WheelHook hook;
        readonly Osd osd = new Osd();
        readonly VolumePopup popup = new VolumePopup();
        readonly ContextMenuStrip menu;
        readonly Timer poll = new Timer();
        Settings settings;
        SettingsForm settingsForm;

        int lastPercent = -2;
        bool lastMuted, lastLight;
        int tick;
        int accum, lastWheelTick, lastTrayRectTick;
        int forceAudioResetAt; // 復帰後しばらくしてからもう一度デバイスを取り直す

        public TrayApp()
        {
            settings = Settings.Load();
            lastLight = Theme.IsLightTaskbar();

            win = new MsgWindow(this);
            tray = new TrayIcon(win.Handle, WM_TRAYCALLBACK);
            menu = BuildMenu();

            popup.VolumeChanged += v => { audio.SetPercent(v); if (v > 0 && settings.UnmuteOnChange) { int p; bool m; if (audio.TryGet(out p, out m) && m) audio.SetMute(false); } UpdateIcon(); };
            popup.MuteChanged += m => { audio.SetMute(m); UpdateIcon(); };
            popup.OpenMixer += () => Launch("sndvol.exe", null);

            UpdateIcon();
            UpdateTrayRect();

            hook = new WheelHook(win.Handle, WM_WHEELZONE, settings);
            hook.Start();

            poll.Interval = 500;
            poll.Tick += delegate { OnPoll(); };
            poll.Start();

            Log.Write("started");
        }

        // ---------- メニュー ----------

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Opening += delegate { FillMenu(m); };
            m.Items.Add("..."); // Opening が呼ばれるようにダミーを入れておく
            return m;
        }

        void FillMenu(ContextMenuStrip m)
        {
            m.Items.Clear();
            int pct; bool muted;
            bool ok = audio.TryGet(out pct, out muted);

            m.Items.Add(new ToolStripMenuItem(ok ? "音量: " + pct + "%" + (muted ? "（ミュート）" : "") : "再生デバイスがありません") { Enabled = false });

            ToolStripMenuItem presets = new ToolStripMenuItem("音量");
            for (int v = 100; v >= 0; v -= 10)
            {
                int target = v;
                ToolStripMenuItem it = new ToolStripMenuItem(v + "%", null, delegate { audio.SetPercent(target); UpdateIcon(); });
                it.Checked = ok && pct == v;
                presets.DropDownItems.Add(it);
            }
            m.Items.Add(presets);

            ToolStripMenuItem mute = new ToolStripMenuItem("ミュート", null, delegate { audio.ToggleMute(); UpdateIcon(); });
            mute.Checked = muted;
            m.Items.Add(mute);

            ToolStripMenuItem devices = new ToolStripMenuItem("再生デバイス");
            foreach (AudioDevice d in audio.ListDevices())
            {
                string id = d.Id;
                ToolStripMenuItem it = new ToolStripMenuItem(d.Name, null, delegate { audio.SetDefaultDevice(id); audio.Reset(); UpdateIcon(); });
                it.Checked = d.IsDefault;
                devices.DropDownItems.Add(it);
            }
            if (devices.DropDownItems.Count == 0) devices.Enabled = false;
            m.Items.Add(devices);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("音量ミキサー", null, delegate { Launch("sndvol.exe", null); });
            m.Items.Add("サウンド設定", null, delegate { Launch("ms-settings:sound", null); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("設定...", null, delegate { ShowSettings(); }) { Font = new Font(m.Font, FontStyle.Bold) });
            m.Items.Add("終了", null, delegate { ExitThread(); });
        }

        // ---------- トレイアイコン ----------

        internal void OnTrayCallback(IntPtr wParam, IntPtr lParam)
        {
            int ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case Native.WM_MOUSEMOVE:
                    if (Environment.TickCount - lastTrayRectTick > 1000) UpdateTrayRect();
                    break;
                case Native.WM_LBUTTONUP:
                    DoClick(settings.LeftClick);
                    break;
                case Native.WM_MBUTTONUP:
                    DoClick(settings.MiddleClick);
                    break;
                case Native.WM_CONTEXTMENU:
                    Native.SetForegroundWindow(win.Handle);
                    menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
                    break;
            }
        }

        void DoClick(ClickAction a)
        {
            switch (a)
            {
                case ClickAction.Slider:
                    if (popup.Visible || Environment.TickCount - popup.LastHiddenTick < 300) return; // クリックで閉じた直後は開き直さない
                    int pct; bool m;
                    audio.TryGet(out pct, out m);
                    popup.ShowNear(tray.GetRect(), pct, m);
                    break;
                case ClickAction.Mute:
                    audio.ToggleMute();
                    UpdateIcon();
                    break;
                case ClickAction.Mixer:
                    Launch("sndvol.exe", null);
                    break;
            }
        }

        void UpdateTrayRect()
        {
            lastTrayRectTick = Environment.TickCount;
            Rectangle r = tray.GetRect();
            if (hook != null) hook.SetTrayRect(r.IsEmpty ? null : new[] { r.Left, r.Top, r.Right, r.Bottom });
        }

        void UpdateIcon() { UpdateIcon(false); }

        void UpdateIcon(bool forceRedraw)
        {
            int pct; bool muted;
            if (!audio.TryGet(out pct, out muted)) { pct = -1; muted = false; }
            if (popup.Visible) popup.SetState(pct, muted);
            if (!forceRedraw && pct == lastPercent && muted == lastMuted) return;
            lastPercent = pct;
            lastMuted = muted;

            int size = Math.Max(16, Native.GetSystemMetrics(Native.SM_CXSMICON));
            Icon icon = IconRenderer.Render(pct, muted, settings.Style, settings.TextColor, lastLight, size);
            tray.Update(icon, pct >= 0 ? ("音量 " + pct + "%" + (muted ? "（ミュート）" : "")) : "音量: 再生デバイスがありません");
        }

        void OnPoll()
        {
            tick++;
            if (tick % 6 == 0)
            {
                bool light = Theme.IsLightTaskbar();
                if (light != lastLight) { lastLight = light; UpdateIcon(true); }
            }
            if (tick % 20 == 0)
            {
                tray.Ensure();        // エクスプローラー側でアイコンが消えていたら登録し直す
                UpdateTrayRect();
            }
            if (forceAudioResetAt != 0 && Environment.TickCount - forceAudioResetAt >= 0)
            {
                forceAudioResetAt = 0;
                audio.Reset();
                UpdateIcon(true);
            }
            UpdateIcon(false);
        }

        // ---------- ホイール ----------

        internal void OnWheel(int delta, int zone)
        {
            Settings s = settings;
            int now = Environment.TickCount;
            int dt = now - lastWheelTick;
            if (dt > 400) accum = 0;
            lastWheelTick = now;
            if (s.Invert) delta = -delta;
            accum += delta;
            int steps = accum / 120;
            accum -= steps * 120;
            if (steps == 0) return;

            int mult = VolumeMath.AccelMultiplier(s, dt);
            int amount = s.Step * mult;

            int cur; bool muted;
            if (!audio.TryGet(out cur, out muted)) return;
            int next = VolumeMath.Next(cur, steps, amount, s.RoundStep);

            if (next != cur) audio.SetPercent(next);
            if (muted && steps > 0 && s.UnmuteOnChange) { audio.SetMute(false); muted = false; }
            if (!muted && next == 0 && s.MuteAtZero) { audio.SetMute(true); muted = true; }
            if (s.Debug) Log.Write("zone=" + zone + " steps=" + steps + " x" + mult + " " + cur + " -> " + next + (muted ? " (muted)" : ""));

            UpdateIcon();
            if (s.ShowOsd)
            {
                try { osd.ShowAt(Cursor.Position, zone, next, muted, s.OsdDurationMs); }
                catch (Exception ex) { Log.Write("OSD failed: " + ex.Message); }
            }
        }

        // ---------- 設定 ----------

        void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(settings.Clone(), IsStartupEnabled(), ApplySettings, () => lastPercent >= 0 ? lastPercent : 50, PreviewOsd);
            settingsForm.FormClosed += delegate { settingsForm.Dispose(); settingsForm = null; };
            settingsForm.Show();
            settingsForm.Activate();
        }

        /// <summary>設定画面の「試しに表示」。画面右下（隅と同じ位置）に出す。</summary>
        void PreviewOsd(int durationMs)
        {
            int pct; bool m;
            if (!audio.TryGet(out pct, out m)) { pct = 0; m = false; }
            Rectangle b = Screen.FromPoint(Cursor.Position).Bounds;
            try { osd.ShowAt(new Point(b.Right - 1, b.Bottom - 1), WheelHook.BR, pct, m, durationMs); }
            catch (Exception ex) { Log.Write("OSD preview failed: " + ex.Message); }
        }

        void ApplySettings(Settings s, bool autoStart)
        {
            bool wasElevated = settings.RunElevated;
            settings = s;
            s.Save();
            hook.UpdateSettings(s);
            SetStartup(autoStart);
            UpdateIcon(true);
            Log.Write("settings applied");
            if (s.RunElevated && !wasElevated) EnableElevation();
            else if (!s.RunElevated && wasElevated) DisableElevation();
        }

        void RevertElevationSetting(bool value)
        {
            settings.RunElevated = value;
            settings.Save();
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.SetRunElevated(value);
        }

        void EnableElevation()
        {
            string err;
            if (!ElevatedTask.RunHelper("--install-task", out err))
            {
                RevertElevationSetting(false);
                MessageBox.Show("「管理者として実行する」を有効にできませんでした。\n" + err, "ScrollVolume", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ElevatedTask.IsElevated()) return;
            // 管理者権限の新しいインスタンスを起動し、こちらは終了する（新しい方は終了を待ってから動き出す）
            if (!ElevatedTask.Start())
            {
                MessageBox.Show("設定は保存しましたが、管理者として再起動できませんでした。次回の起動時から管理者として実行されます。", "ScrollVolume");
                return;
            }
            Log.Write("restarting elevated");
            Timer t = new Timer { Interval = 300 };
            t.Tick += delegate { t.Stop(); t.Dispose(); ExitThread(); };
            t.Start();
        }

        void DisableElevation()
        {
            string err;
            if (!ElevatedTask.RunHelper("--uninstall-task", out err))
            {
                RevertElevationSetting(true);
                MessageBox.Show("「管理者として実行する」を無効にできませんでした。\n" + err, "ScrollVolume", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ElevatedTask.IsElevated())
                MessageBox.Show("無効にしました。次回の起動時から通常の権限で実行されます。", "ScrollVolume");
        }

        static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        static void SetStartup(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enable) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName, false);
                }
            }
            catch (Exception ex)
            {
                Log.Write("SetStartup failed: " + ex.Message);
                MessageBox.Show("自動起動を設定できませんでした。\n" + ex.Message, "ScrollVolume");
            }
        }

        // ---------- その他 ----------

        internal void OnResume(string why)
        {
            Log.Write("resume: " + why);
            hook.Reinstall();
            audio.Reset();
            tray.Ensure();
            UpdateIcon(true);
            forceAudioResetAt = Environment.TickCount + 5000;
            if (forceAudioResetAt == 0) forceAudioResetAt = 1;
        }

        static void Launch(string file, string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file);
                if (args != null) psi.Arguments = args;
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("Launch failed: " + file + " " + ex.Message); }
        }

        protected override void ExitThreadCore()
        {
            Log.Write("exit");
            poll.Stop();
            hook.Stop();
            tray.Dispose();
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            popup.Dispose();
            osd.Dispose();
            menu.Dispose();
            audio.Dispose();
            win.Destroy();
            base.ExitThreadCore();
        }

        /// <summary>
        /// 不可視のトップレベルウィンドウ。フック・トレイからの通知と、スリープ復帰・ロック解除・タスクバー再作成の通知を受け取る。
        /// （メッセージ専用ウィンドウは WM_POWERBROADCAST や TaskbarCreated を受け取れないので普通のウィンドウにする）
        /// </summary>
        sealed class MsgWindow : NativeWindow
        {
            readonly TrayApp app;
            readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

            public MsgWindow(TrayApp owner)
            {
                app = owner;
                CreateHandle(new CreateParams { Caption = "ScrollVolumeMsgWindow" });
                try { Native.WTSRegisterSessionNotification(Handle, 0); } catch { }
                try
                {
                    Native.ChangeWindowMessageFilterEx(Handle, WM_TRAYCALLBACK, Native.MSGFLT_ALLOW, IntPtr.Zero);
                    if (taskbarCreated != 0) Native.ChangeWindowMessageFilterEx(Handle, taskbarCreated, Native.MSGFLT_ALLOW, IntPtr.Zero);
                }
                catch { }
            }

            public void Destroy()
            {
                try { Native.WTSUnRegisterSessionNotification(Handle); } catch { }
                DestroyHandle();
            }

            protected override void WndProc(ref Message m)
            {
                try
                {
                    if (m.Msg == (int)WM_WHEELZONE) { app.OnWheel(m.WParam.ToInt32(), m.LParam.ToInt32()); return; }
                    if (m.Msg == (int)WM_TRAYCALLBACK) { app.OnTrayCallback(m.WParam, m.LParam); return; }
                    if (taskbarCreated != 0 && m.Msg == (int)taskbarCreated)
                    {
                        app.tray.OnTaskbarCreated();
                        app.UpdateTrayRect();
                    }
                    else if (m.Msg == Native.WM_POWERBROADCAST)
                    {
                        int ev = m.WParam.ToInt32();
                        if (ev == Native.PBT_APMRESUMEAUTOMATIC || ev == Native.PBT_APMRESUMESUSPEND) app.OnResume("power");
                    }
                    else if (m.Msg == Native.WM_WTSSESSION_CHANGE && m.WParam.ToInt32() == Native.WTS_SESSION_UNLOCK)
                    {
                        app.OnResume("unlock");
                    }
                    else if (m.Msg == Native.WM_DISPLAYCHANGE)
                    {
                        app.hook.Reinstall();
                        app.UpdateTrayRect();
                    }
                }
                catch (Exception ex) { Log.Write("WndProc: " + ex); }
                base.WndProc(ref m);
            }
        }
    }
}
