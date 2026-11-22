using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace ScrollVolume
{
    // ---- Core Audio（アプリの音量。ScrollVolume ミキサーが使う）----

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
        [PreserveSig] int GetMeteringChannelCount(out uint pnChannelCount);
        [PreserveSig] int GetChannelsPeakValues(uint u32ChannelCount, IntPtr afPeakValues);
        [PreserveSig] int QueryHardwareSupport(out uint pdwHardwareSupportMask);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        [PreserveSig] int RegisterSessionNotification(IntPtr SessionNotification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr SessionNotification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionCount, [MarshalAs(UnmanagedType.IUnknown)] out object Session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    // ---- ミキサー画面が表示に使うデータ ----

    sealed class MixerApp
    {
        public string Key;      // 同じアプリのセッションをまとめる鍵（実行ファイルのパス等）
        public string Name;
        public Bitmap Icon;     // 共有のキャッシュ。破棄しないこと
        public bool IsSystem;
        public int Volume;      // 0〜100
        public bool Muted;
        public float Peak;      // 0〜1
    }

    sealed class MixerDevice
    {
        public string Id;
        public string Name;
        public bool IsDefault;
        public int Volume;
        public bool Muted;
        public float Peak;
        public List<MixerApp> Apps = new List<MixerApp>();
    }

    /// <summary>ミキサー画面のデータ元（テストでは偽物に差し替える）。</summary>
    interface IMixerSource
    {
        List<MixerDevice> Devices { get; }
        /// <summary>full: デバイスとアプリの一覧を取り直す。false なら音量・メーターだけ更新。</summary>
        void Refresh(bool full);
        void SetDeviceVolume(string deviceId, int volume);
        void SetDeviceMute(string deviceId, bool mute);
        void SetAppVolume(string deviceId, string appKey, int volume);
        void SetAppMute(string deviceId, string appKey, bool mute);
        void SetDefault(string deviceId);
    }

    /// <summary>
    /// 実際の再生デバイスとアプリ（オーディオ セッション）。UI スレッドからだけ使う。
    /// 一覧の取り直しでは同じセッションを使い回し、消えたものだけ解放する。
    /// </summary>
    sealed class MixerSource : IMixerSource, IDisposable
    {
        const int eRender = 0, CLSCTX_ALL = 23, DEVICE_STATE_ACTIVE = 1, StateExpired = 2;
        static Guid Ctx = new Guid("6F1C2C3E-6E5B-4C59-9E0B-3D7B2A1C9F41");

        sealed class Sess
        {
            public string Iid;
            public IAudioSessionControl2 Ctl;
            public ISimpleAudioVolume Vol;
            public IAudioMeterInformation Meter;
            public string Group;
        }

        sealed class Dev
        {
            public string Id;
            public IMMDevice D;
            public IAudioEndpointVolume Vol;
            public IAudioMeterInformation Meter;
            public IAudioSessionManager2 Mgr;
            public MixerDevice Model;
            public readonly List<Sess> Sessions = new List<Sess>();
            public readonly List<string> Order = new List<string>(); // アプリの並び（初めて見た順で固定）
            public readonly Dictionary<string, MixerApp> Apps = new Dictionary<string, MixerApp>();
        }

        sealed class AppInfo { public string Key, Name, Path; }

        IMMDeviceEnumerator en;
        readonly List<Dev> devs = new List<Dev>();
        readonly Dictionary<uint, AppInfo> appInfo = new Dictionary<uint, AppInfo>();
        static readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        List<MixerDevice> models = new List<MixerDevice>();
        bool needFull = true;

        public List<MixerDevice> Devices { get { return models; } }

        public void Refresh(bool full)
        {
            try
            {
                if (full || needFull) Rebuild();
                UpdateValues();
            }
            catch (Exception ex)
            {
                Log.Write("Mixer.Refresh failed: " + ex.Message);
                ReleaseAll();
                needFull = true;
            }
        }

        void Rebuild()
        {
            needFull = false;
            if (en == null) en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();

            string defId = null;
            IMMDevice def;
            if (en.GetDefaultAudioEndpoint(eRender, 0, out def) == 0 && def != null)
            {
                def.GetId(out defId);
                Marshal.ReleaseComObject(def);
            }

            List<Dev> next = new List<Dev>();
            IMMDeviceCollection col = null;
            try
            {
                en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out col);
                uint n;
                col.GetCount(out n);
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice d = null;
                    try
                    {
                        col.Item(i, out d);
                        string id;
                        d.GetId(out id);
                        Dev existing = devs.Find(x => x.Id == id);
                        if (existing != null) { Marshal.ReleaseComObject(d); d = null; next.Add(existing); continue; }
                        Dev nd = OpenDevice(d, id);
                        d = null;
                        if (nd != null) next.Add(nd);
                    }
                    catch (Exception ex) { Log.Write("Mixer device: " + ex.Message); }
                    finally { if (d != null) Marshal.ReleaseComObject(d); }
                }
            }
            catch
            {
                // 列挙の途中で失敗したら、今回新しく開いたデバイスを解放してから伝える（ReleaseAll は devs しか見ないため）
                foreach (Dev nd in next) if (!devs.Contains(nd)) ReleaseDevice(nd);
                throw;
            }
            finally { if (col != null) Marshal.ReleaseComObject(col); }

            foreach (Dev old in devs) if (!next.Contains(old)) ReleaseDevice(old);
            devs.Clear();
            // 既定のデバイスを先頭に、それ以外は Windows の並び順
            Dev first = next.Find(x => x.Id == defId);
            if (first != null) devs.Add(first);
            foreach (Dev d in next) if (d != first) devs.Add(d);

            HashSet<uint> livePids = new HashSet<uint>();
            List<MixerDevice> list = new List<MixerDevice>();
            foreach (Dev d in devs)
            {
                d.Model.IsDefault = d.Id == defId;
                RebuildSessions(d, livePids);
                list.Add(d.Model);
            }
            foreach (uint pid in new List<uint>(appInfo.Keys)) if (!livePids.Contains(pid)) appInfo.Remove(pid);
            models = list;
        }

        Dev OpenDevice(IMMDevice d, string id)
        {
            Dev nd = new Dev { Id = id, D = d };
            try
            {
                object o;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
                nd.Vol = (IAudioEndpointVolume)o;
                try { iid = typeof(IAudioMeterInformation).GUID; d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o); nd.Meter = (IAudioMeterInformation)o; } catch { }
                try { iid = typeof(IAudioSessionManager2).GUID; d.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o); nd.Mgr = (IAudioSessionManager2)o; } catch { }
                nd.Model = new MixerDevice { Id = id, Name = Audio.FriendlyName(d) ?? id };
                return nd;
            }
            catch (Exception ex)
            {
                Log.Write("Mixer open device: " + ex.Message);
                ReleaseDevice(nd);
                return null;
            }
        }

        void RebuildSessions(Dev d, HashSet<uint> livePids)
        {
            List<Sess> seen = new List<Sess>();
            IAudioSessionEnumerator e = null;
            try
            {
                if (d.Mgr == null || d.Mgr.GetSessionEnumerator(out e) != 0 || e == null) return;
                int n;
                if (e.GetCount(out n) != 0) return;
                for (int i = 0; i < n; i++)
                {
                    object o = null;
                    try
                    {
                        if (e.GetSession(i, out o) != 0 || o == null) continue;
                        IAudioSessionControl2 c = o as IAudioSessionControl2;
                        if (c == null) continue;
                        string iid;
                        c.GetSessionInstanceIdentifier(out iid);
                        int state;
                        c.GetState(out state);
                        uint pid;
                        c.GetProcessId(out pid);
                        bool system = c.IsSystemSoundsSession() == 0;
                        Sess existing = d.Sessions.Find(x => x.Iid == iid);
                        bool alive = state != StateExpired && (system || pid == 0 || Native.ProcessAlive(pid));
                        if (alive && !system && pid != 0) livePids.Add(pid);
                        if (!alive || existing != null || seen.Exists(x => x.Iid == iid))
                        {
                            if (alive && existing != null && !seen.Contains(existing)) seen.Add(existing);
                            continue; // 使い回す（今回取得した分は finally で解放）
                        }
                        AppInfo info = Info(system, pid, c);
                        seen.Add(new Sess { Iid = iid, Ctl = c, Vol = c as ISimpleAudioVolume, Meter = c as IAudioMeterInformation, Group = info.Key });
                        if (!d.Apps.ContainsKey(info.Key))
                            d.Apps[info.Key] = new MixerApp { Key = info.Key, Name = info.Name, IsSystem = system, Icon = system ? null : IconFor(info.Path) };
                        o = null; // 保持する
                    }
                    catch (Exception ex) { Log.Write("Mixer session: " + ex.Message); }
                    finally { if (o != null) Marshal.ReleaseComObject(o); }
                }
            }
            finally { if (e != null) Marshal.ReleaseComObject(e); }

            foreach (Sess s in d.Sessions) if (!seen.Contains(s)) ReleaseSession(s);
            d.Sessions.Clear();
            d.Sessions.AddRange(seen);

            // 並び順を保ったまま、今あるアプリだけを並べる（システム音は最後）
            HashSet<string> groups = new HashSet<string>();
            foreach (Sess s in seen) groups.Add(s.Group);
            foreach (string g in groups) if (!d.Order.Contains(g)) d.Order.Add(g);
            d.Order.RemoveAll(g => !groups.Contains(g));
            foreach (string k in new List<string>(d.Apps.Keys)) if (!groups.Contains(k)) d.Apps.Remove(k);
            List<MixerApp> apps = new List<MixerApp>();
            MixerApp sys = null;
            foreach (string g in d.Order)
            {
                MixerApp a = d.Apps[g];
                if (a.IsSystem) sys = a; else apps.Add(a);
            }
            if (sys != null) apps.Add(sys);
            d.Model.Apps = apps;
        }

        AppInfo Info(bool system, uint pid, IAudioSessionControl2 c)
        {
            if (system) return new AppInfo { Key = "system", Name = Terms.SystemSounds };
            AppInfo ai;
            if (appInfo.TryGetValue(pid, out ai)) return ai;
            string path = pid == 0 ? null : Native.ProcessPath(pid);
            string display;
            if (c.GetDisplayName(out display) != 0) display = null;
            string name = null;
            if (!string.IsNullOrEmpty(display) && !display.StartsWith("@")) name = display;
            if (name == null && path != null)
            {
                try { string desc = FileVersionInfo.GetVersionInfo(path).FileDescription; if (!string.IsNullOrEmpty(desc) && desc.Trim().Length > 0) name = desc.Trim(); } catch { }
                if (name == null) name = Path.GetFileNameWithoutExtension(path);
            }
            if (name == null) name = "PID " + pid;
            ai = new AppInfo { Key = path != null ? path.ToLowerInvariant() : "pid:" + pid, Name = name, Path = path };
            appInfo[pid] = ai;
            return ai;
        }

        static Bitmap IconFor(string path)
        {
            if (path == null) return null;
            Bitmap b;
            if (iconCache.TryGetValue(path, out b)) return b;
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(path))
                    b = ic == null ? null : ic.ToBitmap();
            }
            catch { b = null; }
            iconCache[path] = b;
            return b;
        }

        void UpdateValues()
        {
            foreach (Dev d in devs)
            {
                try
                {
                    float f;
                    bool m;
                    d.Vol.GetMasterVolumeLevelScalar(out f);
                    d.Vol.GetMute(out m);
                    d.Model.Volume = (int)Math.Round(f * 100f);
                    d.Model.Muted = m;
                    float pk = 0;
                    if (d.Meter != null && d.Meter.GetPeakValue(out pk) != 0) pk = 0;
                    d.Model.Peak = pk;
                }
                catch { needFull = true; continue; }

                foreach (MixerApp a in d.Model.Apps) a.Peak = 0;
                HashSet<string> done = new HashSet<string>();
                foreach (Sess s in d.Sessions)
                {
                    MixerApp a;
                    if (!d.Apps.TryGetValue(s.Group, out a)) continue;
                    try
                    {
                        if (!done.Contains(s.Group) && s.Vol != null)
                        {
                            float v; bool m;
                            if (s.Vol.GetMasterVolume(out v) == 0) a.Volume = (int)Math.Round(v * 100f);
                            if (s.Vol.GetMute(out m) == 0) a.Muted = m;
                            done.Add(s.Group);
                        }
                        float pk;
                        if (s.Meter != null && s.Meter.GetPeakValue(out pk) == 0 && pk > a.Peak) a.Peak = pk;
                    }
                    catch { needFull = true; }
                }
            }
        }

        Dev Find(string id) { return devs.Find(x => x.Id == id); }

        public void SetDeviceVolume(string deviceId, int volume)
        {
            Dev d = Find(deviceId);
            if (d == null) return;
            volume = Math.Max(0, Math.Min(100, volume));
            try { d.Vol.SetMasterVolumeLevelScalar(volume / 100f, ref Ctx); d.Model.Volume = volume; }
            catch (Exception ex) { Log.Write("Mixer.SetDeviceVolume: " + ex.Message); needFull = true; }
        }

        public void SetDeviceMute(string deviceId, bool mute)
        {
            Dev d = Find(deviceId);
            if (d == null) return;
            try { d.Vol.SetMute(mute, ref Ctx); d.Model.Muted = mute; }
            catch (Exception ex) { Log.Write("Mixer.SetDeviceMute: " + ex.Message); needFull = true; }
        }

        public void SetAppVolume(string deviceId, string appKey, int volume)
        {
            Dev d = Find(deviceId);
            MixerApp a;
            if (d == null || !d.Apps.TryGetValue(appKey, out a)) return;
            volume = Math.Max(0, Math.Min(100, volume));
            foreach (Sess s in d.Sessions)
                if (s.Group == appKey && s.Vol != null)
                    try { s.Vol.SetMasterVolume(volume / 100f, ref Ctx); } catch { needFull = true; }
            a.Volume = volume;
        }

        public void SetAppMute(string deviceId, string appKey, bool mute)
        {
            Dev d = Find(deviceId);
            MixerApp a;
            if (d == null || !d.Apps.TryGetValue(appKey, out a)) return;
            foreach (Sess s in d.Sessions)
                if (s.Group == appKey && s.Vol != null)
                    try { s.Vol.SetMute(mute, ref Ctx); } catch { needFull = true; }
            a.Muted = mute;
        }

        public void SetDefault(string deviceId)
        {
            Audio.SetDefaultDevice(deviceId);
            needFull = true;
        }

        static void ReleaseSession(Sess s)
        {
            // Ctl / Vol / Meter は同じ RCW
            if (s.Ctl != null) { try { Marshal.FinalReleaseComObject(s.Ctl); } catch { } }
            s.Ctl = null; s.Vol = null; s.Meter = null;
        }

        static void ReleaseDevice(Dev d)
        {
            foreach (Sess s in d.Sessions) ReleaseSession(s);
            d.Sessions.Clear();
            if (d.Mgr != null) { try { Marshal.ReleaseComObject(d.Mgr); } catch { } d.Mgr = null; }
            if (d.Meter != null) { try { Marshal.ReleaseComObject(d.Meter); } catch { } d.Meter = null; }
            if (d.Vol != null) { try { Marshal.ReleaseComObject(d.Vol); } catch { } d.Vol = null; }
            if (d.D != null) { try { Marshal.ReleaseComObject(d.D); } catch { } d.D = null; }
        }

        void ReleaseAll()
        {
            foreach (Dev d in devs) ReleaseDevice(d);
            devs.Clear();
            models = new List<MixerDevice>();
            appInfo.Clear();
            if (en != null) { try { Marshal.ReleaseComObject(en); } catch { } en = null; }
        }

        public void Dispose() { ReleaseAll(); }
    }
}
