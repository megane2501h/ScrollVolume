using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScrollVolume
{
    sealed class TrayApp : ApplicationContext
    {
        public const uint WM_WHEELZONE = Native.WM_APP + 1;
        public const uint WM_TRAYCALLBACK = Native.WM_APP + 2;
        public const uint WM_ZONECLICK = Native.WM_APP + 3;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ScrollVolume";
        /// <summary>サウンド コントロール パネルの「再生」タブ（デバイスの有効・無効・プロパティ）。</summary>
        internal const string SoundControlPanelArgs = "mmsys.cpl,,0";
        const int HK_UP = 1, HK_DOWN = 2, HK_MUTE = 3, HK_MIXER = 4, HK_NEXT = 5;

        readonly MsgWindow win;
        readonly TrayIcon tray;
        readonly Audio audio = new Audio();
        readonly MixerSource mixer = new MixerSource();
        readonly MixerFlyout flyout;
        readonly WheelHook hook;
        readonly Osd osd = new Osd();
        readonly ContextMenuStrip menu;
        readonly Timer poll = new Timer();
        readonly WheelAccumulator wheel = new WheelAccumulator();
        readonly WheelAccumulator keys = new WheelAccumulator();
        readonly bool[] hotkeyFailed = new bool[5];
        int[] registeredHotkeys = new int[5];
        bool hotkeysRegistered, exiting;
        Font menuBold;
        Settings settings;
        SettingsForm settingsForm;

        int lastPercent = -2;
        bool lastMuted, lastLight;
        int tick;
        int lastTrayRectTick;
        int forceAudioResetAt; // 復帰後しばらくしてからもう一度デバイスを取り直す

        public TrayApp()
        {
            settings = Settings.Load();
            L.Set(settings.Language);
            lastLight = Theme.IsLightTaskbar();

            win = new MsgWindow(this);
            tray = new TrayIcon(win.Handle, WM_TRAYCALLBACK);
            menu = BuildMenu();

            flyout = new MixerFlyout(mixer, () => settings);
            flyout.VolumeChanged += () => UpdateIcon();
            flyout.OpenSettings += ShowSettings;
            flyout.StateChanged += SaveMixerState;
            flyout.ShownBounds += r => hook.SetOwnRect(r);

            UpdateIcon();
            UpdateTrayRect();

            hook = new WheelHook(win.Handle, WM_WHEELZONE, WM_ZONECLICK, settings);
            hook.Start();
            RegisterHotkeys();

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

        static IEnumerable<ToolStripItem> Items(ContextMenuStrip m)
        {
            foreach (ToolStripItem it in m.Items) yield return it;
        }

        void FillMenu(ContextMenuStrip m)
        {
            // 前回の項目を破棄してから作り直す（開くたびに増え続けないように）
            List<ToolStripItem> old = new List<ToolStripItem>(Items(m));
            m.Items.Clear();
            foreach (ToolStripItem it in old) it.Dispose();
            if (menuBold == null) menuBold = new Font(m.Font, FontStyle.Bold);
            int pct; bool muted;
            bool ok = audio.TryGet(out pct, out muted);

            m.Items.Add(new ToolStripMenuItem(Terms.VolumeLine(ok ? pct : -1, muted)) { Enabled = false });

            ToolStripMenuItem presets = new ToolStripMenuItem(L.T("音量を設定", "Set volume"));
            for (int v = 100; v >= 0; v -= 10)
            {
                int target = v;
                ToolStripMenuItem it = new ToolStripMenuItem(v + "%", null, delegate { audio.SetPercent(target); UpdateIcon(); });
                it.Checked = ok && pct == v;
                presets.DropDownItems.Add(it);
            }
            m.Items.Add(presets);

            ToolStripMenuItem mute = new ToolStripMenuItem(Terms.Mute, null, delegate { audio.ToggleMute(); UpdateIcon(); });
            mute.Checked = muted;
            m.Items.Add(mute);

            ToolStripMenuItem devices = new ToolStripMenuItem(Terms.PlaybackDevice);
            foreach (AudioDevice d in audio.ListDevices())
            {
                string id = d.Id;
                ToolStripMenuItem it = new ToolStripMenuItem(d.Name, null, delegate { Audio.SetDefaultDevice(id); audio.Reset(); UpdateIcon(); });
                it.Checked = d.IsDefault;
                devices.DropDownItems.Add(it);
            }
            if (devices.DropDownItems.Count == 0) devices.Enabled = false;
            m.Items.Add(devices);

            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem mixerItem = new ToolStripMenuItem(Terms.Mixer + "...", null, delegate { ShowFlyout(); });
            m.Items.Add(mixerItem);
            m.Items.Add(new ToolStripSeparator());
            // Windows 側の画面は Windows での呼び名のまま、1 か所にまとめる
            ToolStripMenuItem win = new ToolStripMenuItem(Terms.WindowsSound);
            win.DropDownItems.Add(Terms.WindowsSoundPage, null, delegate { Launch("ms-settings:sound", null); });
            ToolStripMenuItem winMixer = new ToolStripMenuItem(Terms.WindowsMixer, null, delegate { Launch("sndvol.exe", null); });
            win.DropDownItems.Add(winMixer);
            win.DropDownItems.Add(new ToolStripMenuItem(Terms.WindowsMoreSound, null, delegate { Launch("control.exe", SoundControlPanelArgs); }) { ToolTipText = Terms.WindowsMoreSoundTip });
            win.DropDown.ShowItemToolTips = true;
            // 設定の画面（Windows 側・ScrollVolume 側）はひとまとまりにする
            m.Items.Add(win);
            m.Items.Add(Terms.Settings + "...", null, delegate { ShowSettings(); });
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(L.T("終了", "Exit"), null, delegate { ExitThread(); });

            // 太字は「左クリックと同じ動作」（Windows のトレイメニューの慣習）
            ToolStripMenuItem bold = DefaultItem(settings.LeftClick, mixerItem, mute, winMixer);
            if (bold != null) bold.Font = menuBold;
        }

        /// <summary>メニューで太字にする項目（左クリックと同じ動作の項目。何もしない設定なら null）。</summary>
        internal static ToolStripMenuItem DefaultItem(ClickAction leftClick, ToolStripMenuItem mixer, ToolStripMenuItem mute, ToolStripMenuItem windowsMixer)
        {
            switch (leftClick)
            {
                case ClickAction.Slider: return mixer;
                case ClickAction.Mute: return mute;
                case ClickAction.Mixer: return windowsMixer;
                default: return null;
            }
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
                case Native.NIN_KEYSELECT: // キーボードでアイコンを選んで Enter / Space
                    DoClick(settings.LeftClick);
                    break;
                case Native.WM_CONTEXTMENU:
                {
                    // NOTIFYICON_VERSION_4 では wParam にアイコンの位置が入る（キーボードで開いたときはマウスの位置と違う）
                    long wp = wParam.ToInt64();
                    Point at = new Point((short)(wp & 0xFFFF), (short)((wp >> 16) & 0xFFFF));
                    if (at == Point.Empty) at = Cursor.Position;
                    Native.SetForegroundWindow(win.Handle);
                    // カーソルのある画面からはみ出さない向きに開く（左や上のタスクバーで隣の画面に出ないように）
                    menu.Show(at, MenuPlacement.Direction(at, Screen.FromPoint(at).WorkingArea));
                    break;
                }
            }
        }

        void DoClick(ClickAction a)
        {
            switch (a)
            {
                case ClickAction.Slider: ToggleFlyout(); break;
                case ClickAction.Mute: audio.ToggleMute(); UpdateIcon(); break;
                case ClickAction.Mixer: Launch("sndvol.exe", null); break;
            }
        }

        void ToggleFlyout()
        {
            if (flyout.Visible && flyout.Windowed)
            {
                // ウィンドウ表示: 最小化 → 元に戻す、手前にある（直前までアクティブ）→ 閉じる、裏にある → 手前へ
                if (flyout.WindowState == FormWindowState.Minimized) { flyout.WindowState = FormWindowState.Normal; flyout.Activate(); }
                else if (Environment.TickCount - flyout.LastDeactivatedTick < 300) flyout.HideFlyout();
                else { flyout.Activate(); Native.SetForegroundWindow(flyout.Handle); }
                return;
            }
            if (flyout.Visible) { flyout.HideFlyout(); return; }
            if (Environment.TickCount - flyout.LastHiddenTick < 300) return; // クリックで閉じた直後は開き直さない
            ShowFlyout();
        }

        void ShowFlyout()
        {
            try { flyout.ShowNear(tray.GetRect()); }
            catch (Exception ex) { Log.Write("Flyout failed: " + ex); }
        }

        void SaveMixerState()
        {
            settings.MixerWindowed = flyout.Windowed;
            settings.MixerTopmost = flyout.Topmost;
            settings.SetCollapsed(flyout.CollapsedIds);
            settings.SetMixerSize(flyout.UserClientSize);
            if (flyout.Windowed && flyout.Visible && flyout.WindowState == FormWindowState.Normal) settings.MixerPosition = flyout.Left + "," + flyout.Top;
            settings.Save();
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
            if (!forceRedraw && pct == lastPercent && muted == lastMuted) return;
            lastPercent = pct;
            lastMuted = muted;

            int size = Math.Max(16, Native.GetSystemMetrics(Native.SM_CXSMICON));
            Icon icon = IconRenderer.Render(pct, muted, settings.Style, settings.TextColor, lastLight, size);
            tray.Update(icon, Terms.VolumeLine(pct, muted));
        }

        void OnPoll()
        {
            tick++;
            GameDetect.Update();
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

        // ---------- 音量の変更（ホイール・ホットキー共通）----------

        internal void OnWheel(int delta, int zone)
        {
            Settings s = settings;
            int dt;
            int n = wheel.Feed(s.Invert ? -delta : delta, Environment.TickCount, out dt);
            if (n != 0) Adjust(n, dt, Cursor.Position, zone);
        }

        void Adjust(int notches, int dtMs, Point osdAt, int zone)
        {
            Settings s = settings;
            int cur; bool muted;
            if (!audio.TryGet(out cur, out muted)) return;
            int next = VolumeMath.Next(cur, notches, s, dtMs);
            bool nm = MuteRules.After(muted, next, notches > 0, s);
            if (next != cur) audio.SetPercent(next);
            if (nm != muted) audio.SetMute(nm);
            if (s.Debug) Log.Write("zone=" + zone + " notches=" + notches + " dt=" + dtMs + " " + cur + " -> " + next + (nm ? " (muted)" : ""));
            UpdateIcon();
            ShowOsd(osdAt, zone, next, nm);
        }

        internal void OnZoneClick(int zone)
        {
            audio.ToggleMute();
            UpdateIcon();
            int p; bool m;
            if (audio.TryGet(out p, out m)) ShowOsd(Cursor.Position, zone, p, m);
        }

        bool OsdAllowed { get { return settings.ShowOsd && !(settings.OsdHideInGames && GameDetect.Active); } }

        void ShowOsd(Point at, int zone, int pct, bool muted)
        {
            if (!OsdAllowed) return;
            try { osd.ShowAt(at, zone, pct, muted, settings.OsdDurationMs); }
            catch (Exception ex) { Log.Write("OSD failed: " + ex.Message); }
        }

        /// <summary>ホットキー用: カーソルのある画面の下中央。</summary>
        static Point BottomCenter()
        {
            Rectangle b = Screen.FromPoint(Cursor.Position).Bounds;
            return new Point(b.Left + b.Width / 2, b.Bottom - 1);
        }

        // ---------- ホットキー ----------

        void RegisterHotkeys()
        {
            UnregisterHotkeys();
            int[] hk = settings.Hotkeys();
            for (int i = 0; i < hk.Length; i++)
            {
                hotkeyFailed[i] = false;
                if (hk[i] == 0) continue;
                int id = i + 1;
                uint mods = (uint)Hotkey.Mods(hk[i]);
                if (id != HK_UP && id != HK_DOWN) mods |= Native.MOD_NOREPEAT; // 音量の上げ下げだけ押しっぱなしで繰り返す
                if (!Native.RegisterHotKey(win.Handle, id, mods, (uint)Hotkey.Key(hk[i])))
                {
                    hotkeyFailed[i] = true;
                    Log.Write("RegisterHotKey failed: " + Hotkey.Format(hk[i]));
                }
            }
            hotkeysRegistered = true;
            registeredHotkeys = hk;
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.SetHotkeyStatus(hotkeyFailed, registeredHotkeys);
        }

        void UnregisterHotkeys()
        {
            if (!hotkeysRegistered) return;
            for (int id = 1; id <= 5; id++) Native.UnregisterHotKey(win.Handle, id);
            hotkeysRegistered = false;
        }

        internal void OnHotkey(int id)
        {
            switch (id)
            {
                case HK_UP:
                case HK_DOWN:
                    int dt;
                    int n = keys.Feed(id == HK_UP ? 120 : -120, Environment.TickCount, out dt);
                    if (n != 0) Adjust(n, dt, BottomCenter(), WheelHook.BOTTOM);
                    break;
                case HK_MUTE:
                    audio.ToggleMute();
                    UpdateIcon();
                    int p; bool m;
                    if (audio.TryGet(out p, out m)) ShowOsd(BottomCenter(), WheelHook.BOTTOM, p, m);
                    break;
                case HK_MIXER:
                    ToggleFlyout();
                    break;
                case HK_NEXT:
                    NextDevice();
                    break;
            }
        }

        void NextDevice()
        {
            List<AudioDevice> list = audio.ListDevices();
            if (list.Count == 0) return;
            int cur = list.FindIndex(d => d.IsDefault);
            AudioDevice next = list[(cur + 1) % list.Count];
            if (list.Count > 1) { Audio.SetDefaultDevice(next.Id); audio.Reset(); }
            UpdateIcon(true);
            if (!OsdAllowed) return;
            try { osd.ShowText(BottomCenter(), WheelHook.BOTTOM, next.Name, settings.OsdDurationMs); }
            catch (Exception ex) { Log.Write("OSD failed: " + ex.Message); }
        }

        // ---------- 設定 ----------

        void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(settings.Clone(), IsStartupEnabled(), ApplySettings, () => lastPercent >= 0 ? lastPercent : 50, PreviewOsd);
            settingsForm.SetHotkeyStatus(hotkeyFailed, registeredHotkeys);
            settingsForm.HotkeyCapture += capturing => { if (exiting) return; if (capturing) UnregisterHotkeys(); else RegisterHotkeys(); };
            settingsForm.FormClosed += delegate { settingsForm.Dispose(); settingsForm = null; if (!hotkeysRegistered && !exiting) RegisterHotkeys(); };
            settingsForm.Show();
            settingsForm.Activate();
        }

        /// <summary>設定画面の「プレビュー」。画面右下（隅と同じ位置）に出す。</summary>
        void PreviewOsd(int durationMs)
        {
            int pct; bool m;
            if (!audio.TryGet(out pct, out m)) { pct = 0; m = false; }
            Rectangle b = Screen.FromPoint(Cursor.Position).Bounds;
            try { osd.ShowAt(new Point(b.Right - 1, b.Bottom - 1), WheelHook.BR, pct, m, durationMs); }
            catch (Exception ex) { Log.Write("OSD preview failed: " + ex.Message); }
        }

        /// <summary>設定画面を開いている間に変わったミキサーの状態（ピン・折りたたみ・位置・大きさ）は、画面の値で上書きしない。</summary>
        internal static void KeepMixerState(Settings from, Settings to)
        {
            to.MixerWindowed = from.MixerWindowed;
            to.MixerTopmost = from.MixerTopmost;
            to.MixerPosition = from.MixerPosition;
            to.MixerSize = from.MixerSize;
            to.CollapsedDevices = from.CollapsedDevices;
        }

        void ApplySettings(Settings s, bool autoStart)
        {
            bool wasElevated = settings.RunElevated;
            bool langChanged = settings.Language != s.Language;
            KeepMixerState(settings, s);
            settings = s;
            L.Set(s.Language);
            s.Save();
            hook.UpdateSettings(s);
            RegisterHotkeys();
            SetStartup(autoStart);
            UpdateIcon(true);
            Log.Write("settings applied");
            if (langChanged && settingsForm != null && !settingsForm.IsDisposed)
            {
                // 言語を変えたら設定画面を開き直す（適用ボタンの処理が終わってから）
                SettingsForm old = settingsForm;
                old.BeginInvoke((Action)delegate { int tab = old.Tabs.SelectedIndex; old.Close(); ShowSettings(); if (settingsForm != null) settingsForm.Tabs.SelectedIndex = tab; });
            }
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
                MessageBox.Show(L.T("「管理者として実行する」を有効にできませんでした。\n", "Could not turn on \"Run as administrator\".\n") + err, "ScrollVolume", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ElevatedTask.IsElevated()) return;
            // 管理者権限の新しいインスタンスを起動し、こちらは終了する（新しい方は終了を待ってから動き出す）
            if (!ElevatedTask.Start())
            {
                MessageBox.Show(L.T("設定は保存しましたが、管理者として再起動できませんでした。次回の起動時から管理者として実行されます。", "Saved, but could not restart as administrator. It will run as administrator from the next start."), "ScrollVolume");
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
                MessageBox.Show(L.T("「管理者として実行する」を無効にできませんでした。\n", "Could not turn off \"Run as administrator\".\n") + err, "ScrollVolume", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (ElevatedTask.IsElevated())
                MessageBox.Show(L.T("無効にしました。次回の起動時から通常の権限で実行されます。", "Turned off. It will run without elevation from the next start."), "ScrollVolume");
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
                MessageBox.Show(L.T("自動起動を設定できませんでした。\n", "Could not change Start with Windows.\n") + ex.Message, "ScrollVolume");
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
            exiting = true;
            poll.Stop();
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            UnregisterHotkeys();
            hook.Stop();
            tray.Dispose();
            flyout.Dispose();
            mixer.Dispose();
            osd.Dispose();
            foreach (ToolStripItem it in new List<ToolStripItem>(Items(menu))) it.Dispose();
            menu.Dispose();
            if (menuBold != null) menuBold.Dispose();
            audio.Dispose();
            win.Destroy();
            base.ExitThreadCore();
        }

        /// <summary>
        /// 不可視のトップレベルウィンドウ。フック・トレイ・ホットキーからの通知と、スリープ復帰・ロック解除・タスクバー再作成の通知を受け取る。
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
                    if (m.Msg == (int)WM_ZONECLICK) { app.OnZoneClick(m.LParam.ToInt32()); return; }
                    if (m.Msg == (int)WM_TRAYCALLBACK) { app.OnTrayCallback(m.WParam, m.LParam); return; }
                    if (m.Msg == Native.WM_HOTKEY) { app.OnHotkey(m.WParam.ToInt32()); return; }
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
